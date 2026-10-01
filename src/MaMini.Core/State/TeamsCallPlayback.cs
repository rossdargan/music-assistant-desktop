namespace MaMini.Core.State;

public enum TeamsPlaybackAction
{
    None,
    Pause,
    Resume,
}

/// <summary>Remembers only playback paused by Teams detection, never a pre-existing pause.</summary>
public sealed class TeamsCallPlayback
{
    // Server updates can briefly report the old "playing" state right after we pause.
    private static readonly TimeSpan PauseGrace = TimeSpan.FromSeconds(5);

    private string? _pausedPlayerId;
    private DateTimeOffset _pausedAt;

    public bool HasPendingResume => _pausedPlayerId is not null;

    /// <summary>Why the last pending resume was dropped (for diagnostics).</summary>
    public string? LastClearReason { get; private set; }

    public void Clear(string reason)
    {
        if (_pausedPlayerId is not null)
        {
            LastClearReason = reason;
        }

        _pausedPlayerId = null;
    }

    public void OnNowPlayingChanged(NowPlaying nowPlaying, DateTimeOffset now)
    {
        if (_pausedPlayerId is null)
        {
            return;
        }

        if (nowPlaying.PlayerId != _pausedPlayerId)
        {
            Clear("the selected speaker changed");
        }
        else if (nowPlaying.IsPlaying && now - _pausedAt > PauseGrace)
        {
            Clear("playback was resumed manually");
        }
    }

    public TeamsPlaybackAction OnCallChanged(bool active, NowPlaying nowPlaying, bool resumeEnabled, DateTimeOffset now)
    {
        if (active)
        {
            LastClearReason = null;
            if (!nowPlaying.IsPlaying || nowPlaying.PlayerId is null)
            {
                return TeamsPlaybackAction.None;
            }

            _pausedPlayerId = nowPlaying.PlayerId;
            _pausedAt = now;
            return TeamsPlaybackAction.Pause;
        }

        if (_pausedPlayerId is null)
        {
            return TeamsPlaybackAction.None;
        }

        if (!resumeEnabled)
        {
            Clear("auto-resume is turned off");
            return TeamsPlaybackAction.None;
        }

        if (nowPlaying.PlayerId != _pausedPlayerId)
        {
            Clear("the selected speaker changed");
            return TeamsPlaybackAction.None;
        }

        _pausedPlayerId = null;
        return nowPlaying.IsPlaying ? TeamsPlaybackAction.None : TeamsPlaybackAction.Resume;
    }
}
