namespace Clipping.Core.Display;

/// <summary>
/// Owns which monitor is being captured and the list of monitors to choose from.
/// </summary>
public interface IDisplayTarget
{
    /// <summary>
    /// Returns the monitor to capture.
    /// </summary>
    /// <remarks>
    /// The primary display unless another has been chosen with <see cref="SelectMonitor"/>.
    /// </remarks>
    MonitorInfo GetSelectedMonitor();

    /// <summary>
    /// Returns the monitors attached right now, including ones plugged in or
    /// reconfigured since startup.
    /// </summary>
    IReadOnlyList<MonitorInfo> GetAvailableMonitors();

    /// <summary>
    /// Chooses the monitor to capture.
    /// </summary>
    /// <param name="deviceName">
    /// <see cref="MonitorInfo.DeviceName"/> of an attached monitor, such as
    /// <c>\\.\DISPLAY2</c>. Compared case-insensitively.
    /// </param>
    /// <exception cref="ArgumentException">
    /// No attached monitor has that device name. The selection is left unchanged.
    /// </exception>
    void SelectMonitor(string deviceName);

    /// <summary>
    /// Raised with the new selection whenever <see cref="GetSelectedMonitor"/> would
    /// return a different result.
    /// </summary>
    /// <remarks>
    /// Raised when <see cref="SelectMonitor"/> picks a different monitor, and when a
    /// display change affects the selected monitor: it is unplugged (the selection
    /// falls back to the primary display), its bounds change, or the primary display
    /// changes while it is the selection.
    /// <para>
    /// May be raised on any thread. Handlers that touch UI must marshal to the UI
    /// thread themselves.
    /// </para>
    /// </remarks>
    event EventHandler<MonitorInfo>? SelectionChanged;
}
