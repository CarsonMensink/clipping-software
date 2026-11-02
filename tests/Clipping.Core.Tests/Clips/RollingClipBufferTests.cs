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

    // Short gameplay is retained intact and reports the duration that a saved highlight will contain.
    [Fact]
    public void Snapshot_WithinRetention_PreservesAllFramesAndReportsDuration()
    {
        // Arrange
        var buffer = new RollingClipBuffer(TimeSpan.FromSeconds(3));

        // Act
        AddSeconds(buffer, 2);
        var snapshot = buffer.Snapshot();

        // Assert
        Assert.Equal(Enumerable.Range(0, 20), Frames(snapshot));
        Assert.Equal(TimeSpan.FromSeconds(2), snapshot.Duration);
        Assert.Equal(snapshot.Duration, buffer.RetainedDuration);
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

    // A resolution change must start a fresh decodable clip rather than mixing incompatible frames.
    [Fact]
    public void ResolutionChange_DropsOldVideoAndWaitsForNewKeyframe()
    {
        // Arrange
        var buffer = new RollingClipBuffer(TimeSpan.FromSeconds(3));
        AddSeconds(buffer, 2);

        // Act
        buffer.Add(Sample(20, keyframe: false, width: 1920, height: 1080));

        // Assert
        Assert.Empty(buffer.Snapshot().Samples);

        // Act: the new stream becomes playable at its first keyframe.
        buffer.Add(Sample(21, keyframe: true, width: 1920, height: 1080));

        // Assert
        var sample = Assert.Single(buffer.Snapshot().Samples);
        Assert.Equal(At(21), sample.Timestamp);
        Assert.True(sample.IsKeyframe);
        Assert.Equal((1920, 1080), (sample.Width, sample.Height));
    }

    // A keyframe arriving with the resize should be usable immediately.
    [Fact]
    public void ResolutionChange_StartingWithKeyframe_IsImmediatelyDecodable()
    {
        // Arrange
        var buffer = new RollingClipBuffer(TimeSpan.FromSeconds(3));
        AddSeconds(buffer, 2);

        // Act
        buffer.Add(Sample(20, keyframe: true, width: 1920, height: 1080));

        // Assert
        var sample = Assert.Single(buffer.Snapshot().Samples);
        Assert.Equal(At(20), sample.Timestamp);
        Assert.True(sample.IsKeyframe);
        Assert.Equal((1920, 1080), (sample.Width, sample.Height));
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

    // Saving and capture must both make progress while every observed snapshot remains decodable (#7).
    [Fact]
    public async Task ConcurrentCaptureAndSnapshots_ProduceDecodableOrderedVideo()
    {
        // Arrange
        var buffer = new RollingClipBuffer(TimeSpan.FromSeconds(1));
        AddSeconds(buffer, 1);
        using var round = new Barrier(2);
        const int rounds = 100;
        var snapshots = new List<ClipSnapshot>();

        // Act: both workers enter each round together so the reader cannot miss the whole run.
        var writer = Task.Run(() =>
        {
            for (var i = 0; i < rounds; i++)
            {
                Assert.True(round.SignalAndWait(TimeSpan.FromSeconds(5)));
                AddSeconds(buffer, 10, startSecond: 1 + i * 10);
            }
        });
        var reader = Task.Run(() =>
        {
            for (var i = 0; i < rounds; i++)
            {
                Assert.True(round.SignalAndWait(TimeSpan.FromSeconds(5)));
                snapshots.Add(buffer.Snapshot());
            }
        });
        await Task.WhenAll(writer, reader).WaitAsync(TimeSpan.FromSeconds(10));

        // Assert: inspect the saved snapshots after further writes as well as their original acquisition.
        Assert.Equal(rounds, snapshots.Count);
        Assert.All(snapshots, snapshot =>
        {
            Assert.NotEmpty(snapshot.Samples);
            Assert.True(snapshot.Samples[0].IsKeyframe);
            Assert.InRange(snapshot.Duration, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));
            var times = snapshot.Samples.Select(sample => sample.Timestamp).ToArray();
            Assert.Equal(times.Order(), times);
            Assert.Equal(times.Length, times.Distinct().Count());
        });
    }

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
