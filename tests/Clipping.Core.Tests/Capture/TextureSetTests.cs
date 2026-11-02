using Clipping.Core.Capture;

namespace Clipping.Core.Tests.Capture;

public sealed class TextureSetTests
{
    [Fact]
    public void Constructor_WithNoTextures_Throws()
    {
        Assert.Throws<ArgumentException>(() => new TextureSet<FakeTexture>([]));
    }

    // Capture drops frames when every texture is in use instead of sharing a live texture.
    [Fact]
    public void Rent_ExhaustsCapacityWithoutSharingTextures()
    {
        // Arrange
        var textures = CreateTextures(3);
        using var set = new TextureSet<FakeTexture>(textures);
        var leases = new List<TextureLease<FakeTexture>>();

        // Act
        for (var i = 0; i < 3; i++)
        {
            Assert.True(set.TryRent(out var lease));
            leases.Add(lease);
        }
        var available = set.TryRent(out var excess);

        // Assert
        Assert.Equivalent(textures, leases.Select(l => l.Texture), strict: true);
        Assert.False(available);
        Assert.Null(excess);
        foreach (var lease in leases) lease.Dispose();
    }

    // Returning a frame should make its live texture available for reuse.
    [Fact]
    public void ReturnedLease_RemainsUsableForTheNextFrame()
    {
        // Arrange
        using var set = new TextureSet<FakeTexture>(CreateTextures(1));
        Assert.True(set.TryRent(out var first));
        var texture = first.Texture;

        // Act
        first.Dispose();
        var available = set.TryRent(out var second);

        // Assert
        Assert.True(available);
        Assert.Same(texture, second!.Texture);
        Assert.False(texture.IsDisposed);
        second.Dispose();
    }

    // Duplicate frame releases must not allow the same texture to be lent twice.
    [Fact]
    public void DuplicateLeaseReturn_DoesNotCreateAnotherAvailableTexture()
    {
        // Arrange
        using var set = new TextureSet<FakeTexture>(CreateTextures(1));
        Assert.True(set.TryRent(out var first));

        // Act
        first.Dispose();
        first.Dispose();

        // Assert
        Assert.True(set.TryRent(out var second));
        Assert.False(set.TryRent(out _));
        second.Dispose();
    }

    // Stopping capture frees idle textures while allowing the encoder to finish its borrowed frame.
    [Fact]
    public void RetiredSet_ReleasesIdleTexturesAndDefersBorrowedTextureUntilReturn()
    {
        // Arrange
        var textures = CreateTextures(3);
        var set = new TextureSet<FakeTexture>(textures);
        Assert.True(set.TryRent(out var lease));
        var borrowed = lease.Texture;
        var idle = textures.Where(t => t != borrowed).ToArray();

        // Act
        set.Dispose();

        // Assert
        Assert.Equal(2, idle.Length);
        Assert.All(idle, texture => Assert.Equal(1, texture.DisposeCount));
        Assert.False(borrowed.IsDisposed);

        // Act: the encoder finishes after capture has stopped.
        lease.Dispose();

        // Assert
        Assert.Equal(1, borrowed.DisposeCount);
        Assert.All(idle, texture => Assert.Equal(1, texture.DisposeCount));
    }

    // Repeated shutdown must not release any texture twice.
    [Fact]
    public void RepeatedSetDisposal_ReleasesEachIdleTextureOnlyOnce()
    {
        // Arrange
        var textures = CreateTextures(2);
        var set = new TextureSet<FakeTexture>(textures);
        set.Dispose();

        // Act
        set.Dispose();

        // Assert
        Assert.All(textures, texture => Assert.Equal(1, texture.DisposeCount));
    }

    // Retired capture resources must not be lent to new frames.
    [Fact]
    public void RetiredSet_RejectsNewRentals()
    {
        // Arrange
        var set = new TextureSet<FakeTexture>(CreateTextures(1));
        set.Dispose();

        // Act
        var error = Record.Exception(() => set.TryRent(out _));

        // Assert
        Assert.IsType<ObjectDisposedException>(error);
    }

    [Fact]
    public void RentAndReturn_FromManyThreads_NeverLendsATextureTwice()
    {
        var textures = CreateTextures(4);
        using var set = new TextureSet<FakeTexture>(textures);
        var inUse = new HashSet<FakeTexture>();
        var inUseLock = new Lock();
        var lentTwice = false;

        Parallel.For(0, 10_000, _ =>
        {
            if (!set.TryRent(out var lease))
            {
                return;
            }

            lock (inUseLock)
            {
                lentTwice |= !inUse.Add(lease.Texture);
            }

            lock (inUseLock)
            {
                inUse.Remove(lease.Texture);
            }

            lease.Dispose();
        });

        Assert.False(lentTwice);
    }

    private static FakeTexture[] CreateTextures(int count) =>
        Enumerable.Range(0, count).Select(_ => new FakeTexture()).ToArray();

    private sealed class FakeTexture : IDisposable
    {
        public int DisposeCount { get; private set; }

        public bool IsDisposed => DisposeCount > 0;

        public void Dispose() => DisposeCount++;
    }
}
