using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MaMini.Core.Api;
using MaMini.Core.Discovery;
using MaMini.Core.Input;
using MaMini.Core.Models;
using MaMini.Core.Settings;

namespace MaMini.App.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly AppSettings _original;

    public SettingsViewModel(AppSettings settings, string? token, IEnumerable<(string Id, string Name)> players)
    {
        _original = settings.Clone();
        serverUrl = settings.ServerUrl ?? string.Empty;
        this.token = token ?? string.Empty;
        followActivePlayer = settings.FollowActivePlayer;
        alwaysOnTop = settings.AlwaysOnTop;
        locked = settings.Locked;
        compact = settings.Compact;
        snapToEdges = settings.SnapToEdges;
        opacityPercent = (int)Math.Round(settings.Opacity * 100);
        hideWhenFullscreen = settings.HideWhenFullscreen;
        showProgress = settings.ShowProgress;
        theme = settings.Theme;
        defaultCorner = settings.DefaultCorner;
        startWithWindows = settings.StartWithWindows;
        notifyOnTrackChange = settings.NotifyOnTrackChange;
        mediaKeys = settings.MediaKeys;
        captureVolumeKeys = settings.CaptureVolumeKeys;
        volumeStep = settings.VolumeStep;
        hotkeyToggleWidget = settings.HotkeyToggleWidget ?? string.Empty;
        hotkeyPlayPause = settings.HotkeyPlayPause ?? string.Empty;
        hotkeyNext = settings.HotkeyNext ?? string.Empty;
        hotkeyFavorite = settings.HotkeyFavorite ?? string.Empty;
        foreach (var (id, name) in players)
        {
            Players.Add(new KeyValuePair<string, string>(id, name));
        }

        selectedPlayerId = settings.SelectedPlayerId;
    }

    /// <summary>Raised with the result when the user saves (true) or cancels (false).</summary>
    public event EventHandler<bool>? CloseRequested;

    public ObservableCollection<DiscoveredServer> DiscoveredServers { get; } = new();

    public ObservableCollection<KeyValuePair<string, string>> Players { get; } = new();

    public IReadOnlyList<KeyValuePair<ThemeMode, string>> Themes { get; } =
    [
        new(ThemeMode.System, "Follow Windows"),
        new(ThemeMode.Light, "Light"),
        new(ThemeMode.Dark, "Dark"),
    ];

    public IReadOnlyList<KeyValuePair<WidgetCorner, string>> Corners { get; } =
    [
        new(WidgetCorner.BottomRight, "Bottom right"),
        new(WidgetCorner.BottomLeft, "Bottom left"),
        new(WidgetCorner.TopRight, "Top right"),
        new(WidgetCorner.TopLeft, "Top left"),
    ];

    public IReadOnlyList<KeyValuePair<MediaKeyMode, string>> MediaKeyModes { get; } =
    [
        new(MediaKeyMode.Smtc, "Windows media controls (recommended)"),
        new(MediaKeyMode.Hotkey, "Global hotkeys"),
        new(MediaKeyMode.Hook, "Keyboard hook"),
        new(MediaKeyMode.Off, "Off"),
    ];

    /// <summary>Current media-key backend status, shown next to the mode picker.</summary>
    public string MediaKeyStatus { get; init; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private string serverUrl;

    [ObservableProperty]
    private string token;

    [ObservableProperty]
    private string? selectedPlayerId;

    [ObservableProperty]
    private bool followActivePlayer;

    [ObservableProperty]
    private bool alwaysOnTop;

    [ObservableProperty]
    private bool locked;

    [ObservableProperty]
    private bool compact;

    [ObservableProperty]
    private bool snapToEdges;

    [ObservableProperty]
    private int opacityPercent;

    [ObservableProperty]
    private bool hideWhenFullscreen;

    [ObservableProperty]
    private bool showProgress;

    [ObservableProperty]
    private ThemeMode theme;

    [ObservableProperty]
    private WidgetCorner defaultCorner;

    [ObservableProperty]
    private bool startWithWindows;

    [ObservableProperty]
    private bool notifyOnTrackChange;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHookMode))]
    private MediaKeyMode mediaKeys;

    [ObservableProperty]
    private bool captureVolumeKeys;

    [ObservableProperty]
    private int volumeStep;

    [ObservableProperty]
    private string hotkeyToggleWidget;

    [ObservableProperty]
    private string hotkeyPlayPause;

    [ObservableProperty]
    private string hotkeyNext;

    [ObservableProperty]
    private string hotkeyFavorite;

    [ObservableProperty]
    private string? connectionStatus;

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private DiscoveredServer? selectedDiscoveredServer;

    public bool IsHookMode => MediaKeys == MediaKeyMode.Hook;

    public bool CanSave => string.IsNullOrWhiteSpace(ServerUrl) || ServerAddress.TryNormalize(ServerUrl, out _);

    /// <summary>Produces the updated settings; the token is handled separately (it's encrypted by the caller).</summary>
    public AppSettings ToSettings()
    {
        var s = _original.Clone();
        s.ServerUrl = ServerAddress.TryNormalize(ServerUrl, out var uri) ? uri.ToString() : null;
        s.SelectedPlayerId = SelectedPlayerId;
        s.FollowActivePlayer = FollowActivePlayer;
        s.AlwaysOnTop = AlwaysOnTop;
        s.Locked = Locked;
        s.Compact = Compact;
        s.SnapToEdges = SnapToEdges;
        s.Opacity = OpacityPercent / 100.0;
        s.HideWhenFullscreen = HideWhenFullscreen;
        s.ShowProgress = ShowProgress;
        s.Theme = Theme;
        s.DefaultCorner = DefaultCorner;
        s.StartWithWindows = StartWithWindows;
        s.NotifyOnTrackChange = NotifyOnTrackChange;
        s.MediaKeys = MediaKeys;
        s.CaptureVolumeKeys = CaptureVolumeKeys;
        s.VolumeStep = VolumeStep;
        s.HotkeyToggleWidget = NullIfEmpty(HotkeyToggleWidget);
        s.HotkeyPlayPause = NullIfEmpty(HotkeyPlayPause);
        s.HotkeyNext = NullIfEmpty(HotkeyNext);
        s.HotkeyFavorite = NullIfEmpty(HotkeyFavorite);
        s.Normalize();
        return s;
    }

    /// <summary>Kicks off discovery automatically when no server is configured yet.</summary>
    public void OnShown()
    {
        if (string.IsNullOrWhiteSpace(ServerUrl))
        {
            FindServersCommand.Execute(null);
        }
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    partial void OnSelectedDiscoveredServerChanged(DiscoveredServer? value)
    {
        if (value is not null)
        {
            ServerUrl = value.BaseUrl.ToString().TrimEnd('/');
        }
    }

    [RelayCommand]
    private async Task FindServersAsync()
    {
        IsBusy = true;
        ConnectionStatus = "Looking for Music Assistant on your network…";
        try
        {
            var servers = await MdnsDiscovery.DiscoverAsync(TimeSpan.FromSeconds(3));
            DiscoveredServers.Clear();
            foreach (var server in servers)
            {
                DiscoveredServers.Add(server);
            }

            if (servers.Count == 1 && string.IsNullOrWhiteSpace(ServerUrl))
            {
                SelectedDiscoveredServer = servers[0];
            }

            ConnectionStatus = servers.Count switch
            {
                0 => "No servers found. Enter the address manually (e.g. http://192.168.1.10:8095).",
                1 => $"Found {servers[0].Name}.",
                _ => $"Found {servers.Count} servers – pick one from the list.",
            };
        }
        catch (Exception ex)
        {
            ConnectionStatus = $"Discovery failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task TestConnectionAsync()
    {
        if (!ServerAddress.TryNormalize(ServerUrl, out var uri))
        {
            ConnectionStatus = "That doesn't look like a valid address.";
            return;
        }

        IsBusy = true;
        ConnectionStatus = $"Connecting to {uri}…";
        await using var client = new MaClient { ConnectTimeout = TimeSpan.FromSeconds(8), RequestTimeout = TimeSpan.FromSeconds(8) };
        var done = new TaskCompletionSource<ConnectionState>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.StateChanged += (_, state) =>
        {
            if (state is ConnectionState.Connected or ConnectionState.AuthFailed or ConnectionState.SetupRequired or ConnectionState.Disconnected)
            {
                done.TrySetResult(state);
            }
        };

        try
        {
            await client.StartAsync(uri, Token);
            var finished = await Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromSeconds(12)));
            if (finished != done.Task)
            {
                ConnectionStatus = "Timed out. Check the address and that port 8095 is reachable.";
                return;
            }

            var state = done.Task.Result;
            if (state != ConnectionState.Connected)
            {
                ConnectionStatus = state switch
                {
                    ConnectionState.AuthFailed => "Connected, but the token was rejected. Create a new token in Music Assistant (Settings → Profile).",
                    ConnectionState.SetupRequired => "The server hasn't finished its first-time setup. Open Music Assistant in a browser to complete it.",
                    _ => $"Couldn't connect: {client.LastError ?? "unknown error"}",
                };
                return;
            }

            var players = await client.SendCommandAsync<List<Player>>("players/all");
            var version = client.ServerInfo?.ServerVersion;
            ConnectionStatus = $"✓ Connected to Music Assistant {version} – {players?.Count(p => p.IsSelectable) ?? 0} speakers.";
            Players.Clear();
            foreach (var p in players?.Where(p => p.IsSelectable).OrderBy(p => p.DisplayLabel, StringComparer.CurrentCultureIgnoreCase) ?? Enumerable.Empty<Player>())
            {
                Players.Add(new KeyValuePair<string, string>(p.PlayerId, p.DisplayLabel));
            }
        }
        catch (MaCommandException ex) when (ex.ErrorCode is MaErrorCodes.AuthenticationRequired or MaErrorCodes.InvalidToken or MaErrorCodes.InsufficientPermissions)
        {
            ConnectionStatus = "This server needs a token. Create one in Music Assistant (Settings → Profile → Long-lived tokens) and paste it here.";
        }
        catch (Exception ex)
        {
            ConnectionStatus = $"Couldn't connect: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Save()
    {
        foreach (var text in new[] { HotkeyToggleWidget, HotkeyPlayPause, HotkeyNext, HotkeyFavorite })
        {
            if (!string.IsNullOrWhiteSpace(text) && !Hotkey.TryParse(text, out _))
            {
                ConnectionStatus = $"'{text}' isn't a valid shortcut.";
                return;
            }
        }

        CloseRequested?.Invoke(this, true);
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, false);
}
