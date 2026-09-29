using System.Text.Json;
using System.Text.Json.Serialization;

namespace MaMini.Core.Models;

public static class MaJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

public enum PlaybackState
{
    Idle,
    Paused,
    Playing,
}

/// <summary>Subset of Music Assistant's PlayerMedia model that the widget needs.</summary>
public sealed class PlayerMedia
{
    public string? Uri { get; init; }
    public string? MediaType { get; init; }
    public string? Title { get; init; }
    public string? Artist { get; init; }
    public string? Album { get; init; }
    public string? ImageUrl { get; init; }
    public double? Duration { get; init; }
    public string? SourceId { get; init; }
    public string? QueueItemId { get; init; }
    public double? ElapsedTime { get; init; }
    public double? ElapsedTimeLastUpdated { get; init; }
}

/// <summary>Subset of Music Assistant's Player (state) model.</summary>
public sealed class Player
{
    public string PlayerId { get; init; } = "";
    public string? Provider { get; init; }
    public string? Type { get; init; }
    public string? Name { get; init; }

    /// <summary>Older servers (schema &lt; 26) used display_name.</summary>
    public string? DisplayName { get; init; }

    public bool Available { get; init; } = true;
    public bool Enabled { get; init; } = true;
    public bool HideInUi { get; init; }

    [JsonPropertyName("playback_state")]
    public string? PlaybackStateRaw { get; init; }

    /// <summary>Older servers used "state" instead of "playback_state".</summary>
    [JsonPropertyName("state")]
    public string? LegacyState { get; init; }

    public double? ElapsedTime { get; init; }
    public double? ElapsedTimeLastUpdated { get; init; }
    public bool? Powered { get; init; }
    public double? VolumeLevel { get; init; }
    public bool? VolumeMuted { get; init; }
    public double? GroupVolume { get; init; }
    public bool? GroupVolumeMuted { get; init; }
    public List<string>? GroupMembers { get; init; }
    public string? SyncedTo { get; init; }
    public string? ActiveGroup { get; init; }
    public string? ActiveSource { get; init; }
    public PlayerMedia? CurrentMedia { get; init; }
    public string? Icon { get; init; }

    [JsonIgnore]
    public string DisplayLabel => !string.IsNullOrWhiteSpace(Name) ? Name! : DisplayName ?? PlayerId;

    [JsonIgnore]
    public PlaybackState State => ParseState(PlaybackStateRaw ?? LegacyState);

    [JsonIgnore]
    public int GroupSize => GroupMembers?.Count ?? 0;

    /// <summary>True for players that should be offered in the speaker picker.</summary>
    [JsonIgnore]
    public bool IsSelectable => Available && Enabled && !HideInUi && string.IsNullOrEmpty(SyncedTo);

    public static PlaybackState ParseState(string? raw) => raw?.ToLowerInvariant() switch
    {
        "playing" => PlaybackState.Playing,
        "paused" => PlaybackState.Paused,
        _ => PlaybackState.Idle,
    };
}

public sealed class ServerInfo
{
    public string? ServerId { get; init; }
    public string? ServerVersion { get; init; }
    public int SchemaVersion { get; init; }
    public int MinSupportedSchemaVersion { get; init; }
    public string? BaseUrl { get; init; }
    public string? InternalUrl { get; init; }
    public string? ExternalUrl { get; init; }
    public string? Name { get; init; }
    public bool HomeassistantAddon { get; init; }
    public bool OnboardDone { get; init; }

    [JsonIgnore]
    public string? WebUrl => InternalUrl ?? BaseUrl;
}

/// <summary>Minimal media item as returned by music/item_by_uri.</summary>
public sealed class MediaItem
{
    public string? ItemId { get; init; }
    public string? Provider { get; init; }
    public string? Uri { get; init; }
    public string? Name { get; init; }
    public string? MediaType { get; init; }
    public bool? Favorite { get; init; }
}

public sealed class QueueItem
{
    public string? QueueItemId { get; init; }
    public string? Name { get; init; }
    public double? Duration { get; init; }
}

public sealed class PlayerQueue
{
    public string? QueueId { get; init; }
    public string? DisplayName { get; init; }
    public int? CurrentIndex { get; init; }
    public QueueItem? CurrentItem { get; init; }
    public QueueItem? NextItem { get; init; }
}

public sealed class FavoriteUpdate
{
    public string? Uri { get; init; }
    public string? MediaType { get; init; }
    public string? ItemId { get; init; }
    public bool? Favorite { get; init; }
    public string? UserId { get; init; }
}
