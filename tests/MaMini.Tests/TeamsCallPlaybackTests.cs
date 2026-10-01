using MaMini.Core.Models;
using MaMini.Core.State;

namespace MaMini.Tests;

public sealed class TeamsCallPlaybackTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static readonly NowPlaying Playing = new()
    {
        PlayerId = "kitchen",
        Uri = "library://track/1",
        State = PlaybackState.Playing,
    };

    [Theory]
    [InlineData(PlaybackState.Paused)]
    [InlineData(PlaybackState.Idle)]
    public void Resumes_the_speaker_it_paused(PlaybackState stateAfterPause)
    {
        var playback = new TeamsCallPlayback();

        Assert.Equal(TeamsPlaybackAction.Pause, playback.OnCallChanged(true, Playing, true, T0));
        var paused = Playing with { State = stateAfterPause, Uri = "library://track/other" };
        playback.OnNowPlayingChanged(paused, T0.AddSeconds(1));
        Assert.Equal(TeamsPlaybackAction.Resume, playback.OnCallChanged(false, paused, true, T0.AddMinutes(10)));
        Assert.Equal(TeamsPlaybackAction.None, playback.OnCallChanged(false, paused, true, T0.AddMinutes(11)));
    }

    [Fact]
    public void Ignores_a_stale_playing_update_right_after_pausing()
    {
        var playback = new TeamsCallPlayback();
        playback.OnCallChanged(true, Playing, true, T0);
        playback.OnNowPlayingChanged(Playing, T0.AddSeconds(2));

        Assert.True(playback.HasPendingResume);
    }

    [Fact]
    public void Never_resumes_music_already_paused_before_a_call()
    {
        var playback = new TeamsCallPlayback();
        var paused = Playing with { State = PlaybackState.Paused };

        Assert.Equal(TeamsPlaybackAction.None, playback.OnCallChanged(true, paused, true, T0));
        Assert.Equal(TeamsPlaybackAction.None, playback.OnCallChanged(false, paused, true, T0.AddMinutes(1)));
    }

    [Fact]
    public void Does_not_resume_when_option_is_disabled()
    {
        var playback = new TeamsCallPlayback();
        playback.OnCallChanged(true, Playing, false, T0);

        Assert.Equal(TeamsPlaybackAction.None, playback.OnCallChanged(false, Playing with { State = PlaybackState.Paused }, false, T0.AddMinutes(1)));
    }

    [Fact]
    public void Forgets_the_resume_when_the_speaker_changes()
    {
        var playback = new TeamsCallPlayback();
        playback.OnCallChanged(true, Playing, true, T0);
        playback.OnNowPlayingChanged(Playing with { PlayerId = "bedroom", State = PlaybackState.Paused }, T0.AddSeconds(1));

        Assert.False(playback.HasPendingResume);
        Assert.Equal(TeamsPlaybackAction.None, playback.OnCallChanged(false, Playing with { State = PlaybackState.Paused }, true, T0.AddMinutes(1)));
    }

    [Fact]
    public void Forgets_the_resume_when_the_user_presses_play_during_the_call()
    {
        var playback = new TeamsCallPlayback();
        playback.OnCallChanged(true, Playing, true, T0);
        playback.OnNowPlayingChanged(Playing, T0.AddSeconds(30));

        Assert.False(playback.HasPendingResume);
        Assert.Equal("playback was resumed manually", playback.LastClearReason);
    }
}
