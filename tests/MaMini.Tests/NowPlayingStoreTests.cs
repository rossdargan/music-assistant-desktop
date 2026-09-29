using MaMini.Core.Models;
using MaMini.Core.State;

namespace MaMini.Tests;

public sealed class NowPlayingStoreTests
{
    internal static Player MakePlayer(
        string id,
        string state = "idle",
        string? title = null,
        string? uri = null,
        bool available = true,
        string? syncedTo = null,
        string? activeGroup = null,
        double? volume = 50,
        List<string>? members = null,
        double? groupVolume = null,
        bool hidden = false) => new()
        {
            PlayerId = id,
            Name = id.ToUpperInvariant(),
            Available = available,
            PlaybackStateRaw = state,
            VolumeLevel = volume,
            VolumeMuted = false,
            SyncedTo = syncedTo,
            ActiveGroup = activeGroup,
            GroupMembers = members,
            GroupVolume = groupVolume,
            HideInUi = hidden,
            CurrentMedia = title is null && uri is null ? null : new PlayerMedia
            {
                Title = title,
                Artist = "Artist",
                Uri = uri,
                MediaType = "track",
                Duration = 200,
                ElapsedTime = 10,
                ElapsedTimeLastUpdated = 1_700_000_000,
            },
        };

    [Fact]
    public void Selects_the_playing_player_when_nothing_was_selected()
    {
        var store = new NowPlayingStore();
        store.SetPlayers([MakePlayer("a"), MakePlayer("b", "playing", "Song")]);

        Assert.Equal("b", store.SelectedPlayerId);
        Assert.Equal("Song", store.Current.Title);
        Assert.True(store.Current.IsPlaying);
    }

    [Fact]
    public void Keeps_a_restored_selection()
    {
        var store = new NowPlayingStore();
        store.RestoreSelection("a");
        store.SetPlayers([MakePlayer("a"), MakePlayer("b", "playing", "Song")]);

        Assert.Equal("a", store.SelectedPlayerId);
    }

    [Fact]
    public void Replaces_a_selection_that_no_longer_exists()
    {
        var store = new NowPlayingStore();
        store.RestoreSelection("gone");
        store.SetPlayers([MakePlayer("a")]);

        Assert.Equal("a", store.SelectedPlayerId);
    }

    [Fact]
    public void Hides_unavailable_hidden_and_synced_players_from_the_picker()
    {
        var store = new NowPlayingStore();
        store.SetPlayers([
            MakePlayer("a"),
            MakePlayer("b", available: false),
            MakePlayer("c", syncedTo: "a"),
            MakePlayer("d", hidden: true),
        ]);

        Assert.Equal(["a"], store.SelectablePlayers.Select(p => p.PlayerId));
    }

    [Fact]
    public void Player_updates_raise_now_playing_changes()
    {
        var store = new NowPlayingStore();
        store.SetPlayers([MakePlayer("a", "playing", "One", "lib://1")]);
        NowPlaying? seen = null;
        store.NowPlayingChanged += (_, np) => seen = np;

        store.UpsertPlayer(MakePlayer("a", "playing", "Two", "lib://2"));

        Assert.Equal("Two", seen?.Title);
    }

    [Fact]
    public void Unrelated_updates_do_not_raise_changes()
    {
        var store = new NowPlayingStore();
        store.SetPlayers([MakePlayer("a", "playing", "One"), MakePlayer("b")]);
        var raised = 0;
        store.NowPlayingChanged += (_, _) => raised++;

        store.UpsertPlayer(MakePlayer("b", volume: 10));

        Assert.Equal(0, raised);
    }

    [Fact]
    public void Follow_active_player_switches_to_a_speaker_that_starts_playing()
    {
        var store = new NowPlayingStore { FollowActivePlayer = true };
        store.SetPlayers([MakePlayer("a"), MakePlayer("b")]);
        store.Select("a");

        store.UpsertPlayer(MakePlayer("b", "playing", "Song"));

        Assert.Equal("b", store.SelectedPlayerId);
    }

    [Fact]
    public void Without_follow_the_selection_is_sticky()
    {
        var store = new NowPlayingStore();
        store.SetPlayers([MakePlayer("a"), MakePlayer("b")]);
        store.Select("a");

        store.UpsertPlayer(MakePlayer("b", "playing", "Song"));

        Assert.Equal("a", store.SelectedPlayerId);
    }

    [Fact]
    public void Group_member_without_media_shows_the_leaders_media()
    {
        var store = new NowPlayingStore();
        store.SetPlayers([
            MakePlayer("leader", "playing", "Group song", members: ["leader", "member"]),
            MakePlayer("member", activeGroup: "leader"),
        ]);
        store.Select("member");

        Assert.Equal("Group song", store.Current.Title);
        Assert.True(store.Current.IsPlaying);
    }

    [Fact]
    public void Grouped_players_use_group_volume()
    {
        var store = new NowPlayingStore();
        store.SetPlayers([MakePlayer("leader", volume: 10, members: ["leader", "x"], groupVolume: 70)]);

        Assert.Equal(70, store.Current.Volume);
        Assert.Equal(2, store.Current.GroupSize);
    }

    [Fact]
    public void Optimistic_play_state_is_shown_until_the_server_confirms()
    {
        var store = new NowPlayingStore();
        store.SetPlayers([MakePlayer("a", "paused", "Song")]);

        store.SetOptimisticPlaying(true);
        Assert.True(store.Current.IsPlaying);

        // A volume-only update with the old state doesn't undo the guess...
        store.UpsertPlayer(MakePlayer("a", "paused", "Song", volume: 20));
        Assert.True(store.Current.IsPlaying);

        // ...but a real state change replaces it.
        store.UpsertPlayer(MakePlayer("a", "playing", "Song"));
        store.UpsertPlayer(MakePlayer("a", "paused", "Song"));
        Assert.False(store.Current.IsPlaying);
    }

    [Fact]
    public void Favorite_state_follows_the_current_uri()
    {
        var store = new NowPlayingStore();
        store.SetPlayers([MakePlayer("a", "playing", "Song", "library://track/1")]);
        Assert.Null(store.Current.IsFavorite);

        store.SetFavorite("library://track/1", true);
        Assert.True(store.Current.IsFavorite);

        store.UpsertPlayer(MakePlayer("a", "playing", "Other", "library://track/2"));
        Assert.Null(store.Current.IsFavorite);
    }

    [Fact]
    public void Position_is_extrapolated_while_playing()
    {
        var at = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var np = new NowPlaying { State = PlaybackState.Playing, Elapsed = 10, ElapsedAt = at, Duration = 100 };

        Assert.Equal(15, np.PositionAt(at.AddSeconds(5)));
        Assert.Equal(100, np.PositionAt(at.AddSeconds(500)));
        Assert.Equal(0.15, np.ProgressAt(at.AddSeconds(5))!.Value, 3);

        var paused = np with { State = PlaybackState.Paused };
        Assert.Equal(10, paused.PositionAt(at.AddSeconds(5)));
    }

    [Theory]
    [InlineData("library://track/1", "track", true)]
    [InlineData("spotify://track/abc", "track", true)]
    [InlineData("http://stream.example/radio.mp3", "unknown", false)]
    [InlineData(null, "track", false)]
    [InlineData("plugin://x", "plugin_source", false)]
    public void Can_favorite_only_real_media_items(string? uri, string mediaType, bool expected) =>
        Assert.Equal(expected, new NowPlaying { Uri = uri, MediaType = mediaType }.CanFavorite);
}
