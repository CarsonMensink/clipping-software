using Clipping.Core.Clips;
using Clipping.Core.Encoding;
using Clipping.Windows.Encoding;

namespace Clipping.Core.Tests.Windows;

public sealed class ClipCompatibilityTests
{
    // Later keyframes remain saveable after the only in-band parameter sets leave the buffer.
    [Fact]
    public void Headers_SurviveFirstGopEviction()
    {
        var headers = new H264ParameterSets();
        var buffer = new RollingClipBuffer(TimeSpan.FromSeconds(1));
        byte[] first = [0, 0, 0, 1, 0x67, 0x12, 0, 0, 1, 0x68, 0x34, 0, 0, 1, 0x65, 0x56];
        byte[] later = [0, 0, 1, 0x65, 0x78];
        for (var second = 0; second < 4; second++)
        {
            var data = second == 0 ? first : later;
            buffer.Add(new EncodedSample(TimeSpan.FromSeconds(second), TimeSpan.FromSeconds(1), true, 1920, 1080, data)
            {
                SequenceHeader = headers.Update(data),
            });
        }

        var saved = Assert.Single(buffer.Snapshot().Samples);
        Assert.Equal(TimeSpan.FromSeconds(3), saved.Timestamp);
        Assert.Equal(new byte[] { 0, 0, 0, 1, 0x67, 0x12, 0, 0, 0, 1, 0x68, 0x34 }, saved.SequenceHeader);
    }

    // Media-type headers can seed the cache before any keyframe carries them in-band.
    [Fact]
    public void Headers_FromMediaType_AreAttachedToLaterSamples()
    {
        var headers = new H264ParameterSets();
        var seeded = headers.Update([0, 0, 1, 0x67, 0x11, 0, 0, 0, 1, 0x68, 0x22]);
        Assert.NotNull(seeded);
        Assert.Same(seeded, headers.Update([0, 0, 1, 0x65, 0x33]));
    }

    // An SPS alone cannot describe a playable stream, and a new encoder cannot borrow old headers.
    [Fact]
    public void Headers_RequireBothParameterSetsAndStayWithinSession()
    {
        var headers = new H264ParameterSets();
        Assert.Null(headers.Update([0, 0, 1, 0x67, 0x11]));
        Assert.NotNull(headers.Update([0, 0, 1, 0x68, 0x22]));
        Assert.Null(new H264ParameterSets().Update([0, 0, 1, 0x65, 0x33]));
    }

    // Updating the cache must not mutate header bytes already held by a saved snapshot.
    [Fact]
    public void Headers_UpdateWithoutChangingEarlierSnapshots()
    {
        var headers = new H264ParameterSets();
        var previous = headers.Update([0, 0, 1, 0x67, 0x11, 0, 0, 1, 0x68, 0x22]);
        var next = headers.Update([0, 0, 1, 0x67, 0x33, 0, 0, 1, 0x68, 0x44]);
        Assert.Equal(new byte[] { 0, 0, 0, 1, 0x67, 0x11, 0, 0, 0, 1, 0x68, 0x22 }, previous);
        Assert.NotEqual(previous, next);
    }

    // Any failed write, including a managed IOException, must remove the partial clip.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedWrite_RemovesPartialFile(bool cancel)
    {
        var path = Path.Combine(Path.GetTempPath(), $"clipping-test-{Guid.NewGuid():N}.mp4");
        Exception failure = cancel ? new OperationCanceledException() : new IOException("No codec headers.");
        try
        {
            var actual = Record.Exception(() => ClipFileWrite.WriteOrDelete(path, () =>
            {
                File.WriteAllBytes(path, [0, 1, 2]);
                throw failure;
            }));
            Assert.Same(failure, actual);
            Assert.False(File.Exists(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    // Successful writes must remain available for playback.
    [Fact]
    public void SuccessfulWrite_PreservesFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"clipping-test-{Guid.NewGuid():N}.mp4");
        try
        {
            ClipFileWrite.WriteOrDelete(path, () => File.WriteAllBytes(path, [1, 2, 3]));
            Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
