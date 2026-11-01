namespace Clipping.Core.Clips;

/// <summary>
/// Names clip files after when they were saved.
/// </summary>
public static class ClipFileNames
{
    /// <summary>
    /// A path in <paramref name="directory"/> that no file uses yet, such as
    /// <c>Clip 2026-10-31 22-16-05.mp4</c>, or <c>Clip 2026-10-31 22-16-05 (2).mp4</c> if that's taken.
    /// </summary>
    /// <param name="directory">The folder the clip goes in.</param>
    /// <param name="savedAt">When the clip is saved, in local time.</param>
    /// <param name="extension">The file extension, with its dot.</param>
    public static string Next(string directory, DateTime savedAt, string extension)
    {
        var name = $"Clip {savedAt:yyyy-MM-dd HH-mm-ss}";
        var path = Path.Combine(directory, name + extension);
        for (var copy = 2; File.Exists(path); copy++)
        {
            path = Path.Combine(directory, $"{name} ({copy}){extension}");
        }

        return path;
    }
}
