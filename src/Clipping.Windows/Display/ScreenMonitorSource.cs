using Clipping.Core.Display;
using Microsoft.Win32;

namespace Clipping.Windows.Display;

/// <summary>
/// Reads attached displays from WinForms <see cref="Screen"/>.
/// </summary>
/// <remarks>
/// <see cref="Screen"/> has no human-readable name, so friendly names come from
/// <see cref="DisplayConfigNames"/>.
/// </remarks>
internal sealed class ScreenMonitorSource : IMonitorSource, IDisposable
{
    public ScreenMonitorSource()
    {
        // Covers hot-plug as well as resolution, arrangement, and primary changes.
        // Subscribing from an STA thread makes SystemEvents deliver through that
        // thread's message queue, so it must be running a message loop.
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    /// <inheritdoc />
    /// <remarks>
    /// When created on an STA thread, as the app's UI thread is, raised on that thread
    /// and only while it runs a message loop such as <see cref="Application.Run()"/>.
    /// </remarks>
    public event EventHandler? MonitorsChanged;

    /// <inheritdoc />
    public IReadOnlyList<MonitorInfo> GetMonitors()
    {
        var friendlyNames = DisplayConfigNames.GetFriendlyNames();

        return Screen.AllScreens
            .Select(screen => new MonitorInfo(
                screen.DeviceName,
                friendlyNames.GetValueOrDefault(screen.DeviceName) ?? FallbackName(screen.DeviceName),
                screen.Bounds,
                screen.Primary))
            .ToList();
    }

    public void Dispose()
    {
        // SystemEvents is static, so an undisposed source would never be collected.
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) =>
        MonitorsChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// Turns <c>\\.\DISPLAY2</c> into <c>Display 2</c> for monitors that report no name,
    /// which is common for built-in laptop panels.
    /// </summary>
    private static string FallbackName(string deviceName)
    {
        const string prefix = @"\\.\DISPLAY";
        return deviceName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? "Display " + deviceName[prefix.Length..]
            : deviceName;
    }
}
