using Clipping.Core.Display;

namespace Clipping.Core.Capture;

/// <summary>
/// Keeps an <see cref="IFrameSource"/> capturing whichever monitor an
/// <see cref="IDisplayTarget"/> has selected.
/// </summary>
/// <remarks>
/// While started, restarts capture whenever the selection changes: another monitor is
/// chosen, the selected one is unplugged, or its bounds change. Does not own the display
/// target or the frame source; disposing only stops capture and unsubscribes.
/// </remarks>
public sealed class CaptureCoordinator : IDisposable
{
    /// <summary>
    /// How long capture waits after the last selection change before restarting.
    /// </summary>
    /// <remarks>
    /// Windows keeps rebuilding monitors for a few hundred milliseconds after it reports a
    /// display change. A capture started in that window stays tied to the old monitor and
    /// stops delivering frames without reporting an error.
    /// </remarks>
    public static readonly TimeSpan RestartDelay = TimeSpan.FromSeconds(1);

    private readonly IDisplayTarget _target;
    private readonly IFrameSource _source;
    private readonly ITimer _restartTimer;

    // Serializes Start/Stop with SelectionChanged and the restart timer, which run on any thread.
    private readonly Lock _lock = new();

    // True between Start and Stop, including while a restart is pending or has failed.
    private bool _started;
    private bool _restartPending;
    private bool _disposed;

    /// <param name="target">Decides which monitor to capture.</param>
    /// <param name="source">Captures the monitor.</param>
    /// <param name="timeProvider">Times <see cref="RestartDelay"/>. Defaults to the system clock.</param>
    public CaptureCoordinator(IDisplayTarget target, IFrameSource source, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(source);

        _target = target;
        _source = source;
        _restartTimer = (timeProvider ?? TimeProvider.System).CreateTimer(
            _ => OnRestartDelayElapsed(), state: null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _target.SelectionChanged += OnSelectionChanged;
    }

    /// <summary>
    /// Starts capturing the selected monitor. Does nothing if already started.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The selected monitor disappeared before capture could start.
    /// </exception>
    public void Start()
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_started)
            {
                return;
            }

            _source.StartCapture(_target.GetSelectedMonitor());
            _started = true;
        }
    }

    /// <summary>
    /// Stops capturing and cancels any pending restart. Does nothing if not started.
    /// </summary>
    public void Stop()
    {
        lock (_lock)
        {
            if (!_started)
            {
                return;
            }

            _restartTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _restartPending = false;
            _source.StopCapture();
            _started = false;
        }
    }

    /// <summary>
    /// Stops capturing and stops following selection changes.
    /// </summary>
    public void Dispose()
    {
        _target.SelectionChanged -= OnSelectionChanged;

        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        Stop();
        _restartTimer.Dispose();
    }

    private void OnSelectionChanged(object? sender, MonitorInfo e)
    {
        lock (_lock)
        {
            if (!_started || _disposed)
            {
                return;
            }

            // Stop now so nothing is captured from a monitor that is being rebuilt, and
            // restart once changes have settled. Each change pushes the restart back, so a
            // burst of changes restarts once.
            _source.StopCapture();
            _restartPending = true;
            _restartTimer.Change(RestartDelay, Timeout.InfiniteTimeSpan);
        }
    }

    private void OnRestartDelayElapsed()
    {
        lock (_lock)
        {
            if (!_started || !_restartPending || _disposed)
            {
                return;
            }

            _restartPending = false;

            // Events raised on different threads can arrive out of order, so restart on the
            // latest selection rather than the one any single event carried.
            try
            {
                _source.StartCapture(_target.GetSelectedMonitor());
            }
            catch (ArgumentException)
            {
                // The monitor is gone even after settling. DisplayTarget raises
                // SelectionChanged again when it falls back to another, which retries;
                // capture stays stopped until then.
            }
        }
    }
}
