using Clipping.Windows.Capture;

namespace Clipping.Core.Tests.Windows;

public sealed class CaptureResizeTests
{
    // Out-of-memory during allocation must leave the old textures alive for a later retry.
    [Fact]
    public void AllocationFailure_PreservesCurrentResources()
    {
        var original = new Resource();
        Resource? current = original;
        Assert.Throws<IOException>(() => CaptureResize.Replace(ref current,
            () => throw new IOException("GPU allocation failed."), () => { }));
        Assert.Same(original, current);
        Assert.False(original.Disposed);
    }

    // Pool recreation failure releases only the uncommitted replacement; retry can still succeed.
    [Fact]
    public void RecreateFailure_PreservesCurrentAndDisposesReplacementThenRetries()
    {
        var original = new Resource();
        var replacement = new Resource();
        Resource? current = original;
        Assert.Throws<IOException>(() => CaptureResize.Replace(ref current, () => replacement,
            () => throw new IOException("Pool recreation failed.")));
        Assert.Same(original, current);
        Assert.False(original.Disposed);
        Assert.True(replacement.Disposed);

        var retry = new Resource();
        CaptureResize.Replace(ref current, () => retry, () => { });
        Assert.Same(retry, current);
        Assert.True(original.Disposed);
        Assert.False(retry.Disposed);
    }

    private sealed class Resource : IDisposable
    {
        public bool Disposed { get; private set; }

        // The flag lets resize tests detect premature retirement of a usable resource.
        public void Dispose() => Disposed = true;
    }
}
