using Clipping.Core.Clips;
using Clipping.Core.Encoding;

namespace Clipping.Core.Tests.Clips;

public sealed class RollingClipBufferTests
{
    private static readonly TimeSpan _start = TimeSpan.FromHours(5);
    private static readonly TimeSpan _frame = TimeSpan.FromTicks(TimeSpan.TicksPerSecond / 10);

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_WithNonPositiveRetention_Throws(int seconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RollingClipBuffer(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void Snapshot_WhenEmpty_HasNoSamples()
    {
        var buffer = new RollingClipBuffer(TimeSpan.FromSeconds(3));

        Assert.Empty(buffer.Snapshot().Samples);
        Assert.Equal(TimeSpan.Zero, buffer.RetainedDuration);
    }

    [Fact]
    public void Add_FramesBeforeFirstKeyframe_AreDropped()
    {
        var buffer = new RollingClipBuffer(TimeSpan.FromSeconds(3));

        buffer.Add(Sample(0, keyframe: false));
        buffer.Add(Sample(1, keyframe: false));
        buffer.Add(Sample(2, keyframe: true));

        Assert.Equal([2], Frames(buffer.Snapshot()));
    }

    [Fact]
    public void Add_WithinRetention_KeepsEverything()
    {
        var buffer = new RollingClipBuffer(TimeSpan.FromSeconds(3));

        AddSeconds(buffer, 2);

        Assert.Equal(20, buffer.Snapshot().Samples.Count);
        Assert.Equal(TimeSpan.FromSeconds(2), buffer.RetainedDuration);
    }

    [Fact]
    public void Add_PastRetention_EvictsWholeOldestGops()
    {
        var buffer = new RollingClipBuffer(TimeSpan.FromSeconds(3));

        AddSeconds(buffer, 10);

        // The last three one-second GOPs exactly cover the retention.
        var snapshot = buffer.Snapshot();
        Assert.Equal(At(70), snapshot.Samples[0].Timestamp);
        Assert.True(snapshot.Samples[0].IsKeyframe);
        Assert.Equal(TimeSpan.FromSeconds(3), buffer.RetainedDuration);
    }

    [Fact]
    public void Add_PartwayThroughGop_KeepsUpToOneExtraGop()
    {
        var buffer = new RollingClipBuffer(TimeSpan.FromSeconds(3));

        AddSeconds(buffer, 10);
        buffer.Add(Sample(100, keyframe: true));
        buffer.Add(Sample(101, keyframe: false));

        // Dropping the oldest GOP would leave less than three seconds, so it stays.
        Assert.Equal(At(70), buffer.Snapshot().Samples[0].Timestamp);
        Assert.Equal(TimeSpan.FromSeconds(3.2), buffer.RetainedDuration);
    }

    [Fact]
    public void Add_LongRun_StaysBounded()
    {
        var buffer = new RollingClipBuffer(TimeSpan.FromSeconds(3));

        AddSeconds(buffer, 60 * 60);

        Assert.InRange(buffer.Snapshot().Samples.Count, 30, 40);
    }

    [Fact]
    public void Add_SizeChange_StartsOver()
    {
        var buffer = new RollingClipBuffer(TimeSpan.FromSeconds(3));
        AddSeconds(buffer, 2);

        buffer.Add(Sample(20, keyframe: true, width: 1920, height: 1080));

        var snapshot = buffer.Snapshot();
        Assert.Equal([20], Frames(snapshot));
        Assert.Equal(1920, snapshot.Samples[0].Width);
    }

    [Fact]
    public void Add_SizeChangeWithoutKeyframe_IsDroppedUntilOne()
    {
        var buffer = new RollingClipBuffer(TimeSpan.FromSeconds(3));
        AddSeconds(buffer, 2);

        buffer.Add(Sample(20, keyframe: false, width: 1920, height: 1080));

        Assert.Empty(buffer.Snapshot().Samples);
    }

    [Fact]
    public void Retention_Lowered_EvictsOnNextAdd()
    {
        var buffer = new RollingClipBuffer(TimeSpan.FromSeconds(5));
        AddSeconds(buffer, 5);

        buffer.Retention = TimeSpan.FromSeconds(2);
        buffer.Add(Sample(50, keyframe: true));

        Assert.Equal(At(30), buffer.Snapshot().Samples[0].Timestamp);
    }

    [Fact]
    public void Snapshot_IsUnaffectedByLaterAdds()
    {
        var buffer = new RollingClipBuffer(TimeSpan.FromSeconds(3));
        AddSeconds(buffer, 1);

        var snapshot = buffer.Snapshot();
        AddSeconds(buffer, 10, startSecond: 1);

        Assert.Equal(10, snapshot.Samples.Count);
        Assert.Equal(At(0), snapshot.Samples[0].Timestamp);
    }

    [Fact]
    public void Snapshot_Duration_SpansFirstFrameToEndOfLast()
    {
        var buffer = new RollingClipBuffer(TimeSpan.FromSeconds(3));
        AddSeconds(buffer, 2);

        Assert.Equal(TimeSpan.FromSeconds(2), buffer.Snapshot().Duration);
    }

    [Fact]
    public async Task AddAndSnapshot_FromDifferentThreads_AlwaysStartOnKeyframe()
    {
        var buffer = new RollingClipBuffer(TimeSpan.FromSeconds(1));
        var stop = false;
        var torn = false;

        var reader = Task.Run(() =>
        {
            while (!Volatile.Read(ref stop))
            {
                var samples = buffer.Snapshot().Samples;
                torn |= samples.Count > 0 && !samples[0].IsKeyframe;
            }
        });

        AddSeconds(buffer, 600);
        Volatile.Write(ref stop, true);
        await reader;

        Assert.False(torn);
    }

    // Ten frames a second, a keyframe every second.
    private static void AddSeconds(RollingClipBuffer buffer, int seconds, int startSecond = 0)
    {
        for (var i = startSecond * 10; i < (startSecond + seconds) * 10; i++)
        {
            buffer.Add(Sample(i, keyframe: i % 10 == 0));
        }
    }

    private static EncodedSample Sample(int frame, bool keyframe, int width = 2560, int height = 1440) =>
        new(At(frame), _frame, keyframe, width, height, [(byte)frame]);

    private static TimeSpan At(int frame) => _start + _frame * frame;

    private static int[] Frames(ClipSnapshot snapshot) => snapshot.Samples.Select(s => (int)s.Data[0]).ToArray();
}
