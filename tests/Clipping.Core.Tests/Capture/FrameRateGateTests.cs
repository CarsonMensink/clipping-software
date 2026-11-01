using Clipping.Core.Capture;

namespace Clipping.Core.Tests.Capture;

public sealed class FrameRateGateTests
{
    // WGC's boot-relative timestamps start well past zero.
    private static readonly TimeSpan _start = TimeSpan.FromHours(5);

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_WithNonPositiveFrameRate_Throws(int frameRate)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FrameRateGate(frameRate));
    }

    [Fact]
    public void TryPass_FirstFrame_Passes()
    {
        var gate = new FrameRateGate(30);

        Assert.True(gate.TryPass(_start));
    }

    [Fact]
    public void TryPass_FrameSoonAfterLast_IsSkipped()
    {
        var gate = new FrameRateGate(30);
        gate.TryPass(_start);

        Assert.False(gate.TryPass(_start + TimeSpan.FromMilliseconds(4)));
    }

    [Fact]
    public void TryPass_FrameOneIntervalLater_Passes()
    {
        var gate = new FrameRateGate(30);
        gate.TryPass(_start);

        Assert.True(gate.TryPass(_start + TimeSpan.FromSeconds(1.0 / 30)));
    }

    [Fact]
    public void TryPass_FrameOlderThanLastPassed_IsSkipped()
    {
        var gate = new FrameRateGate(30);
        gate.TryPass(_start);

        Assert.False(gate.TryPass(_start - TimeSpan.FromSeconds(1)));
    }

    [Theory]
    [InlineData(240, 30)]
    [InlineData(240, 60)]
    [InlineData(144, 60)]
    [InlineData(60, 60)]
    public void TryPass_MonitorFasterOrEqual_PassesFrameRatePerSecond(int monitorHz, int frameRate)
    {
        var gate = new FrameRateGate(frameRate);

        var passed = CountPassed(gate, monitorHz, seconds: 10);

        Assert.InRange(passed, frameRate * 10 - 1, frameRate * 10 + 1);
    }

    [Theory]
    [InlineData(24, 30)]
    [InlineData(50, 60)]
    public void TryPass_MonitorSlower_PassesEveryFrame(int monitorHz, int frameRate)
    {
        var gate = new FrameRateGate(frameRate);

        var passed = CountPassed(gate, monitorHz, seconds: 10);

        Assert.Equal(monitorHz * 10, passed);
    }

    [Fact]
    public void TryPass_AfterIdleGap_PassesOneFrameNotABurst()
    {
        var gate = new FrameRateGate(30);
        gate.TryPass(_start);
        var resumed = _start + TimeSpan.FromSeconds(3);

        Assert.True(gate.TryPass(resumed));
        Assert.False(gate.TryPass(resumed + TimeSpan.FromMilliseconds(4)));
    }

    [Fact]
    public void TryPass_WithJitteryArrivals_DoesNotDrift()
    {
        var gate = new FrameRateGate(60);
        var random = new Random(1234);
        var passed = 0;

        // 240 Hz with up to ±1 ms of jitter on each frame.
        for (var i = 0; i < 240 * 10; i++)
        {
            var jitter = TimeSpan.FromMilliseconds(random.NextDouble() * 2 - 1);
            if (gate.TryPass(_start + TimeSpan.FromSeconds(i / 240.0) + jitter))
            {
                passed++;
            }
        }

        Assert.InRange(passed, 599, 601);
    }

    [Fact]
    public void TryPass_MonitorAtFrameRateWithJitter_PassesEveryFrame()
    {
        var gate = new FrameRateGate(60);
        var random = new Random(1234);

        // 60 Hz with up to ±2 ms of jitter, so frames straddle every interval boundary.
        for (var i = 0; i < 60 * 10; i++)
        {
            var jitter = TimeSpan.FromMilliseconds(random.NextDouble() * 4 - 2);
            Assert.True(gate.TryPass(_start + TimeSpan.FromSeconds(i / 60.0) + jitter));
        }
    }

    private static int CountPassed(FrameRateGate gate, int monitorHz, int seconds)
    {
        var passed = 0;
        for (var i = 0; i < monitorHz * seconds; i++)
        {
            if (gate.TryPass(_start + TimeSpan.FromSeconds((double)i / monitorHz)))
            {
                passed++;
            }
        }

        return passed;
    }
}
