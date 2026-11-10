namespace Clipping.Core.Clips;

/// <summary>
/// Chooses the subfolder of the output folder that a clip is filed in, one per application.
/// </summary>
/// <remarks>
/// Each subfolder records which application it belongs to, by <see cref="ClipSource.Id"/>, in a
/// hidden <c>.clipsource</c> file. An application's existing folder is reused even if its name
/// has since changed, such as when it's named from a window title that differs between runs, or
/// the user renamed the folder. When two applications have the same name, the second gets its
/// executable name added, such as <c>Launcher (fooclient)</c>, and then a number, rather than
/// sharing the first one's folder. A folder that already exists without that file is claimed by
/// the first application filed in it; a name taken by a file, or a folder that can't be read or
/// claimed, is passed over.
/// <para>
/// A subfolder that is a junction or symbolic link is followed on purpose, so the user can send
/// one application's clips to another drive. The owner file itself is never read or written
/// through a link.
/// </para>
/// </remarks>
public static class ClipFolders
{
    /// <summary>
    /// The subfolder for clips whose application couldn't be identified.
    /// </summary>
    public const string Unknown = "Unknown";

    internal const string OwnerFile = ".clipsource";

    private const int MaxNameLength = 80;

    // Gives up on a name long before this in practice; it only stops a runaway loop.
    private const int MaxCopies = 1000;

    // Invalid in Windows file names, whichever system this runs on.
    private static readonly char[] InvalidChars = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    // Windows also reserves the superscript digits ¹ ² ³ after COM and LPT.
    private static readonly HashSet<string> ReservedNames = new(
        [
            "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
            .. "123456789¹²³".SelectMany(n => new[] { $"COM{n}", $"LPT{n}" }),
        ],
        StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Creates, if needed, the folder in <paramref name="root"/> for clips from <paramref name="source"/>.
    /// </summary>
    /// <param name="root">The output folder.</param>
    /// <param name="source">The application, or <see langword="null"/> if it's unknown.</param>
    /// <returns>
    /// The full path of the folder. Its name is always directly inside <paramref name="root"/>; the
    /// application's name can't add path separators or <c>..</c> to it.
    /// </returns>
    /// <exception cref="IOException">The folder couldn't be created or read.</exception>
    /// <exception cref="UnauthorizedAccessException">The folder couldn't be created or read.</exception>
    public static string For(string root, ClipSource? source)
    {
        var stem = source is null ? null : Sanitize(Path.GetFileNameWithoutExtension(source.Executable));
        var name = (source is null ? null : Sanitize(source.DisplayName)) ?? stem ?? Unknown;

        var owner = source?.Id ?? "";

        // A problem with the output folder itself fails the save rather than every name in turn.
        Directory.CreateDirectory(root);

        if (source is not null && FolderOwnedBy(root, owner) is { } existing)
        {
            return existing;
        }

        foreach (var candidate in Candidates(name, stem))
        {
            var folder = Path.Combine(root, candidate);
            if (TryClaim(folder, owner))
            {
                return folder;
            }
        }

        throw new IOException($"Every folder name for {name} in {root} is taken.");
    }

    /// <summary>
    /// Makes <paramref name="name"/> safe to use as a single folder name on Windows.
    /// </summary>
    /// <returns><see langword="null"/> if nothing usable is left.</returns>
    internal static string? Sanitize(string? name)
    {
        if (name is null)
        {
            return null;
        }

        var chars = name.Select(c => char.IsControl(c) || InvalidChars.Contains(c) ? ' ' : c).ToArray();
        var clean = string.Join(' ', new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (clean.Length > MaxNameLength)
        {
            // Don't cut between the two halves of a character such as an emoji.
            clean = clean[..(char.IsHighSurrogate(clean[MaxNameLength - 1]) ? MaxNameLength - 1 : MaxNameLength)];
        }

        // Windows drops trailing dots and spaces, which would turn ".." into the parent folder.
        clean = clean.TrimEnd('.', ' ');
        if (clean.Length == 0)
        {
            return null;
        }

        // Device names like CON can't name a folder, even with an extension after them.
        var dot = clean.IndexOf('.');
        var device = (dot < 0 ? clean : clean[..dot]).TrimEnd();
        return ReservedNames.Contains(device) ? device + "_" + clean[device.Length..] : clean;
    }

    private static IEnumerable<string> Candidates(string name, string? stem)
    {
        yield return name;

        if (stem is not null && !string.Equals(stem, name, StringComparison.OrdinalIgnoreCase))
        {
            yield return $"{name} ({stem})";
        }

        for (var copy = 2; copy <= MaxCopies; copy++)
        {
            yield return $"{name} ({copy})";
        }
    }

    private static string? FolderOwnedBy(string root, string owner) =>
        Directory.EnumerateDirectories(root)
            .FirstOrDefault(folder => string.Equals(ReadOwner(folder), owner, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The application a folder belongs to.
    /// </summary>
    /// <returns><see langword="null"/> if it has no owner file, or it can't be read.</returns>
    private static string? ReadOwner(string folder)
    {
        try
        {
            var ownerFile = Path.Combine(folder, OwnerFile);
            return File.Exists(ownerFile) && !IsLink(ownerFile) ? File.ReadAllText(ownerFile).Trim() : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool TryClaim(string folder, string owner)
    {
        if (File.Exists(folder))
        {
            return false;
        }

        var ownerFile = Path.Combine(folder, OwnerFile);
        if (IsLink(ownerFile))
        {
            return false;
        }

        if (File.Exists(ownerFile))
        {
            return string.Equals(ReadOwner(folder), owner, StringComparison.OrdinalIgnoreCase);
        }

        // Written whole, then moved into place, so a crash can't leave a half-written owner behind.
        var pending = ownerFile + ".tmp";
        try
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(pending, owner);
            File.SetAttributes(pending, File.GetAttributes(pending) | FileAttributes.Hidden);
            File.Move(pending, ownerFile, overwrite: false);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            TryDelete(pending);

            // Another claim may have got there first; it may even be ours.
            return string.Equals(ReadOwner(folder), owner, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Whether <paramref name="path"/> is a link, even one whose target is missing.
    /// </summary>
    private static bool IsLink(string path)
    {
        try
        {
            return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Left behind; it's hidden and harmless.
        }
    }
}
