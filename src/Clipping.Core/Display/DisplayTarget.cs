namespace Clipping.Core.Display;

/// <summary>
/// Tracks the selected monitor against the displays reported by an <see cref="IMonitorSource"/>.
/// </summary>
public sealed class DisplayTarget : IDisplayTarget, IDisposable
{
    private readonly IMonitorSource _source;
    private readonly Lock _lock = new();

    private IReadOnlyList<MonitorInfo> _monitors;

    // Null means follow the primary display, which can itself change at runtime.
    private string? _selectedDeviceName;
    private MonitorInfo _selected;

    /// <param name="source">Where the attached displays are read from.</param>
    /// <exception cref="InvalidOperationException">The source reports no displays.</exception>
    public DisplayTarget(IMonitorSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        _source = source;
        _monitors = source.GetMonitors();
        if (_monitors.Count == 0)
        {
            throw new InvalidOperationException("No displays are attached.");
        }

        _selected = Resolve(_monitors, _selectedDeviceName);
        _source.MonitorsChanged += OnMonitorsChanged;
    }

    /// <inheritdoc />
    public event EventHandler<MonitorInfo>? SelectionChanged;

    /// <inheritdoc />
    public event EventHandler? MonitorsChanged;

    /// <inheritdoc />
    public MonitorInfo GetSelectedMonitor()
    {
        lock (_lock)
        {
            return _selected;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<MonitorInfo> GetAvailableMonitors()
    {
        lock (_lock)
        {
            return _monitors;
        }
    }

    /// <inheritdoc />
    public void SelectMonitor(string deviceName)
    {
        ArgumentNullException.ThrowIfNull(deviceName);

        MonitorInfo? changed = null;

        lock (_lock)
        {
            var monitor = Find(_monitors, deviceName)
                ?? throw new ArgumentException(
                    $"No attached display is named '{deviceName}'. Attached displays: " +
                    string.Join(", ", _monitors.Select(m => m.DeviceName)) + ".",
                    nameof(deviceName));

            _selectedDeviceName = monitor.DeviceName;
            if (monitor != _selected)
            {
                _selected = monitor;
                changed = monitor;
            }
        }

        if (changed is not null)
        {
            SelectionChanged?.Invoke(this, changed);
        }
    }

    /// <summary>
    /// Stops listening for display changes.
    /// </summary>
    public void Dispose()
    {
        _source.MonitorsChanged -= OnMonitorsChanged;
    }

    private void OnMonitorsChanged(object? sender, EventArgs e)
    {
        var monitors = _source.GetMonitors();

        // Windows can briefly report no displays mid-reconfiguration; wait for the next change.
        if (monitors.Count == 0)
        {
            return;
        }

        MonitorInfo? changed = null;
        bool listChanged;

        lock (_lock)
        {
            listChanged = !_monitors.SequenceEqual(monitors);
            _monitors = monitors;

            // An unplugged selection falls back to following the primary display.
            if (_selectedDeviceName is not null && Find(monitors, _selectedDeviceName) is null)
            {
                _selectedDeviceName = null;
            }

            var selected = Resolve(monitors, _selectedDeviceName);
            if (selected != _selected)
            {
                _selected = selected;
                changed = selected;
            }
        }

        if (changed is not null)
        {
            SelectionChanged?.Invoke(this, changed);
        }

        if (listChanged)
        {
            MonitorsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private static MonitorInfo Resolve(IReadOnlyList<MonitorInfo> monitors, string? deviceName) =>
        (deviceName is null ? null : Find(monitors, deviceName))
        ?? monitors.FirstOrDefault(m => m.IsPrimary)
        ?? monitors[0];

    private static MonitorInfo? Find(IReadOnlyList<MonitorInfo> monitors, string deviceName) =>
        monitors.FirstOrDefault(m => string.Equals(m.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase));
}
