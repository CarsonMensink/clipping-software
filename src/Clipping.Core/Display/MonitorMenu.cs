using Clipping.Core.Settings;
using Clipping.Core.Shell;

namespace Clipping.Core.Display;

/// <summary>
/// Lets the user choose the captured monitor from the tray, and remembers the choice.
/// </summary>
/// <remarks>
/// On creation, restores the monitor saved in <see cref="AppSettings.SelectedMonitor"/>, or
/// tells the user and keeps the primary display if that monitor is no longer attached. The
/// saved choice is kept in that case, so it applies again once the monitor is back.
/// <para>
/// Create it before capture starts, so capture begins on the restored monitor.
/// </para>
/// </remarks>
public sealed class MonitorMenu : IDisposable
{
    private readonly IDisplayTarget _display;
    private readonly ISettingsStore _settings;
    private readonly ITraySubmenu _submenu;

    // Display events may arrive on any thread; guards _entries and _disposed.
    private readonly Lock _lock = new();
    private List<(string DeviceName, IMenuEntry Entry)> _entries = [];
    private bool _disposed;

    /// <param name="display">The monitors to offer and the selection to change.</param>
    /// <param name="settings">Where the choice is saved and restored from.</param>
    /// <param name="menu">The menu the monitor submenu is added to.</param>
    /// <param name="notifications">Tells the user when the saved monitor is missing.</param>
    public MonitorMenu(
        IDisplayTarget display, ISettingsStore settings, ITrayMenu menu, INotificationService notifications)
    {
        ArgumentNullException.ThrowIfNull(display);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(menu);
        ArgumentNullException.ThrowIfNull(notifications);

        _display = display;
        _settings = settings;

        RestoreSavedMonitor(notifications);

        _submenu = menu.AddSubmenu("Monitor", MenuGroup.Capture);
        _display.SelectionChanged += OnSelectionChanged;
        _display.MonitorsChanged += OnMonitorsChanged;
        Rebuild();
    }

    /// <summary>
    /// Stops following display changes. The menu entries are left as they are.
    /// </summary>
    public void Dispose()
    {
        _display.SelectionChanged -= OnSelectionChanged;
        _display.MonitorsChanged -= OnMonitorsChanged;

        lock (_lock)
        {
            _disposed = true;
        }
    }

    private void RestoreSavedMonitor(INotificationService notifications)
    {
        var saved = _settings.Settings.SelectedMonitor;
        if (saved is null)
        {
            return;
        }

        try
        {
            _display.SelectMonitor(saved);
        }
        catch (ArgumentException)
        {
            // Device names can shift when displays are replugged; never capture a guess silently.
            notifications.Show(
                "Saved monitor not found",
                $"{saved} isn't attached, so {_display.GetSelectedMonitor().FriendlyName} is being captured instead.",
                NotificationKind.Info);
        }
    }

    private void Select(string deviceName)
    {
        // Selected first, so a monitor that has just gone is never saved.
        _display.SelectMonitor(deviceName);
        _settings.Update(s => s with { SelectedMonitor = deviceName });
    }

    private void OnMonitorsChanged(object? sender, EventArgs e) => Rebuild();

    private void OnSelectionChanged(object? sender, MonitorInfo e)
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            // Events from different threads can arrive out of order, so check the latest selection.
            var selected = _display.GetSelectedMonitor().DeviceName;
            foreach (var (deviceName, entry) in _entries)
            {
                entry.Checked = IsSameDevice(deviceName, selected);
            }
        }
    }

    private void Rebuild()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            foreach (var (_, entry) in _entries)
            {
                entry.Remove();
            }

            var selected = _display.GetSelectedMonitor().DeviceName;
            _entries = _display.GetAvailableMonitors()
                .Select(monitor =>
                {
                    var entry = _submenu.Add(monitor.FriendlyName, MenuGroup.Capture, () => Select(monitor.DeviceName));
                    entry.Checked = IsSameDevice(monitor.DeviceName, selected);
                    return (monitor.DeviceName, entry);
                })
                .ToList();
        }
    }

    private static bool IsSameDevice(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
