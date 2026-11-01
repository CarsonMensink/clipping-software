using Clipping.Core.Capture;
using Clipping.Core.Encoding;

namespace Clipping.Core.Tests.Encoding;

public sealed class FramePacerTests
{
    // WGC's boot-relative timestamps start well past zero.
    private static readonly TimeSpan _start = TimeSpan.FromHours(5);

    private static readonly TimeSpan _interval60 = TimeSpan.FromTicks(TimeSpan.TicksPerSecond / 60);

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_WithNonPositiveFrameRate_Throws(int frameRate)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FramePacer(frameRate));
    }

    [Fact]
    public void Place_FirstFrame_StartsTimelineAtItsTimestamp()
    {
        var pacer = new FramePacer(60);

        var placement = pacer.Place(_start);

        Assert.Empty(placement.Repeats);
        Assert.Equal(_start, placement.Time);
    }

    [Fact]
    public void Place_FrameInNextSlot_HasNoRepeats()
    {
        var pacer = new FramePacer(60);
        pacer.Place(_start);

        var placement = pacer.Place(_start + _interval60);

        Assert.Empty(placement.Repeats);
        Assert.Equal(At(1), placement.Time);
    }

    [Fact]
    public void Place_AfterGap_RepeatsPreviousFrameForEachSkippedSlot()
    {
        var pacer = new FramePacer(60);
        pacer.Place(_start);

        var placement = pacer.Place(_start + TimeSpan.FromSeconds(4.0 / 60));

        Assert.Equal([At(1), At(2), At(3)], placement.Repeats);
        Assert.Equal(At(4), placement.Time);
    }

    [Fact]
    public void Place_SnapsJitteredTimestampToNearestSlot()
    {
        var pacer = new FramePacer(60);
        pacer.Place(_start);

        var placement = pacer.Place(_start + _interval60 * 2 + TimeSpan.FromMilliseconds(3));

        Assert.Equal([At(1)], placement.Repeats);
        Assert.Equal(At(2), placement.Time);
    }

    [Fact]
    public void Place_FrameWhoseSlotIsTaken_MovesToNextSlot()
    {
        var pacer = new FramePacer(60);
        pacer.Place(_start);

        // Nearest slot is 0, already taken; slot 1 is only 0.6 intervals ahead.
        var placement = pacer.Place(_start + _interval60 * 0.4);

        Assert.Empty(placement.Repeats);
        Assert.Equal(At(1), placement.Time);
    }

    [Fact]
    public void Place_FrameThatWouldRunAheadOfRealTime_IsDropped()
    {
        var pacer = new FramePacer(60);
        pacer.Place(_start);
        pacer.Place(_start + _interval60 * 0.4);

        // Slot 1 is taken, and slot 2 would be 1.5 intervals ahead.
        var placement = pacer.Place(_start + _interval60 * 0.5);

        Assert.Empty(placement.Repeats);
        Assert.Null(placement.Time);
    }

    [Fact]
    public void Place_FrameOlderThanTimeline_IsDropped()
    {
        var pacer = new FramePacer(60);
        pacer.Place(_start);

        Assert.Null(pacer.Place(_start - TimeSpan.FromSeconds(1)).Time);
    }

    [Fact]
    public void FillUntil_BeforeFirstFrame_ReturnsNothing()
    {
        var pacer = new FramePacer(60);

        Assert.Empty(pacer.FillUntil(_start));
    }

    [Fact]
    public void FillUntil_RepeatsLastFrameForEverySlotUpToTime()
    {
        var pacer = new FramePacer(60);
        pacer.Place(_start);

        var repeats = pacer.FillUntil(_start + _interval60 * 3.5);

        Assert.Equal([At(1), At(2), At(3)], repeats);
    }

    [Fact]
    public void FillUntil_CalledAgain_OnlyFillsNewSlots()
    {
        var pacer = new FramePacer(60);
        pacer.Place(_start);
        pacer.FillUntil(At(2));

        var repeats = pacer.FillUntil(At(4));

        Assert.Equal([At(3), At(4)], repeats);
    }

    [Fact]
    public void FillUntil_ThenFrame_TakesSlotAfterFilledOnes()
    {
        var pacer = new FramePacer(60);
        pacer.Place(_start);
        pacer.FillUntil(At(2));

        var placement = pacer.Place(_start + _interval60 * 3);

        Assert.Empty(placement.Repeats);
        Assert.Equal(At(3), placement.Time);
    }

    [Fact]
    public void FillUntil_TimeInPast_ReturnsNothing()
    {
        var pacer = new FramePacer(60);
        pacer.Place(_start);
        pacer.Place(_start + _interval60 * 5);

        Assert.Empty(pacer.FillUntil(At(2)));
    }

    [Theory]
    [InlineData(24, 30)]
    [InlineData(37, 60)]
    [InlineData(60, 60)]
    public void Timeline_WithSlowerOrEqualSource_HasExactlyFrameRateSlotsPerSecond(int sourceHz, int frameRate)
    {
        var pacer = new FramePacer(frameRate);
        var slots = 0;

        for (var i = 0; i < sourceHz * 10; i++)
        {
            var placement = pacer.Place(_start + TimeSpan.FromSeconds((double)i / sourceHz));
            slots += placement.Repeats.Count + (placement.Time is null ? 0 : 1);
        }

        // The last frame lands just under 10 s in, on slot 10 × frameRate − (a fraction).
        Assert.InRange(slots, frameRate * 10 - 1, frameRate * 10);
    }

    [Fact]
    public void Timeline_ThroughGate_IsGaplessAndNeverAheadOfRealTime()
    {
        // 240 Hz with jitter, thinned by the gate, then paced: every slot is filled exactly once.
        var gate = new FrameRateGate(60);
        var pacer = new FramePacer(60);
        var random = new Random(1234);
        var times = new List<TimeSpan>();

        for (var i = 0; i < 240 * 10; i++)
        {
            var timestamp = _start + TimeSpan.FromSeconds(i / 240.0) + TimeSpan.FromMilliseconds(random.NextDouble() * 2 - 1);
            if (!gate.TryPass(timestamp))
            {
                continue;
            }

            var placement = pacer.Place(timestamp);
            times.AddRange(placement.Repeats);
            if (placement.Time is { } time)
            {
                Assert.True(time - timestamp < _interval60);
                times.Add(time);
            }
        }

        // The timeline starts at the first frame through the gate, jitter and all.
        for (var slot = 0; slot < times.Count; slot++)
        {
            Assert.Equal(times[0] + TimeSpan.FromTicks(slot * TimeSpan.TicksPerSecond / 60), times[slot]);
        }

        Assert.InRange(times.Count, 599, 601);
    }

    private static TimeSpan At(long slot) => _start + TimeSpan.FromTicks(slot * TimeSpan.TicksPerSecond / 60);
}
