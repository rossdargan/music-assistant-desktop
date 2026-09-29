using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MaMini.Tests.Fakes;

/// <summary>Result that the fake server streams back in several partial messages.</summary>
public sealed record PartialResult(IReadOnlyList<object?> Items, int ChunkSize);

/// <summary>
/// In-process Music Assistant WebSocket server (raw TCP + manual upgrade, so no http.sys/URL ACLs needed).
/// </summary>
public sealed class FakeMaServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<WebSocket, SemaphoreSlim> _sockets = new();
    private Task? _acceptLoop;

    public string? RequiredToken { get; set; }

    public bool OnboardDone { get; set; } = true;

    /// <summary>Old servers answer the auth command with InvalidCommand (12).</summary>
    public bool SupportsAuth { get; set; } = true;

    public string ServerVersion { get; set; } = "2.7.0";

    public ConcurrentDictionary<string, Func<JsonElement, Task<object?>>> Handlers { get; } = new();

    public ConcurrentQueue<(string Command, JsonElement Args)> Received { get; } = new();

    public int ConnectionCount;

    public Uri HttpBase => new($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}");

    public FakeMaServer Start()
    {
        _listener.Start();
        _acceptLoop = AcceptLoopAsync();
        return this;
    }

    public void Handle(string command, Func<JsonElement, object?> handler) =>
        Handlers[command] = args => Task.FromResult(handler(args));

    public void HandleAsync(string command, Func<JsonElement, Task<object?>> handler) => Handlers[command] = handler;

    public IEnumerable<JsonElement> ArgsFor(string command) => Received.Where(r => r.Command == command).Select(r => r.Args);

    public async Task SendEventAsync(string name, string? objectId, object? data)
    {
        foreach (var socket in _sockets.Keys)
        {
            await SendAsync(socket, new { @event = name, object_id = objectId, data });
        }
    }

    /// <summary>Abruptly drops every connected client.</summary>
    public void DropConnections()
    {
        foreach (var socket in _sockets.Keys)
        {
            socket.Abort();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        DropConnections();
        if (_acceptLoop is not null)
        {
            try
            {
                await _acceptLoop;
            }
            catch
            {
                // ignored
            }
        }
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token);
            }
            catch
            {
                return;
            }

            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using var tcp = client;
        var stream = client.GetStream();
        var key = await ReadUpgradeRequestAsync(stream);
        if (key is null)
        {
            return;
        }

        var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
        var response = "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
                       $"Sec-WebSocket-Accept: {accept}\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(response));

        using var socket = WebSocket.CreateFromStream(stream, isServer: true, subProtocol: null, keepAliveInterval: TimeSpan.Zero);
        _sockets[socket] = new SemaphoreSlim(1, 1);
        Interlocked.Increment(ref ConnectionCount);
        try
        {
            await SendAsync(socket, new
            {
                server_id = "fake",
                server_version = ServerVersion,
                schema_version = 28,
                min_supported_schema_version = 24,
                base_url = HttpBase.ToString().TrimEnd('/'),
                homeassistant_addon = false,
                onboard_done = OnboardDone,
            });

            if (!OnboardDone)
            {
                await SendAsync(socket, new { message_id = "connection", error_code = 503, details = "Setup required" });
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "setup", CancellationToken.None);
                return;
            }

            var authenticated = RequiredToken is null;
            var buffer = new byte[64 * 1024];
            while (socket.State == WebSocketState.Open && !_cts.IsCancellationRequested)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(buffer, _cts.Token);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        return;
                    }

                    ms.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                using var doc = JsonDocument.Parse(ms.ToArray());
                var root = doc.RootElement;
                var id = root.GetProperty("message_id").ToString();
                var command = root.GetProperty("command").GetString()!;
                var args = root.TryGetProperty("args", out var a) ? a.Clone() : default;
                Received.Enqueue((command, args));

                if (command == "auth")
                {
                    if (!SupportsAuth)
                    {
                        await SendAsync(socket, new { message_id = id, error_code = 12, details = "Invalid command: auth" });
                        continue;
                    }

                    var token = args.TryGetProperty("token", out var t) ? t.GetString() : null;
                    if (RequiredToken is not null && token != RequiredToken)
                    {
                        await SendAsync(socket, new { message_id = id, error_code = 23, details = "Invalid token" });
                        continue;
                    }

                    authenticated = true;
                    await SendAsync(socket, new { message_id = id, result = new { authenticated = true, user = new { user_id = "user-1", username = "test" } } });
                    continue;
                }

                if (!authenticated)
                {
                    await SendAsync(socket, new { message_id = id, error_code = 20, details = "Authentication required" });
                    continue;
                }

                _ = RespondAsync(socket, id, command, args);
            }
        }
        catch
        {
            // Connection dropped.
        }
        finally
        {
            _sockets.TryRemove(socket, out var _);
        }
    }

    private async Task RespondAsync(WebSocket socket, string id, string command, JsonElement args)
    {
        try
        {
            if (!Handlers.TryGetValue(command, out var handler))
            {
                await SendAsync(socket, new { message_id = id, error_code = 12, details = $"Invalid command: {command}" });
                return;
            }

            object? result;
            try
            {
                result = await handler(args);
            }
            catch (Exception ex)
            {
                await SendAsync(socket, new { message_id = id, error_code = 999, details = ex.Message });
                return;
            }

            if (result is PartialResult partial)
            {
                var chunks = partial.Items.Chunk(partial.ChunkSize).ToList();
                for (var i = 0; i < chunks.Count - 1; i++)
                {
                    await SendAsync(socket, new { message_id = id, result = chunks[i], partial = true });
                }

                await SendAsync(socket, new { message_id = id, result = chunks.Count > 0 ? chunks[^1] : Array.Empty<object?>() });
                return;
            }

            await SendAsync(socket, new { message_id = id, result });
        }
        catch
        {
            // Connection dropped.
        }
    }

    private async Task SendAsync(WebSocket socket, object payload)
    {
        if (!_sockets.TryGetValue(socket, out var gate))
        {
            gate = new SemaphoreSlim(1, 1);
        }

        var json = payload is JsonNode node ? node.ToJsonString() : JsonSerializer.Serialize(payload);
        await gate.WaitAsync();
        try
        {
            await socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None);
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task<string?> ReadUpgradeRequestAsync(NetworkStream stream)
    {
        var header = new StringBuilder();
        var one = new byte[1];
        while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            if (await stream.ReadAsync(one) == 0 || header.Length > 16 * 1024)
            {
                return null;
            }

            header.Append((char)one[0]);
        }

        foreach (var line in header.ToString().Split("\r\n"))
        {
            if (line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase))
            {
                return line["Sec-WebSocket-Key:".Length..].Trim();
            }
        }

        return null;
    }
}
