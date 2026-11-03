using Clipping.Windows.Capture;
using Microsoft.Extensions.Time.Testing;

namespace Clipping.Core.Tests.Windows;

public sealed class CaptureResizeRetryTests
{
    // A 60 fps stream of mismatched frames must not turn a persistent failure into 60 attempts a second.
    [Fact]
    public void PersistentFailure_AttemptsAndReportsOncePerRetryInterval()
    {
        // Arrange
        var time = new FakeTimeProvider();
        var failures = new List<Exception>();
        var retry = new CaptureResizeRetry(TimeSpan.FromSeconds(1), failures.Add, time);
        Resource? current = new Resource();
        var attempts = 0;

        // Act: two seconds of frames at 60 fps, every resize failing.
        for (var frame = 0; frame < 120; frame++)
        {
            retry.TryReplace(ref current, () =>
            {
                attempts++;
                throw new IOException("GPU allocation failed.");
            }, () => { });
            time.Advance(TimeSpan.FromSeconds(1.0 / 60));
        }

        // Assert: one attempt at the start, one after the first second.
        Assert.Equal(2, attempts);
        Assert.Equal(2, failures.Count);
    }

    // A failed attempt must leave capture on its working textures and free the half-built replacement.
    [Fact]
    public void FailedAttempt_KeepsCurrentResourcesAndReleasesReplacement()
    {
        // Arrange
        var failures = new List<Exception>();
        var retry = new CaptureResizeRetry(TimeSpan.FromSeconds(1), failures.Add, new FakeTimeProvider());
        var original = new Resource();
        Resource? current = original;
        var replacement = new Resource();
        var poolFailure = new IOException("Pool recreation failed.");

        // Act
        var resized = retry.TryReplace(ref current, () => replacement, () => throw poolFailure);

        // Assert
        Assert.False(resized);
        Assert.Same(original, current);
        Assert.False(original.Disposed);
        Assert.True(replacement.Disposed);
        Assert.Same(poolFailure, Assert.Single(failures));
    }

    // Frames inside the retry delay are dropped without allocating textures or logging again.
    [Fact]
    public void DuringBackoff_SkipsWithoutAllocatingOrReporting()
    {
        // Arrange
        var time = new FakeTimeProvider();
        var failures = new List<Exception>();
        var retry = new CaptureResizeRetry(TimeSpan.FromSeconds(1), failures.Add, time);
        var original = new Resource();
        Resource? current = original;
        retry.TryReplace(ref current, () => throw new IOException("GPU allocation failed."), () => { });
        time.Advance(TimeSpan.FromSeconds(1) - TimeSpan.FromTicks(1));
        var allocations = 0;

        // Act
        var resized = retry.TryReplace(ref current, () =>
        {
            allocations++;
            return new Resource();
        }, () => { });

        // Assert
        Assert.False(resized);
        Assert.Equal(0, allocations);
        Assert.Single(failures);
        Assert.Same(original, current);
    }

    // Once the full delay has passed, a working resize installs the new resources and retires the old.
    [Fact]
    public void AfterRetryDelay_SuccessfulResizeInstallsReplacement()
    {
        // Arrange
        var time = new FakeTimeProvider();
        var retry = new CaptureResizeRetry(TimeSpan.FromSeconds(1), _ => { }, time);
        var original = new Resource();
        Resource? current = original;
        retry.TryReplace(ref current, () => throw new IOException("GPU allocation failed."), () => { });
        time.Advance(TimeSpan.FromSeconds(1));
        var replacement = new Resource();

        // Act
        var resized = retry.TryReplace(ref current, () => replacement, () => { });

        // Assert
        Assert.True(resized);
        Assert.Same(replacement, current);
        Assert.True(original.Disposed);
        Assert.False(replacement.Disposed);
    }

    private sealed class Resource : IDisposable
    {
        public bool Disposed { get; private set; }

        // The flag lets tests detect premature retirement of a usable resource.
        public void Dispose() => Disposed = true;
    }
}
