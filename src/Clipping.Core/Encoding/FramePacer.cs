namespace Clipping.Core.Encoding;

/// <summary>
/// Places captured frames on a constant-frame-rate timeline, repeating the previous frame
/// wherever the screen sent nothing.
/// </summary>
/// <remarks>
/// The timeline is a grid of slots one frame interval apart, starting at the first frame.
/// Each frame takes the slot nearest its timestamp; every slot it skipped over gets a repeat
/// of the frame before it. A frame whose nearest slot is already taken moves to the next one,
/// unless that is a whole interval or more ahead of its timestamp, in which case it is
/// dropped, so the timeline never runs ahead of real time.
/// <para>
/// While the screen is idle no frames arrive, so the caller also calls
/// <see cref="FillUntil"/> regularly to repeat the last frame as time passes, and once more
/// before saving.
/// </para>
/// <para>
/// Works on timestamps only; it never holds frames. Not thread-safe; the caller serializes
/// calls.
/// </para>
/// </remarks>
public sealed class FramePacer
{
    private readonly int _frameRate;
    private TimeSpan? _origin;
    private long _lastSlot = -1;

    /// <param name="frameRate">Frames per second of the timeline.</param>
    public FramePacer(int frameRate)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frameRate);

        _frameRate = frameRate;
    }

    /// <summary>
    /// Places a newly captured frame.
    /// </summary>
    /// <param name="timestamp">When the frame was captured.</param>
    /// <returns>
    /// Times at which to repeat the previous frame, oldest first, then the time for this frame,
    /// or <see langword="null"/> to drop it. Repeats are still owed when it is dropped.
    /// </returns>
    public FramePlacement Place(TimeSpan timestamp)
    {
        if (_origin is not { } origin)
        {
            _origin = timestamp;
            _lastSlot = 0;
            return new FramePlacement([], timestamp);
        }

        // In units of 1/(frameRate) ticks, so one slot is exactly TicksPerSecond.
        var position = (timestamp - origin).Ticks * _frameRate;
        var nearest = FloorDiv(position + TimeSpan.TicksPerSecond / 2, TimeSpan.TicksPerSecond);
        var slot = Math.Max(nearest, _lastSlot + 1);

        if (slot * TimeSpan.TicksPerSecond - position >= TimeSpan.TicksPerSecond)
        {
            // Every slot near this frame is taken; it arrived too soon after the last one.
            return new FramePlacement([], null);
        }

        var repeats = SlotTimes(_lastSlot + 1, slot);
        _lastSlot = slot;
        return new FramePlacement(repeats, SlotTime(slot));
    }

    /// <summary>
    /// Repeats the last frame for every empty slot up to <paramref name="time"/>.
    /// </summary>
    /// <param name="time">
    /// Fill slots at or before this time. Leave a margin before the present for frames still
    /// on their way, or a late frame finds its slot already filled and is dropped.
    /// </param>
    /// <returns>Times at which to repeat the last frame, oldest first; empty before the first frame.</returns>
    public IReadOnlyList<TimeSpan> FillUntil(TimeSpan time)
    {
        if (_origin is not { } origin)
        {
            return [];
        }

        // The last slot whose time, rounded down to a whole tick by SlotTime, is at or before time.
        var last = FloorDiv(((time - origin).Ticks + 1) * _frameRate - 1, TimeSpan.TicksPerSecond);
        if (last <= _lastSlot)
        {
            return [];
        }

        var repeats = SlotTimes(_lastSlot + 1, last + 1);
        _lastSlot = last;
        return repeats;
    }

    private TimeSpan[] SlotTimes(long from, long to)
    {
        var times = new TimeSpan[Math.Max(to - from, 0)];
        for (var i = 0; i < times.Length; i++)
        {
            times[i] = SlotTime(from + i);
        }

        return times;
    }

    // Computed from the slot number each time, so rounding never accumulates.
    private TimeSpan SlotTime(long slot) =>
        _origin!.Value + TimeSpan.FromTicks(slot * TimeSpan.TicksPerSecond / _frameRate);

    private static long FloorDiv(long value, long divisor) =>
        value >= 0 ? value / divisor : -((-value + divisor - 1) / divisor);
}

/// <summary>
/// Where a captured frame goes on the timeline.
/// </summary>
/// <param name="Repeats">Times at which to repeat the previous frame first, oldest first.</param>
/// <param name="Time">The time for this frame, or <see langword="null"/> to drop it.</param>
public readonly record struct FramePlacement(IReadOnlyList<TimeSpan> Repeats, TimeSpan? Time);
