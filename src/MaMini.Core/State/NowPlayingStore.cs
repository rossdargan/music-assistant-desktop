using MaMini.Core.Models;

namespace MaMini.Core.State;

/// <summary>
/// Holds the known players and the selected speaker, and derives <see cref="NowPlaying"/>.
/// Not thread-safe: all calls are expected on one thread (the UI thread in the app).
/// </summary>
public sealed class NowPlayingStore
{
    private readonly Dictionary<string, Player> _players = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool?> _favorites = new(StringComparer.Ordinal);
    private bool? _optimisticPlaying;
    private int? _optimisticVolume;
    private bool? _optimisticMuted;

    public string? SelectedPlayerId { get; private set; }

    public bool FollowActivePlayer { get; set; }

    public NowPlaying Current { get; private set; } = NowPlaying.Empty;

    public event EventHandler? PlayersChanged;

    public event EventHandler<NowPlaying>? NowPlayingChanged;

    public event EventHandler<string?>? SelectedPlayerChanged;

    public Player? SelectedPlayer => SelectedPlayerId is not null && _players.TryGetValue(SelectedPlayerId, out var p) ? p : null;

    /// <summary>Players to offer in the speaker picker, sorted by name.</summary>
    public IReadOnlyList<Player> SelectablePlayers =>
        _players.Values
            .Where(p => p.IsSelectable)
            .OrderBy(p => p.DisplayLabel, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    public Player? GetPlayer(string id) => _players.TryGetValue(id, out var p) ? p : null;

    /// <summary>Restores a previously persisted selection without raising change events.</summary>
    public void RestoreSelection(string? playerId) => SelectedPlayerId = playerId;

    public void SetPlayers(IEnumerable<Player> players)
    {
        _players.Clear();
        foreach (var p in players)
        {
            if (!string.IsNullOrEmpty(p.PlayerId))
            {
                _players[p.PlayerId] = p;
            }
        }

        EnsureSelection();
        PlayersChanged?.Invoke(this, EventArgs.Empty);
        Recompute();
    }

    public void UpsertPlayer(Player player)
    {
        if (string.IsNullOrEmpty(player.PlayerId))
        {
            return;
        }

        _players.TryGetValue(player.PlayerId, out var old);
        _players[player.PlayerId] = player;

        if (player.PlayerId == SelectedPlayerId)
        {
            // A real update supersedes optimistic guesses.
            if (old is null || old.State != player.State || _optimisticPlaying == (player.State == PlaybackState.Playing))
            {
                _optimisticPlaying = null;
            }

            _optimisticVolume = null;
            _optimisticMuted = null;
        }

        if (FollowActivePlayer &&
            player.IsSelectable &&
            player.State == PlaybackState.Playing &&
            old?.State != PlaybackState.Playing &&
            player.PlayerId != SelectedPlayerId)
        {
            Select(player.PlayerId);
        }

        if (old is null ||
            old.IsSelectable != player.IsSelectable ||
            old.DisplayLabel != player.DisplayLabel ||
            old.GroupSize != player.GroupSize ||
            old.State != player.State)
        {
            if (SelectedPlayer is null)
            {
                EnsureSelection();
            }

            PlayersChanged?.Invoke(this, EventArgs.Empty);
        }

        Recompute();
    }

    public void RemovePlayer(string playerId)
    {
        if (_players.Remove(playerId))
        {
            PlayersChanged?.Invoke(this, EventArgs.Empty);
            Recompute();
        }
    }

    public void Select(string? playerId)
    {
        if (SelectedPlayerId == playerId)
        {
            return;
        }

        SelectedPlayerId = playerId;
        _optimisticPlaying = null;
        _optimisticVolume = null;
        _optimisticMuted = null;
        SelectedPlayerChanged?.Invoke(this, playerId);
        PlayersChanged?.Invoke(this, EventArgs.Empty);
        Recompute();
    }

    public void SetOptimisticPlaying(bool? playing)
    {
        _optimisticPlaying = playing;
        Recompute();
    }

    public void SetOptimisticVolume(int? volume)
    {
        _optimisticVolume = volume is null ? null : Math.Clamp(volume.Value, 0, 100);
        Recompute();
    }

    public void SetOptimisticMuted(bool? muted)
    {
        _optimisticMuted = muted;
        Recompute();
    }

    public void SetFavorite(string uri, bool? favorite)
    {
        _favorites[uri] = favorite;
        if (Current.Uri == uri)
        {
            Recompute();
        }
    }

    public bool TryGetFavorite(string uri, out bool? favorite) => _favorites.TryGetValue(uri, out favorite);

    private void EnsureSelection()
    {
        // Keep a remembered selection until we actually know which players exist.
        if (_players.Count == 0 || (SelectedPlayerId is not null && _players.ContainsKey(SelectedPlayerId)))
        {
            return;
        }

        var selectable = SelectablePlayers;
        var pick = selectable.FirstOrDefault(p => p.State == PlaybackState.Playing) ?? selectable.FirstOrDefault();
        if (pick is not null && pick.PlayerId != SelectedPlayerId)
        {
            SelectedPlayerId = pick.PlayerId;
            SelectedPlayerChanged?.Invoke(this, pick.PlayerId);
        }
    }

    private void Recompute()
    {
        var next = Build();
        if (next != Current)
        {
            Current = next;
            NowPlayingChanged?.Invoke(this, next);
        }
    }

    private NowPlaying Build()
    {
        var player = SelectedPlayer;
        if (player is null)
        {
            return NowPlaying.Empty with { PlayerId = SelectedPlayerId };
        }

        var leaderId = player.ActiveGroup ?? player.SyncedTo;
        var leader = leaderId is not null && _players.TryGetValue(leaderId, out var l) ? l : null;
        var mediaOwner = player.CurrentMedia is null && leader?.CurrentMedia is not null ? leader : player;
        var media = mediaOwner.CurrentMedia;

        var state = mediaOwner.State;
        if (_optimisticPlaying is { } optimistic)
        {
            state = optimistic ? PlaybackState.Playing : PlaybackState.Paused;
        }

        var elapsed = media?.ElapsedTime ?? mediaOwner.ElapsedTime;
        var elapsedAt = media?.ElapsedTimeLastUpdated ?? mediaOwner.ElapsedTimeLastUpdated;

        var grouped = player.GroupSize > 1;
        var volume = grouped ? player.GroupVolume ?? player.VolumeLevel : player.VolumeLevel;
        var muted = grouped ? player.GroupVolumeMuted ?? player.VolumeMuted : player.VolumeMuted;

        bool? favorite = null;
        if (media?.Uri is { } uri && _favorites.TryGetValue(uri, out var fav))
        {
            favorite = fav;
        }

        return new NowPlaying
        {
            PlayerId = player.PlayerId,
            PlayerName = player.DisplayLabel,
            PlayerAvailable = player.Available,
            GroupSize = player.GroupSize,
            Title = media?.Title,
            Artist = media?.Artist,
            Album = media?.Album,
            ImageUrl = media?.ImageUrl,
            Uri = media?.Uri,
            MediaType = media?.MediaType,
            QueueId = media?.SourceId ?? player.ActiveSource,
            State = state,
            Duration = media?.Duration,
            Elapsed = elapsed,
            ElapsedAt = elapsedAt is { } ts ? DateTimeOffset.FromUnixTimeMilliseconds((long)(ts * 1000)) : null,
            Volume = _optimisticVolume ?? (volume is { } v ? (int)Math.Round(v) : null),
            Muted = _optimisticMuted ?? muted ?? false,
            IsFavorite = favorite,
        };
    }
}
