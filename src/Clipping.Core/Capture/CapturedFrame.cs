namespace Clipping.Core.Capture;

/// <summary>
/// A single captured image, independent of the platform that captured it.
/// </summary>
/// <remarks>
/// Holds a texture lent by the frame source until disposed. Dispose it as soon as it
/// is no longer needed so the texture can be reused for later frames.
/// </remarks>
  public sealed class CapturedFrame : IDisposable
  {
      private readonly IDisposable _owner;

    /// <param name="timestamp">When the frame was captured, on a monotonic clock.</param>
    /// <param name="width">Width of the image in pixels.</param>
    /// <param name="height">Height of the image in pixels.</param>
    /// <param name="nativeTexture">Handle to the platform's GPU texture holding the image.</param>
    /// <param name="owner">Returns the texture to the frame source when disposed.</param>
      public CapturedFrame(TimeSpan timestamp, int width, int height, nint nativeTexture, IDisposable owner)
      {
        ArgumentNullException.ThrowIfNull(owner);

          Timestamp = timestamp;
          Width = width;
          Height = height;
          NativeTexture = nativeTexture;
          _owner = owner;
      }

    /// <summary>
    /// When the frame was captured.
    /// </summary>
    /// <remarks>
    /// Measured on a monotonic clock, so it orders frames reliably but is not a
    /// wall-clock time. Only differences between timestamps are meaningful.
    /// </remarks>
      public TimeSpan Timestamp { get; }

    /// <summary>
    /// Width of the image in pixels.
    /// </summary>
    /// <remarks>
    /// Can change between frames when the display mode changes.
    /// </remarks>
      public int Width { get; }

    /// <summary>
    /// Height of the image in pixels.
    /// </summary>
    /// <remarks>
    /// Can change between frames when the display mode changes.
    /// </remarks>
      public int Height { get; }

    /// <summary>
    /// Handle to the GPU texture holding the image.
    /// </summary>
    /// <remarks>
    /// Platform-specific: an <c>ID3D11Texture2D</c> pointer on Windows. Only
    /// platform code such as the encoder should interpret it. Valid only until the
    /// frame is disposed.
    /// </remarks>
      public nint NativeTexture { get; }

    /// <summary>
    /// Returns the texture to the frame source so it can be reused for later frames.
    /// </summary>
      public void Dispose() => _owner.Dispose();
  }
