namespace Clipping.Core.Settings;

/// <summary>
/// User preferences that persist between sessions.
/// </summary>
/// <remarks>
/// Immutable: change settings by creating a copy with <c>with</c> and passing it
/// to <see cref="ISettingsStore"/>. A new instance holds the defaults used on
/// first run or when the settings file is missing or corrupt.
/// </remarks>
public sealed record AppSettings
{
    /// <summary>
    /// Device name of the monitor to capture, such as <c>\\.\DISPLAY1</c>.
    /// </summary>
    /// <value>
    /// <see langword="null"/> to capture the primary display. This is the default.
    /// </value>
    public string? SelectedMonitor { get; init; }
    
    /// <summary>
    /// Key combination that saves the current clip.
    /// </summary>
    /// <value>Defaults to <c>Alt+F10</c>.</value>
    public string SelectedHotkey { get; init; } = "Alt+F10";
    
    /// <summary>
    /// Root folder that clips are saved under.
    /// </summary>
    /// <value>
    /// <see langword="null"/> to use the user's Videos folder. This is the default.
    /// </value>
    public string? OutputDirectory { get; init; }

    /// <summary>
    /// The length that the clip will be.
    /// </summary>
    /// <value>Defaults to <c>30s</c></value>
    public long BufferLength { get; init; } = 30;
}
