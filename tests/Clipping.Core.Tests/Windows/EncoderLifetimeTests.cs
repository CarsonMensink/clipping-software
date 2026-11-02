using System.Threading.Channels;
using Clipping.Core.Capture;
using Clipping.Core.Encoding;
using Clipping.Windows.Encoding;
using Microsoft.Extensions.Time.Testing;

namespace Clipping.Core.Tests.Windows;

public sealed class EncoderLifetimeTests
{
    // A real surviving worker must retain ownership through both submission and constructor failures.
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void TimedOutRetirement_BlocksReplacementAndLateOutputUntilCleanup(bool startupFailure, bool shutdownFailure)
    {
        using var allowExit = new ManualResetEventSlim();
        using var entered = new ManualResetEventSlim();
        var clock = new FakeTimeProvider();
        var lifetime = new EncoderLifetime(TimeSpan.FromMilliseconds(20));
        EncoderLifetime.Session? oldSession = null;
        ChannelWriter<EncodedSample>? output = null;
        var lateOutput = 0;
        var shutdownRequests = 0;
        var released = false;
        var activateShutdown = false;
        var activations = 0;
        var order = new List<string>();
        var worker = new Thread(() =>
        {
            entered.Set();
            allowExit.Wait();
            oldSession!.RunIfActive(() =>
            {
                Interlocked.Increment(ref lateOutput);
                output!.TryWrite(new EncodedSample(TimeSpan.Zero, TimeSpan.Zero, true, 1920, 1080, []));
            });
        }) { IsBackground = true };

        // Begin is the same activation gate used before H264Encoder allocates or activates anything.
        IVideoEncoder Create(int width, int height, int rate, ChannelWriter<EncodedSample> samples)
        {
            if (activations == 0)
            {
                output = samples;
                oldSession = lifetime.Begin(worker, () =>
                {
                    shutdownRequests++;
                    if (shutdownFailure)
                    {
                        throw new IOException("Native shutdown failed.");
                    }
                }, () =>
                {
                    released = true;
                    activateShutdown = true;
                    order.Add("cleanup-old");
                });
                activations++;
                order.Add("activate-old");
                worker.Start();
                Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
                if (startupFailure)
                {
                    oldSession.Retire();
                    throw new InvalidOperationException("Startup failed after the thread started.");
                }

                return new TestEncoder(oldSession, failSubmission: true);
            }

            var replacement = lifetime.Begin(new Thread(() => { }), () => { }, () => { });
            activations++;
            order.Add("activate-new");
            return new TestEncoder(replacement);
        }

        var loop = new EncodingLoop(Channel.CreateUnbounded<CapturedFrame>().Reader, Create, 60, clock);
        var failures = new List<Exception>();
        loop.Failed += failures.Add;
        try
        {
            var owner = new FrameOwner();
            loop.Process(Frame(owner));
            Assert.True(owner.Disposed);
            Assert.True(worker.IsAlive);
            Assert.False(released);
            Assert.False(activateShutdown);
            Assert.False(EncoderProgress.JoinIfStarted(worker, TimeSpan.Zero));
            Assert.NotEmpty(failures);

            clock.Advance(EncodingLoop.RetryDelay);
            loop.Process(Frame(new FrameOwner()));
            Assert.Equal(1, activations);
            Assert.Equal(1, shutdownRequests);
            Assert.False(released);
            Assert.False(activateShutdown);

            // A late completion is suppressed even before a retry gets a chance to finish cleanup.
            allowExit.Set();
            Assert.True(worker.Join(TimeSpan.FromSeconds(5)));
            Assert.Equal(0, lateOutput);
            Assert.False(loop.Samples.TryRead(out _));
            Assert.False(released);

            clock.Advance(EncodingLoop.RetryDelay);
            loop.Process(Frame(new FrameOwner()));
            Assert.True(released);
            Assert.True(activateShutdown);
            Assert.Equal(2, activations);
            Assert.Equal(["activate-old", "cleanup-old", "activate-new"], order);
            Assert.Equal(1, shutdownRequests);
        }
        finally
        {
            allowExit.Set();
            worker.Join(TimeSpan.FromSeconds(5));
            loop.Dispose();
            lifetime.Dispose();
        }
    }

    // Joining must not hold the publication lock needed by a worker that is trying to exit.
    [Fact]
    public void Retirement_AllowsWorkerToFinishWithoutPublishing()
    {
        using var lifetime = new EncoderLifetime(TimeSpan.FromSeconds(5));
        using var allowExit = new ManualResetEventSlim();
        var published = false;
        var released = false;
        EncoderLifetime.Session? session = null;
        var worker = new Thread(() =>
        {
            allowExit.Wait();
            session!.RunIfActive(() => published = true);
        }) { IsBackground = true };
        session = lifetime.Begin(worker, allowExit.Set, () => released = true);
        worker.Start();
        try
        {
            session.Retire();
            Assert.False(worker.IsAlive);
            Assert.False(published);
            Assert.True(released);
        }
        finally
        {
            allowExit.Set();
            worker.Join(TimeSpan.FromSeconds(5));
        }
    }

    // Failed activation cleanup must stay closed and must never run a resource's Dispose twice.
    [Fact]
    public void CleanupFailure_RejectsReplacementWithoutRepeatingCleanup()
    {
        var lifetime = new EncoderLifetime(TimeSpan.Zero);
        var releases = 0;
        var session = lifetime.Begin(new Thread(() => { }), () => { }, () =>
        {
            releases++;
            throw new IOException("Activation shutdown failed.");
        });
        Assert.Throws<InvalidOperationException>(session.Retire);
        Assert.Throws<InvalidOperationException>(() => lifetime.Begin(new Thread(() => { }), () => { }, () => { }));
        Assert.Throws<InvalidOperationException>(lifetime.Dispose);
        Assert.Equal(1, releases);
    }

    // Shared app services cannot be disposed while a retained worker still owns native resources.
    [Fact]
    public async Task AppShutdown_WaitsForRetiredWorkerBeforeReleasingResources()
    {
        using var allowExit = new ManualResetEventSlim();
        using var shutdownRequested = new ManualResetEventSlim();
        var released = false;
        var worker = new Thread(allowExit.Wait) { IsBackground = true };
        var lifetime = new EncoderLifetime(TimeSpan.Zero);
        var session = lifetime.Begin(worker, shutdownRequested.Set, () => released = true);
        worker.Start();
        Assert.Throws<InvalidOperationException>(session.Retire);
        var stop = Task.Run(lifetime.Dispose);
        try
        {
            Assert.True(shutdownRequested.Wait(TimeSpan.FromSeconds(5)));
            Assert.False(stop.IsCompleted);
            Assert.False(released);
        }
        finally
        {
            allowExit.Set();
            await stop.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.False(worker.IsAlive);
        Assert.True(released);
        Assert.Throws<ObjectDisposedException>(() => lifetime.Begin(new Thread(() => { }), () => { }, () => { }));
    }

    // Real frame ownership checks ensure failed or blocked attempts do not exhaust capture textures.
    private static CapturedFrame Frame(FrameOwner owner) => new(TimeSpan.Zero, 1920, 1080, 1, owner);

    private sealed class FrameOwner : IDisposable
    {
        public bool Disposed { get; private set; }

        // A released lease must be observable on every refused-replacement path.
        public void Dispose() => Disposed = true;
    }

    private sealed class TestEncoder(EncoderLifetime.Session session, bool failSubmission = false) : IVideoEncoder
    {
        // Submission failures exercise EncodingLoop's real disposal and retry handling.
        public void Encode(CapturedFrame frame, TimeSpan timestamp, CancellationToken cancellationToken = default)
        {
            if (failSubmission)
            {
                throw new InvalidOperationException("Encoder stopped making progress.");
            }
        }

        // Use the production retirement policy so a fake cannot accidentally bypass the activation gate.
        public void Dispose() => session.Retire();
    }
}
