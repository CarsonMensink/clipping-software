using Clipping.Core.Settings;

namespace Clipping.Core.Clips;

/// <summary>
/// Saves what the buffer holds as a clip file.
/// </summary>
/// <remarks>
/// Each save empties the buffer, so a clip covers only the video since the previous one,
/// up to the buffer's retention.
/// <para>
/// Saves run one at a time, so two in quick succession get different file names rather than
/// racing for the same one. Failures come back as a result, never as an exception.
/// </para>
/// </remarks>
public sealed class ClipSaver
{
    private readonly IClipBuffer _buffer;
    private readonly IClipWriter _writer;
    private readonly ISettingsStore _settings;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _oneAtATime = new(1);

    /// <param name="buffer">Where the video comes from.</param>
    /// <param name="writer">Writes the file.</param>
    /// <param name="settings">Supplies the output folder.</param>
    /// <param name="timeProvider">Dates the file name. Defaults to the system clock.</param>
    public ClipSaver(IClipBuffer buffer, IClipWriter writer, ISettingsStore settings, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(settings);

        _buffer = buffer;
        _writer = writer;
        _settings = settings;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// The folder clips are saved in: the output directory setting, or the user's Videos folder.
    /// </summary>
    public string OutputDirectory =>
        _settings.Settings.OutputDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);

    /// <summary>
    /// Takes the buffer's contents, emptying it, and writes them to a new file in <see cref="OutputDirectory"/>.
    /// </summary>
    /// <remarks>
    /// The snapshot is taken before waiting for an earlier save, so the clip covers the moment
    /// this was called.
    /// Cancelling after that point discards the video taken, since the buffer is already empty.
    /// </remarks>
    public async Task<ClipSaveResult> SaveAsync(CancellationToken cancellationToken = default)
    {
        var clip = _buffer.SnapshotAndClear();
        if (clip.Samples.Count == 0)
        {
            return ClipSaveResult.Failed("No new video to save yet.");
        }

        await _oneAtATime.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = OutputDirectory;
            Directory.CreateDirectory(directory);

            var path = ClipFileNames.Next(directory, _time.GetLocalNow().DateTime, _writer.Extension);
            await _writer.WriteAsync(clip, path, cancellationToken).ConfigureAwait(false);
            return ClipSaveResult.Saved(path, clip.Duration);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // Missing drive, no space, no permission, or an invalid output folder setting.
            return ClipSaveResult.Failed(e.Message);
        }
        finally
        {
            _oneAtATime.Release();
        }
    }
}

/// <summary>
/// What happened when a clip was saved.
/// </summary>
/// <param name="Path">The file written, if the save succeeded.</param>
/// <param name="Duration">How long the clip is, if the save succeeded.</param>
/// <param name="Error">Why the save failed, in words a user can act on.</param>
public sealed record ClipSaveResult(string? Path, TimeSpan Duration, string? Error)
{
    /// <summary>
    /// Whether the clip was written.
    /// </summary>
    public bool Succeeded => Path is not null;

    internal static ClipSaveResult Saved(string path, TimeSpan duration) => new(path, duration, null);

    internal static ClipSaveResult Failed(string error) => new(null, TimeSpan.Zero, error);
}
