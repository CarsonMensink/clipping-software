using Clipping.Core.Audio;
using Microsoft.Extensions.Time.Testing;

namespace Clipping.Core.Tests.Audio;

public sealed class RollingAudioBufferTests
{
    // One frame per millisecond keeps sample positions readable.
    private static readonly AudioFormat _mono = new(1000, 1);

    private readonly FakeTimeProvider _time = new();

    [Fact]
    public void Add_BeyondRetention_KeepsOnlyTheMostRecentAudio()
    {
        // Arrange
        var buffer = new RollingAudioBuffer(Ms(100), _time);

        // Act
        for (var ms = 0; ms < 300; ms += 10)
        {
            buffer.Add(Packet(Ms(ms), 10, 1));
        }

        // Assert
        Assert.InRange(buffer.RetainedDuration, Ms(100), Ms(110));
    }

    [Fact]
    public void Add_InADifferentFormat_StartsOver()
    {
        // Arrange
        var buffer = new RollingAudioBuffer(Ms(100), _time);
        buffer.Add(Packet(Ms(0), 10, 1));

        // Act
        buffer.Add(new AudioPacket(Ms(10), new AudioFormat(1000, 2), new short[20]));

        // Assert
        Assert.Equal(Ms(10), buffer.RetainedDuration);
    }

    [Fact]
    public async Task ReadAsync_WhenAudioReachesTheEnd_ReturnsThatStretchWithoutWaiting()
    {
        // Arrange
        var now = Now();
        var buffer = new RollingAudioBuffer(TimeSpan.FromSeconds(1), _time);
        buffer.Add(Packet(now - Ms(30), 10, 1));
        buffer.Add(Packet(now - Ms(20), 10, 2));
        buffer.Add(Packet(now - Ms(10), 10, 3));

        // Act
        var read = buffer.ReadAsync(now - Ms(25), now - Ms(15));

        // Assert
        Assert.True(read.IsCompletedSuccessfully);
        short[] expected = [1, 1, 1, 1, 1, 2, 2, 2, 2, 2];
        Assert.Equal(expected, (await read).Samples);
    }

    // The device delivers audio a little after the video for the same moment is buffered.
    [Fact]
    public async Task ReadAsync_WhenTheEndHasNotArrivedYet_WaitsForIt()
    {
        // Arrange
        var now = Now();
        var buffer = new RollingAudioBuffer(TimeSpan.FromSeconds(1), _time);
        buffer.Add(Packet(now - Ms(10), 5, 1));

        // Act
        var read = buffer.ReadAsync(now - Ms(10), now);
        var waited = !read.IsCompleted;
        buffer.Add(Packet(now - Ms(5), 5, 2));
        _time.Advance(RollingAudioBuffer.PollInterval);

        // Assert
        Assert.True(waited);
        short[] expected = [1, 1, 1, 1, 1, 2, 2, 2, 2, 2];
        Assert.Equal(expected, (await read).Samples);
    }

    // Nothing arrives while nothing plays, which must not hold a save up indefinitely.
    [Fact]
    public async Task ReadAsync_WhenNothingArrives_ReturnsSilenceOnceLateAudioCanNoLongerCome()
    {
        // Arrange
        var now = Now();
        var buffer = new RollingAudioBuffer(TimeSpan.FromSeconds(1), _time);

        // Act
        var read = buffer.ReadAsync(now - Ms(20), now);
        await AdvanceAsync(RollingAudioBuffer.MaxLatency - RollingAudioBuffer.PollInterval);
        var waitedUntilLimit = !read.IsCompleted;
        await AdvanceAsync(RollingAudioBuffer.PollInterval);

        // Assert
        Assert.True(waitedUntilLimit);
        var track = await read;
        Assert.Equal(Ms(20), track.Duration);
        Assert.All(track.Samples, sample => Assert.Equal(0, sample));
    }

    // One poll at a time, giving each wake-up the chance to schedule the next before time moves on.
    private async Task AdvanceAsync(TimeSpan total)
    {
        for (var step = TimeSpan.Zero; step < total; step += RollingAudioBuffer.PollInterval)
        {
            _time.Advance(RollingAudioBuffer.PollInterval);
            await Task.Delay(5);
        }
    }

    private TimeSpan Now() => _time.GetElapsedTime(0, _time.GetTimestamp());

    private static AudioPacket Packet(TimeSpan timestamp, int frames, short value) =>
        new(timestamp, _mono, Enumerable.Repeat(value, frames).ToArray());

    private static TimeSpan Ms(int milliseconds) => TimeSpan.FromMilliseconds(milliseconds);
}
