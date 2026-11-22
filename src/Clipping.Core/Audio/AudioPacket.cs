namespace Clipping.Core.Audio;

/// <summary>
/// A run of captured audio, as delivered by the device.
/// </summary>
/// <param name="Timestamp">
/// When the first frame was played, on the same clock as
/// <see cref="Capture.CapturedFrame.Timestamp"/>, so audio and video line up.
/// </param>
/// <param name="Format">The layout of <paramref name="Samples"/>.</param>
/// <param name="Samples">Interleaved samples; a whole number of frames.</param>
public sealed record AudioPacket(TimeSpan Timestamp, AudioFormat Format, short[] Samples)
{
    /// <summary>
    /// The number of frames, each holding one sample per channel.
    /// </summary>
    public int Frames => Samples.Length / Format.Channels;

    /// <summary>
    /// When the last frame finishes playing.
    /// </summary>
    public TimeSpan End => Timestamp + Format.DurationOf(Frames);
}
