using System.Drawing;

namespace Clipping.Core.Display;

/// <summary>
/// Describes an attached display, independent of the API that discovered it.
/// </summary>
/// <param name="DeviceName">
/// GDI device name, such as <c>\\.\DISPLAY1</c>. This is the key that identifies a
/// monitor across APIs: WinForms <c>Screen.DeviceName</c> and DXGI
/// <c>DXGI_OUTPUT_DESC.DeviceName</c> both report it.
/// </param>
/// <param name="FriendlyName">Human-readable name to show the user, such as <c>DELL U2720Q</c>.</param>
/// <param name="Bounds">
/// Position and size in virtual-desktop pixels. Matches captured frame dimensions
/// only when the process is per-monitor DPI aware.
/// </param>
/// <param name="IsPrimary">Whether this is the primary display.</param>
public sealed record MonitorInfo(
    string DeviceName,
    string FriendlyName,
    Rectangle Bounds,
    bool IsPrimary);
