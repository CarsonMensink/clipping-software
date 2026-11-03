using System.Threading.Channels;
using Clipping.Core.Capture;
using Clipping.Core.Display;
using Clipping.Core.Settings;
using Clipping.Windows.Graphics;
using Vortice.Direct3D11;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace Clipping.Windows.Capture;

public class FrameSource : IFrameSource
{
    private const int QueueCapacity = 6;

    // Enough for a full queue, plus the frame the reader is encoding, the one being copied,
    // and the latest frame kept for repeating while the screen is static.
    private const int TextureCount = QueueCapacity + 3;

    private const DirectXPixelFormat PixelFormat = DirectXPixelFormat.B8G8R8A8UIntNormalized;
    private const int PoolBuffers = 2;

    // After a failed resize, frames needing it are dropped for this long before trying again.
    private static readonly TimeSpan ResizeRetryDelay = TimeSpan.FromSeconds(1);

    private readonly Channel<CapturedFrame> _frames;
    private readonly ISettingsStore _settings;
    private readonly IDirect3DDevice _device;
    private readonly ID3D11Device _d3dDevice;
    private readonly ID3D11DeviceContext _context;

    // Guards the capture fields below against FrameArrived, which runs on a WGC worker thread.
    private readonly Lock _lock = new();

    private GraphicsCaptureItem? _item;
    private Direct3D11CaptureFramePool? _framePool;
    private GraphicsCaptureSession? _session;
    private TextureSet<ID3D11Texture2D>? _textures;
    private bool _disposed;

    public ChannelReader<CapturedFrame> Frames
    {
        get { return _frames.Reader; }
    }

    /// <param name="settings">Supplies the frame rate, read each time capture starts.</param>
    /// <param name="device">The shared device as WinRT sees it, for capture.</param>
    /// <param name="d3dDevice">The same device, for creating textures.</param>
    /// <param name="context">The device's immediate context, for copying frames.</param>
    public FrameSource(ISettingsStore settings, IDirect3DDevice device, ID3D11Device d3dDevice, ID3D11DeviceContext context)
    {
        _settings = settings;
        _device = device;
        _d3dDevice = d3dDevice;
        _context = context;
        _frames = Channel.CreateBounded<CapturedFrame>(
            new BoundedChannelOptions(QueueCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleWriter = true,
                SingleReader = true,
            },
        itemDropped: frame => frame.Dispose());
    }

    public void StartCapture(MonitorInfo monitor)
    {
        ArgumentNullException.ThrowIfNull(monitor);

        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_session is not null)
            {
                throw new InvalidOperationException("Capture is already running.");
            }

            _item = CaptureItemFactory.CreateForMonitor(monitor);

            // The size of the pool's buffers and of our textures; CopyResource needs them equal.
            var size = _item.Size;
            _textures = CaptureTextures.Create(_d3dDevice, size.Width, size.Height, TextureCount);

            // Free-threaded: FrameArrived fires on a WGC worker thread.
            var framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(_device, PixelFormat, PoolBuffers, size);
            _framePool = framePool;

            // Only touched inside the handler, under the lock.
            var gate = new FrameRateGate(_settings.Settings.FrameRate);
            var resize = new CaptureResizeRetry(
                ResizeRetryDelay, failure => Console.Error.WriteLine($"Capture resize failed: {failure}"));

            framePool.FrameArrived += (_, _) =>
            {
                lock (_lock)
                {
                    // A frame can arrive while StopCapture retires this pool; ignore it.
                    if (_framePool != framePool)
                    {
                        return;
                    }

                    // using allows this to return the buffer to the pool once out of scope.
                    using var frame = framePool.TryGetNextFrame();
                    if (frame is null)
                    {
                        return;
                    }

                    // Skips frames that arrive faster than the frame rate, before they cost a copy.
                    if (!gate.TryPass(frame.SystemRelativeTime))
                    {
                        return;
                    }

                    if (frame.ContentSize.Width != size.Width || frame.ContentSize.Height != size.Height)
                    {
                        // The monitor's resolution changed. Rebuild the pool and textures at the new
                        // size, and skip this frame: its buffer is still the old size.
                        var nextSize = frame.ContentSize;

                        // A failure keeps the previous size and textures; frames during the retry
                        // delay are dropped without another attempt.
                        resize.TryReplace(
                            ref _textures,
                            () => CaptureTextures.Create(_d3dDevice, nextSize.Width, nextSize.Height, TextureCount),
                            () =>
                            {
                                framePool.Recreate(_device, PixelFormat, PoolBuffers, nextSize);
                                size = nextSize;
                            });

                        return;
                    }

                    // Every texture is queued or held by the reader; drop this frame.
                    if (!_textures!.TryRent(out var lease))
                    {
                        return;
                    }

                    CapturedFrame captured;
                    try
                    {
                        using var source = SurfaceInterop.GetTexture(frame.Surface);

                        // GPU to GPU; the frame's buffer goes back to WGC when frame is disposed.
                        _context.CopyResource(lease.Texture, source);
                        captured = new CapturedFrame(
                            frame.SystemRelativeTime, size.Width, size.Height, lease.Texture.NativePointer, lease);
                    }
                    catch
                    {
                        lease.Dispose();
                        throw;
                    }

                    // Fails only once Dispose has completed the channel.
                    if (!_frames.Writer.TryWrite(captured))
                    {
                        captured.Dispose();
                    }
                }
            };

            _session = framePool.CreateCaptureSession(_item);
            _session.IsCursorCaptureEnabled = true;

            // Unpackaged apps can turn the border off without GraphicsCaptureAccess.RequestAccessAsync.
            _session.IsBorderRequired = false;

            _session.StartCapture();
        }
    }

    public void StopCapture()
    {
        GraphicsCaptureSession? session;
        Direct3D11CaptureFramePool? framePool;
        TextureSet<ID3D11Texture2D>? textures;

        lock (_lock)
        {
            session = _session;
            framePool = _framePool;
            textures = _textures;
            _session = null;
            _framePool = null;
            _textures = null;
            _item = null;
        }

        // Disposed outside the lock: a FrameArrived handler waiting on it then sees the pool
        // is retired and returns, so disposal never waits on a handler that waits on us.
        session?.Dispose();
        framePool?.Dispose();

        // Textures the reader still holds stay valid and are released when it disposes them.
        textures?.Dispose();
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        StopCapture();

        // Ends the reader's ReadAllAsync loop once it has read the frames still queued;
        // the reader still owns and disposes those.
        _frames.Writer.TryComplete();
    }
}
