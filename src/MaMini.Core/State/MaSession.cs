using System.Text.Json;
using MaMini.Core.Api;
using MaMini.Core.Diagnostics;
using MaMini.Core.Models;

namespace MaMini.Core.State;

/// <summary>
/// Glues <see cref="MaClient"/> to <see cref="NowPlayingStore"/>: loads players, applies events,
/// and exposes the playback commands the widget needs. All store mutations and events are
/// marshalled to the supplied <see cref="SynchronizationContext"/> (the UI thread in the app).
/// </summary>
public sealed class MaSession : IAsyncDisposable
{
    private static readonly TimeSpan VolumeThrottle = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan OptimisticTimeout = TimeSpan.FromSeconds(3);

    private readonly MaClient _client;
    private readonly SynchronizationContext? _context;
    private readonly Dictionary<string, string?> _upNext = new(StringComparer.Ordinal);
    private CancellationTokenSource? _favoriteLookup;
    private int _loadGeneration;
    private int? _pendingVolume;
    private bool _volumeLoopRunning;
    private NowPlaying _lastNotified = NowPlaying.Empty;
    private ConnectionState _state = ConnectionState.Stopped;
    private string? _lastError;

    public MaSession(MaClient client, NowPlayingStore store, SynchronizationContext? context)
    {
        _client = client;
        Store = store;
        _context = context;
        _client.StateChanged += (_, s) => Post(() => OnClientState(s));
        _client.EventReceived += (_, e) => Post(() => OnEvent(e));
        Store.NowPlayingChanged += (_, np) => OnNowPlayingChanged(np);
    }

    public NowPlayingStore Store { get; }

    public MaClient Client => _client;

    public ConnectionState State => _state;

    public string? LastError => _lastError ?? _client.LastError;

    public ServerInfo? ServerInfo => _client.ServerInfo;

    /// <summary>Raised on the context thread when <see cref="State"/> changes.</summary>
    public event EventHandler<ConnectionState>? StateChanged;

    /// <summary>A new track started on the selected speaker (for notifications).</summary>
    public event EventHandler<NowPlaying>? TrackChanged;

    /// <summary>A user-initiated command failed; the argument is a short, user-facing message.</summary>
    public event EventHandler<string>? CommandFailed;

    public Task StartAsync(Uri httpBase, string? token)
    {
        _lastError = null;
        return _client.StartAsync(httpBase, token);
    }

    public Task StopAsync() => _client.StopAsync();

    public void ReconnectNow(bool force = false) => _client.ReconnectNow(force);

    /// <summary>
    /// Re-reads all players. Also acts as a liveness probe: if the request fails the connection is recycled.
    /// </summary>
    public void Refresh()
    {
        if (_state == ConnectionState.Connected)
        {
            _ = LoadPlayersAsync(++_loadGeneration);
        }
        else
        {
            _client.ReconnectNow();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _favoriteLookup?.Cancel();
        await _client.DisposeAsync().ConfigureAwait(false);
    }

    public Task PlayPauseAsync()
    {
        var np = Store.Current;
        if (np.PlayerId is null)
        {
            return Task.CompletedTask;
        }

        Store.SetOptimisticPlaying(!np.IsPlaying);
        return RunPlayerCommandAsync("players/cmd/play_pause", np.PlayerId, clearOptimistic: true);
    }

    public Task NextAsync() => Store.Current.PlayerId is { } id ? RunPlayerCommandAsync("players/cmd/next", id) : Task.CompletedTask;

    public Task PreviousAsync() => Store.Current.PlayerId is { } id ? RunPlayerCommandAsync("players/cmd/previous", id) : Task.CompletedTask;

    public Task StopPlaybackAsync() => Store.Current.PlayerId is { } id ? RunPlayerCommandAsync("players/cmd/stop", id) : Task.CompletedTask;

    /// <summary>Sets the absolute volume (0..100), coalescing rapid changes.</summary>
    public void SetVolume(int volume)
    {
        if (Store.Current.PlayerId is null || Store.Current.Volume is null)
        {
            return;
        }

        volume = Math.Clamp(volume, 0, 100);
        Store.SetOptimisticVolume(volume);
        _pendingVolume = volume;
        if (!_volumeLoopRunning)
        {
            _ = VolumeLoopAsync();
        }
    }

    public void StepVolume(int delta)
    {
        if (Store.Current.Volume is { } current)
        {
            SetVolume(current + delta);
        }
    }

    public Task ToggleMuteAsync()
    {
        var np = Store.Current;
        if (np.PlayerId is null)
        {
            return Task.CompletedTask;
        }

        var muted = !np.Muted;
        Store.SetOptimisticMuted(muted);
        var command = np.GroupSize > 1 ? "players/cmd/group_volume_mute" : "players/cmd/volume_mute";
        return RunAsync(command, new Dictionary<string, object?> { ["player_id"] = np.PlayerId, ["muted"] = muted }, () => Store.SetOptimisticMuted(null));
    }

    public async Task ToggleFavoriteAsync()
    {
        var np = Store.Current;
        if (!np.CanFavorite || np.Uri is not { } uri)
        {
            return;
        }

        var target = !(np.IsFavorite ?? false);
        Store.SetFavorite(uri, target);
        try
        {
            if (target)
            {
                await _client.SendCommandAsync("music/favorites/add_item", new Dictionary<string, object?> { ["item"] = uri });
            }
            else
            {
                var item = await _client.SendCommandAsync<MediaItem>("music/item_by_uri", new Dictionary<string, object?> { ["uri"] = uri });
                if (item?.ItemId is null || item.Provider != "library")
                {
                    // Not in the library, so it cannot be a favourite.
                    return;
                }

                await _client.SendCommandAsync("music/favorites/remove_item", new Dictionary<string, object?>
                {
                    ["media_type"] = item.MediaType ?? np.MediaType,
                    ["library_item_id"] = item.ItemId,
                });
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Favourite toggle failed for {uri}.", ex);
            Store.SetFavorite(uri, !target);
            CommandFailed?.Invoke(this, target ? "Couldn't add to favourites" : "Couldn't remove from favourites");
        }
    }

    /// <summary>Returns the name of the next queued item for the selected speaker, if known.</summary>
    public async Task<string?> GetUpNextAsync()
    {
        var np = Store.Current;
        if (np.PlayerId is null || _state != ConnectionState.Connected)
        {
            return null;
        }

        if (np.QueueId is { } qid && _upNext.TryGetValue(qid, out var cached))
        {
            return cached;
        }

        try
        {
            var queue = await _client.SendCommandAsync<PlayerQueue>("player_queues/get_active_queue", new Dictionary<string, object?> { ["player_id"] = np.PlayerId });
            var name = queue?.NextItem?.Name;
            if (queue?.QueueId is { } id)
            {
                _upNext[id] = name;
            }

            return name;
        }
        catch (Exception ex)
        {
            Log.Warn("Could not fetch the up-next item.", ex);
            return null;
        }
    }

    private Task RunPlayerCommandAsync(string command, string playerId, bool clearOptimistic = false) =>
        RunAsync(command, new Dictionary<string, object?> { ["player_id"] = playerId }, clearOptimistic ? () => Store.SetOptimisticPlaying(null) : null);

    private async Task RunAsync(string command, object args, Action? rollback)
    {
        try
        {
            await _client.SendCommandAsync(command, args);
            if (rollback is not null)
            {
                // If the server never confirms (e.g. the player ignored it), drop the guess.
                _ = ClearLaterAsync(rollback);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Command {command} failed.", ex);
            rollback?.Invoke();
            CommandFailed?.Invoke(this, ex is MaConnectionException ? "Not connected to Music Assistant" : "The speaker didn't respond");
        }
    }

    private static async Task ClearLaterAsync(Action clear)
    {
        await Task.Delay(OptimisticTimeout);
        clear();
    }

    private async Task VolumeLoopAsync()
    {
        _volumeLoopRunning = true;
        try
        {
            while (_pendingVolume is { } volume)
            {
                _pendingVolume = null;
                var np = Store.Current;
                if (np.PlayerId is null)
                {
                    break;
                }

                var command = np.GroupSize > 1 ? "players/cmd/group_volume" : "players/cmd/volume_set";
                try
                {
                    await _client.SendCommandAsync(command, new Dictionary<string, object?> { ["player_id"] = np.PlayerId, ["volume_level"] = volume });
                }
                catch (Exception ex)
                {
                    Log.Warn("Volume change failed.", ex);
                    Store.SetOptimisticVolume(null);
                    CommandFailed?.Invoke(this, "Couldn't change the volume");
                    _pendingVolume = null;
                    break;
                }

                await Task.Delay(VolumeThrottle);
            }
        }
        finally
        {
            _volumeLoopRunning = false;
        }

        _ = ClearLaterAsync(() =>
        {
            if (!_volumeLoopRunning)
            {
                Store.SetOptimisticVolume(null);
            }
        });
    }

    private void OnClientState(ConnectionState state)
    {
        if (state == ConnectionState.Connected)
        {
            _ = LoadPlayersAsync(++_loadGeneration);
            return; // Report Connected once the players are loaded.
        }

        _loadGeneration++;
        SetState(state);
    }

    private async Task LoadPlayersAsync(int generation)
    {
        try
        {
            var players = await _client.SendCommandAsync<List<Player>>("players/all");
            if (generation != _loadGeneration)
            {
                return;
            }

            _upNext.Clear();
            Store.SetPlayers(players ?? []);
            _lastError = null;
            SetState(ConnectionState.Connected);
            RefreshFavorite(Store.Current, force: true);
        }
        catch (MaCommandException ex) when (ex.ErrorCode is MaErrorCodes.AuthenticationRequired or MaErrorCodes.InvalidToken or MaErrorCodes.InsufficientPermissions)
        {
            if (generation == _loadGeneration)
            {
                _lastError = string.IsNullOrEmpty(_client.Token)
                    ? "This server needs an access token. Create one in Music Assistant under Settings → Profile."
                    : "The access token was rejected.";
                SetState(ConnectionState.AuthFailed);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Loading players failed.", ex);
            if (generation == _loadGeneration)
            {
                _client.ReconnectNow(force: true);
            }
        }
    }

    private void SetState(ConnectionState state)
    {
        if (_state == state)
        {
            return;
        }

        _state = state;
        StateChanged?.Invoke(this, state);
    }

    private void OnEvent(MaEvent e)
    {
        switch (e.Name)
        {
            case "player_added":
            case "player_updated":
                if (TryDeserialize<Player>(e.Data) is { } player)
                {
                    Store.UpsertPlayer(player);
                }

                break;
            case "player_removed":
                if (e.ObjectId is { } removed)
                {
                    Store.RemovePlayer(removed);
                }

                break;
            case "queue_updated":
                if (TryDeserialize<PlayerQueue>(e.Data) is { QueueId: { } queueId } queue)
                {
                    _upNext[queueId] = queue.NextItem?.Name;
                }
                else if (e.ObjectId is { } qid)
                {
                    _upNext.Remove(qid);
                }

                break;
            case "queue_items_updated":
                if (e.ObjectId is { } itemsQueue)
                {
                    _upNext.Remove(itemsQueue);
                }

                break;
            case "favorite_updated":
                if (TryDeserialize<FavoriteUpdate>(e.Data) is { Uri: { } favUri } fav &&
                    (fav.UserId is null || _client.UserId is null || fav.UserId == _client.UserId))
                {
                    Store.SetFavorite(favUri, fav.Favorite ?? false);
                }

                break;
            case "media_item_updated":
                if (TryDeserialize<MediaItem>(e.Data) is { Uri: { } itemUri, Favorite: { } isFav } &&
                    Store.TryGetFavorite(itemUri, out _))
                {
                    Store.SetFavorite(itemUri, isFav);
                }

                break;
        }
    }

    private void OnNowPlayingChanged(NowPlaying np)
    {
        var previous = _lastNotified;
        _lastNotified = np;
        var trackChanged = np.Uri != previous.Uri || np.Title != previous.Title || np.PlayerId != previous.PlayerId;
        if (!trackChanged)
        {
            return;
        }

        RefreshFavorite(np, force: false);
        if (np.IsPlaying && np.HasMedia && np.PlayerId == previous.PlayerId)
        {
            TrackChanged?.Invoke(this, np);
        }
    }

    private void RefreshFavorite(NowPlaying np, bool force)
    {
        _favoriteLookup?.Cancel();
        _favoriteLookup = null;
        if (!np.CanFavorite || np.Uri is not { } uri || _state != ConnectionState.Connected)
        {
            return;
        }

        if (!force && Store.TryGetFavorite(uri, out var cached) && cached is not null)
        {
            return;
        }

        var cts = new CancellationTokenSource();
        _favoriteLookup = cts;
        _ = LookupFavoriteAsync(uri, cts.Token);
    }

    private async Task LookupFavoriteAsync(string uri, CancellationToken ct)
    {
        try
        {
            var item = await _client.SendCommandAsync<MediaItem>("music/item_by_uri", new Dictionary<string, object?> { ["uri"] = uri }, ct);
            if (!ct.IsCancellationRequested)
            {
                Store.SetFavorite(uri, item?.Favorite ?? false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Info($"Favourite lookup failed for {uri}: {ex.Message}");
        }
    }

    private static T? TryDeserialize<T>(JsonElement data)
        where T : class
    {
        if (data.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        try
        {
            return data.Deserialize<T>(MaJson.Options);
        }
        catch (JsonException ex)
        {
            Log.Warn($"Could not parse {typeof(T).Name} from event.", ex);
            return null;
        }
    }

    private void Post(Action action)
    {
        if (_context is null || SynchronizationContext.Current == _context)
        {
            action();
        }
        else
        {
            _context.Post(_ => action(), null);
        }
    }
}
