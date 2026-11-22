namespace Clipping.Core.Audio;

/// <summary>
/// The layout of uncompressed audio: interleaved signed 16-bit samples.
/// </summary>
/// <param name="SampleRate">Frames per second, such as 48000.</param>
/// <param name="Channels">Samples per frame, such as 2 for stereo.</param>
public sealed record AudioFormat(int SampleRate, int Channels)
{
    /// <summary>
    /// 48 kHz stereo, the format audio is captured, buffered and encoded in.
    /// </summary>
    public static AudioFormat Default { get; } = new(48_000, 2);

    /// <summary>
    /// The number of whole frames closest to <paramref name="duration"/>.
    /// </summary>
    public long FramesIn(TimeSpan duration) =>
        (long)Math.Round(duration.Ticks * (double)SampleRate / TimeSpan.TicksPerSecond, MidpointRounding.AwayFromZero);

    /// <summary>
    /// How long <paramref name="frames"/> frames play for.
    /// </summary>
    public TimeSpan DurationOf(long frames) => TimeSpan.FromTicks(frames * TimeSpan.TicksPerSecond / SampleRate);
}
