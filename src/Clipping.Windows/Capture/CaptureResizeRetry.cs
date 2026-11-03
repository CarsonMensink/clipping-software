namespace Clipping.Windows.Capture;

/// <summary>
/// Runs capture resizes, waiting a retry interval after a failure before trying again.
/// </summary>
/// <remarks>
/// Without the wait, persistent GPU memory pressure or a failing device would retry the
/// resize, and log its failure, on every frame. Not thread-safe; the caller serializes calls.
/// </remarks>
internal sealed class CaptureResizeRetry
{
    private readonly TimeProvider _time;
    private readonly TimeSpan _retryDelay;
    private readonly Action<Exception> _reportFailure;
    private long? _failedAt;

    /// <param name="retryDelay">How long to skip resizes after one fails.</param>
    /// <param name="reportFailure">Called once per failed attempt.</param>
    public CaptureResizeRetry(TimeSpan retryDelay, Action<Exception> reportFailure, TimeProvider? timeProvider = null)
    {
        _retryDelay = retryDelay;
        _reportFailure = reportFailure;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Attempts the resize unless a recent failure is still backing off.
    /// </summary>
    /// <returns><see langword="true"/> if the replacement was installed.</returns>
    public bool TryReplace<T>(ref T? current, Func<T> create, Action recreate) where T : class, IDisposable
    {
        if (_failedAt is { } failedAt && _time.GetElapsedTime(failedAt) < _retryDelay)
        {
            return false;
        }

        try
        {
            CaptureResize.Replace(ref current, create, recreate);
        }
        catch (Exception failure)
        {
            _failedAt = _time.GetTimestamp();
            _reportFailure(failure);
            return false;
        }

        _failedAt = null;
        return true;
    }
}
