using Clipping.Core.Audio;

namespace Clipping.Core.Tests.Audio;

public sealed class AudioTimelineTests
{
    // One frame per millisecond keeps sample positions readable.
    private static readonly AudioFormat _mono = new(1000, 1);
    private static readonly TimeSpan _start = TimeSpan.FromHours(5);

    // Loopback sends nothing while nothing plays; sound after the quiet must stay where it happened.
    [Fact]
    public void Render_GapBetweenPackets_IsSilenceAndLaterSoundKeepsItsTime()
    {
        // Arrange
        AudioPacket[] packets =
        [
            new(_start, _mono, [1, 1, 1, 1, 1]),
            new(_start + Ms(20), _mono, [2, 2, 2, 2, 2]),
        ];

        // Act
        var track = AudioTimeline.Render(_mono, packets, _start, _start + Ms(30));

        // Assert
        short[] expected = [1, 1, 1, 1, 1, .. new short[15], 2, 2, 2, 2, 2, .. new short[5]];
        Assert.Equal(expected, track.Samples);
    }

    // Timestamp jitter must not open tiny gaps or overlaps, which play as clicks.
    [Fact]
    public void Render_PacketSlightlyOffThePreviousEnd_IsJoinedOnContinuously()
    {
        // Arrange
        AudioPacket[] packets =
        [
            new(_start, _mono, [1, 1, 1, 1, 1]),
            new(_start + Ms(7), _mono, [2, 2, 2]),
            new(_start + Ms(6), _mono, [3, 3]),
        ];

        // Act
        var track = AudioTimeline.Render(_mono, packets, _start, _start + Ms(12));

        // Assert
        short[] expected = [1, 1, 1, 1, 1, 2, 2, 2, 3, 3, 0, 0];
        Assert.Equal(expected, track.Samples);
    }

    [Fact]
    public void Render_PacketBeyondJitterTolerance_GoesBackToItsOwnTimestamp()
    {
        // Arrange
        var tolerance = (int)AudioTimeline.JitterTolerance.TotalMilliseconds;
        AudioPacket[] packets =
        [
            new(_start, _mono, [1, 1]),
            new(_start + Ms(2 + tolerance + 1), _mono, [2]),
        ];

        // Act
        var track = AudioTimeline.Render(_mono, packets, _start, _start + Ms(2 + tolerance + 2));

        // Assert
        Assert.Equal(2, track.Samples[2 + tolerance + 1]);
        Assert.Equal(0, track.Samples[2]);
    }

    // The track starts exactly with the clip's first frame and lasts exactly as long as its video.
    [Fact]
    public void Render_PacketsStraddlingTheRange_AreTrimmedToIt()
    {
        // Arrange
        var stereo = new AudioFormat(1000, 2);
        AudioPacket[] packets =
        [
            new(_start - Ms(1), stereo, [1, -1, 2, -2, 3, -3]),
            new(_start + Ms(2), stereo, [4, -4, 5, -5]),
        ];

        // Act
        var track = AudioTimeline.Render(stereo, packets, _start, _start + Ms(3));

        // Assert
        short[] expected = [2, -2, 3, -3, 4, -4];
        Assert.Equal(expected, track.Samples);
        Assert.Equal(Ms(3), track.Duration);
    }

    [Fact]
    public void Render_NoPackets_IsSilenceForTheWholeRange()
    {
        // Act
        var track = AudioTimeline.Render(_mono, [], _start, _start + Ms(4));

        // Assert
        Assert.Equal(new short[4], track.Samples);
    }

    private static TimeSpan Ms(int milliseconds) => TimeSpan.FromMilliseconds(milliseconds);
}
