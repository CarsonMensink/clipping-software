namespace Clipping.Core.Clips;

/// <summary>
/// Writes buffered video to a file, independent of the container format.
/// </summary>
public interface IClipWriter
{
    /// <summary>
    /// The file extension this writer produces, such as <c>.mp4</c>.
    /// </summary>
    string Extension { get; }

    /// <summary>
    /// Writes <paramref name="clip"/> to <paramref name="path"/>, with timestamps starting at zero.
    /// </summary>
    /// <remarks>
    /// If the clip has audio that can't be encoded, the video is still written, without it.
    /// </remarks>
    /// <param name="clip">A snapshot that starts on a keyframe and holds at least one frame.</param>
    /// <param name="path">A file that does not exist yet, in a folder that does.</param>
    /// <exception cref="IOException">
    /// Writing failed, such as when the disk is full. No partial file is left behind.
    /// </exception>
    Task<ClipWriteResult> WriteAsync(ClipSnapshot clip, string path, CancellationToken cancellationToken = default);
}

/// <summary>
/// How a clip file was written.
/// </summary>
/// <param name="AudioError">
/// Why the clip's audio was left out, in words a user can act on; <see langword="null"/> if
/// everything in the snapshot was written.
/// </param>
public sealed record ClipWriteResult(string? AudioError = null)
{
    /// <summary>
    /// Everything in the snapshot was written.
    /// </summary>
    public static ClipWriteResult Complete { get; } = new();
}
