using System.Diagnostics.CodeAnalysis;

namespace Clipping.Core.Capture;

/// <summary>
/// A fixed set of reusable textures that a frame source lends out one at a time.
/// </summary>
/// <remarks>
/// Independent of the graphics API: <typeparamref name="T"/> is whatever the platform
/// uses for a texture. Owns every texture it was given. Thread-safe: textures are rented
/// on the capture thread and returned from whichever thread disposes the frame.
/// </remarks>
/// <typeparam name="T">The platform's texture type.</typeparam>
public sealed class TextureSet<T> : IDisposable
    where T : class, IDisposable
{
    // Guards _free and _disposed against renters, returners, and Dispose on different threads.
    private readonly Lock _lock = new();
    private readonly Stack<T> _free;
    private bool _disposed;

    /// <param name="textures">The textures to lend out. The set takes ownership of them.</param>
    /// <exception cref="ArgumentException"><paramref name="textures"/> is empty.</exception>
    public TextureSet(IEnumerable<T> textures)
    {
        ArgumentNullException.ThrowIfNull(textures);

        _free = new Stack<T>(textures);
        if (_free.Count == 0)
        {
            throw new ArgumentException("A texture set needs at least one texture.", nameof(textures));
        }
    }

    /// <summary>
    /// Lends out a texture that nothing else is using.
    /// </summary>
    /// <param name="lease">
    /// Holds the texture; dispose it to return the texture. <see langword="null"/> if every
    /// texture is lent out.
    /// </param>
    /// <returns><see langword="true"/> if a texture was free.</returns>
    public bool TryRent([NotNullWhen(true)] out TextureLease<T>? lease)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (!_free.TryPop(out var texture))
            {
                lease = null;
                return false;
            }

            lease = new TextureLease<T>(this, texture);
            return true;
        }
    }

    /// <summary>
    /// Releases the textures that are free now. Textures still lent out stay valid and are
    /// released when their lease is disposed.
    /// </summary>
    /// <remarks>
    /// The frame source disposes the set when capture stops or the capture size changes,
    /// while the reader may still hold frames from it.
    /// </remarks>
    public void Dispose()
    {
        T[] free;

        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            free = _free.ToArray();
            _free.Clear();
        }

        foreach (var texture in free)
        {
            texture.Dispose();
        }
    }

    internal void Return(T texture)
    {
        lock (_lock)
        {
            if (!_disposed)
            {
                _free.Push(texture);
                return;
            }
        }

        // The set was retired while this texture was lent out, so nothing will rent it again.
        texture.Dispose();
    }
}

/// <summary>
/// A texture lent out by a <see cref="TextureSet{T}"/>.
/// </summary>
/// <remarks>
/// Disposing returns the texture to its set. Disposing again does nothing, so the same
/// texture can never be lent out twice at once.
/// </remarks>
/// <typeparam name="T">The platform's texture type.</typeparam>
public sealed class TextureLease<T> : IDisposable
    where T : class, IDisposable
{
    private readonly TextureSet<T> _set;
    private int _returned;

    internal TextureLease(TextureSet<T> set, T texture)
    {
        _set = set;
        Texture = texture;
    }

    /// <summary>
    /// The lent texture. Do not use it after disposing the lease.
    /// </summary>
    public T Texture { get; }

    /// <summary>
    /// Returns the texture to its set.
    /// </summary>
    public void Dispose()
    {
        // The reader and the channel's DropOldest can both dispose a frame; only the first counts.
        if (Interlocked.Exchange(ref _returned, 1) == 1)
        {
            return;
        }

        _set.Return(Texture);
    }
}
