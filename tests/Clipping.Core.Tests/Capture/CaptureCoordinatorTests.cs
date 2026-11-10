using System.Drawing;
using System.Threading.Channels;
using Clipping.Core.Capture;
using Clipping.Core.Display;
using Microsoft.Extensions.Time.Testing;

namespace Clipping.Core.Tests.Capture;

public sealed class CaptureCoordinatorTests
{
    private static readonly MonitorInfo _main = new(@"\\.\DISPLAY1", "Main", new Rectangle(0, 0, 2560, 1440), IsPrimary: true);
    private static readonly MonitorInfo _side = new(@"\\.\DISPLAY2", "Side", new Rectangle(2560, 0, 1920, 1080), IsPrimary: false);

    private readonly FakeDisplayTarget _target = new(_main);
    private readonly FakeFrameSource _source = new();
    private readonly FakeTimeProvider _time = new();

    // Construction and display notifications must not begin recording before Start.
    [Fact]
    public void BeforeStart_DisplayChangesLeaveCaptureIdle()
    {
        // Arrange
        using var coordinator = CreateCoordinator();

        // Act
        _target.Select(_side);
        _time.Advance(TimeSpan.FromSeconds(2));

        // Assert
        Assert.Null(_source.ActiveMonitor);
        Assert.Empty(_source.SuccessfulStarts);
    }

    // Repeated start requests must keep one active capture session.
    [Fact]
    public void Start_CapturesSelectedMonitorWithoutDuplicateSessions()
    {
        // Arrange
        using var coordinator = CreateCoordinator();
        _target.Select(_side);

        // Act
        coordinator.Start();
        coordinator.Start();

        // Assert
        Assert.Equal(_side, _source.ActiveMonitor);
        Assert.Equal([_side], _source.SuccessfulStarts);
    }

    // Explicit stop must keep capture stopped until the next Start request.
    [Fact]
    public void Stop_LeavesCaptureIdleDespiteLaterDisplayChanges()
    {
        // Arrange
        using var coordinator = CreateCoordinator();
        coordinator.Start();

        // Act
        coordinator.Stop();
        coordinator.Stop();
        _target.Select(_side);
        _time.Advance(TimeSpan.FromSeconds(2));

        // Assert
        Assert.Null(_source.ActiveMonitor);
        Assert.Equal([_main], _source.SuccessfulStarts);
    }

    // A closed coordinator cannot reacquire capture resources.
    [Fact]
    public void Start_AfterDisposal_IsRejected()
    {
        // Arrange
        var coordinator = CreateCoordinator();
        coordinator.Dispose();

        // Act
        var error = Record.Exception(coordinator.Start);

        // Assert
        Assert.IsType<ObjectDisposedException>(error);
        Assert.Null(_source.ActiveMonitor);
    }

    // Display changes pause recording until the last change has settled for one second (#6).
    [Fact]
    public void DisplayChangeBurst_PausesCaptureAndResumesOnceAfterQuietPeriod()
    {
        // Arrange
        using var coordinator = CreateCoordinator();
        coordinator.Start();

        // Act
        _target.Select(_side);

        // Assert
        Assert.Null(_source.ActiveMonitor);

        // Act: a second change resets the settling period.
        _time.Advance(TimeSpan.FromMilliseconds(500));
        _target.Select(_main);
        _time.Advance(TimeSpan.FromMilliseconds(999));

        // Assert
        Assert.Null(_source.ActiveMonitor);
        Assert.Equal([_main], _source.SuccessfulStarts);

        // Act
        _time.Advance(TimeSpan.FromMilliseconds(1));

        // Assert
        Assert.Equal(_main, _source.ActiveMonitor);

        // Act: settling schedules one restart, rather than a repeating timer.
        _time.Advance(TimeSpan.FromSeconds(2));

        // Assert
        Assert.Equal(_main, _source.ActiveMonitor);
        Assert.Equal([_main, _main], _source.SuccessfulStarts);
    }

    // Out-of-order notifications must not make capture resume on the wrong display.
    [Fact]
    public void Restart_UsesCurrentSelectionRatherThanStaleEvent()
    {
        // Arrange
        using var coordinator = CreateCoordinator();
        coordinator.Start();

        // Act
        _target.RaiseSelectionChanged(_main);
        _target.Selected = _side;
        _time.Advance(TimeSpan.FromSeconds(1));

        // Assert
        Assert.Equal(_side, _source.ActiveMonitor);
    }

    // An unplug during restart leaves capture stopped until a usable display is selected.
    [Fact]
    public void FailedRestart_RecoversOnNextDisplayChange()
    {
        // Arrange
        using var coordinator = CreateCoordinator();
        coordinator.Start();
        _source.FailNextStart = true;

        // Act
        _target.Select(_side);
        _time.Advance(TimeSpan.FromSeconds(1));

        // Assert
        Assert.Null(_source.ActiveMonitor);

        // Act
        _target.Select(_main);
        _time.Advance(TimeSpan.FromSeconds(1));

        // Assert
        Assert.Equal(_main, _source.ActiveMonitor);
        Assert.Equal([_main, _main], _source.SuccessfulStarts);
    }

    // Explicit stop and shutdown must both prevent a delayed restart from reviving capture.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StopOrDispose_CancelsPendingRestart(bool dispose)
    {
        // Arrange
        using var coordinator = CreateCoordinator();
        coordinator.Start();
        _target.Select(_side);

        // Act
        if (dispose) coordinator.Dispose();
        else coordinator.Stop();
        _target.Select(_main);
        _time.Advance(TimeSpan.FromSeconds(2));

        // Assert
        Assert.Null(_source.ActiveMonitor);
        Assert.Equal([_main], _source.SuccessfulStarts);
        Assert.False(_source.Disposed);
    }

    // Resuming after a cancelled restart should start immediately without an extra timer activation.
    [Fact]
    public void Resume_AfterCancelledRestart_StartsCurrentDisplayOnce()
    {
        // Arrange
        using var coordinator = CreateCoordinator();
        coordinator.Start();
        _target.Select(_side);
        coordinator.Stop();

        // Act
        coordinator.Start();

        // Assert
        Assert.Equal(_side, _source.ActiveMonitor);

        // Act
        _time.Advance(TimeSpan.FromSeconds(2));

        // Assert
        Assert.Equal(_side, _source.ActiveMonitor);
        Assert.Equal([_main, _side], _source.SuccessfulStarts);
    }

    // Shutdown stops active capture without taking ownership of the shared frame source.
    [Fact]
    public void Dispose_StopsCaptureAndIgnoresSubsequentDisplayChanges()
    {
        // Arrange
        var coordinator = CreateCoordinator();
        coordinator.Start();

        // Act
        coordinator.Dispose();
        _target.Select(_side);
        _time.Advance(TimeSpan.FromSeconds(2));

        // Assert
        Assert.False(_target.HasSubscribers);
        Assert.Null(_source.ActiveMonitor);
        Assert.Equal([_main], _source.SuccessfulStarts);
        Assert.False(_source.Disposed);
    }

    // Every scenario uses a controllable clock so display settling never depends on wall time.
    private CaptureCoordinator CreateCoordinator() => new(_target, _source, _time);

    private sealed class FakeDisplayTarget(MonitorInfo selected) : IDisplayTarget
    {
        public MonitorInfo Selected { get; set; } = selected;

        public event EventHandler<MonitorInfo>? SelectionChanged;

        public event EventHandler? MonitorsChanged { add { } remove { } }

        public bool HasSubscribers => SelectionChanged is not null;

        public MonitorInfo GetSelectedMonitor() => Selected;

        public IReadOnlyList<MonitorInfo> GetAvailableMonitors() => [Selected];

        public void SelectMonitor(string deviceName) => throw new NotSupportedException();

        // Publish a new selection as the display component would after a user choice.
        public void Select(MonitorInfo monitor)
        {
            Selected = monitor;
            RaiseSelectionChanged(monitor);
        }

        public void RaiseSelectionChanged(MonitorInfo monitor) => SelectionChanged?.Invoke(this, monitor);
    }

    private sealed class FakeFrameSource : IFrameSource
    {
        public MonitorInfo? ActiveMonitor { get; private set; }
        public List<MonitorInfo> SuccessfulStarts { get; } = [];
        public bool FailNextStart { get; set; }
        public bool Disposed { get; private set; }
        public ChannelReader<CapturedFrame> Frames { get; } = Channel.CreateUnbounded<CapturedFrame>().Reader;

        // Expose which monitor is being captured without prescribing the sequence of stop calls.
        public void StartCapture(MonitorInfo monitor)
        {
            if (FailNextStart)
            {
                FailNextStart = false;
                throw new ArgumentException("No such display.", nameof(monitor));
            }

            ActiveMonitor = monitor;
            SuccessfulStarts.Add(monitor);
        }

        public void StopCapture() => ActiveMonitor = null;
        public void Dispose() => Disposed = true;
    }
}
