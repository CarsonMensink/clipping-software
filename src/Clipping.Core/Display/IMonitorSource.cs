namespace Clipping.Core.Display;

/// <summary>
/// Reads the attached displays from the operating system.
/// </summary>
/// <remarks>
/// The platform seam behind <see cref="DisplayTarget"/>. Application code depends on
/// <see cref="IDisplayTarget"/> instead of using this directly.
/// </remarks>
public interface IMonitorSource
{
    /// <summary>
    /// Returns the displays attached right now.
    /// </summary>
    /// <remarks>
    /// May briefly return an empty list while the display configuration is changing.
    /// </remarks>
    IReadOnlyList<MonitorInfo> GetMonitors();

    /// <summary>
    /// Raised when displays are added, removed, or reconfigured.
    /// </summary>
    /// <remarks>
    /// May be raised on any thread.
    /// </remarks>
    event EventHandler? MonitorsChanged;
}
