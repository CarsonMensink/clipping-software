namespace Clipping.Core.Audio;

/// <summary>
/// Holds the most recent captured audio in memory, uncompressed, so any stretch of it can be
/// cut out exactly to match a clip's video.
/// </summary>
/// <remarks>
/// Packets older than <see cref="Retention"/> before the newest one are evicted. Reading never
/// removes anything: each clip's video starts after the previous clip's ends, so trimming to
/// the video's range is enough to keep clips from sharing audio.
/// <para>
/// Packets are immutable, so a read only copies references under the lock; rendering the
/// track happens outside it and never blocks <see cref="Add"/>.
/// </para>
/// </remarks>
public sealed class RollingAudioBuffer
{
    /// <summary>
    /// How long after a moment its audio may still be on its way from the device.
    /// </summary>
    public static readonly TimeSpan MaxLatency = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// How often a read checks whether the audio it is waiting for has arrived.
    /// </summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(10);

    private readonly Lock _lock = new();
    private readonly LinkedList<AudioPacket> _packets = new();
    private readonly TimeProvider _time;
    private TimeSpan _retention;

    /// <param name="retention">How much audio to keep.</param>
    /// <param name="timeProvider">
    /// Measures how long a read has waited, on the clock packet timestamps use. Defaults to the
    /// system clock.
    /// </param>
    public RollingAudioBuffer(TimeSpan retention, TimeProvider? timeProvider = null)
    {
        Retention = retention;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// How much audio to keep. Changing it takes effect as the next packet arrives.
    /// </summary>
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

    /// <summary>
    /// From the start of the oldest packet held to the end of the newest; zero if empty.
    /// </summary>
    public TimeSpan RetainedDuration
    {
        get
        {
            lock (_lock)
            {
                return _packets.Count == 0 ? TimeSpan.Zero : _packets.Last!.Value.End - _packets.First!.Value.Timestamp;
            }
        }
    }

    /// <summary>
    /// Adds the next captured packet, evicting audio older than <see cref="Retention"/>.
    /// </summary>
    /// <remarks>
    /// A packet in a different format from the one before it starts the buffer over.
    /// </remarks>
    public void Add(AudioPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);

        lock (_lock)
        {
            if (_packets.Last is { } newest && newest.Value.Format != packet.Format)
            {
                _packets.Clear();
            }

            _packets.AddLast(packet);
            var end = packet.End;
            while (_packets.First!.Next is not null && end - _packets.First.Value.End > _retention)
            {
                _packets.RemoveFirst();
            }
        }
    }

    /// <summary>
    /// Discards everything held.
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _packets.Clear();
        }
    }

    /// <summary>
    /// Renders the audio from <paramref name="start"/> to <paramref name="end"/> as one track,
    /// with silence wherever nothing was captured.
    /// </summary>
    /// <remarks>
    /// Audio for the last moments may still be on its way, so this first waits until a packet
    /// reaches <paramref name="end"/> or <see cref="MaxLatency"/> has passed since it. When
    /// nothing is playing, no packet ever comes, and the wait ends at that limit.
    /// </remarks>
    /// <param name="start">When the track starts, on the packets' clock.</param>
    /// <param name="end">When it ends.</param>
    /// <param name="cancellationToken">Abandons the wait.</param>
    public async Task<AudioTrack> ReadAsync(TimeSpan start, TimeSpan end, CancellationToken cancellationToken = default)
    {
        while (!Covers(end) && Now() < end + MaxLatency)
        {
            await Task.Delay(PollInterval, _time, cancellationToken).ConfigureAwait(false);
        }

        AudioPacket[] packets;
        lock (_lock)
        {
            packets = _packets.Where(packet => packet.End > start && packet.Timestamp < end).ToArray();
        }

        return AudioTimeline.Render(packets.FirstOrDefault()?.Format ?? AudioFormat.Default, packets, start, end);
    }

    private bool Covers(TimeSpan time)
    {
        lock (_lock)
        {
            return _packets.Last is { } newest && newest.Value.End >= time;
        }
    }

    // Packet timestamps count from the same origin as GetTimestamp.
    private TimeSpan Now() => _time.GetElapsedTime(0, _time.GetTimestamp());
}
