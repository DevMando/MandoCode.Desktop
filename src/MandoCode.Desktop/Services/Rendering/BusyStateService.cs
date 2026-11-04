namespace MandoCode.Desktop.Services;

/// <summary>
/// The WinUI replacement for SpinnerService's terminal spinner. Start/Stop/Update
/// mirror the CLI's spinner lifecycle calls; MainWindow subscribes to show a
/// ProgressRing plus the activity line. Thread-safe; events may fire on any thread.
/// </summary>
public sealed class BusyStateService
{
    private readonly object _gate = new();
    private int _depth;
    private string? _activity;

    /// <summary>(isBusy, activityText) — activityText may be null for a plain "working" state.</summary>
    public event Action<bool, string?>? Changed;

    public bool IsBusy { get { lock (_gate) return _depth > 0; } }

    public void Start(string? activity = null)
    {
        lock (_gate)
        {
            _depth++;
            _activity = activity ?? _activity;
        }
        Changed?.Invoke(true, activity);
    }

    public void Update(string? activity)
    {
        bool busy;
        lock (_gate)
        {
            _activity = activity;
            busy = _depth > 0;
        }
        if (busy) Changed?.Invoke(true, activity);
    }

    public void Stop()
    {
        bool stillBusy;
        string? activity;
        lock (_gate)
        {
            _depth = Math.Max(0, _depth - 1);
            stillBusy = _depth > 0;
            if (!stillBusy) _activity = null;
            activity = _activity;
        }
        Changed?.Invoke(stillBusy, activity);
    }

    /// <summary>Force-clears the busy state regardless of nesting (end-of-request cleanup).</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _depth = 0;
            _activity = null;
        }
        Changed?.Invoke(false, null);
    }
}
