using System.Text.Json;
using MaMini.Core.Api;
using MaMini.Core.State;
using MaMini.Tests.Fakes;
using static MaMini.Tests.MaClientTests;

namespace MaMini.Tests;

public sealed class MaSessionTests
{
    private static object PlayerJson(string id, string state, string title, string uri, int volume = 30) => new
    {
        player_id = id,
        name = id.ToUpperInvariant(),
        available = true,
        enabled = true,
        playback_state = state,
        volume_level = volume,
        volume_muted = false,
        group_members = Array.Empty<string>(),
        current_media = new { uri, media_type = "track", title, artist = "Artist", duration = 180 },
    };

    private static async Task<(FakeMaServer Server, MaSession Session)> StartAsync(Action<FakeMaServer>? configure = null)
    {
        var server = new FakeMaServer().Start();
        server.Handle("players/all", _ => new[] { PlayerJson("kitchen", "playing", "Song A", "library://track/1") });
        server.Handle("music/item_by_uri", args => new
        {
            item_id = "1",
            provider = "library",
            uri = args.GetProperty("uri").GetString(),
            media_type = "track",
            name = "Song A",
            favorite = false,
        });
        server.Handle("players/cmd/play_pause", _ => null);
        server.Handle("players/cmd/play", _ => null);
        server.Handle("players/cmd/pause", _ => null);
        server.Handle("players/cmd/next", _ => null);
        server.Handle("players/cmd/volume_set", _ => null);
        server.Handle("music/favorites/add_item", _ => null);
        server.Handle("music/favorites/remove_item", _ => null);
        configure?.Invoke(server);

        var session = new MaSession(NewClient(), new NowPlayingStore(), context: null);
        await session.StartAsync(server.HttpBase, null);
        await WaitUntilAsync(() => session.State == ConnectionState.Connected);
        return (server, session);
    }

    [Fact]
    public async Task Loads_players_after_connecting()
    {
        var (server, session) = await StartAsync();
        await using (server)
        await using (session)
        {
            Assert.Equal("kitchen", session.Store.SelectedPlayerId);
            Assert.Equal("Song A", session.Store.Current.Title);
            await WaitUntilAsync(() => session.Store.Current.IsFavorite == false);
        }
    }

    [Fact]
    public async Task Player_events_update_now_playing()
    {
        var (server, session) = await StartAsync();
        await using (server)
        await using (session)
        {
            await server.SendEventAsync("player_updated", "kitchen", PlayerJson("kitchen", "playing", "Song B", "library://track/2"));

            await WaitUntilAsync(() => session.Store.Current.Title == "Song B");
        }
    }

    [Fact]
    public async Task Play_pause_is_optimistic_and_sends_the_command()
    {
        var (server, session) = await StartAsync();
        await using (server)
        await using (session)
        {
            var task = session.PlayPauseAsync();
            Assert.False(session.Store.Current.IsPlaying);
            await task;

            var args = Assert.Single(server.ArgsFor("players/cmd/play_pause"));
            Assert.Equal("kitchen", args.GetProperty("player_id").GetString());
        }
    }

    [Fact]
    public async Task Explicit_pause_and_play_send_their_commands()
    {
        var (server, session) = await StartAsync();
        await using (server)
        await using (session)
        {
            await session.PauseAsync();
            Assert.False(session.Store.Current.IsPlaying);
            await session.PlayAsync();
            Assert.True(session.Store.Current.IsPlaying);

            Assert.Equal("kitchen", Assert.Single(server.ArgsFor("players/cmd/pause")).GetProperty("player_id").GetString());
            Assert.Equal("kitchen", Assert.Single(server.ArgsFor("players/cmd/play")).GetProperty("player_id").GetString());
        }
    }

    [Fact]
    public async Task Failed_command_rolls_back_and_reports()
    {
        var (server, session) = await StartAsync(s => s.Handle("players/cmd/play_pause", _ => throw new InvalidOperationException("nope")));
        await using (server)
        await using (session)
        {
            string? failure = null;
            session.CommandFailed += (_, msg) => failure = msg;

            await session.PlayPauseAsync();

            Assert.True(session.Store.Current.IsPlaying);
            Assert.NotNull(failure);
        }
    }

    [Fact]
    public async Task Toggling_favorite_adds_then_removes_by_library_id()
    {
        var (server, session) = await StartAsync();
        await using (server)
        await using (session)
        {
            await WaitUntilAsync(() => session.Store.Current.IsFavorite == false);

            await session.ToggleFavoriteAsync();
            Assert.True(session.Store.Current.IsFavorite);
            Assert.Equal("library://track/1", Assert.Single(server.ArgsFor("music/favorites/add_item")).GetProperty("item").GetString());

            await session.ToggleFavoriteAsync();
            Assert.False(session.Store.Current.IsFavorite);
            var remove = Assert.Single(server.ArgsFor("music/favorites/remove_item"));
            Assert.Equal("track", remove.GetProperty("media_type").GetString());
            Assert.Equal("1", remove.GetProperty("library_item_id").GetString());
        }
    }

    [Fact]
    public async Task Favorite_events_from_other_clients_are_applied()
    {
        var (server, session) = await StartAsync();
        await using (server)
        await using (session)
        {
            await WaitUntilAsync(() => session.Store.Current.IsFavorite == false);

            await server.SendEventAsync("favorite_updated", null, new { uri = "library://track/1", media_type = "track", item_id = "1", favorite = true });

            await WaitUntilAsync(() => session.Store.Current.IsFavorite == true);
        }
    }

    [Fact]
    public async Task Volume_changes_are_coalesced()
    {
        var (server, session) = await StartAsync();
        await using (server)
        await using (session)
        {
            for (var v = 31; v <= 60; v++)
            {
                session.SetVolume(v);
            }

            Assert.Equal(60, session.Store.Current.Volume);
            await WaitUntilAsync(() => server.ArgsFor("players/cmd/volume_set").Any(a => a.GetProperty("volume_level").GetInt32() == 60));
            Assert.True(server.ArgsFor("players/cmd/volume_set").Count() <= 3);
        }
    }

    [Fact]
    public async Task Missing_token_is_reported_as_auth_failure()
    {
        await using var server = new FakeMaServer { RequiredToken = "secret" }.Start();

        // The server requires auth but the user left the token empty, so players/all is rejected.
        await using var session = new MaSession(NewClient(), new NowPlayingStore(), context: null);
        await session.StartAsync(server.HttpBase, null);

        await WaitUntilAsync(() => session.State == ConnectionState.AuthFailed);
        Assert.Contains("token", session.LastError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Up_next_comes_from_the_active_queue()
    {
        var (server, session) = await StartAsync(s => s.Handle("player_queues/get_active_queue", _ => new
        {
            queue_id = "kitchen",
            next_item = new { queue_item_id = "q2", name = "Next Song" },
        }));
        await using (server)
        await using (session)
        {
            Assert.Equal("Next Song", await session.GetUpNextAsync());
            Assert.Equal(JsonValueKind.Object, server.ArgsFor("player_queues/get_active_queue").Single().ValueKind);
        }
    }
}
