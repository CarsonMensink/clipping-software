using System.Threading.Channels;
using Clipping.Core.Capture;
using Clipping.Core.Encoding;
using Microsoft.Extensions.Time.Testing;

namespace Clipping.Core.Tests.Encoding;

public sealed class EncodingLoopTests
{
    private static readonly TimeSpan _start = TimeSpan.FromHours(5);

    private readonly Channel<CapturedFrame> _frames = Channel.CreateUnbounded<CapturedFrame>();
    private readonly List<FakeEncoder> _encoders = [];
    private readonly List<FakeOwner> _owners = [];

    [Fact]
    public void Process_FirstFrame_CreatesEncoderOfItsSizeAndEncodesIt()
    {
        using var loop = CreateLoop();

        loop.Process(Frame(1, At(0)));

        var encoder = Assert.Single(_encoders);
        Assert.Equal((2560, 1440, 60), (encoder.Width, encoder.Height, encoder.FrameRate));
        Assert.Equal([(1, At(0))], encoder.Encoded);
    }

    [Fact]
    public void Process_AfterGap_RepeatsPreviousFrameThenEncodesNew()
    {
        using var loop = CreateLoop();
        loop.Process(Frame(1, At(0)));

        loop.Process(Frame(2, At(3)));

        Assert.Equal([(1, At(0)), (1, At(1)), (1, At(2)), (2, At(3))], _encoders[0].Encoded);
    }

    [Fact]
    public void Process_KeepsLatestFrameAndDisposesTheOneBefore()
    {
        using var loop = CreateLoop();
        loop.Process(Frame(1, At(0)));

        loop.Process(Frame(2, At(1)));

        Assert.True(_owners[0].IsDisposed);
        Assert.False(_owners[1].IsDisposed);
    }

    [Fact]
    public void Process_FrameThePacerDrops_IsDisposedAndNotEncoded()
    {
        using var loop = CreateLoop();
        loop.Process(Frame(1, At(0)));
        loop.Process(Frame(2, At(0.4)));

        loop.Process(Frame(3, At(0.5)));

        Assert.Equal([(1, At(0)), (2, At(1))], _encoders[0].Encoded);
        Assert.True(_owners[2].IsDisposed);
        Assert.False(_owners[1].IsDisposed);
    }

    [Fact]
    public void Process_SizeChange_DrainsOldEncoderAndStartsNewTimeline()
    {
        using var loop = CreateLoop();
        loop.Process(Frame(1, At(0)));

        loop.Process(Frame(2, At(5), 1920, 1080));

        Assert.Equal(2, _encoders.Count);
        Assert.True(_encoders[0].IsDisposed);
        Assert.True(_owners[0].IsDisposed);

        // No repeats of the old frame: the new timeline starts at the new frame.
        Assert.Equal([(2, At(5))], _encoders[1].Encoded);
        Assert.Equal((1920, 1080), (_encoders[1].Width, _encoders[1].Height));
    }

    [Fact]
    public void Process_WhenEncoderFails_DropsItAndNextFrameStartsANewOne()
    {
        using var loop = CreateLoop();
        loop.Process(Frame(1, At(0)));
        _encoders[0].FailNext = true;

        loop.Process(Frame(2, At(1)));
        loop.Process(Frame(3, At(2)));

        Assert.True(_encoders[0].IsDisposed);
        Assert.True(_owners[0].IsDisposed);
        Assert.True(_owners[1].IsDisposed);
        Assert.Equal([(3, At(2))], _encoders[1].Encoded);
    }

    [Fact]
    public void Fill_BeforeFirstFrame_DoesNothing()
    {
        using var loop = CreateLoop();

        loop.Fill(At(100));

        Assert.Empty(_encoders);
    }

    [Fact]
    public void Fill_RepeatsLatestFrameUpToHoldbackBeforeNow()
    {
        using var loop = CreateLoop();
        loop.Process(Frame(1, At(0)));

        // Six slots behind now is the 100 ms holdback; slots 1–3 are before it.
        loop.Fill(At(9.5));

        Assert.Equal([(1, At(0)), (1, At(1)), (1, At(2)), (1, At(3))], _encoders[0].Encoded);
    }

    [Fact]
    public void Fill_ThenLateFrame_TakesNextFreeSlot()
    {
        using var loop = CreateLoop();
        loop.Process(Frame(1, At(0)));
        loop.Fill(At(9.5));

        loop.Process(Frame(2, At(4)));

        Assert.Equal((2, At(4)), _encoders[0].Encoded[^1]);
    }

    [Fact]
    public void Dispose_DrainsEncoderReleasesLatestFrameAndCompletesSamples()
    {
        var loop = CreateLoop();
        loop.Process(Frame(1, At(0)));

        loop.Dispose();

        Assert.True(_encoders[0].IsDisposed);
        Assert.True(_owners[0].IsDisposed);

        // A channel only reports completion once its items are read.
        while (loop.Samples.TryRead(out _))
        {
        }

        Assert.True(loop.Samples.Completion.IsCompleted);
    }

    [Fact]
    public void Encoders_WriteToTheLoopsSamples()
    {
        using var loop = CreateLoop();
        loop.Process(Frame(1, At(0)));

        Assert.True(loop.Samples.TryRead(out var sample));
        Assert.Equal(At(0), sample.Timestamp);
    }

    [Fact]
    public async Task RunAsync_ProcessesFramesUntilChannelCompletes()
    {
        var time = new FakeTimeProvider();
        using var loop = CreateLoop(time);

        // Frames on the fake clock, so idle filling has nothing to fill.
        var now = time.GetElapsedTime(0, time.GetTimestamp());
        _frames.Writer.TryWrite(Frame(1, now));
        _frames.Writer.TryWrite(Frame(2, now + TimeSpan.FromSeconds(1.0 / 60)));
        _frames.Writer.Complete();

        await loop.RunAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal([1, 2], _encoders[0].Encoded.Select(e => (int)e.Frame));
    }

    [Fact]
    public async Task RunAsync_WhenCancelled_ReturnsWithoutThrowing()
    {
        using var loop = CreateLoop(new FakeTimeProvider());
        using var cancellation = new CancellationTokenSource();

        var run = loop.RunAsync(cancellation.Token);
        cancellation.Cancel();

        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private EncodingLoop CreateLoop(TimeProvider? time = null) =>
        new(_frames.Reader, CreateEncoder, frameRate: 60, time);

    private IVideoEncoder CreateEncoder(int width, int height, int frameRate, ChannelWriter<EncodedSample> output)
    {
        var encoder = new FakeEncoder(width, height, frameRate, output);
        _encoders.Add(encoder);
        return encoder;
    }

    private CapturedFrame Frame(int id, TimeSpan timestamp, int width = 2560, int height = 1440)
    {
        var owner = new FakeOwner();
        _owners.Add(owner);
        return new CapturedFrame(timestamp, width, height, id, owner);
    }

    private static TimeSpan At(double slot) => _start + TimeSpan.FromTicks((long)(slot * TimeSpan.TicksPerSecond / 60));

    private sealed class FakeEncoder(int width, int height, int frameRate, ChannelWriter<EncodedSample> output) : IVideoEncoder
    {
        public int Width { get; } = width;

        public int Height { get; } = height;

        public int FrameRate { get; } = frameRate;

        public List<(nint Frame, TimeSpan Time)> Encoded { get; } = [];

        public bool FailNext { get; set; }

        public bool IsDisposed { get; private set; }

        public void Encode(CapturedFrame frame, TimeSpan timestamp)
        {
            if (FailNext)
            {
                FailNext = false;
                throw new InvalidOperationException("Encoder failed.");
            }

            Encoded.Add((frame.NativeTexture, timestamp));
            output.TryWrite(new EncodedSample(timestamp, TimeSpan.Zero, Encoded.Count == 1, []));
        }

        public void Dispose() => IsDisposed = true;
    }

    private sealed class FakeOwner : IDisposable
    {
        public bool IsDisposed { get; private set; }

        public void Dispose() => IsDisposed = true;
    }
}
