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

    [Fact]
    public void Constructor_DoesNotStartCapture()
    {
        using var coordinator = CreateCoordinator();

        Assert.Empty(_source.Calls);
    }

    [Fact]
    public void Start_CapturesSelectedMonitor()
    {
        using var coordinator = CreateCoordinator();

        coordinator.Start();

        Assert.Equal([$"start {_main.DeviceName}"], _source.Calls);
    }

    [Fact]
    public void Start_WhenAlreadyStarted_DoesNothing()
    {
        using var coordinator = CreateCoordinator();
        coordinator.Start();

        coordinator.Start();

        Assert.Equal([$"start {_main.DeviceName}"], _source.Calls);
    }

    [Fact]
    public void Start_WhenDisposed_Throws()
    {
        var coordinator = CreateCoordinator();
        coordinator.Dispose();

        Assert.Throws<ObjectDisposedException>(coordinator.Start);
    }

    [Fact]
    public void SelectionChanged_WhenStarted_StopsImmediately()
    {
        using var coordinator = CreateCoordinator();
        coordinator.Start();

        _target.Select(_side);

        Assert.Equal([$"start {_main.DeviceName}", "stop"], _source.Calls);
    }

    [Fact]
    public void SelectionChanged_WhenStarted_RestartsOnNewMonitorAfterDelay()
    {
        using var coordinator = CreateCoordinator();
        coordinator.Start();
        _target.Select(_side);

        _time.Advance(CaptureCoordinator.RestartDelay);

        Assert.Equal([$"start {_main.DeviceName}", "stop", $"start {_side.DeviceName}"], _source.Calls);
    }

    [Fact]
    public void SelectionChanged_BeforeDelayElapses_DoesNotRestart()
    {
        using var coordinator = CreateCoordinator();
        coordinator.Start();
        _target.Select(_side);

        _time.Advance(CaptureCoordinator.RestartDelay - TimeSpan.FromMilliseconds(1));

        Assert.Equal([$"start {_main.DeviceName}", "stop"], _source.Calls);
    }

    [Fact]
    public void SelectionChanged_InBurst_RestartsOnceAfterLastChange()
    {
        using var coordinator = CreateCoordinator();
        coordinator.Start();
        var halfDelay = CaptureCoordinator.RestartDelay / 2;

        _target.Select(_side);
        _time.Advance(halfDelay);
        _target.Select(_main);
        _time.Advance(halfDelay);

        // The second change pushed the restart back, so it hasn't happened yet.
        Assert.Equal([$"start {_main.DeviceName}", "stop", "stop"], _source.Calls);

        _time.Advance(halfDelay);

        Assert.Equal([$"start {_main.DeviceName}", "stop", "stop", $"start {_main.DeviceName}"], _source.Calls);
    }

    [Fact]
    public void SelectionChanged_WhenNotStarted_DoesNothing()
    {
        using var coordinator = CreateCoordinator();

        _target.Select(_side);
        _time.Advance(CaptureCoordinator.RestartDelay);

        Assert.Empty(_source.Calls);
    }

    [Fact]
    public void SelectionChanged_RestartsOnLatestSelectionNotEventArgument()
    {
        using var coordinator = CreateCoordinator();
        coordinator.Start();

        // An event carrying a selection that has already moved on.
        _target.RaiseSelectionChanged(_main);
        _target.Selected = _side;
        _time.Advance(CaptureCoordinator.RestartDelay);

        Assert.Equal($"start {_side.DeviceName}", _source.Calls[^1]);
    }

    [Fact]
    public void SelectionChanged_WhenRestartFails_RetriesOnNextChange()
    {
        using var coordinator = CreateCoordinator();
        coordinator.Start();
        _source.FailNextStart = true;

        _target.Select(_side);
        _time.Advance(CaptureCoordinator.RestartDelay);
        _target.Select(_main);
        _time.Advance(CaptureCoordinator.RestartDelay);

        Assert.Equal(
            [$"start {_main.DeviceName}", "stop", $"start {_side.DeviceName} (failed)", "stop", $"start {_main.DeviceName}"],
            _source.Calls);
    }

    [Fact]
    public void Stop_StopsCapture()
    {
        using var coordinator = CreateCoordinator();
        coordinator.Start();

        coordinator.Stop();

        Assert.Equal([$"start {_main.DeviceName}", "stop"], _source.Calls);
    }

    [Fact]
    public void Stop_WhenNotStarted_DoesNothing()
    {
        using var coordinator = CreateCoordinator();

        coordinator.Stop();

        Assert.Empty(_source.Calls);
    }

    [Fact]
    public void Stop_CancelsPendingRestart()
    {
        using var coordinator = CreateCoordinator();
        coordinator.Start();
        _target.Select(_side);

        coordinator.Stop();
        _time.Advance(CaptureCoordinator.RestartDelay);

        Assert.Equal([$"start {_main.DeviceName}", "stop", "stop"], _source.Calls);
    }

    [Fact]
    public void Start_AfterStopDuringPendingRestart_StartsImmediatelyOnce()
    {
        using var coordinator = CreateCoordinator();
        coordinator.Start();
        _target.Select(_side);
        coordinator.Stop();

        coordinator.Start();
        _time.Advance(CaptureCoordinator.RestartDelay);

        Assert.Equal([$"start {_main.DeviceName}", "stop", "stop", $"start {_side.DeviceName}"], _source.Calls);
    }

    [Fact]
    public void SelectionChanged_AfterStop_DoesNothing()
    {
        using var coordinator = CreateCoordinator();
        coordinator.Start();
        coordinator.Stop();

        _target.Select(_side);
        _time.Advance(CaptureCoordinator.RestartDelay);

        Assert.Equal([$"start {_main.DeviceName}", "stop"], _source.Calls);
    }

    [Fact]
    public void Dispose_StopsCaptureAndUnsubscribes()
    {
        var coordinator = CreateCoordinator();
        coordinator.Start();

        coordinator.Dispose();

        Assert.Equal([$"start {_main.DeviceName}", "stop"], _source.Calls);
        Assert.False(_target.HasSubscribers);
    }

    [Fact]
    public void Dispose_CancelsPendingRestart()
    {
        var coordinator = CreateCoordinator();
        coordinator.Start();
        _target.Select(_side);

        coordinator.Dispose();
        _time.Advance(CaptureCoordinator.RestartDelay);

        Assert.Equal([$"start {_main.DeviceName}", "stop", "stop"], _source.Calls);
    }

    private CaptureCoordinator CreateCoordinator() => new(_target, _source, _time);

    private sealed class FakeDisplayTarget(MonitorInfo selected) : IDisplayTarget
    {
        public MonitorInfo Selected { get; set; } = selected;

        public event EventHandler<MonitorInfo>? SelectionChanged;

        public bool HasSubscribers => SelectionChanged is not null;

        public MonitorInfo GetSelectedMonitor() => Selected;

        public IReadOnlyList<MonitorInfo> GetAvailableMonitors() => [Selected];

        public void SelectMonitor(string deviceName) => throw new NotSupportedException();

        public void Select(MonitorInfo monitor)
        {
            Selected = monitor;
            RaiseSelectionChanged(monitor);
        }

        public void RaiseSelectionChanged(MonitorInfo monitor) => SelectionChanged?.Invoke(this, monitor);
    }

    private sealed class FakeFrameSource : IFrameSource
    {
        public List<string> Calls { get; } = [];

        public bool FailNextStart { get; set; }

        public ChannelReader<CapturedFrame> Frames { get; } = Channel.CreateUnbounded<CapturedFrame>().Reader;

        public void StartCapture(MonitorInfo monitor)
        {
            if (FailNextStart)
            {
                FailNextStart = false;
                Calls.Add($"start {monitor.DeviceName} (failed)");
                throw new ArgumentException("No such display.", nameof(monitor));
            }

            Calls.Add($"start {monitor.DeviceName}");
        }

        public void StopCapture() => Calls.Add("stop");

        public void Dispose()
        {
        }
    }
}
