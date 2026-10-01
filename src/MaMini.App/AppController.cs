using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using MaMini.App.Services;
using MaMini.App.ViewModels;
using MaMini.App.Views;
using MaMini.Core.Api;
using MaMini.Core.Diagnostics;
using MaMini.Core.Input;
using MaMini.Core.Settings;
using MaMini.Core.State;

namespace MaMini.App;

/// <summary>Composition root: creates the services, wires them together and applies settings.</summary>
internal sealed class AppController : IDisposable
{
    private const string GlobalHotkeyGroup = "global";

    private readonly App _app;
    private readonly Dispatcher _dispatcher;
    private readonly SettingsService _settings;
    private readonly NowPlayingStore _store = new();
    private readonly TeamsCallPlayback _teamsPlayback = new();
    private readonly MaSession _session;
    private readonly ImageLoader _images;
    private readonly ThemeService _theme;

    private WidgetViewModel? _vm;
    private WidgetWindow? _widget;
    private WindowPlacementService? _placement;
    private MessageWindow? _messageWindow;
    private HotkeyService? _hotkeys;
    private MediaKeyManager? _mediaKeys;
    private TrayService? _tray;
    private SystemEventsWatcher? _systemEvents;
    private TeamsCallDetector? _teamsCallDetector;
    private FullscreenDetector? _fullscreen;
    private SettingsWindow? _settingsWindow;

    private string? _connectedUrl;
    private string? _connectedToken;
    private (MediaKeyMode Mode, bool Volume)? _appliedMediaKeys;
    private string? _appliedHotkeys;
    private bool _hiddenForFullscreen;
    private bool _placementRestored;
    private bool _disposed;

    public AppController(App app)
    {
        _app = app;
        _dispatcher = app.Dispatcher;
        _settings = new SettingsService(Path.Combine(SettingsService.DefaultDirectory, "settings.json"), new DpapiTokenProtector());
        _session = new MaSession(new MaClient(), _store, new DispatcherSynchronizationContext(_dispatcher));
        _images = new ImageLoader(() => _session.Client.HttpBase, () => _session.Client.Token);
        _theme = new ThemeService(app);
    }

    private AppSettings Settings => _settings.Current;

    public void Start()
    {
        var settings = _settings.Load();
        _theme.Apply(settings.Theme);
        _store.FollowActivePlayer = settings.FollowActivePlayer;
        _store.RestoreSelection(settings.SelectedPlayerId);

        _vm = new WidgetViewModel(_session, _images);
        _vm.OpenSettingsRequested += (_, _) => OpenSettings();
        _widget = new WidgetWindow(_vm)
        {
            MenuFactory = () =>
            {
                var menu = new ContextMenu();
                PopulateMenu(menu);
                return menu;
            },
        };
        new WindowInteropHelper(_widget).EnsureHandle();
        _placement = new WindowPlacementService(_widget) { DefaultCorner = settings.DefaultCorner, SnapToEdges = settings.SnapToEdges };
        _placement.PlacementChanged += (_, placement) => _settings.Update(s => s.Placement = placement);
        _widget.IsOnRightHalf = _placement.IsOnRightHalf;
        _widget.CompactToggled += (_, compact) => _settings.Update(s => s.Compact = compact);
        _widget.SizeChanged += (_, e) =>
        {
            if (e.WidthChanged && e.PreviousSize.Width > 0 && _widget.IsVisible)
            {
                // Shrinking/expanding shifts the window when anchored right; keep it on-screen and remember it.
                _dispatcher.BeginInvoke(() =>
                {
                    _placement.EnsureOnScreen();
                    _placement.SaveCurrent();
                }, DispatcherPriority.Background);
            }
        };

        _messageWindow = new MessageWindow();
        _hotkeys = new HotkeyService(_messageWindow);
        _mediaKeys = new MediaKeyManager(_dispatcher, _messageWindow, _hotkeys, CanHandleMediaKeys);
        _mediaKeys.CommandReceived += (_, command) => OnMediaKey(command);
        _vm.ArtworkChanged += (_, e) => _mediaKeys.UpdateDisplay(e.NowPlaying, e.Artwork);

        _tray = new TrayService(PopulateMenu, ToggleWidget);
        _fullscreen = new FullscreenDetector(_messageWindow, () => _placement.CurrentMonitorBounds(), () => _widget.Handle);
        _fullscreen.FullscreenChanged += (_, fullscreen) => OnFullscreenChanged(fullscreen);

        _systemEvents = new SystemEventsWatcher(_dispatcher);
        _systemEvents.Resumed += (_, _) => _session.ReconnectNow(force: true);
        _systemEvents.NetworkChanged += (_, _) => _session.Refresh();

        _teamsCallDetector = new TeamsCallDetector(_dispatcher);
        _teamsCallDetector.CallActiveChanged += (_, active) => OnTeamsCallChanged(active);

        _store.NowPlayingChanged += (_, np) => OnNowPlayingChanged(np);
        _store.SelectedPlayerChanged += (_, id) =>
        {
            if (Settings.SelectedPlayerId != id)
            {
                _settings.Update(s => s.SelectedPlayerId = id);
            }
        };
        _session.StateChanged += (_, _) => UpdateTrayToolTip();
        _session.TrackChanged += (_, np) => OnTrackChanged(np);
        _settings.Changed += (_, s) => ApplySettings(s);

        ApplySettings(settings);
        if (!settings.Hidden)
        {
            _widget.Show();
        }

        // Positioning needs the SizeToContent size, which is only known once the window has been shown.
        _placementRestored = _widget.IsVisible;
        _placement.Restore(settings.Placement);

        UpdateTrayToolTip();
        Connect(settings);

        if (string.IsNullOrWhiteSpace(settings.ServerUrl))
        {
            _dispatcher.BeginInvoke(OpenSettings, DispatcherPriority.Background);
        }
    }

    public void ShowWidget()
    {
        if (_widget is null)
        {
            return;
        }

        _hiddenForFullscreen = false;
        _widget.Show();
        if (!_placementRestored)
        {
            _placementRestored = true;
            _placement?.Restore(Settings.Placement);
        }

        _placement?.EnsureOnScreen();
        if (Settings.Hidden)
        {
            _settings.Update(s => s.Hidden = false);
        }
    }

    public void HideWidget()
    {
        _widget?.Hide();
        if (!Settings.Hidden)
        {
            _settings.Update(s => s.Hidden = true);
        }
    }

    public void ToggleWidget()
    {
        if (_widget?.IsVisible == true)
        {
            HideWidget();
        }
        else
        {
            ShowWidget();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _settingsWindow?.Close();
        _fullscreen?.Dispose();
        _mediaKeys?.Dispose();
        _teamsCallDetector?.Dispose();
        _hotkeys?.Dispose();
        _systemEvents?.Dispose();
        _tray?.Dispose();
        _placement?.Dispose();
        _vm?.Dispose();
        _widget?.Close();
        _messageWindow?.Dispose();
        _theme.Dispose();
        _images.Dispose();

        // Close the socket politely, but never hang shutdown on it.
        try
        {
            _session.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception ex)
        {
            Log.Warn("Error while closing the connection.", ex);
        }
    }

    private bool CanHandleMediaKeys() => _session.State == ConnectionState.Connected && _store.Current.PlayerId is not null;

    private void Connect(AppSettings settings)
    {
        var token = _settings.GetToken(settings);
        if (settings.ServerUrl == _connectedUrl && token == _connectedToken)
        {
            return;
        }

        _connectedUrl = settings.ServerUrl;
        _connectedToken = token;
        if (ServerAddress.TryNormalize(settings.ServerUrl, out var uri))
        {
            Log.Info($"Connecting to {uri}.");
            _ = _session.StartAsync(uri, token);
        }
        else
        {
            _ = _session.StopAsync();
        }
    }

    private void ApplySettings(AppSettings s)
    {
        if (_widget is null || _vm is null || _placement is null)
        {
            return;
        }

        _theme.Apply(s.Theme);
        _widget.Topmost = s.AlwaysOnTop;
        _widget.Locked = s.Locked;
        _widget.ClickThrough = s.ClickThrough;
        _widget.Compact = s.Compact;
        _widget.Opacity = s.Opacity;
        _placement.SnapToEdges = s.SnapToEdges;
        _placement.DefaultCorner = s.DefaultCorner;
        _vm.ShowProgressSetting = s.ShowProgress;
        _vm.VolumeStep = s.VolumeStep;
        _store.FollowActivePlayer = s.FollowActivePlayer;
        if (s.SelectedPlayerId is not null && s.SelectedPlayerId != _store.SelectedPlayerId && _store.GetPlayer(s.SelectedPlayerId) is not null)
        {
            _store.Select(s.SelectedPlayerId);
        }

        if (AutostartService.IsEnabled != s.StartWithWindows)
        {
            AutostartService.Apply(s.StartWithWindows);
        }

        if (_fullscreen is not null)
        {
            _fullscreen.Enabled = s.HideWhenFullscreen;
        }

        if (_teamsCallDetector is not null)
        {
            if (!s.AutoPauseOnTeamsCall)
            {
                _teamsPlayback.Clear("Teams call pausing was turned off");
            }

            _teamsCallDetector.Enabled = s.AutoPauseOnTeamsCall;
        }

        if (_mediaKeys is not null && _appliedMediaKeys != (s.MediaKeys, s.CaptureVolumeKeys))
        {
            _appliedMediaKeys = (s.MediaKeys, s.CaptureVolumeKeys);
            _mediaKeys.Apply(s.MediaKeys, s.CaptureVolumeKeys);
            _mediaKeys.UpdateDisplay(_store.Current, null);
        }

        RegisterGlobalHotkeys(s);
        Connect(s);
    }

    private void RegisterGlobalHotkeys(AppSettings s)
    {
        if (_hotkeys is null)
        {
            return;
        }

        var signature = string.Join('|', s.HotkeyToggleWidget, s.HotkeyPlayPause, s.HotkeyNext, s.HotkeyFavorite);
        if (signature == _appliedHotkeys)
        {
            return;
        }

        _appliedHotkeys = signature;
        _hotkeys.Clear(GlobalHotkeyGroup);
        var failed = new List<string>();
        void Register(string? text, Action action)
        {
            if (Hotkey.TryParse(text, out var hotkey) && hotkey.Value.IsValid && !_hotkeys.Register(GlobalHotkeyGroup, hotkey.Value, action))
            {
                failed.Add(hotkey.Value.ToString());
            }
        }

        Register(s.HotkeyToggleWidget, ToggleWidget);
        Register(s.HotkeyPlayPause, () => _ = _session.PlayPauseAsync());
        Register(s.HotkeyNext, () => _ = _session.NextAsync());
        Register(s.HotkeyFavorite, () => _ = _session.ToggleFavoriteAsync());

        if (failed.Count > 0)
        {
            _tray?.ShowNotification("Shortcut unavailable", $"{string.Join(", ", failed)} is already used by another app. Pick a different one in Settings.");
        }
    }

    private void OnMediaKey(MediaKeyCommand command)
    {
        var np = _store.Current;
        switch (command)
        {
            case MediaKeyCommand.PlayPause:
            case MediaKeyCommand.Play when !np.IsPlaying:
            case MediaKeyCommand.Pause when np.IsPlaying:
                _ = _session.PlayPauseAsync();
                break;
            case MediaKeyCommand.Next:
                _ = _session.NextAsync();
                break;
            case MediaKeyCommand.Previous:
                _ = _session.PreviousAsync();
                break;
            case MediaKeyCommand.Stop:
                _ = _session.StopPlaybackAsync();
                break;
            case MediaKeyCommand.VolumeUp:
                _vm?.ChangeVolumeBy(1);
                break;
            case MediaKeyCommand.VolumeDown:
                _vm?.ChangeVolumeBy(-1);
                break;
            case MediaKeyCommand.Mute:
                _ = _session.ToggleMuteAsync();
                _vm?.ShowVolumeOverlay();
                break;
        }
    }

    private void OnNowPlayingChanged(NowPlaying np)
    {
        var pending = _teamsPlayback.HasPendingResume;
        _teamsPlayback.OnNowPlayingChanged(np, DateTimeOffset.UtcNow);
        if (pending && !_teamsPlayback.HasPendingResume)
        {
            Log.Info($"Won't resume after the Teams call: {_teamsPlayback.LastClearReason}.");
        }

        _mediaKeys?.UpdateDisplay(np, null);
        UpdateTrayToolTip();
    }

    private void OnTeamsCallChanged(bool active)
    {
        var np = _store.Current;
        var action = _teamsPlayback.OnCallChanged(active, np, Settings.AutoResumeAfterTeamsCall, DateTimeOffset.UtcNow);
        switch (action)
        {
            case TeamsPlaybackAction.Pause:
                Log.Info("Active Microsoft Teams audio detected; pausing Music Assistant.");
                _ = _session.PauseAsync();
                break;
            case TeamsPlaybackAction.Resume when _session.State == ConnectionState.Connected:
                Log.Info($"Microsoft Teams audio ended; resuming Music Assistant (speaker state was {np.State}).");
                _ = _session.PlayAsync();
                break;
            case TeamsPlaybackAction.Resume:
                Log.Info("Microsoft Teams audio ended, but Music Assistant is not connected; not resuming.");
                break;
            default:
                if (!active)
                {
                    Log.Info($"Microsoft Teams audio ended; nothing to resume ({_teamsPlayback.LastClearReason ?? "music wasn't paused by MA Mini"}).");
                }

                break;
        }
    }

    private void OnTrackChanged(NowPlaying np)
    {
        if (Settings.NotifyOnTrackChange && np.HasMedia && _widget?.IsVisible != true)
        {
            _tray?.ShowNotification(np.Title ?? "Now playing", string.IsNullOrEmpty(np.Artist) ? np.PlayerName ?? "" : $"{np.Artist} · {np.PlayerName}");
        }
    }

    private void OnFullscreenChanged(bool fullscreen)
    {
        if (_widget is null)
        {
            return;
        }

        if (fullscreen && _widget.IsVisible)
        {
            _hiddenForFullscreen = true;
            _widget.Hide();
        }
        else if (!fullscreen && _hiddenForFullscreen)
        {
            _hiddenForFullscreen = false;
            if (!Settings.Hidden)
            {
                _widget.Show();
            }
        }
    }

    private void UpdateTrayToolTip()
    {
        if (_tray is null)
        {
            return;
        }

        var np = _store.Current;
        string text;
        if (_session.State != ConnectionState.Connected)
        {
            text = $"MA Mini – {_vm?.StatusText ?? _session.State.ToString()}";
        }
        else if (np.HasMedia)
        {
            text = $"{np.Title}\n{np.Artist}\n{(np.IsPlaying ? "▶" : "⏸")} {np.PlayerName}";
        }
        else
        {
            text = np.PlayerName is null ? "MA Mini – no speaker selected" : $"MA Mini – {np.PlayerName}";
        }

        _tray.SetToolTip(text);
    }

    private void PopulateMenu(ContextMenu menu)
    {
        menu.Items.Clear();
        var np = _store.Current;
        var connected = _session.State == ConnectionState.Connected;

        var header = np.HasMedia ? $"{np.Title} – {np.Artist}" : _vm?.StatusText ?? "MA Mini";
        menu.Items.Add(new MenuItem { Header = Truncate(header, 48), IsEnabled = false });
        menu.Items.Add(new Separator());

        var hasPlayer = connected && np.PlayerId is not null;
        menu.Items.Add(Item(np.IsPlaying ? "Pause" : "Play", () => _ = _session.PlayPauseAsync(), hasPlayer));
        menu.Items.Add(Item("Next track", () => _ = _session.NextAsync(), hasPlayer));
        menu.Items.Add(Item("Previous track", () => _ = _session.PreviousAsync(), hasPlayer));
        if (np.CanFavorite)
        {
            menu.Items.Add(Item(np.IsFavorite == true ? "Remove from favourites" : "Add to favourites", () => _ = _session.ToggleFavoriteAsync(), connected));
        }

        var speakers = new MenuItem { Header = "Speaker" };
        foreach (var player in _store.SelectablePlayers)
        {
            var id = player.PlayerId;
            var label = player.GroupSize > 1 ? $"{player.DisplayLabel} (+{player.GroupSize - 1})" : player.DisplayLabel;
            var item = Item(label, () => _store.Select(id));
            item.IsCheckable = true;
            item.IsChecked = id == _store.SelectedPlayerId;
            speakers.Items.Add(item);
        }

        if (speakers.Items.Count == 0)
        {
            speakers.Items.Add(new MenuItem { Header = connected ? "No speakers found" : "Not connected", IsEnabled = false });
        }

        speakers.Items.Add(new Separator());
        speakers.Items.Add(Toggle("Follow active speaker", Settings.FollowActivePlayer, v => _settings.Update(s => s.FollowActivePlayer = v)));
        menu.Items.Add(speakers);
        menu.Items.Add(new Separator());

        menu.Items.Add(Toggle("Show widget", _widget?.IsVisible == true, v =>
        {
            if (v)
            {
                ShowWidget();
            }
            else
            {
                HideWidget();
            }
        }));
        menu.Items.Add(Toggle("Always on top", Settings.AlwaysOnTop, v => _settings.Update(s => s.AlwaysOnTop = v)));
        menu.Items.Add(Toggle("Lock position", Settings.Locked, v => _settings.Update(s => s.Locked = v)));
        menu.Items.Add(Toggle("Click-through", Settings.ClickThrough, v => _settings.Update(s => s.ClickThrough = v)));
        menu.Items.Add(Toggle("Album art only", Settings.Compact, v => _settings.Update(s => s.Compact = v)));

        var corner = new MenuItem { Header = "Move to corner" };
        foreach (var c in Enum.GetValues<WidgetCorner>())
        {
            corner.Items.Add(Item(c switch
            {
                WidgetCorner.TopLeft => "Top left",
                WidgetCorner.TopRight => "Top right",
                WidgetCorner.BottomLeft => "Bottom left",
                _ => "Bottom right",
            }, () =>
            {
                _settings.Update(s => s.DefaultCorner = c);
                _placement?.ResetToCorner(c);
            }));
        }

        menu.Items.Add(corner);
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Open Music Assistant", () => WidgetViewModel.OpenMusicAssistant(_session), _session.Client.HttpBase is not null));
        menu.Items.Add(Item("Reconnect", () => _session.ReconnectNow(force: true), _session.Client.HttpBase is not null));
        menu.Items.Add(Item("Settings…", OpenSettings));
        menu.Items.Add(Item("Copy diagnostics", CopyDiagnostics));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Exit", () => _app.Shutdown()));
    }

    private static MenuItem Item(string header, Action action, bool enabled = true)
    {
        var item = new MenuItem { Header = header, IsEnabled = enabled };
        item.Click += (_, _) => action();
        return item;
    }

    private static MenuItem Toggle(string header, bool isChecked, Action<bool> onChange)
    {
        var item = new MenuItem { Header = header, IsCheckable = true, IsChecked = isChecked };
        item.Click += (_, _) => onChange(item.IsChecked);
        return item;
    }

    private static string Truncate(string text, int max) => text.Length > max ? text[..(max - 1)] + "…" : text;

    private void CopyDiagnostics()
    {
        try
        {
            Clipboard.SetText(DiagnosticsService.Build(_session, Settings, _mediaKeys?.Status ?? "Off"));
            _tray?.ShowNotification("MA Mini", "Diagnostics copied to the clipboard.");
        }
        catch (Exception ex)
        {
            Log.Warn("Could not copy diagnostics.", ex);
        }
    }

    private void OpenSettings()
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }

        var players = _store.SelectablePlayers.Select(p => (p.PlayerId, p.DisplayLabel));
        var vm = new SettingsViewModel(Settings, _settings.GetToken(), players) { MediaKeyStatus = _mediaKeys?.Status ?? "Off" };
        _settingsWindow = new SettingsWindow(vm);
        _settingsWindow.Closed += (_, _) =>
        {
            var window = _settingsWindow;
            _settingsWindow = null;
            if (window?.Saved != true)
            {
                return;
            }

            var updated = vm.ToSettings();
            _settings.SetToken(updated, vm.Token);
            _settings.Save(updated);
        };
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }
}
