using Clipping.Core.Audio;

namespace Clipping.Core.Clips;

/// <summary>
/// Supplies the audio to go with a clip's video.
/// </summary>
public interface IClipAudio
{
    /// <summary>
    /// Renders the audio for a stretch of video.
    /// </summary>
    /// <param name="start">When the clip's first frame starts.</param>
    /// <param name="end">When its last frame ends.</param>
    /// <param name="cancellationToken">Abandons waiting for audio still on its way.</param>
    /// <returns>
    /// A track covering exactly that stretch, or <see langword="null"/> if audio is turned off
    /// and the clip should have none.
    /// </returns>
    Task<AudioTrack?> ReadAsync(TimeSpan start, TimeSpan end, CancellationToken cancellationToken = default);
}
