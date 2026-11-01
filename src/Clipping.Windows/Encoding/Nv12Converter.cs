using System.Runtime.InteropServices;
using Clipping.Core.Capture;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Clipping.Windows.Encoding;

/// <summary>
/// Converts captured BGRA frames to NV12, the format hardware encoders take, on the GPU.
/// </summary>
/// <remarks>
/// Uses the D3D11 video processor, which runs on the GPU's video hardware, so frames never
/// pass through the CPU. Colours are converted to BT.709 limited range, the standard for HD
/// video. One converter handles one frame size.
/// </remarks>
internal sealed class Nv12Converter : IDisposable
{
    private readonly ID3D11VideoDevice _videoDevice;
    private readonly ID3D11VideoContext1 _videoContext;
    private readonly ID3D11VideoProcessorEnumerator _enumerator;
    private readonly ID3D11VideoProcessor _processor;
    private readonly ID3D11Device _device;

    /// <param name="device">The device the frames' textures live on.</param>
    /// <param name="context">Its immediate context.</param>
    /// <param name="width">Width of the captured frames.</param>
    /// <param name="height">Height of the captured frames.</param>
    /// <param name="frameRate">Frames per second, a hint for the video hardware.</param>
    public Nv12Converter(ID3D11Device device, ID3D11DeviceContext context, int width, int height, int frameRate)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 2);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 2);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frameRate);

        _device = device;

        // NV12 stores colour once per 2×2 block, so its size must be even. An odd monitor
        // size is scaled down by one pixel.
        Width = width & ~1;
        Height = height & ~1;

        _videoDevice = device.QueryInterface<ID3D11VideoDevice>();
        _videoContext = context.QueryInterface<ID3D11VideoContext1>();

        var description = new VideoProcessorContentDescription
        {
            InputFrameFormat = VideoFrameFormat.Progressive,
            InputWidth = (uint)width,
            InputHeight = (uint)height,
            InputFrameRate = new Rational((uint)frameRate, 1),
            OutputWidth = (uint)Width,
            OutputHeight = (uint)Height,
            OutputFrameRate = new Rational((uint)frameRate, 1),
            Usage = VideoUsage.OptimalSpeed,
        };
        _enumerator = _videoDevice.CreateVideoProcessorEnumerator(description);
        _processor = _videoDevice.CreateVideoProcessor(_enumerator, 0);

        // Screen content is full-range sRGB; encoders expect limited-range BT.709.
        _videoContext.VideoProcessorSetStreamColorSpace1(_processor, 0, ColorSpaceType.RgbFullG22NoneP709);
        _videoContext.VideoProcessorSetOutputColorSpace1(_processor, ColorSpaceType.YcbcrStudioG22LeftP709);
        _videoContext.VideoProcessorSetStreamFrameFormat(_processor, 0, VideoFrameFormat.Progressive);

        // Leaves out driver "enhancements" such as sharpening, which would alter the recording.
        _videoContext.VideoProcessorSetStreamAutoProcessingMode(_processor, 0, false);
    }

    /// <summary>
    /// Width of the NV12 output, the input width rounded down to even.
    /// </summary>
    public int Width { get; }

    /// <summary>
    /// Height of the NV12 output, the input height rounded down to even.
    /// </summary>
    public int Height { get; }

    /// <summary>
    /// Creates an NV12 texture of the output size, for <see cref="Convert"/> to write into.
    /// </summary>
    public ID3D11Texture2D CreateTarget()
    {
        return _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)Width,
            Height = (uint)Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.NV12,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
        });
    }

    /// <summary>
    /// Converts a captured frame into <paramref name="target"/>.
    /// </summary>
    /// <param name="frame">A frame of the size this converter was made for.</param>
    /// <param name="target">A texture from <see cref="CreateTarget"/>.</param>
    public void Convert(CapturedFrame frame, ID3D11Texture2D target)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(target);

        // The frame keeps its own reference; this wrapper takes one more and releases it.
        Marshal.AddRef(frame.NativeTexture);
        using var source = new ID3D11Texture2D(frame.NativeTexture);

        // Views are cheap to create next to the conversion itself; cache them if profiling says otherwise.
        using var inputView = _videoDevice.CreateVideoProcessorInputView(source, _enumerator, new VideoProcessorInputViewDescription
        {
            ViewDimension = VideoProcessorInputViewDimension.Texture2D,
        });
        using var outputView = _videoDevice.CreateVideoProcessorOutputView(target, _enumerator, new VideoProcessorOutputViewDescription
        {
            ViewDimension = VideoProcessorOutputViewDimension.Texture2D,
        });

        var stream = new VideoProcessorStream
        {
            Enable = true,
            InputSurface = inputView,
        };
        _videoContext.VideoProcessorBlt(_processor, outputView, 0, 1, [stream]).CheckError();
    }

    public void Dispose()
    {
        _processor.Dispose();
        _enumerator.Dispose();
        _videoContext.Dispose();
        _videoDevice.Dispose();
    }
}
