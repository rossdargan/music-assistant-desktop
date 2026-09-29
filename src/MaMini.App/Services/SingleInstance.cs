namespace MaMini.App.Services;

/// <summary>Per-user single-instance guard; a second launch signals the first to show its widget.</summary>
internal sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _showEvent;
    private readonly string _name;
    private RegisteredWaitHandle? _registration;
    private bool _owned;

    public SingleInstance(string name)
    {
        _name = name;
        _mutex = new Mutex(false, $@"Local\{name}");
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\{name}-show");
    }

    public event EventHandler? ShowRequested;

    public bool TryAcquire()
    {
        try
        {
            _owned = _mutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            // The previous instance crashed; we own it now.
            _owned = true;
        }

        if (_owned)
        {
            _registration = ThreadPool.RegisterWaitForSingleObject(
                _showEvent,
                (_, _) => ShowRequested?.Invoke(this, EventArgs.Empty),
                null,
                Timeout.Infinite,
                executeOnlyOnce: false);
        }

        return _owned;
    }

    public void SignalExisting() => _showEvent.Set();

    public void Dispose()
    {
        _registration?.Unregister(null);
        if (_owned)
        {
            _mutex.ReleaseMutex();
        }

        _mutex.Dispose();
        _showEvent.Dispose();
    }

    public override string ToString() => _name;
}
