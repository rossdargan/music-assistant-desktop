using MaMini.Core.Models;

namespace MaMini.Core.State;

/// <summary>Immutable snapshot of what the selected speaker is doing.</summary>
public sealed record NowPlaying
{
    public static readonly NowPlaying Empty = new();

    public string? PlayerId { get; init; }
    public string? PlayerName { get; init; }
    public bool PlayerAvailable { get; init; }

    /// <summary>Number of players in the group this speaker leads (0 or 1 when not grouped).</summary>
    public int GroupSize { get; init; }

    public string? Title { get; init; }
    public string? Artist { get; init; }
    public string? Album { get; init; }
    public string? ImageUrl { get; init; }
    public string? Uri { get; init; }
    public string? MediaType { get; init; }
    public string? QueueId { get; init; }

    public PlaybackState State { get; init; }
    public double? Duration { get; init; }

    /// <summary>Elapsed seconds as last reported by the server.</summary>
    public double? Elapsed { get; init; }

    /// <summary>When <see cref="Elapsed"/> was measured (UTC).</summary>
    public DateTimeOffset? ElapsedAt { get; init; }

    /// <summary>0..100, or null when the speaker has no volume control.</summary>
    public int? Volume { get; init; }

    public bool Muted { get; init; }

    /// <summary>Null while unknown (not looked up yet).</summary>
    public bool? IsFavorite { get; init; }

    public bool IsPlaying => State == PlaybackState.Playing;

    public bool HasMedia => !string.IsNullOrEmpty(Title) || !string.IsNullOrEmpty(Uri);

    public bool CanFavorite => !string.IsNullOrEmpty(Uri) &&
        !Uri!.StartsWith("http", StringComparison.OrdinalIgnoreCase) &&
        MediaType is not ("unknown" or "flow_stream" or "plugin_source" or "announcement" or "folder");

    /// <summary>Extrapolates the playback position (seconds) at <paramref name="now"/>.</summary>
    public double? PositionAt(DateTimeOffset now)
    {
        if (Elapsed is null)
        {
            return null;
        }

        var position = Elapsed.Value;
        if (IsPlaying && ElapsedAt is { } at)
        {
            position += Math.Max(0, (now - at).TotalSeconds);
        }

        if (Duration is > 0)
        {
            position = Math.Min(position, Duration.Value);
        }

        return Math.Max(0, position);
    }

    /// <summary>0..1 progress at <paramref name="now"/>, or null when unknown.</summary>
    public double? ProgressAt(DateTimeOffset now)
    {
        if (Duration is not > 0 || PositionAt(now) is not { } position)
        {
            return null;
        }

        return Math.Clamp(position / Duration.Value, 0, 1);
    }
}
