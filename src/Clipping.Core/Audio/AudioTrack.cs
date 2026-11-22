namespace Clipping.Core.Audio;

/// <summary>
/// Continuous uncompressed audio for one clip, starting exactly when its video starts.
/// </summary>
/// <param name="Format">The layout of <paramref name="Samples"/>.</param>
/// <param name="Samples">Interleaved samples, with silence wherever nothing was playing.</param>
public sealed record AudioTrack(AudioFormat Format, short[] Samples)
{
    /// <summary>
    /// The number of frames, each holding one sample per channel.
    /// </summary>
    public int Frames => Samples.Length / Format.Channels;

    /// <summary>
    /// How long the track plays for.
    /// </summary>
    public TimeSpan Duration => Format.DurationOf(Frames);
}
