using System.Text.Json;

namespace Clipping.Core.Settings;

/// <summary>
/// Persists <see cref="AppSettings"/> as a JSON file on disk.
/// </summary>
public sealed class JsonSettingsStore : ISettingsStore
{
    private static readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    private readonly string _filePath;
    private readonly Lock _lock = new();

    /// <inheritdoc />
    public AppSettings Settings { get; private set; }

    /// <param name="filePath">Full path of the settings file to read and write.</param>
    public JsonSettingsStore(string filePath)
    {
        _filePath = filePath;
        Settings = Load(filePath);
    }

    /// <inheritdoc />
    public event EventHandler<SettingsChangedEventArgs>? Changed;

    /// <inheritdoc />
    public void Update(Func<AppSettings, AppSettings> change)
    {
        ArgumentNullException.ThrowIfNull(change);

        AppSettings previousSettings;
        AppSettings updatedSettings;

        lock (_lock)
        {
            previousSettings = Settings;
            updatedSettings = change.Invoke(previousSettings)
                ?? throw new InvalidOperationException("The settings change returned null.");

            // Buffer time must be between 15 second and 5 minutes
            ArgumentOutOfRangeException.ThrowIfLessThan(updatedSettings.BufferLength, 15);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(updatedSettings.BufferLength, 300);

            // Throws on failure, leaving Settings unchanged and Changed unraised.
            Save(updatedSettings);

            Settings = updatedSettings;
        }

        Changed?.Invoke(this, new SettingsChangedEventArgs(previousSettings, updatedSettings));
    }

    /// <summary>
    /// Reads settings from disk, falling back to defaults if the file is missing,
    /// unreadable, or corrupt.
    /// </summary>
    private static AppSettings Load(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return new AppSettings();
        }

        try
        {
            var json = File.ReadAllText(filePath);
            return JsonSerializer.Deserialize<AppSettings>(json, _jsonOptions) ?? new AppSettings();
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return new AppSettings();
        }
    }

    /// <summary>
    /// Writes settings to disk atomically: the file holds either the old or the new
    /// settings in full, never a partial write.
    /// </summary>
    private void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_filePath))!);

        var tempPath = _filePath + ".tmp";

        // We either write the entire file or none of it.
        try
        {
            File.WriteAllText(tempPath, JsonSerializer.Serialize(settings, _jsonOptions));
            File.Move(tempPath, _filePath, overwrite: true);
        }
        catch
        {
            File.Delete(tempPath);
            throw;
        }
    }
}
