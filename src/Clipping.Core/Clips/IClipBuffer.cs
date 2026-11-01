using Clipping.Core.Encoding;

namespace Clipping.Core.Clips;

/// <summary>
/// Keeps the most recent stretch of encoded video, ready to be saved as a clip.
/// </summary>
/// <remarks>
/// Thread-safe: the encoder adds samples while the save path takes snapshots.
/// </remarks>
public interface IClipBuffer
{
    /// <summary>
    /// How much video to keep. Changing it takes effect as the next sample arrives.
    /// </summary>
    TimeSpan Retention { get; set; }

    /// <summary>
    /// How much video is held right now.
    /// </summary>
    TimeSpan RetainedDuration { get; }

    /// <summary>
    /// Adds the next encoded frame, evicting video older than <see cref="Retention"/>.
    /// </summary>
    void Add(EncodedSample sample);

    /// <summary>
    /// Copies out what the buffer holds, starting on a keyframe. Later additions don't
    /// change the snapshot.
    /// </summary>
    ClipSnapshot Snapshot();
}

/// <summary>
/// The video a buffer held at one moment, oldest first.
/// </summary>
/// <param name="Samples">Encoded frames, starting on a keyframe; empty if nothing was buffered.</param>
public sealed record ClipSnapshot(IReadOnlyList<EncodedSample> Samples)
{
    /// <summary>
    /// From the first frame's start to the last frame's end.
    /// </summary>
    public TimeSpan Duration => Samples.Count == 0
        ? TimeSpan.Zero
        : Samples[^1].Timestamp + Samples[^1].Duration - Samples[0].Timestamp;
}
