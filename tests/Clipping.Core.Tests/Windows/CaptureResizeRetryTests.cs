using Clipping.Windows.Capture;
using Microsoft.Extensions.Time.Testing;

namespace Clipping.Core.Tests.Windows;

public sealed class CaptureResizeRetryTests
{
    private static readonly TimeSpan Delay = TimeSpan.FromSeconds(1);

    private readonly FakeTimeProvider _time = new();
    private readonly List<Exception> _failures = [];
    private readonly CaptureResizeRetry _retry;

    public CaptureResizeRetryTests()
    {
        _retry = new CaptureResizeRetry(Delay, _failures.Add, _time);
    }

    // A 60 fps stream of mismatched frames must not turn a persistent failure into 60 attempts a second.
    [Fact]
    public void RepeatedFailures_AttemptOncePerRetryInterval()
    {
        var original = new Resource();
        Resource? current = original;
        var allocations = 0;
        Resource Fail()
        {
            allocations++;
            throw new IOException("GPU allocation failed.");
        }

        for (var frame = 0; frame < 120; frame++)
        {
            Assert.False(_retry.TryReplace(ref current, Fail, () => { }));
            _time.Advance(TimeSpan.FromSeconds(1.0 / 60));
        }

        // Attempts at 0 s and once the delay has elapsed, around 1 s.
        Assert.Equal(2, allocations);
        Assert.Equal(2, _failures.Count);
        Assert.Same(original, current);
        Assert.False(original.Disposed);
    }

    // Frames skipped during backoff neither allocate nor log; the next attempt waits for the full delay.
    [Fact]
    public void DuringBackoff_SkipsWithoutAllocatingOrLogging()
    {
        Resource? current = new Resource();
        var replacements = new List<Resource>();
        Resource Create()
        {
            var replacement = new Resource();
            replacements.Add(replacement);
            return replacement;
        }

        Assert.False(_retry.TryReplace(ref current, Create, () => throw new IOException("Pool recreation failed.")));
        Assert.True(Assert.Single(replacements).Disposed);

        _time.Advance(Delay - TimeSpan.FromTicks(1));
        Assert.False(_retry.TryReplace(ref current, Create, () => { }));
        Assert.Single(replacements);
        Assert.Single(_failures);

        _time.Advance(TimeSpan.FromTicks(1));
        Assert.True(_retry.TryReplace(ref current, Create, () => { }));
        Assert.Equal(2, replacements.Count);
        Assert.Same(replacements[1], current);
    }

    // Recovery installs the new resources and clears the backoff so a later resize is attempted at once.
    [Fact]
    public void SuccessAfterFailure_InstallsReplacementAndClearsBackoff()
    {
        var original = new Resource();
        Resource? current = original;
        Assert.False(_retry.TryReplace(ref current, () => throw new IOException("GPU allocation failed."), () => { }));

        _time.Advance(Delay);
        var resized = new Resource();
        Assert.True(_retry.TryReplace(ref current, () => resized, () => { }));
        Assert.Same(resized, current);
        Assert.True(original.Disposed);

        var next = new Resource();
        Assert.True(_retry.TryReplace(ref current, () => next, () => { }));
        Assert.Same(next, current);
        Assert.True(resized.Disposed);
        Assert.Single(_failures);
    }

    private sealed class Resource : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }
}
