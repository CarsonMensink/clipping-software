namespace Clipping.Core.Clips;

/// <summary>
/// Works out which application a clip was recorded from.
/// </summary>
public interface IClipSourceResolver
{
    /// <summary>
    /// The application that was on the captured monitor for longest between
    /// <paramref name="start"/> and <paramref name="end"/>, counting the end of that time for more.
    /// </summary>
    /// <param name="start">Start of the clip, on the same clock as <see cref="Encoding.EncodedSample.Timestamp"/>.</param>
    /// <param name="end">End of the clip, on the same clock.</param>
    /// <returns><see langword="null"/> if no application was identified in that time.</returns>
    ClipSource? Resolve(TimeSpan start, TimeSpan end);
}
