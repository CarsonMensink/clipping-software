namespace Clipping.Core.Audio;

/// <summary>
/// Lays captured packets out on the clip's timeline as one continuous track.
/// </summary>
/// <remarks>
/// Each packet goes where its timestamp says, so silence (when the device sent nothing) stays
/// silence and later sound is never pulled earlier. Timestamps jitter slightly, and the audio
/// clock drifts slowly from the video clock, so a packet that lands within
/// <see cref="JitterTolerance"/> of where the previous one ended is joined on directly instead.
/// That keeps playback free of clicks while holding audio within that tolerance of the video:
/// once drift exceeds it, the next packet goes back to its own timestamp.
/// </remarks>
public static class AudioTimeline
{
    /// <summary>
    /// How far a packet may sit from the end of the previous one and still be joined onto it.
    /// </summary>
    /// <remarks>
    /// Well under the roughly 45 ms at which viewers notice audio leading or trailing video.
    /// </remarks>
    public static readonly TimeSpan JitterTolerance = TimeSpan.FromMilliseconds(10);

    /// <summary>
    /// Renders the audio from <paramref name="start"/> to <paramref name="end"/>.
    /// </summary>
    /// <param name="format">The track's format. Packets in any other format are left out.</param>
    /// <param name="packets">Packets in capture order. Those outside the range are trimmed away.</param>
    /// <param name="start">When the track starts: the clip's first video frame.</param>
    /// <param name="end">When the track ends: the end of the clip's last video frame.</param>
    public static AudioTrack Render(AudioFormat format, IEnumerable<AudioPacket> packets, TimeSpan start, TimeSpan end)
    {
        ArgumentNullException.ThrowIfNull(format);
        ArgumentNullException.ThrowIfNull(packets);

        var channels = format.Channels;
        var length = Math.Max(format.FramesIn(end - start), 0);
        var samples = new short[checked(length * channels)];
        var tolerance = format.FramesIn(JitterTolerance);

        long? cursor = null;
        foreach (var packet in packets)
        {
            if (packet.Format != format)
            {
                continue;
            }

            var ideal = format.FramesIn(packet.Timestamp - start);
            var at = cursor is { } previousEnd && Math.Abs(ideal - previousEnd) <= tolerance ? previousEnd : ideal;
            cursor = at + packet.Frames;

            var from = Math.Max(at, 0);
            var to = Math.Min(at + packet.Frames, length);
            if (from < to)
            {
                Array.Copy(packet.Samples, (from - at) * channels, samples, from * channels, (to - from) * channels);
            }
        }

        return new AudioTrack(format, samples);
    }
}
