namespace Clipping.Core.Encoding;

/// <summary>
/// One compressed video frame, independent of the encoder that produced it.
/// </summary>
/// <param name="Timestamp">When the frame was captured, on the same clock as <see cref="Capture.CapturedFrame.Timestamp"/>.</param>
/// <param name="Duration">How long the frame is shown.</param>
/// <param name="IsKeyframe">
/// Whether the frame decodes on its own. A clip must start on a keyframe.
/// </param>
/// <param name="Width">Width of the video in pixels.</param>
/// <param name="Height">Height of the video in pixels.</param>
/// <param name="Data">The compressed H.264 bytes, in Annex B format.</param>
public sealed record EncodedSample(
    TimeSpan Timestamp,
    TimeSpan Duration,
    bool IsKeyframe,
    int Width,
    int Height,
    byte[] Data)
{
    /// <summary>Cached Annex B SPS/PPS for this encoder session, even after its first GOP is evicted.</summary>
    public byte[]? SequenceHeader { get; init; }
}
