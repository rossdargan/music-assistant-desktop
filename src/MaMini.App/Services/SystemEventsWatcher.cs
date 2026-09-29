using System.Net.NetworkInformation;
using System.Windows.Threading;
using MaMini.Core.Diagnostics;
using Microsoft.Win32;

namespace MaMini.App.Services;

/// <summary>
/// Turns sleep/resume and network changes into debounced events on the UI thread, so the connection
/// recovers promptly instead of waiting on a dead socket.
/// </summary>
internal sealed class SystemEventsWatcher : IDisposable
{
    private readonly DispatcherTimer _resumeTimer;
    private readonly DispatcherTimer _networkTimer;
    private readonly Dispatcher _dispatcher;

    public SystemEventsWatcher(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _resumeTimer = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Normal, OnResumeTick, dispatcher) { IsEnabled = false };
        _networkTimer = new DispatcherTimer(TimeSpan.FromSeconds(3), DispatcherPriority.Normal, OnNetworkTick, dispatcher) { IsEnabled = false };

        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;
        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
    }

    /// <summary>The machine woke up: the socket is probably dead, reconnect.</summary>
    public event EventHandler? Resumed;

    /// <summary>The network changed: probe the connection.</summary>
    public event EventHandler? NetworkChanged;

    public void Dispose()
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
        _resumeTimer.Stop();
        _networkTimer.Stop();
    }

    private static void Restart(DispatcherTimer timer)
    {
        timer.Stop();
        timer.Start();
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
        {
            Log.Info("System resumed.");
            _dispatcher.BeginInvoke(() => Restart(_resumeTimer));
        }
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.RemoteConnect or SessionSwitchReason.ConsoleConnect)
        {
            _dispatcher.BeginInvoke(() => Restart(_networkTimer));
        }
    }

    private void OnNetworkChanged(object? sender, EventArgs e) => _dispatcher.BeginInvoke(() => Restart(_networkTimer));

    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e) => _dispatcher.BeginInvoke(() => Restart(_networkTimer));

    private void OnResumeTick(object? sender, EventArgs e)
    {
        _resumeTimer.Stop();
        _networkTimer.Stop();
        Resumed?.Invoke(this, EventArgs.Empty);
    }

    private void OnNetworkTick(object? sender, EventArgs e)
    {
        _networkTimer.Stop();
        Log.Info("Network changed; checking the connection.");
        NetworkChanged?.Invoke(this, EventArgs.Empty);
    }
}
