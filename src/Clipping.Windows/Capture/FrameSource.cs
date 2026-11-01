using System.Threading.Channels;
using Clipping.Core.Capture;
using Clipping.Core.Display;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace Clipping.Windows.Capture;

public class FrameSource : IFrameSource
{
    private readonly Channel<CapturedFrame> _frames;
    private readonly IDirect3DDevice _device;

    // Guards the capture fields below against FrameArrived, which runs on a WGC worker thread.
    private readonly Lock _lock = new();

    private GraphicsCaptureItem? _item;
    private Direct3D11CaptureFramePool? _framePool;
    private GraphicsCaptureSession? _session;
    private bool _disposed;

    public ChannelReader<CapturedFrame> Frames
    {
        get { return _frames.Reader; }
    }

    public FrameSource(IDirect3DDevice device)
    {
        _device = device;
        _frames = Channel.CreateBounded<CapturedFrame>(
            new BoundedChannelOptions(capacity: 6)
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

                    using var frame = framePool.TryGetNextFrame();
                    if (frame is null)
                    {
                        return;
                    }

                    // Pass off to the frame handler here: frame-rate gate → take a free texture →
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

        lock (_lock)
        {
            session = _session;
            framePool = _framePool;
            _session = null;
            _framePool = null;
            _item = null;
        }

        // Disposed outside the lock: a FrameArrived handler waiting on it then sees the pool
        // is retired and returns, so disposal never waits on a handler that waits on us.
        session?.Dispose();
        framePool?.Dispose();
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
