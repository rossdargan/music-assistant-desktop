using System.Diagnostics;
using System.Windows.Threading;
using MaMini.Core.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace MaMini.App.Services;

internal sealed class TeamsCallDetector : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly HashSet<string> TeamsProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Teams",
        "ms-teams",
        "msteams",
    };
    private readonly DispatcherTimer _timer;
    private bool _enabled;
    private bool _reportedActive;
    private int _consecutiveActivePolls;
    private int _consecutiveInactivePolls;
    private int _generation;
    private int _polling;
    private bool _loggedPollFailure;

    public TeamsCallDetector(Dispatcher dispatcher)
    {
        _timer = new DispatcherTimer(PollInterval, DispatcherPriority.Background, OnTimerTick, dispatcher)
        {
            IsEnabled = false,
        };
    }

    public event EventHandler<bool>? CallActiveChanged;

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value)
            {
                return;
            }

            _enabled = value;
            _generation++;
            _consecutiveActivePolls = 0;
            _consecutiveInactivePolls = 0;
            _reportedActive = false;
            if (value)
            {
                _timer.Start();
                OnTimerTick(this, EventArgs.Empty);
            }
            else
            {
                _timer.Stop();
            }
        }
    }

    public void Dispose() => _timer.Stop();

    private async void OnTimerTick(object? sender, EventArgs e)
    {
        if (!_enabled || Interlocked.Exchange(ref _polling, 1) != 0)
        {
            return;
        }

        try
        {
            var generation = _generation;
            var active = await Task.Run(IsTeamsAudioActive);
            _loggedPollFailure = false;
            if (!_enabled || generation != _generation)
            {
                return;
            }

            _consecutiveActivePolls = active ? _consecutiveActivePolls + 1 : 0;
            _consecutiveInactivePolls = active ? 0 : _consecutiveInactivePolls + 1;
            var callActive = _reportedActive
                ? _consecutiveInactivePolls < 5
                : _consecutiveActivePolls >= 2;
            if (callActive != _reportedActive)
            {
                _reportedActive = callActive;
                CallActiveChanged?.Invoke(this, callActive);
            }
        }
        catch (Exception ex)
        {
            if (!_loggedPollFailure)
            {
                Log.Warn("Could not check for active Microsoft Teams audio sessions.", ex);
                _loggedPollFailure = true;
            }
        }
        finally
        {
            Volatile.Write(ref _polling, 0);
        }
    }

    private static bool IsTeamsAudioActive()
    {
        using var enumerator = new MMDeviceEnumerator();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.All, DeviceState.Active))
        {
            using (device)
            {
                var sessions = device.AudioSessionManager.Sessions;
                for (var i = 0; i < sessions.Count; i++)
                {
                    using var session = sessions[i];
                    var processId = session.GetProcessID;
                    if (session.State != AudioSessionState.AudioSessionStateActive || processId is 0 or > int.MaxValue)
                    {
                        continue;
                    }

                    try
                    {
                        using var process = Process.GetProcessById((int)processId);
                        if (TeamsProcessNames.Contains(process.ProcessName))
                        {
                            return true;
                        }
                    }
                    catch (ArgumentException)
                    {
                        // The process may exit between session enumeration and lookup.
                    }
                }
            }
        }

        return false;
    }
}
