using Clipping.Core.Encoding;

namespace Clipping.Core.Clips;

/// <summary>
/// Holds the most recent encoded video in memory, in whole groups of pictures (GOPs).
/// </summary>
/// <remarks>
/// A GOP is a keyframe and the frames after it up to the next keyframe; it is the smallest
/// piece that decodes on its own, so video is only ever evicted a whole GOP at a time. The
/// oldest GOP goes once the GOPs after it cover <see cref="Retention"/> by themselves, so the
/// buffer holds between <see cref="Retention"/> and one keyframe interval more.
/// <para>
/// Eviction goes by timestamp rather than by count, since frames in a GOP vary in size and
/// a gap can leave a GOP short. A change in frame size starts the buffer over, because one
/// clip can't change resolution partway through. Frames before the first keyframe are
/// dropped.
/// </para>
/// <para>
/// Samples are immutable, so a snapshot only copies references, under a lock held for
/// microseconds; writing it out never blocks <see cref="Add"/>.
/// </para>
/// </remarks>
public sealed class RollingClipBuffer : IClipBuffer
{
    private readonly Lock _lock = new();
    private readonly LinkedList<List<EncodedSample>> _gops = new();
    private TimeSpan _retention;

    /// <param name="retention">How much video to keep.</param>
    public RollingClipBuffer(TimeSpan retention)
    {
        Retention = retention;
    }

    /// <inheritdoc/>
    public TimeSpan Retention
    {
        get
        {
            lock (_lock)
            {
                return _retention;
            }
        }

        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value, TimeSpan.Zero);

            lock (_lock)
            {
                _retention = value;
            }
        }
    }

    /// <inheritdoc/>
    public TimeSpan RetainedDuration
    {
        get
        {
            lock (_lock)
            {
                return _gops.Count == 0 ? TimeSpan.Zero : End() - _gops.First!.Value[0].Timestamp;
            }
        }
    }

    /// <inheritdoc/>
    public void Add(EncodedSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);

        lock (_lock)
        {
            if (_gops.Last is { } newest)
            {
                var previous = newest.Value[^1];
                if (sample.Width != previous.Width || sample.Height != previous.Height)
                {
                    _gops.Clear();
                }
            }

            if (sample.IsKeyframe)
            {
                _gops.AddLast([sample]);
            }
            else if (_gops.Last is { } current)
            {
                current.Value.Add(sample);
            }
            else
            {
                return;
            }

            while (_gops.First!.Next is { } second && End() - second.Value[0].Timestamp >= _retention)
            {
                _gops.RemoveFirst();
            }
        }
    }

    /// <inheritdoc/>
    public ClipSnapshot Snapshot()
    {
        lock (_lock)
        {
            var samples = new List<EncodedSample>(_gops.Sum(gop => gop.Count));
            foreach (var gop in _gops)
            {
                samples.AddRange(gop);
            }

            return new ClipSnapshot(samples);
        }
    }

    /// <inheritdoc/>
    public ClipSnapshot SnapshotAndClear()
    {
        lock (_lock)
        {
            var snapshot = Snapshot();
            _gops.Clear();
            return snapshot;
        }
    }

    private TimeSpan End()
    {
        var last = _gops.Last!.Value[^1];
        return last.Timestamp + last.Duration;
    }
}
