using Clipping.Core.Capture;

namespace Clipping.Core.Tests.Capture;

public sealed class TextureSetTests
{
    [Fact]
    public void Constructor_WithNoTextures_Throws()
    {
        Assert.Throws<ArgumentException>(() => new TextureSet<FakeTexture>([]));
    }

    [Fact]
    public void TryRent_LendsEachTextureOnce()
    {
        var textures = CreateTextures(3);
        using var set = new TextureSet<FakeTexture>(textures);

        var rented = new List<FakeTexture>();
        for (var i = 0; i < textures.Length; i++)
        {
            Assert.True(set.TryRent(out var lease));
            rented.Add(lease.Texture);
        }

        Assert.Equivalent(textures, rented, strict: true);
    }

    [Fact]
    public void TryRent_WhenAllLentOut_ReturnsFalse()
    {
        using var set = new TextureSet<FakeTexture>(CreateTextures(1));
        set.TryRent(out _);

        Assert.False(set.TryRent(out var lease));
        Assert.Null(lease);
    }

    [Fact]
    public void LeaseDispose_ReturnsTextureForReuse()
    {
        using var set = new TextureSet<FakeTexture>(CreateTextures(1));
        set.TryRent(out var first);
        var texture = first!.Texture;

        first.Dispose();

        Assert.True(set.TryRent(out var second));
        Assert.Same(texture, second.Texture);
    }

    [Fact]
    public void LeaseDispose_Twice_ReturnsTextureOnce()
    {
        using var set = new TextureSet<FakeTexture>(CreateTextures(2));
        set.TryRent(out var first);
        set.TryRent(out _);

        first!.Dispose();
        first.Dispose();

        // Only one texture came back, so a second rent finds none.
        Assert.True(set.TryRent(out _));
        Assert.False(set.TryRent(out _));
    }

    [Fact]
    public void LeaseDispose_DoesNotDisposeTexture()
    {
        using var set = new TextureSet<FakeTexture>(CreateTextures(1));
        set.TryRent(out var lease);

        lease!.Dispose();

        Assert.False(lease.Texture.IsDisposed);
    }

    [Fact]
    public void Dispose_DisposesFreeTextures()
    {
        var textures = CreateTextures(2);
        var set = new TextureSet<FakeTexture>(textures);

        set.Dispose();

        Assert.All(textures, texture => Assert.True(texture.IsDisposed));
    }

    [Fact]
    public void Dispose_KeepsLentTexturesValid()
    {
        var set = new TextureSet<FakeTexture>(CreateTextures(2));
        set.TryRent(out var lease);

        set.Dispose();

        Assert.False(lease!.Texture.IsDisposed);
    }

    [Fact]
    public void LeaseDispose_AfterSetDisposed_DisposesTexture()
    {
        var set = new TextureSet<FakeTexture>(CreateTextures(1));
        set.TryRent(out var lease);
        set.Dispose();

        lease!.Dispose();

        Assert.True(lease.Texture.IsDisposed);
    }

    [Fact]
    public void Dispose_Twice_DisposesTexturesOnce()
    {
        var textures = CreateTextures(1);
        var set = new TextureSet<FakeTexture>(textures);

        set.Dispose();
        set.Dispose();

        Assert.Equal(1, textures[0].DisposeCount);
    }

    [Fact]
    public void TryRent_AfterDispose_Throws()
    {
        var set = new TextureSet<FakeTexture>(CreateTextures(1));
        set.Dispose();

        Assert.Throws<ObjectDisposedException>(() => set.TryRent(out _));
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
