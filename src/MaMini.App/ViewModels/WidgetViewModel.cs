using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MaMini.App.Services;
using MaMini.Core.Api;
using MaMini.Core.Diagnostics;
using MaMini.Core.Models;
using MaMini.Core.State;

namespace MaMini.App.ViewModels;

public sealed partial class PlayerItem : ObservableObject
{
    public PlayerItem(string id, string name, bool isPlaying, int groupSize, bool isSelected)
    {
        Id = id;
        Name = name;
        IsPlaying = isPlaying;
        GroupSize = groupSize;
        IsSelected = isSelected;
    }

    public string Id { get; }

    public string Name { get; }

    public bool IsPlaying { get; }

    public int GroupSize { get; }

    public string Label => GroupSize > 1 ? $"{Name} (+{GroupSize - 1})" : Name;

    [ObservableProperty]
    private bool isSelected;
}

/// <summary>Everything the widget binds to. Lives on the UI thread.</summary>
public sealed partial class WidgetViewModel : ObservableObject, IDisposable
{
    private readonly MaSession _session;
    private readonly ImageLoader _images;
    private readonly DispatcherTimer _progressTimer;
    private readonly DispatcherTimer _volumeOverlayTimer;
    private CancellationTokenSource? _imageLoad;
    private string? _imageUrl;
    private NowPlaying _current = NowPlaying.Empty;

    internal WidgetViewModel(MaSession session, ImageLoader images)
    {
        _session = session;
        _images = images;
        _progressTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _progressTimer.Tick += (_, _) => UpdateProgress();
        _volumeOverlayTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.2) };
        _volumeOverlayTimer.Tick += (_, _) =>
        {
            _volumeOverlayTimer.Stop();
            VolumeOverlayVisible = false;
        };

        _session.StateChanged += (_, _) => UpdateConnection();
        _session.Store.NowPlayingChanged += (_, np) => Apply(np);
        _session.Store.PlayersChanged += (_, _) => RebuildPlayers();
        _session.Store.SelectedPlayerChanged += (_, _) => RebuildPlayers();
        _session.CommandFailed += (_, message) => ShowToast(message);
        UpdateConnection();
        Apply(_session.Store.Current);
    }

    /// <summary>Raised when new cover art bytes are available (used for the Windows media flyout).</summary>
    internal event EventHandler<(NowPlaying NowPlaying, byte[]? Artwork)>? ArtworkChanged;

    public event EventHandler? OpenSettingsRequested;

    public ObservableCollection<PlayerItem> Players { get; } = new();

    [ObservableProperty]
    private string title = "Not playing";

    [ObservableProperty]
    private string artist = "";

    [ObservableProperty]
    private string subtitle = "";

    [ObservableProperty]
    private string playerName = "Choose a speaker";

    [ObservableProperty]
    private ImageSource? cover;

    [ObservableProperty]
    private bool isPlaying;

    [ObservableProperty]
    private bool hasMedia;

    [ObservableProperty]
    private bool? isFavorite;

    [ObservableProperty]
    private bool canFavorite;

    [ObservableProperty]
    private double progress;

    [ObservableProperty]
    private bool hasProgress;

    [ObservableProperty]
    private bool showProgressSetting = true;

    [ObservableProperty]
    private bool isConnected;

    [ObservableProperty]
    private string statusText = "Connecting…";

    [ObservableProperty]
    private bool hasPlayer;

    [ObservableProperty]
    private int volume;

    [ObservableProperty]
    private bool hasVolume;

    [ObservableProperty]
    private bool muted;

    [ObservableProperty]
    private bool volumeOverlayVisible;

    [ObservableProperty]
    private string? toast;

    [ObservableProperty]
    private string? upNext;

    public int VolumeStep { get; set; } = 5;

    public NowPlaying Current => _current;

    public bool ShowProgress => ShowProgressSetting && HasProgress;

    public string PlayPauseTooltip => IsPlaying ? "Pause" : "Play";

    public string FavoriteTooltip => IsFavorite == true ? "Remove from favourites" : "Add to favourites";

    public string AccessibleSummary => HasMedia ? $"{Title} by {Artist} on {PlayerName}" : $"{StatusText}";

    /// <summary>The widget is visible; the progress timer only runs while it is.</summary>
    public bool IsViewVisible
    {
        get => _isViewVisible;
        set
        {
            _isViewVisible = value;
            UpdateTimer();
        }
    }

    private bool _isViewVisible = true;

    public void Dispose()
    {
        _progressTimer.Stop();
        _volumeOverlayTimer.Stop();
        _imageLoad?.Cancel();
    }

    public void ChangeVolumeBy(int direction)
    {
        if (!HasVolume)
        {
            return;
        }

        _session.StepVolume(direction * VolumeStep);
        ShowVolumeOverlay();
    }

    public void ShowVolumeOverlay()
    {
        if (!HasVolume)
        {
            return;
        }

        VolumeOverlayVisible = true;
        _volumeOverlayTimer.Stop();
        _volumeOverlayTimer.Start();
    }

    public async Task RefreshUpNextAsync()
    {
        try
        {
            UpNext = await _session.GetUpNextAsync();
        }
        catch (Exception ex)
        {
            Log.Info($"Up-next lookup failed: {ex.Message}");
            UpNext = null;
        }
    }

    [RelayCommand]
    private Task PlayPause() => _session.PlayPauseAsync();

    [RelayCommand]
    private Task Next() => _session.NextAsync();

    [RelayCommand]
    private Task Previous() => _session.PreviousAsync();

    [RelayCommand]
    private Task ToggleFavorite() => _session.ToggleFavoriteAsync();

    [RelayCommand]
    private Task ToggleMute() => _session.ToggleMuteAsync();

    [RelayCommand]
    private void SelectPlayer(PlayerItem? item)
    {
        if (item is not null)
        {
            _session.Store.Select(item.Id);
        }
    }

    [RelayCommand]
    private void OpenSettings() => OpenSettingsRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void OpenWebUi() => OpenMusicAssistant(_session);

    internal static void OpenMusicAssistant(MaSession session)
    {
        // The address the user connected with is the one known to be reachable from this PC.
        var url = session.Client.HttpBase?.ToString() ?? session.ServerInfo?.WebUrl;
        if (url is null)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn("Could not open the Music Assistant web UI.", ex);
        }
    }

    partial void OnIsPlayingChanged(bool value)
    {
        OnPropertyChanged(nameof(PlayPauseTooltip));
        UpdateTimer();
    }

    partial void OnIsFavoriteChanged(bool? value) => OnPropertyChanged(nameof(FavoriteTooltip));

    partial void OnHasProgressChanged(bool value) => OnPropertyChanged(nameof(ShowProgress));

    partial void OnShowProgressSettingChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowProgress));
        UpdateTimer();
    }

    private void Apply(NowPlaying np)
    {
        var trackChanged = np.Uri != _current.Uri || np.Title != _current.Title || np.PlayerId != _current.PlayerId;
        _current = np;

        HasPlayer = np.PlayerId is not null;
        PlayerName = np.PlayerName ?? "Choose a speaker";
        HasMedia = np.HasMedia;
        Title = np.HasMedia ? np.Title ?? "Unknown title" : HasPlayer ? "Nothing playing" : "No speaker selected";
        Artist = np.Artist ?? "";
        IsPlaying = np.IsPlaying;
        IsFavorite = np.IsFavorite;
        CanFavorite = np.CanFavorite && IsConnected;
        HasVolume = np.Volume is not null;
        Volume = np.Volume ?? 0;
        Muted = np.Muted;
        UpdateProgress();
        UpdateSubtitle();

        if (trackChanged)
        {
            UpNext = null;
        }

        LoadCover(np, trackChanged);
    }

    private void LoadCover(NowPlaying np, bool trackChanged)
    {
        var url = _images.Normalize(np.ImageUrl);
        if (url == _imageUrl)
        {
            if (trackChanged)
            {
                ArtworkChanged?.Invoke(this, (np, null));
            }

            return;
        }

        _imageUrl = url;
        _imageLoad?.Cancel();
        if (url is null)
        {
            Cover = null;
            ArtworkChanged?.Invoke(this, (np, null));
            return;
        }

        var cts = _imageLoad = new CancellationTokenSource();
        _ = LoadCoverAsync(url, np, cts.Token);
    }

    private async Task LoadCoverAsync(string url, NowPlaying np, CancellationToken ct)
    {
        var image = await _images.LoadAsync(url, ct);
        if (ct.IsCancellationRequested)
        {
            return;
        }

        Cover = image?.Image;
        ArtworkChanged?.Invoke(this, (np, image?.Bytes));
    }

    private void UpdateConnection()
    {
        IsConnected = _session.State == ConnectionState.Connected;
        StatusText = _session.State switch
        {
            ConnectionState.Connected => "Connected",
            ConnectionState.Connecting => "Connecting…",
            ConnectionState.Disconnected => "Reconnecting…",
            ConnectionState.AuthFailed => "Sign-in failed – check your token",
            ConnectionState.SetupRequired => "Finish setting up Music Assistant",
            _ => "Not connected",
        };
        CanFavorite = _current.CanFavorite && IsConnected;
        UpdateSubtitle();
    }

    private void UpdateSubtitle()
    {
        Subtitle = IsConnected ? Artist : StatusText;
        OnPropertyChanged(nameof(Current));
        OnPropertyChanged(nameof(AccessibleSummary));
    }

    private void RebuildPlayers()
    {
        var selected = _session.Store.SelectedPlayerId;
        Players.Clear();
        foreach (var player in _session.Store.SelectablePlayers)
        {
            Players.Add(new PlayerItem(player.PlayerId, player.DisplayLabel, player.State == PlaybackState.Playing, player.GroupSize, player.PlayerId == selected));
        }
    }

    private void UpdateProgress()
    {
        var value = _current.ProgressAt(DateTimeOffset.UtcNow);
        HasProgress = value is not null && HasMedia;
        Progress = value ?? 0;
    }

    private void UpdateTimer()
    {
        var run = _isViewVisible && IsPlaying && ShowProgressSetting;
        if (run && !_progressTimer.IsEnabled)
        {
            _progressTimer.Start();
        }
        else if (!run && _progressTimer.IsEnabled)
        {
            _progressTimer.Stop();
        }
    }

    internal async void ShowToast(string message)
    {
        Toast = message;
        await Task.Delay(TimeSpan.FromSeconds(3));
        if (Toast == message)
        {
            Toast = null;
        }
    }
}
