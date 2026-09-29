using System.Buffers;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using MaMini.Core.Diagnostics;
using MaMini.Core.Models;

namespace MaMini.Core.Api;

/// <summary>
/// Music Assistant WebSocket API client: connects, authenticates, correlates command replies by
/// message_id, raises server events and reconnects with exponential backoff.
/// </summary>
public sealed class MaClient : IAsyncDisposable
{
    private readonly Func<ClientWebSocket> _socketFactory;
    private readonly ConcurrentDictionary<string, PendingRequest> _pending = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly object _gate = new();
    private readonly Random _random = new();

    private CancellationTokenSource? _runCts;
    private Task? _runTask;
    private CancellationTokenSource _delayCts = new();
    private ClientWebSocket? _socket;
    private TaskCompletionSource<ServerInfo>? _serverInfoTcs;
    private long _nextId;
    private ConnectionState _state = ConnectionState.Stopped;

    public MaClient(Func<ClientWebSocket>? socketFactory = null)
    {
        _socketFactory = socketFactory ?? (() => new ClientWebSocket());
    }

    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(15);
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(10);
    public TimeSpan MinBackoff { get; set; } = TimeSpan.FromSeconds(1);
    public TimeSpan MaxBackoff { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan AuthFailedRetry { get; set; } = TimeSpan.FromMinutes(5);

    public ConnectionState State
    {
        get { lock (_gate) { return _state; } }
    }

    public ServerInfo? ServerInfo { get; private set; }

    /// <summary>The user id reported by the auth reply (if any); used to filter per-user events.</summary>
    public string? UserId { get; private set; }

    public string? LastError { get; private set; }

    public Uri? HttpBase { get; private set; }

    public string? Token { get; private set; }

    public event EventHandler<ConnectionState>? StateChanged;

    public event EventHandler<MaEvent>? EventReceived;

    /// <summary>Starts (or restarts) the connection loop against <paramref name="httpBase"/>.</summary>
    public async Task StartAsync(Uri httpBase, string? token)
    {
        await StopAsync().ConfigureAwait(false);

        HttpBase = httpBase;
        Token = string.IsNullOrWhiteSpace(token) ? null : token.Trim();
        var cts = new CancellationTokenSource();
        var wsUri = ServerAddress.ToWebSocketUri(httpBase);
        lock (_gate)
        {
            _runCts = cts;
            _delayCts = new CancellationTokenSource();
            _runTask = Task.Run(() => RunAsync(wsUri, cts.Token));
        }
    }

    public async Task StopAsync()
    {
        CancellationTokenSource? cts;
        Task? run;
        lock (_gate)
        {
            cts = _runCts;
            run = _runTask;
            _runCts = null;
            _runTask = null;
        }

        if (cts is null)
        {
            return;
        }

        cts.Cancel();
        _socket?.Abort();
        if (run is not null)
        {
            try
            {
                await run.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        cts.Dispose();
        SetState(ConnectionState.Stopped);
    }

    /// <summary>
    /// Skips any pending backoff. With <paramref name="force"/>, also drops a connection that looks
    /// alive (useful after resume from sleep, when the TCP connection may be silently dead).
    /// </summary>
    public void ReconnectNow(bool force = false)
    {
        CancellationTokenSource old;
        lock (_gate)
        {
            if (_runCts is null)
            {
                return;
            }

            old = _delayCts;
            _delayCts = new CancellationTokenSource();
        }

        old.Cancel();
        if (force)
        {
            _socket?.Abort();
        }
    }

    public async Task<JsonElement> SendCommandAsync(string command, object? args = null, CancellationToken cancellationToken = default)
    {
        if (State != ConnectionState.Connected)
        {
            throw new MaConnectionException("Not connected to Music Assistant.");
        }

        return await SendRawAsync(command, args, cancellationToken).ConfigureAwait(false);
    }

    public async Task<T?> SendCommandAsync<T>(string command, object? args = null, CancellationToken cancellationToken = default)
    {
        var result = await SendCommandAsync(command, args, cancellationToken).ConfigureAwait(false);
        return result.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
            ? default
            : result.Deserialize<T>(MaJson.Options);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _sendLock.Dispose();
    }

    private async Task RunAsync(Uri wsUri, CancellationToken ct)
    {
        var attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            SetState(ConnectionState.Connecting);
            var nextState = ConnectionState.Disconnected;
            try
            {
                nextState = await ConnectAndServeAsync(wsUri, ct).ConfigureAwait(false);
                if (nextState == ConnectionState.Disconnected)
                {
                    // We were connected at some point; start the backoff from scratch.
                    attempt = 0;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                LastError = Describe(ex);
                Log.Warn($"Connection to {wsUri} failed: {LastError}");
            }
            finally
            {
                var socket = Interlocked.Exchange(ref _socket, null);
                socket?.Dispose();
                FailAllPending(new MaConnectionException("Connection to Music Assistant was lost."));
            }

            if (ct.IsCancellationRequested)
            {
                break;
            }

            SetState(nextState);
            var delay = nextState switch
            {
                ConnectionState.AuthFailed or ConnectionState.SetupRequired => AuthFailedRetry,
                _ => Backoff(attempt++),
            };

            CancellationToken delayToken;
            lock (_gate)
            {
                delayToken = _delayCts.Token;
            }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, delayToken);
            try
            {
                await Task.Delay(delay, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                attempt = 0;
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>Returns the state to report after the connection ends.</summary>
    private async Task<ConnectionState> ConnectAndServeAsync(Uri wsUri, CancellationToken ct)
    {
        var socket = _socketFactory();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        _socket = socket;

        using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            connectCts.CancelAfter(ConnectTimeout);
            await socket.ConnectAsync(wsUri, connectCts.Token).ConfigureAwait(false);
        }

        var serverInfoTcs = new TaskCompletionSource<ServerInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        _serverInfoTcs = serverInfoTcs;
        var setupRequired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var receive = ReceiveLoopAsync(socket, setupRequired, ct);

        var first = await Task.WhenAny(serverInfoTcs.Task, receive, Task.Delay(ConnectTimeout, ct)).ConfigureAwait(false);
        if (first != serverInfoTcs.Task)
        {
            ct.ThrowIfCancellationRequested();
            throw new MaConnectionException("The server did not identify itself as Music Assistant.");
        }

        ServerInfo = serverInfoTcs.Task.Result;
        Log.Info($"Connected to Music Assistant {ServerInfo.ServerVersion} (schema {ServerInfo.SchemaVersion}) at {wsUri}");

        // The server sends a "setup required" error right after server info if onboarding isn't done.
        await Task.WhenAny(setupRequired.Task, receive, Task.Delay(50, ct)).ConfigureAwait(false);
        if (setupRequired.Task.IsCompleted)
        {
            LastError = "Music Assistant setup has not been completed yet.";
            socket.Abort();
            return ConnectionState.SetupRequired;
        }

        if (Token is not null)
        {
            try
            {
                var auth = await SendRawAsync("auth", new Dictionary<string, object?> { ["token"] = Token }, ct).ConfigureAwait(false);
                UserId = ReadUserId(auth);
            }
            catch (MaCommandException ex) when (ex.ErrorCode == MaErrorCodes.InvalidCommand)
            {
                // Pre-auth server (< 2.7): no auth command, nothing to do.
            }
            catch (MaCommandException ex)
            {
                LastError = ex.Details ?? "The token was rejected.";
                Log.Warn($"Authentication failed: {LastError}");
                socket.Abort();
                return ConnectionState.AuthFailed;
            }
        }

        LastError = null;
        SetState(ConnectionState.Connected);
        await receive.ConfigureAwait(false);
        Log.Info("Connection to Music Assistant closed.");
        return ConnectionState.Disconnected;
    }

    private static string? ReadUserId(JsonElement auth)
    {
        if (auth.ValueKind == JsonValueKind.Object &&
            auth.TryGetProperty("user", out var user) &&
            user.ValueKind == JsonValueKind.Object &&
            user.TryGetProperty("user_id", out var id) &&
            id.ValueKind == JsonValueKind.String)
        {
            return id.GetString();
        }

        return null;
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket, TaskCompletionSource setupRequired, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        var message = new ArrayBufferWriter<byte>(64 * 1024);
        try
        {
            while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                message.Clear();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(buffer, ct).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        return;
                    }

                    message.Write(buffer.AsSpan(0, result.Count));
                }
                while (!result.EndOfMessage);

                if (result.MessageType != WebSocketMessageType.Text)
                {
                    continue;
                }

                try
                {
                    HandleMessage(message.WrittenMemory, setupRequired);
                }
                catch (JsonException ex)
                {
                    Log.Warn("Ignoring malformed message from server.", ex);
                }
            }
        }
        catch (WebSocketException) when (!ct.IsCancellationRequested)
        {
            // Connection dropped; the run loop reconnects.
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private void HandleMessage(ReadOnlyMemory<byte> utf8, TaskCompletionSource setupRequired)
    {
        using var doc = JsonDocument.Parse(utf8);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (root.TryGetProperty("event", out var eventName))
        {
            var objectId = root.TryGetProperty("object_id", out var oid) && oid.ValueKind == JsonValueKind.String ? oid.GetString() : null;
            var data = root.TryGetProperty("data", out var d) ? d.Clone() : default;
            try
            {
                EventReceived?.Invoke(this, new MaEvent(eventName.GetString() ?? "", objectId, data));
            }
            catch (Exception ex)
            {
                Log.Error("Event handler failed.", ex);
            }

            return;
        }

        if (root.TryGetProperty("message_id", out var idElement))
        {
            var id = idElement.ValueKind == JsonValueKind.String ? idElement.GetString() : idElement.GetRawText();
            if (id == "connection" && root.TryGetProperty("error_code", out _))
            {
                setupRequired.TrySetResult();
                return;
            }

            if (id is null || !_pending.TryGetValue(id, out var pending))
            {
                return;
            }

            if (root.TryGetProperty("error_code", out var code))
            {
                var details = root.TryGetProperty("details", out var det) && det.ValueKind == JsonValueKind.String ? det.GetString() : null;
                if (_pending.TryRemove(id, out _))
                {
                    pending.Completion.TrySetException(new MaCommandException(pending.Command, code.GetInt32(), details));
                }

                return;
            }

            var result = root.TryGetProperty("result", out var r) ? r.Clone() : default;
            var partial = root.TryGetProperty("partial", out var p) && p.ValueKind == JsonValueKind.True;
            if (partial)
            {
                pending.AppendPartial(result);
                return;
            }

            if (_pending.TryRemove(id, out _))
            {
                pending.Completion.TrySetResult(pending.Finish(result));
            }

            return;
        }

        if (root.TryGetProperty("server_version", out _) || root.TryGetProperty("server_id", out _))
        {
            var info = root.Deserialize<ServerInfo>(MaJson.Options);
            if (info is not null)
            {
                _serverInfoTcs?.TrySetResult(info);
            }
        }
    }

    private async Task<JsonElement> SendRawAsync(string command, object? args, CancellationToken cancellationToken)
    {
        var socket = _socket;
        if (socket is null || socket.State != WebSocketState.Open)
        {
            throw new MaConnectionException("Not connected to Music Assistant.");
        }

        var id = Interlocked.Increment(ref _nextId).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var pending = new PendingRequest(command);
        _pending[id] = pending;

        var payload = JsonSerializer.SerializeToUtf8Bytes(new CommandEnvelope(id, command, args), MaJson.Options);
        try
        {
            await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await socket.SendAsync(payload, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _sendLock.Release();
            }
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or InvalidOperationException)
        {
            _pending.TryRemove(id, out _);
            throw new MaConnectionException("Failed to send command to Music Assistant.", ex);
        }

        try
        {
            return await pending.Completion.Task.WaitAsync(RequestTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            _pending.TryRemove(id, out _);
            throw new MaConnectionException($"Music Assistant did not answer '{command}' in time.", ex);
        }
        catch (OperationCanceledException)
        {
            _pending.TryRemove(id, out _);
            throw;
        }
    }

    private void FailAllPending(Exception ex)
    {
        foreach (var key in _pending.Keys)
        {
            if (_pending.TryRemove(key, out var pending))
            {
                pending.Completion.TrySetException(ex);
            }
        }
    }

    private TimeSpan Backoff(int attempt)
    {
        var baseMs = MinBackoff.TotalMilliseconds * Math.Pow(2, Math.Min(attempt, 10));
        var capped = Math.Min(baseMs, MaxBackoff.TotalMilliseconds);
        double jitter;
        lock (_random)
        {
            jitter = 0.8 + (_random.NextDouble() * 0.4);
        }

        return TimeSpan.FromMilliseconds(capped * jitter);
    }

    private void SetState(ConnectionState state)
    {
        lock (_gate)
        {
            if (_state == state)
            {
                return;
            }

            _state = state;
        }

        try
        {
            StateChanged?.Invoke(this, state);
        }
        catch (Exception ex)
        {
            Log.Error("State handler failed.", ex);
        }
    }

    private static string Describe(Exception ex) => ex switch
    {
        WebSocketException { InnerException: { } inner } => inner.Message,
        OperationCanceledException => "Timed out connecting.",
        _ => ex.Message,
    };

    private sealed record CommandEnvelope(string MessageId, string Command, object? Args);

    private sealed class PendingRequest
    {
        private List<JsonElement>? _partials;

        public PendingRequest(string command) => Command = command;

        public string Command { get; }

        public TaskCompletionSource<JsonElement> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void AppendPartial(JsonElement chunk)
        {
            _partials ??= new List<JsonElement>();
            _partials.Add(chunk);
        }

        /// <summary>Concatenates any partial (array) chunks received before the final result.</summary>
        public JsonElement Finish(JsonElement final)
        {
            if (_partials is null)
            {
                return final;
            }

            var buffer = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartArray();
                foreach (var chunk in _partials.Append(final))
                {
                    if (chunk.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in chunk.EnumerateArray())
                        {
                            item.WriteTo(writer);
                        }
                    }
                }

                writer.WriteEndArray();
            }

            using var doc = JsonDocument.Parse(buffer.WrittenMemory);
            return doc.RootElement.Clone();
        }
    }
}
