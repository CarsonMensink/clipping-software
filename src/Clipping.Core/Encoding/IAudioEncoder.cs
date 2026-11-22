using Clipping.Core.Audio;

namespace Clipping.Core.Encoding;

/// <summary>
/// Compresses a clip's audio track, independent of the encoder behind it.
/// </summary>
/// <remarks>
/// Audio is buffered uncompressed and encoded only when a clip is saved, so each clip's track
/// can be cut exactly to its video. Encoding runs on the save path, never during capture.
/// </remarks>
public interface IAudioEncoder
{
    /// <summary>
    /// Encodes a whole track.
    /// </summary>
    /// <param name="track">The audio to encode, starting at time zero.</param>
    /// <param name="cancellationToken">Abandons encoding.</param>
    /// <exception cref="InvalidOperationException">The encoder couldn't be started or failed.</exception>
    EncodedAudio Encode(AudioTrack track, CancellationToken cancellationToken = default);
}

/// <summary>
/// A compressed audio track, ready to be written into a file.
/// </summary>
/// <param name="Format">The format the audio plays back in.</param>
/// <param name="BytesPerSecond">Average compressed bytes per second.</param>
/// <param name="CodecConfig">The decoder configuration, an AAC AudioSpecificConfig.</param>
/// <param name="Samples">Compressed frames in playback order, timed from zero.</param>
public sealed record EncodedAudio(
    AudioFormat Format,
    int BytesPerSecond,
    byte[] CodecConfig,
    IReadOnlyList<EncodedAudioSample> Samples);

/// <summary>
/// One compressed audio frame.
/// </summary>
/// <param name="Timestamp">When it starts playing, from the start of the track.</param>
/// <param name="Duration">How long it plays.</param>
/// <param name="Data">The compressed bytes, a raw AAC frame.</param>
public sealed record EncodedAudioSample(TimeSpan Timestamp, TimeSpan Duration, byte[] Data);
