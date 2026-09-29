using System.Runtime.InteropServices;
using System.Windows.Threading;
using MaMini.App.Interop;
using MaMini.Core.Diagnostics;
using MaMini.Core.Input;
using MaMini.Core.Settings;
using MaMini.Core.State;
using Windows.Media;
using Windows.Storage.Streams;

namespace MaMini.App.Services;

internal enum MediaKeyCommand
{
    PlayPause,
    Play,
    Pause,
    Next,
    Previous,
    Stop,
    VolumeUp,
    VolumeDown,
    Mute,
}

internal interface IMediaKeyBackend : IDisposable
{
    /// <summary>Returns false if the backend couldn't be started (the manager then falls back).</summary>
    bool Start();
}

/// <summary>
/// Owns whichever media-key backend the user picked, debounces key presses and forwards them as commands.
/// </summary>
internal sealed class MediaKeyManager : IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(250);

    private readonly Dispatcher _dispatcher;
    private readonly MessageWindow _window;
    private readonly HotkeyService _hotkeys;
    private readonly Func<bool> _canHandle;
    private IMediaKeyBackend? _backend;
    private SmtcMediaKeys? _smtc;
    private MediaKeyCommand? _lastCommand;
    private DateTime _lastAt;

    public MediaKeyManager(Dispatcher dispatcher, MessageWindow window, HotkeyService hotkeys, Func<bool> canHandle)
    {
        _dispatcher = dispatcher;
        _window = window;
        _hotkeys = hotkeys;
        _canHandle = canHandle;
    }

    public event EventHandler<MediaKeyCommand>? CommandReceived;

    /// <summary>Human-readable status for the settings window / diagnostics.</summary>
    public string Status { get; private set; } = "Off";

    public void Apply(MediaKeyMode mode, bool captureVolumeKeys)
    {
        _backend?.Dispose();
        _backend = null;
        _smtc = null;

        IMediaKeyBackend? backend = mode switch
        {
            MediaKeyMode.Smtc => _smtc = new SmtcMediaKeys(_window.Handle, _dispatcher, Raise),
            MediaKeyMode.Hotkey => new HotkeyMediaKeys(_hotkeys, Raise),
            MediaKeyMode.Hook => new HookMediaKeys(_dispatcher, Raise, _canHandle, captureVolumeKeys),
            _ => null,
        };

        if (backend is null)
        {
            Status = "Off";
            return;
        }

        if (backend.Start())
        {
            _backend = backend;
            Status = mode.ToString();
            Log.Info($"Media keys: {mode} backend active.");
            return;
        }

        backend.Dispose();
        _smtc = null;
        Status = $"{mode} unavailable";
        Log.Warn($"Media keys: {mode} backend failed to start.");
    }

    /// <summary>Keeps the Windows media flyout (SMTC) in sync with what's playing.</summary>
    public void UpdateDisplay(NowPlaying nowPlaying, byte[]? artwork) => _smtc?.UpdateDisplay(nowPlaying, artwork);

    public void Dispose()
    {
        _backend?.Dispose();
        _backend = null;
        _smtc = null;
    }

    private void Raise(MediaKeyCommand command)
    {
        var now = DateTime.UtcNow;
        var repeatable = command is MediaKeyCommand.VolumeUp or MediaKeyCommand.VolumeDown;
        if (!repeatable && command == _lastCommand && now - _lastAt < Debounce)
        {
            return;
        }

        _lastCommand = command;
        _lastAt = now;
        CommandReceived?.Invoke(this, command);
    }
}

/// <summary>
/// System Media Transport Controls: the "proper" Windows integration. Media keys, headset buttons and
/// the volume flyout all route here, and the flyout shows our cover art.
/// </summary>
internal sealed class SmtcMediaKeys : IMediaKeyBackend
{
    private readonly IntPtr _hwnd;
    private readonly Dispatcher _dispatcher;
    private readonly Action<MediaKeyCommand> _raise;
    private SystemMediaTransportControls? _controls;
    private string? _lastDisplayKey;
    private byte[]? _lastArtwork;
    private int _displayVersion;

    public SmtcMediaKeys(IntPtr hwnd, Dispatcher dispatcher, Action<MediaKeyCommand> raise)
    {
        _hwnd = hwnd;
        _dispatcher = dispatcher;
        _raise = raise;
    }

    public bool Start()
    {
        try
        {
            _controls = SystemMediaTransportControlsInterop.GetForWindow(_hwnd);
            _controls.IsEnabled = true;
            _controls.IsPlayEnabled = true;
            _controls.IsPauseEnabled = true;
            _controls.IsNextEnabled = true;
            _controls.IsPreviousEnabled = true;
            _controls.IsStopEnabled = true;
            _controls.PlaybackStatus = MediaPlaybackStatus.Closed;
            _controls.ButtonPressed += OnButtonPressed;
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("SMTC could not be initialised.", ex);
            _controls = null;
            return false;
        }
    }

    public void UpdateDisplay(NowPlaying nowPlaying, byte[]? artwork)
    {
        if (_controls is null)
        {
            return;
        }

        try
        {
            _controls.PlaybackStatus = !nowPlaying.HasMedia
                ? MediaPlaybackStatus.Stopped
                : nowPlaying.IsPlaying ? MediaPlaybackStatus.Playing : MediaPlaybackStatus.Paused;

            var key = $"{nowPlaying.Title}|{nowPlaying.Artist}|{nowPlaying.Album}";
            if (key != _lastDisplayKey)
            {
                _lastDisplayKey = key;
                _lastArtwork = null;
                var updater = _controls.DisplayUpdater;
                updater.ClearAll();
                if (nowPlaying.HasMedia)
                {
                    updater.Type = MediaPlaybackType.Music;
                    updater.MusicProperties.Title = nowPlaying.Title ?? string.Empty;
                    updater.MusicProperties.Artist = nowPlaying.Artist ?? string.Empty;
                    updater.MusicProperties.AlbumTitle = nowPlaying.Album ?? string.Empty;
                }

                updater.Update();
                _displayVersion++;
            }

            if (artwork is { Length: > 0 } && !ReferenceEquals(artwork, _lastArtwork))
            {
                _lastArtwork = artwork;
                _ = SetThumbnailAsync(artwork, ++_displayVersion);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("SMTC display update failed.", ex);
        }
    }

    public void Dispose()
    {
        if (_controls is null)
        {
            return;
        }

        try
        {
            _controls.ButtonPressed -= OnButtonPressed;
            _controls.DisplayUpdater.ClearAll();
            _controls.DisplayUpdater.Update();
            _controls.IsEnabled = false;
        }
        catch (Exception ex)
        {
            Log.Warn("SMTC shutdown failed.", ex);
        }

        _controls = null;
    }

    private async Task SetThumbnailAsync(byte[] artwork, int version)
    {
        try
        {
            var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(artwork);
                await writer.StoreAsync();
                await writer.FlushAsync();
                writer.DetachStream();
            }

            if (_controls is null || version != _displayVersion)
            {
                stream.Dispose();
                return;
            }

            _controls.DisplayUpdater.Thumbnail = RandomAccessStreamReference.CreateFromStream(stream);
            _controls.DisplayUpdater.Update();
        }
        catch (Exception ex)
        {
            Log.Warn("SMTC thumbnail update failed.", ex);
        }
    }

    private void OnButtonPressed(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs args)
    {
        MediaKeyCommand? command = args.Button switch
        {
            SystemMediaTransportControlsButton.Play => MediaKeyCommand.Play,
            SystemMediaTransportControlsButton.Pause => MediaKeyCommand.Pause,
            SystemMediaTransportControlsButton.Next => MediaKeyCommand.Next,
            SystemMediaTransportControlsButton.Previous => MediaKeyCommand.Previous,
            SystemMediaTransportControlsButton.Stop => MediaKeyCommand.Stop,
            _ => null,
        };

        // SMTC raises this on a background thread.
        if (command is { } c)
        {
            _dispatcher.BeginInvoke(() => _raise(c));
        }
    }
}

/// <summary>Registers the media keys as global hotkeys. Simple, but other players lose them while we run.</summary>
internal sealed class HotkeyMediaKeys : IMediaKeyBackend
{
    private const string Group = "mediakeys";
    private readonly HotkeyService _hotkeys;
    private readonly Action<MediaKeyCommand> _raise;

    public HotkeyMediaKeys(HotkeyService hotkeys, Action<MediaKeyCommand> raise)
    {
        _hotkeys = hotkeys;
        _raise = raise;
    }

    public bool Start()
    {
        var any = false;
        any |= _hotkeys.Register(Group, new Hotkey(HotkeyModifiers.None, 0xB3), () => _raise(MediaKeyCommand.PlayPause));
        any |= _hotkeys.Register(Group, new Hotkey(HotkeyModifiers.None, 0xB0), () => _raise(MediaKeyCommand.Next));
        any |= _hotkeys.Register(Group, new Hotkey(HotkeyModifiers.None, 0xB1), () => _raise(MediaKeyCommand.Previous));
        any |= _hotkeys.Register(Group, new Hotkey(HotkeyModifiers.None, 0xB2), () => _raise(MediaKeyCommand.Stop));
        return any;
    }

    public void Dispose() => _hotkeys.Clear(Group);
}

/// <summary>
/// Low-level keyboard hook. Only swallows keys while we're connected with a speaker selected, so the
/// keys keep working for other apps otherwise. Optionally captures the volume keys too.
/// </summary>
internal sealed class HookMediaKeys : IMediaKeyBackend
{
    private readonly Dispatcher _dispatcher;
    private readonly Action<MediaKeyCommand> _raise;
    private readonly Func<bool> _canHandle;
    private readonly bool _captureVolume;
    private readonly NativeMethods.LowLevelKeyboardProc _proc;
    private IntPtr _hook;

    public HookMediaKeys(Dispatcher dispatcher, Action<MediaKeyCommand> raise, Func<bool> canHandle, bool captureVolume)
    {
        _dispatcher = dispatcher;
        _raise = raise;
        _canHandle = canHandle;
        _captureVolume = captureVolume;
        _proc = HookProc;
    }

    public bool Start()
    {
        _hook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, _proc, NativeMethods.GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero)
        {
            Log.Warn($"Keyboard hook failed (error {Marshal.GetLastWin32Error()}).");
            return false;
        }

        return true;
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var message = wParam.ToInt32();
            var info = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
            MediaKeyCommand? command = info.vkCode switch
            {
                0xB3 => MediaKeyCommand.PlayPause,
                0xB0 => MediaKeyCommand.Next,
                0xB1 => MediaKeyCommand.Previous,
                0xB2 => MediaKeyCommand.Stop,
                0xAF when _captureVolume => MediaKeyCommand.VolumeUp,
                0xAE when _captureVolume => MediaKeyCommand.VolumeDown,
                0xAD when _captureVolume => MediaKeyCommand.Mute,
                _ => null,
            };

            // The hook runs on the UI thread's message loop, so reading state here is safe; it must stay fast.
            if (command is { } c && _canHandle())
            {
                if (message is NativeMethods.WM_KEYDOWN or NativeMethods.WM_SYSKEYDOWN)
                {
                    _dispatcher.BeginInvoke(() => _raise(c));
                }

                return new IntPtr(1);
            }
        }

        return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
    }
}
