namespace Clipping.Core.Shell;

/// <summary>
/// Where an entry sits in the tray menu. Groups appear in declaration order.
/// </summary>
public enum MenuGroup
{
    /// <summary>What is captured, such as the monitor.</summary>
    Capture,

    /// <summary>Hotkey bindings.</summary>
    Hotkeys,

    /// <summary>Where and how clips are saved.</summary>
    Clips,

    /// <summary>The application itself, such as the log folder. Exit always follows it.</summary>
    App,
}
