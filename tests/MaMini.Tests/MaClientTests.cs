using System.Text.Json;
using MaMini.Core.Api;
using MaMini.Tests.Fakes;

namespace MaMini.Tests;

public sealed class MaClientTests
{
    internal static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Condition was not met in time.");
            }

            await Task.Delay(20);
        }
    }

    internal static MaClient NewClient() => new()
    {
        MinBackoff = TimeSpan.FromMilliseconds(50),
        MaxBackoff = TimeSpan.FromMilliseconds(200),
        AuthFailedRetry = TimeSpan.FromMinutes(5),
        RequestTimeout = TimeSpan.FromSeconds(5),
    };

    [Fact]
    public async Task Connects_and_authenticates_with_token()
    {
        await using var server = new FakeMaServer { RequiredToken = "secret" }.Start();
        await using var client = NewClient();

        await client.StartAsync(server.HttpBase, "secret");
        await WaitUntilAsync(() => client.State == ConnectionState.Connected);

        Assert.Equal("user-1", client.UserId);
        Assert.Equal("2.7.0", client.ServerInfo?.ServerVersion);
        Assert.Contains(server.Received, r => r.Command == "auth");
    }

    [Fact]
    public async Task Invalid_token_reports_auth_failed()
    {
        await using var server = new FakeMaServer { RequiredToken = "secret" }.Start();
        await using var client = NewClient();

        await client.StartAsync(server.HttpBase, "wrong");
        await WaitUntilAsync(() => client.State == ConnectionState.AuthFailed);

        Assert.Equal("Invalid token", client.LastError);
    }

    [Fact]
    public async Task Setup_required_is_reported()
    {
        await using var server = new FakeMaServer { OnboardDone = false }.Start();
        await using var client = NewClient();

        await client.StartAsync(server.HttpBase, null);
        await WaitUntilAsync(() => client.State == ConnectionState.SetupRequired);
    }

    [Fact]
    public async Task Old_server_without_auth_command_still_connects()
    {
        await using var server = new FakeMaServer { SupportsAuth = false }.Start();
        await using var client = NewClient();

        await client.StartAsync(server.HttpBase, "whatever");
        await WaitUntilAsync(() => client.State == ConnectionState.Connected);
    }

    [Fact]
    public async Task Responses_are_correlated_by_message_id()
    {
        await using var server = new FakeMaServer().Start();
        server.HandleAsync("slow", async _ =>
        {
            await Task.Delay(300);
            return "slow-result";
        });
        server.Handle("fast", _ => "fast-result");
        await using var client = NewClient();
        await client.StartAsync(server.HttpBase, null);
        await WaitUntilAsync(() => client.State == ConnectionState.Connected);

        var slow = client.SendCommandAsync<string>("slow");
        var fast = client.SendCommandAsync<string>("fast");

        Assert.Equal("fast-result", await fast);
        Assert.False(slow.IsCompleted);
        Assert.Equal("slow-result", await slow);
    }

    [Fact]
    public async Task Partial_results_are_concatenated()
    {
        await using var server = new FakeMaServer().Start();
        server.Handle("list", _ => new PartialResult(Enumerable.Range(1, 1234).Cast<object?>().ToList(), 500));
        await using var client = NewClient();
        await client.StartAsync(server.HttpBase, null);
        await WaitUntilAsync(() => client.State == ConnectionState.Connected);

        var result = await client.SendCommandAsync<List<int>>("list");

        Assert.Equal(Enumerable.Range(1, 1234), result);
    }

    [Fact]
    public async Task Command_errors_throw_with_code_and_details()
    {
        await using var server = new FakeMaServer().Start();
        await using var client = NewClient();
        await client.StartAsync(server.HttpBase, null);
        await WaitUntilAsync(() => client.State == ConnectionState.Connected);

        var ex = await Assert.ThrowsAsync<MaCommandException>(() => client.SendCommandAsync("does/not/exist"));

        Assert.Equal(12, ex.ErrorCode);
        Assert.Contains("does/not/exist", ex.Details);
    }

    [Fact]
    public async Task Command_arguments_are_sent_verbatim()
    {
        await using var server = new FakeMaServer().Start();
        server.Handle("players/cmd/volume_set", _ => null);
        await using var client = NewClient();
        await client.StartAsync(server.HttpBase, null);
        await WaitUntilAsync(() => client.State == ConnectionState.Connected);

        await client.SendCommandAsync("players/cmd/volume_set", new Dictionary<string, object?> { ["player_id"] = "p1", ["volume_level"] = 42 });

        var args = Assert.Single(server.ArgsFor("players/cmd/volume_set"));
        Assert.Equal("p1", args.GetProperty("player_id").GetString());
        Assert.Equal(42, args.GetProperty("volume_level").GetInt32());
    }

    [Fact]
    public async Task Events_are_raised()
    {
        await using var server = new FakeMaServer().Start();
        await using var client = NewClient();
        var received = new List<MaEvent>();
        client.EventReceived += (_, e) =>
        {
            lock (received)
            {
                received.Add(e);
            }
        };
        await client.StartAsync(server.HttpBase, null);
        await WaitUntilAsync(() => client.State == ConnectionState.Connected);

        await server.SendEventAsync("player_updated", "p1", new { player_id = "p1", name = "Kitchen" });
        await WaitUntilAsync(() => { lock (received) { return received.Count == 1; } });

        Assert.Equal("player_updated", received[0].Name);
        Assert.Equal("p1", received[0].ObjectId);
        Assert.Equal("Kitchen", received[0].Data.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Reconnects_after_the_connection_drops()
    {
        await using var server = new FakeMaServer().Start();
        await using var client = NewClient();
        var states = new List<ConnectionState>();
        client.StateChanged += (_, s) =>
        {
            lock (states)
            {
                states.Add(s);
            }
        };
        await client.StartAsync(server.HttpBase, null);
        await WaitUntilAsync(() => client.State == ConnectionState.Connected);

        server.DropConnections();

        await WaitUntilAsync(() => server.ConnectionCount >= 2 && client.State == ConnectionState.Connected);
        lock (states)
        {
            Assert.Contains(ConnectionState.Disconnected, states);
        }
    }

    [Fact]
    public async Task Pending_commands_fail_when_connection_drops()
    {
        await using var server = new FakeMaServer().Start();
        server.HandleAsync("hang", async _ =>
        {
            await Task.Delay(Timeout.Infinite);
            return null;
        });
        await using var client = NewClient();
        await client.StartAsync(server.HttpBase, null);
        await WaitUntilAsync(() => client.State == ConnectionState.Connected);

        var pending = client.SendCommandAsync("hang");
        await Task.Delay(100);
        server.DropConnections();

        await Assert.ThrowsAsync<MaConnectionException>(() => pending);
    }

    [Fact]
    public async Task Commands_fail_fast_when_not_connected()
    {
        await using var client = NewClient();
        await Assert.ThrowsAsync<MaConnectionException>(() => client.SendCommandAsync("players/all"));
    }

    [Fact]
    public async Task Stop_reports_stopped()
    {
        await using var server = new FakeMaServer().Start();
        await using var client = NewClient();
        await client.StartAsync(server.HttpBase, null);
        await WaitUntilAsync(() => client.State == ConnectionState.Connected);

        await client.StopAsync();

        Assert.Equal(ConnectionState.Stopped, client.State);
    }

    [Theory]
    [InlineData("192.168.1.10", "http://192.168.1.10:8095/")]
    [InlineData("mass.local:1234", "http://mass.local:1234/")]
    [InlineData("https://ma.example.com", "https://ma.example.com/")]
    [InlineData("ws://10.0.0.2:8095/ws", "http://10.0.0.2:8095/")]
    [InlineData("wss://ma.example.com/ws", "https://ma.example.com/")]
    [InlineData(" http://host:8095/ ", "http://host:8095/")]
    public void Server_addresses_are_normalized(string input, string expected)
    {
        Assert.True(ServerAddress.TryNormalize(input, out var uri));
        Assert.Equal(expected, uri.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ftp://host")]
    public void Invalid_server_addresses_are_rejected(string input) =>
        Assert.False(ServerAddress.TryNormalize(input, out _));

    [Fact]
    public void WebSocket_uri_adds_ws_path()
    {
        Assert.Equal("ws://host:8095/ws", ServerAddress.ToWebSocketUri(new Uri("http://host:8095/")).ToString());
        Assert.Equal("wss://host/ws", ServerAddress.ToWebSocketUri(new Uri("https://host/")).ToString());
    }

    [Fact]
    public void Json_element_default_is_undefined()
    {
        // Guards an assumption in MaClient: missing "result" yields an Undefined element.
        Assert.Equal(JsonValueKind.Undefined, default(JsonElement).ValueKind);
    }
}
