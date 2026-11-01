using System.Runtime.InteropServices;
using System.Threading.Channels;
using Clipping.Core.Capture;
using Clipping.Core.Encoding;
using Microsoft.Extensions.Time.Testing;

namespace Clipping.Core.Tests.Encoding;

public sealed class EncodingRecoveryTests
{
    // Persistent startup failures should emit one report and one attempt per retry interval.
    [Fact]
    public void StartupFailure_BacksOffReportsAndReleasesDroppedFrames()
    {
        var clock = new FakeTimeProvider();
        var frames = Channel.CreateUnbounded<CapturedFrame>();
        var attempts = 0;
        var failures = new List<Exception>();
        using var loop = new EncodingLoop(frames.Reader, (_, _, _, _) =>
        {
            attempts++;
            throw new COMException("Hardware encoder unavailable.");
        }, 60, clock);
        loop.Failed += failures.Add;

        var owners = new List<Owner>();
        for (var i = 0; i < 60; i++)
        {
            var owner = new Owner();
            owners.Add(owner);
            loop.Process(Frame(owner));
            clock.Advance(TimeSpan.FromMilliseconds(10));
        }

        Assert.Equal(1, attempts);
        Assert.Single(failures);
        Assert.All(owners, owner => Assert.True(owner.Disposed));
        clock.Advance(EncodingLoop.RetryDelay);
        loop.Process(Frame(new Owner()));
        Assert.Equal(2, attempts);
        Assert.Equal(2, failures.Count);
    }

    // A raw native exception during Encode should retire the broken encoder and allow recovery.
    [Fact]
    public void UnexpectedEncodeException_ReportsAndRecoversAfterBackoff()
    {
        var clock = new FakeTimeProvider();
        var broken = new Encoder { Failure = new COMException("Device removed.") };
        var healthy = new Encoder();
        var attempts = 0;
        using var loop = new EncodingLoop(Channel.CreateUnbounded<CapturedFrame>().Reader,
            (_, _, _, _) => ++attempts == 1 ? broken : healthy, 60, clock);
        var failures = new List<Exception>();
        loop.Failed += failures.Add;
        var dropped = new Owner();

        loop.Process(Frame(dropped));
        loop.Process(Frame(new Owner()));
        Assert.Equal(1, attempts);
        Assert.True(broken.Disposed);
        Assert.True(dropped.Disposed);
        Assert.Single(failures);

        clock.Advance(EncodingLoop.RetryDelay);
        loop.Process(Frame(new Owner()));
        Assert.Equal(2, attempts);
        Assert.Equal(1, healthy.Encoded);
    }

    // Native cleanup exceptions must not leave the old timeline or retained texture installed.
    [Fact]
    public void DisposeFailure_StillReleasesLatestFrameAndCompletesOutput()
    {
        var encoder = new Encoder { DisposeFailure = new COMException("Shutdown failed.") };
        var owner = new Owner();
        var failures = new List<Exception>();
        var loop = new EncodingLoop(Channel.CreateUnbounded<CapturedFrame>().Reader,
            (_, _, _, _) => encoder, 60);
        loop.Failed += failures.Add;
        loop.Process(Frame(owner));

        loop.Dispose();

        Assert.True(owner.Disposed);
        Assert.True(loop.Samples.Completion.IsCompleted);
        Assert.Single(failures);
    }

    // Cancelling during Encode must reach the hardware wait and release the current texture lease.
    [Fact]
    public async Task RunAsync_CancellationDuringEncode_ReleasesFrameAndReturns()
    {
        using var stop = new CancellationTokenSource();
        var frames = Channel.CreateUnbounded<CapturedFrame>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var encoder = new Encoder { Wait = entered };
        using var loop = new EncodingLoop(frames.Reader, (_, _, _, _) => encoder, 60);
        var owner = new Owner();
        frames.Writer.TryWrite(Frame(owner));
        var run = Task.Run(() => loop.RunAsync(stop.Token));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        stop.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(owner.Disposed);
        Assert.True(encoder.Disposed);
    }

    // Test frames own a detectable lease so every error path can be checked for release.
    private static CapturedFrame Frame(Owner owner) => new(TimeSpan.Zero, 1920, 1080, 1, owner);

    private sealed class Owner : IDisposable
    {
        public bool Disposed { get; private set; }

        // Recording the release exposes leaked frame ownership in recovery paths.
        public void Dispose() => Disposed = true;
    }

    private sealed class Encoder : IVideoEncoder
    {
        public Exception? Failure { get; init; }
        public Exception? DisposeFailure { get; init; }
        public TaskCompletionSource? Wait { get; init; }
        public bool Disposed { get; private set; }
        public int Encoded { get; private set; }

        // Mimic either a hardware fault or a blocking hardware wait without Windows dependencies.
        public void Encode(CapturedFrame frame, TimeSpan timestamp, CancellationToken cancellationToken = default)
        {
            if (Wait is not null)
            {
                Wait.SetResult();
                cancellationToken.WaitHandle.WaitOne();
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (Failure is not null)
            {
                throw Failure;
            }

            Encoded++;
        }

        // Cleanup failures are separate from submission failures and must also be handled.
        public void Dispose()
        {
            Disposed = true;
            if (DisposeFailure is not null)
            {
                throw DisposeFailure;
            }
        }
    }
}
