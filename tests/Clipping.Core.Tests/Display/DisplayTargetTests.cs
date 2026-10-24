using System.Drawing;
using Clipping.Core.Display;

namespace Clipping.Core.Tests.Display;

public sealed class DisplayTargetTests
{
    private static readonly MonitorInfo _left = new(@"\\.\DISPLAY1", "Left", new Rectangle(-1920, 0, 1920, 1080), IsPrimary: false);
    private static readonly MonitorInfo _main = new(@"\\.\DISPLAY2", "Main", new Rectangle(0, 0, 2560, 1440), IsPrimary: true);
    private static readonly MonitorInfo _right = new(@"\\.\DISPLAY3", "Right", new Rectangle(2560, 0, 1920, 1080), IsPrimary: false);

    private readonly FakeMonitorSource _source = new(_left, _main, _right);

    [Fact]
    public void GetAvailableMonitors_ReturnsMonitorsFromSource()
    {
        var target = new DisplayTarget(_source);

        Assert.Equal([_left, _main, _right], target.GetAvailableMonitors());
    }

    [Fact]
    public void GetSelectedMonitor_WhenNothingSelected_ReturnsPrimary()
    {
        var target = new DisplayTarget(_source);

        Assert.Equal(_main, target.GetSelectedMonitor());
    }

    [Fact]
    public void Constructor_WhenNoDisplays_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => new DisplayTarget(new FakeMonitorSource()));
    }

    [Fact]
    public void SelectMonitor_SelectsByDeviceName()
    {
        var target = new DisplayTarget(_source);

        target.SelectMonitor(_right.DeviceName);

        Assert.Equal(_right, target.GetSelectedMonitor());
    }

    [Fact]
    public void SelectMonitor_IgnoresCase()
    {
        var target = new DisplayTarget(_source);

        target.SelectMonitor(@"\\.\display3");

        Assert.Equal(_right, target.GetSelectedMonitor());
    }

    [Fact]
    public void SelectMonitor_RaisesSelectionChangedWithNewMonitor()
    {
        var target = new DisplayTarget(_source);
        var raised = new List<MonitorInfo>();
        target.SelectionChanged += (_, m) => raised.Add(m);

        target.SelectMonitor(_left.DeviceName);

        Assert.Equal([_left], raised);
    }

    [Fact]
    public void SelectMonitor_WhenAlreadySelected_DoesNotRaise()
    {
        var target = new DisplayTarget(_source);
        var raised = false;
        target.SelectionChanged += (_, _) => raised = true;

        target.SelectMonitor(_main.DeviceName);

        Assert.False(raised);
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

    [Fact]
    public void MonitorsChanged_WhenMonitorAdded_IsReflectedInEnumeration()
    {
        var target = new DisplayTarget(_source);
        var added = new MonitorInfo(@"\\.\DISPLAY4", "Added", new Rectangle(4480, 0, 1920, 1080), IsPrimary: false);

        _source.SetMonitors(_left, _main, _right, added);

        Assert.Contains(added, target.GetAvailableMonitors());
    }

    [Fact]
    public void MonitorsChanged_WhenAddedMonitorSelected_SelectsIt()
    {
        var target = new DisplayTarget(_source);
        var added = new MonitorInfo(@"\\.\DISPLAY4", "Added", new Rectangle(4480, 0, 1920, 1080), IsPrimary: false);
        _source.SetMonitors(_left, _main, _right, added);

        target.SelectMonitor(added.DeviceName);

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

    [Fact]
    public void MonitorsChanged_WhenSelectedMonitorRemoved_FallsBackToPrimaryAndRaises()
    {
        var target = new DisplayTarget(_source);
        target.SelectMonitor(_right.DeviceName);
        var raised = new List<MonitorInfo>();
        target.SelectionChanged += (_, m) => raised.Add(m);

        _source.SetMonitors(_left, _main);

        Assert.Equal(_main, target.GetSelectedMonitor());
        Assert.Equal([_main], raised);
    }

    [Fact]
    public void MonitorsChanged_WhenSelectedMonitorRemovedThenReattached_KeepsFollowingPrimary()
    {
        var target = new DisplayTarget(_source);
        target.SelectMonitor(_right.DeviceName);

        _source.SetMonitors(_left, _main);
        _source.SetMonitors(_left, _main, _right);

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

    [Fact]
    public void Dispose_StopsListeningForChanges()
    {
        var target = new DisplayTarget(_source);

        target.Dispose();

        Assert.False(_source.HasSubscribers);
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
