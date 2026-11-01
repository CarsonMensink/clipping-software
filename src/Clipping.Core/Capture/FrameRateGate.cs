namespace Clipping.Core.Capture;

/// <summary>
/// Thins incoming frames down to a target frame rate.
/// </summary>
/// <remarks>
/// Keeps a schedule of due times one frame interval apart, and passes the first frame at or
/// after each due time. When frames arrive faster than the frame rate, such as from a
/// 240 Hz monitor, the extras are skipped. When they arrive slower, or the screen is idle,
/// every frame passes; the gate cannot fill the gaps, so that is left to whoever repeats
/// frames.
/// <para>
/// A frame up to half an interval early still passes, so a monitor refreshing at exactly
/// the frame rate is not thinned by jitter. The schedule advances by whole intervals rather
/// than from each frame's timestamp, so the average rate never drifts above the target.
/// </para>
/// <para>
/// Not thread-safe; the caller serializes calls.
/// </para>
/// </remarks>
public sealed class FrameRateGate
{
    // Times are kept in ticks multiplied by the frame rate, which makes one interval exactly
    // TimeSpan.TicksPerSecond; 1/30 s is not a whole number of ticks.
    private const long Interval = TimeSpan.TicksPerSecond;
    private const long EarlyTolerance = Interval / 2;

    private readonly int _frameRate;
    private long? _nextDue;

    /// <param name="frameRate">Frames per second to let through.</param>
    public FrameRateGate(int frameRate)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frameRate);

        _frameRate = frameRate;
    }

    /// <summary>
    /// Decides whether a frame passes.
    /// </summary>
    /// <param name="timestamp">When the frame was captured, on a monotonic clock.</param>
    /// <returns>
    /// <see langword="true"/> if the frame is due; <see langword="false"/> if it came too
    /// soon after the last frame that passed.
    /// </returns>
    public bool TryPass(TimeSpan timestamp)
    {
        var time = timestamp.Ticks * _frameRate;

        if (_nextDue is not { } due)
        {
            _nextDue = time + Interval;
            return true;
        }

        if (time < due - EarlyTolerance)
        {
            return false;
        }

        // A whole interval or more behind schedule means frames stopped coming for a while.
        // Restart the schedule from this frame instead of passing a burst to catch up.
        _nextDue = time - due >= Interval ? time + Interval : due + Interval;
        return true;
    }
}
