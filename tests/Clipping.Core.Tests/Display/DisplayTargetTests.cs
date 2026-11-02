using System.Drawing;
using Clipping.Core.Display;

namespace Clipping.Core.Tests.Display;

public sealed class DisplayTargetTests
{
    private static readonly MonitorInfo _left = new(@"\\.\DISPLAY1", "Left", new Rectangle(-1920, 0, 1920, 1080), IsPrimary: false);
    private static readonly MonitorInfo _main = new(@"\\.\DISPLAY2", "Main", new Rectangle(0, 0, 2560, 1440), IsPrimary: true);
    private static readonly MonitorInfo _right = new(@"\\.\DISPLAY3", "Right", new Rectangle(2560, 0, 1920, 1080), IsPrimary: false);

    private readonly FakeMonitorSource _source = new(_left, _main, _right);

    // Monitor selection must offer every attached display.
    [Fact]
    public void GetAvailableMonitors_ReturnsMonitorsFromSource()
    {
        // Arrange
        var target = new DisplayTarget(_source);

        // Act & Assert
        Assert.Equal([_left, _main, _right], target.GetAvailableMonitors());
    }

    // First launch must capture the primary display by default.
    [Fact]
    public void GetSelectedMonitor_WhenNothingSelected_ReturnsPrimary()
    {
        // Arrange
        var target = new DisplayTarget(_source);

        // Act & Assert
        Assert.Equal(_main, target.GetSelectedMonitor());
    }

    [Fact]
    public void Constructor_WhenNoDisplays_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => new DisplayTarget(new FakeMonitorSource()));
    }

    // Device-name casing must not prevent selecting the intended monitor.
    [Fact]
    public void SelectMonitor_IgnoresDeviceNameCaseAndNotifiesCapture()
    {
        // Arrange
        using var target = new DisplayTarget(_source);
        var raised = new List<MonitorInfo>();
        target.SelectionChanged += (_, monitor) => raised.Add(monitor);

        // Act
        target.SelectMonitor(@"\\.\display3");

        // Assert
        Assert.Equal(_right, target.GetSelectedMonitor());
        Assert.Equal([_right], raised);
    }

    // Re-selecting an explicitly chosen monitor must not interrupt its capture session.
    [Fact]
    public void SelectMonitor_WhenExplicitSelectionAlreadyActive_DoesNotNotify()
    {
        // Arrange
        using var target = new DisplayTarget(_source);
        target.SelectMonitor(_right.DeviceName);
        var raised = new List<MonitorInfo>();
        target.SelectionChanged += (_, monitor) => raised.Add(monitor);

        // Act
        target.SelectMonitor(_right.DeviceName);

        // Assert
        Assert.Equal(_right, target.GetSelectedMonitor());
        Assert.Empty(raised);
    }

    // Choosing the default display explicitly must not interrupt capture on that same display.
    [Fact]
    public void SelectMonitor_WhenDefaultPrimaryAlreadyActive_DoesNotNotify()
    {
        // Arrange
        using var target = new DisplayTarget(_source);
        var raised = new List<MonitorInfo>();
        target.SelectionChanged += (_, monitor) => raised.Add(monitor);

        // Act
        target.SelectMonitor(_main.DeviceName);

        // Assert
        Assert.Equal(_main, target.GetSelectedMonitor());
        Assert.Empty(raised);
    }

    [Fact]
    public void SelectMonitor_WhenNotAttached_ThrowsAndLeavesSelectionUnchanged()
    {
        var target = new DisplayTarget(_source);
        target.SelectMonitor(_right.DeviceName);
        var raised = false;
        target.SelectionChanged += (_, _) => raised = true;

        var ex = Assert.Throws<ArgumentException>(() => target.SelectMonitor(@"\\.\DISPLAY9"));

        Assert.Contains(@"\\.\DISPLAY9", ex.Message);
        Assert.Equal(_right, target.GetSelectedMonitor());
        Assert.False(raised);
    }

    [Fact]
    public void SelectMonitor_WhenNull_Throws()
    {
        var target = new DisplayTarget(_source);

        Assert.Throws<ArgumentNullException>(() => target.SelectMonitor(null!));
    }

    // A newly plugged-in monitor becomes available to capture without restarting the app.
    [Fact]
    public void AttachedMonitor_IsListedAndCanBeSelected()
    {
        // Arrange
        using var target = new DisplayTarget(_source);
        var added = new MonitorInfo(@"\\.\DISPLAY4", "Added", new Rectangle(4480, 0, 1920, 1080), IsPrimary: false);

        // Act
        _source.SetMonitors(_left, _main, _right, added);
        target.SelectMonitor(added.DeviceName);

        // Assert
        Assert.Equal([_left, _main, _right, added], target.GetAvailableMonitors());
        Assert.Equal(added, target.GetSelectedMonitor());
    }

    [Fact]
    public void MonitorsChanged_WhenUnselectedMonitorRemoved_KeepsSelectionAndDoesNotRaise()
    {
        var target = new DisplayTarget(_source);
        target.SelectMonitor(_right.DeviceName);
        var raised = false;
        target.SelectionChanged += (_, _) => raised = true;

        _source.SetMonitors(_main, _right);

        Assert.Equal([_main, _right], target.GetAvailableMonitors());
        Assert.Equal(_right, target.GetSelectedMonitor());
        Assert.False(raised);
    }

    // Unplugging the selected display must notify capture to use the primary display.
    [Fact]
    public void MonitorsChanged_WhenSelectedMonitorRemoved_FallsBackToPrimaryAndRaises()
    {
        // Arrange
        var target = new DisplayTarget(_source);
        target.SelectMonitor(_right.DeviceName);
        var raised = new List<MonitorInfo>();
        target.SelectionChanged += (_, m) => raised.Add(m);

        // Act
        _source.SetMonitors(_left, _main);

        // Assert
        Assert.Equal(_main, target.GetSelectedMonitor());
        Assert.Equal([_main], raised);
    }

    // Reconnecting a display must not silently switch capture away from its fallback.
    [Fact]
    public void MonitorsChanged_WhenSelectedMonitorRemovedThenReattached_KeepsFollowingPrimary()
    {
        // Arrange
        var target = new DisplayTarget(_source);
        target.SelectMonitor(_right.DeviceName);

        // Act
        _source.SetMonitors(_left, _main);
        _source.SetMonitors(_left, _main, _right);

        // Assert
        Assert.Equal(_main, target.GetSelectedMonitor());
    }

    [Fact]
    public void MonitorsChanged_WhenPrimaryChangesWithNoSelection_FollowsNewPrimaryAndRaises()
    {
        var target = new DisplayTarget(_source);
        var raised = new List<MonitorInfo>();
        target.SelectionChanged += (_, m) => raised.Add(m);
        var newPrimary = _left with { IsPrimary = true };

        _source.SetMonitors(newPrimary, _main with { IsPrimary = false }, _right);

        Assert.Equal(newPrimary, target.GetSelectedMonitor());
        Assert.Equal([newPrimary], raised);
    }

    [Fact]
    public void MonitorsChanged_WhenPrimaryChangesWithExplicitSelection_KeepsSelection()
    {
        var target = new DisplayTarget(_source);
        target.SelectMonitor(_main.DeviceName);
        var raised = false;
        target.SelectionChanged += (_, _) => raised = true;

        var demoted = _main with { IsPrimary = false };
        _source.SetMonitors(_left with { IsPrimary = true }, demoted, _right);

        // The descriptor changed, so consumers hear about it, but it is still the same monitor.
        Assert.Equal(demoted, target.GetSelectedMonitor());
        Assert.True(raised);
    }

    [Fact]
    public void MonitorsChanged_WhenSelectedBoundsChange_RaisesWithNewBounds()
    {
        var target = new DisplayTarget(_source);
        var raised = new List<MonitorInfo>();
        target.SelectionChanged += (_, m) => raised.Add(m);
        var resized = _main with { Bounds = new Rectangle(0, 0, 3840, 2160) };

        _source.SetMonitors(_left, resized, _right);

        Assert.Equal([resized], raised);
    }

    [Fact]
    public void MonitorsChanged_WhenNothingRelevantChanges_DoesNotRaise()
    {
        var target = new DisplayTarget(_source);
        var raised = false;
        target.SelectionChanged += (_, _) => raised = true;

        _source.SetMonitors(_left, _main, _right);

        Assert.False(raised);
    }

    [Fact]
    public void MonitorsChanged_WhenSourceBrieflyReportsNoDisplays_KeepsLastKnownState()
    {
        var target = new DisplayTarget(_source);
        target.SelectMonitor(_right.DeviceName);

        _source.SetMonitors();

        Assert.Equal([_left, _main, _right], target.GetAvailableMonitors());
        Assert.Equal(_right, target.GetSelectedMonitor());
    }

    // A closed display target must not send capture changes after shutdown.
    [Fact]
    public void DisposedTarget_DoesNotPublishFurtherDisplayChanges()
    {
        // Arrange
        var target = new DisplayTarget(_source);
        var raised = new List<MonitorInfo>();
        target.SelectionChanged += (_, monitor) => raised.Add(monitor);

        // Act
        target.Dispose();
        _source.SetMonitors(_left);

        // Assert
        Assert.Empty(raised);
    }

    private sealed class FakeMonitorSource(params MonitorInfo[] monitors) : IMonitorSource
    {
        private IReadOnlyList<MonitorInfo> _monitors = monitors;

        public event EventHandler? MonitorsChanged;

        public bool HasSubscribers => MonitorsChanged is not null;

        public IReadOnlyList<MonitorInfo> GetMonitors() => _monitors;

        public void SetMonitors(params MonitorInfo[] monitors)
        {
            _monitors = monitors;
            MonitorsChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
