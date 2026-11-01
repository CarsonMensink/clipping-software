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
    /// <summary>Shortest allowed <see cref="BufferLength"/>, in seconds.</summary>
    public const long MinBufferLength = 15;

    /// <summary>Longest allowed <see cref="BufferLength"/>, in seconds.</summary>
    public const long MaxBufferLength = 300;

    /// <summary>Lowest allowed <see cref="FrameRate"/>, in frames per second.</summary>
    public const int MinFrameRate = 15;

    /// <summary>Highest allowed <see cref="FrameRate"/>, in frames per second.</summary>
    public const int MaxFrameRate = 60;

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

    /// <summary>
    /// Target number of frames captured per second.
    /// </summary>
    /// <remarks>
    /// A ceiling, not a guarantee: capture cannot run faster than the monitor's
    /// refresh rate.
    /// </remarks>
    /// <value>Defaults to <c>30</c>.</value>
    public int FrameRate { get; init; } = 30;
}
