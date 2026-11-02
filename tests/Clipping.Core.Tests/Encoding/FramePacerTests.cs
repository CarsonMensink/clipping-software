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

    // The video timeline must preserve the first capture timestamp.
    [Fact]
    public void Place_FirstFrame_StartsTimelineAtItsTimestamp()
    {
        // Arrange
        var pacer = new FramePacer(60);

        // Act
        var placement = pacer.Place(_start);

        // Assert
        Assert.Empty(placement.Repeats);
        Assert.Equal(_start, placement.Time);
    }

    // Consecutive captured frames need no fabricated duplicates.
    [Fact]
    public void Place_FrameInNextSlot_HasNoRepeats()
    {
        // Arrange
        var pacer = new FramePacer(60);
        pacer.Place(_start);

        // Act
        var placement = pacer.Place(_start + _interval60);

        // Assert
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

    // A slightly late frame can use the next available video slot.
    [Fact]
    public void Place_FrameWhoseSlotIsTaken_MovesToNextSlot()
    {
        // Arrange
        var pacer = new FramePacer(60);
        pacer.Place(_start);

        // Act
        // Nearest slot is 0, already taken; slot 1 is only 0.6 intervals ahead.
        var placement = pacer.Place(_start + _interval60 * 0.4);

        // Assert
        Assert.Empty(placement.Repeats);
        Assert.Equal(At(1), placement.Time);
    }

    // Crowded arrivals must not push video a full frame ahead of capture.
    [Fact]
    public void Place_FrameThatWouldRunAheadOfRealTime_IsDropped()
    {
        // Arrange
        var pacer = new FramePacer(60);
        pacer.Place(_start);
        pacer.Place(_start + _interval60 * 0.4);

        // Act
        // Slot 1 is taken, and slot 2 would be 1.5 intervals ahead.
        var placement = pacer.Place(_start + _interval60 * 0.5);

        // Assert
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

    // Idle gameplay must still fill the video timeline through the requested time.
    [Fact]
    public void FillUntil_RepeatsLastFrameForEverySlotUpToTime()
    {
        // Arrange
        var pacer = new FramePacer(60);
        pacer.Place(_start);

        // Act
        var repeats = pacer.FillUntil(_start + _interval60 * 3.5);

        // Assert
        Assert.Equal([At(1), At(2), At(3)], repeats);
    }

    // Repeated idle filling must not encode the same video slot twice.
    [Fact]
    public void FillUntil_CalledAgain_OnlyFillsNewSlots()
    {
        // Arrange
        var pacer = new FramePacer(60);
        pacer.Place(_start);
        pacer.FillUntil(At(2));

        // Act
        var repeats = pacer.FillUntil(At(4));

        // Assert
        Assert.Equal([At(3), At(4)], repeats);
    }

    // Live capture must resume in the next slot after idle repeats.
    [Fact]
    public void FillUntil_ThenFrame_TakesSlotAfterFilledOnes()
    {
        // Arrange
        var pacer = new FramePacer(60);
        pacer.Place(_start);
        pacer.FillUntil(At(2));

        // Act
        var placement = pacer.Place(_start + _interval60 * 3);

        // Assert
        Assert.Empty(placement.Repeats);
        Assert.Equal(At(3), placement.Time);
    }

    // A stale fill request must not duplicate already encoded video.
    [Fact]
    public void FillUntil_TimeInPast_ReturnsNothing()
    {
        // Arrange
        var pacer = new FramePacer(60);
        pacer.Place(_start);
        pacer.Place(_start + _interval60 * 5);

        // Act & Assert
        Assert.Empty(pacer.FillUntil(At(2)));
    }

    [Fact]
    public void Place_AfterVeryLongGap_RepeatsOnlyTheLastSecond()
    {
        var pacer = new FramePacer(60);
        pacer.Place(_start);

        var placement = pacer.Place(At(60 * 60 * 60));

        Assert.Equal(60, placement.Repeats.Count);
        Assert.Equal(At(60 * 60 * 60 - 60), placement.Repeats[0]);
        Assert.Equal(At(60 * 60 * 60), placement.Time);
    }

    [Fact]
    public void FillUntil_AfterVeryLongGap_RepeatsOnlyTheLastSecond()
    {
        var pacer = new FramePacer(60);
        pacer.Place(_start);

        var repeats = pacer.FillUntil(At(60 * 60 * 60));

        Assert.Equal(60, repeats.Count);
        Assert.Equal(At(60 * 60 * 60), repeats[^1]);
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
