using System.Threading.Channels;
using Clipping.Core.Capture;
using Clipping.Core.Display;
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

    private readonly Channel<CapturedFrame> _frames;
    private readonly IDirect3DDevice _device;
    private readonly ID3D11Device _d3dDevice;

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

    /// <param name="device">The shared device as WinRT sees it, for capture.</param>
    /// <param name="d3dDevice">The same device, for creating and copying textures.</param>
    public FrameSource(IDirect3DDevice device, ID3D11Device d3dDevice)
    {
        _device = device;
        _d3dDevice = d3dDevice;
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
            _textures = CaptureTextures.Create(_d3dDevice, _item.Size.Width, _item.Size.Height, TextureCount);

            // Free-threaded: FrameArrived fires on a WGC worker thread.
            var framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                _device,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                numberOfBuffers: 2,
                _item.Size);
            _framePool = framePool;

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

                    // Pass off to the frame handler here: frame-rate gate → _textures.TryRent →
                    // CopyResource → TryWrite a CapturedFrame. Recreate the pool if frame.ContentSize
                    // changed. Disposing frame (the using above) returns the buffer to the pool.
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
