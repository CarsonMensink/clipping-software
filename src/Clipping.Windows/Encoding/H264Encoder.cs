using System.Runtime.InteropServices;
using System.Threading.Channels;
using Clipping.Core.Capture;
using Clipping.Core.Encoding;
using Vortice.Direct3D11;
using Vortice.MediaFoundation;

namespace Clipping.Windows.Encoding;

/// <summary>
/// Encodes captured frames to H.264 on the GPU's hardware encoder.
/// </summary>
/// <remarks>
/// Frames are converted to NV12 and handed to the encoder as GPU textures, so they never
/// pass through the CPU; only the compressed output is copied out. The encoder is a
/// Media Foundation asynchronous transform: it raises an event when it wants input and
/// another when output is ready, handled here on a dedicated thread.
/// <para>
/// Settings suit a replay buffer: constant bitrate, so a clip's size and quality are
/// predictable whatever is on screen; a keyframe every second so a clip can
/// start on any second, no B-frames so every input produces one output in order, and low
/// latency so the encoder holds nothing back. One encoder handles one frame size.
/// </para>
/// </remarks>
internal sealed class H264Encoder : IVideoEncoder
{
    // NV12 textures handed to the encoder in turn. The encoder reads a texture after
    // ProcessInput returns, so one is reused only once the output for its last input is out.
    private const int InputSlots = 4;

    // MFT_MESSAGE_SET_D3D_MANAGER, missing from Vortice's TMessageType.
    private const TMessageType MessageSetD3DManager = (TMessageType)2;

    // eAVEncH264VProfile_High.
    private const uint ProfileHigh = 100;

    private readonly IMFTransform _transform;
    private readonly IMFMediaEventGenerator _events;
    private readonly IMFDXGIDeviceManager _deviceManager;
    private readonly Nv12Converter _converter;
    private readonly ID3D11Texture2D[] _inputs = new ID3D11Texture2D[InputSlots];
    private readonly long _frameDuration;
    private readonly bool _providesSamples;
    private readonly int _outputSize;

    private readonly ChannelWriter<EncodedSample> _output;

    // Counts NeedInput events not yet answered, and input slots whose output is out.
    private readonly SemaphoreSlim _needInput = new(0);
    private readonly SemaphoreSlim _freeSlots = new(InputSlots);
    private readonly ManualResetEventSlim _drained = new();
    private readonly Thread _eventThread;

    private int _nextSlot;
    private volatile Exception? _failure;
    private bool _disposed;

    /// <param name="encoderActivate">A hardware H.264 encoder on the same GPU as <paramref name="device"/>.</param>
    /// <param name="device">The device the frames' textures live on.</param>
    /// <param name="context">Its immediate context.</param>
    /// <param name="width">Width of the captured frames.</param>
    /// <param name="height">Height of the captured frames.</param>
    /// <param name="frameRate">Frames per second.</param>
    /// <param name="bitrate">Bits per second, spent whether or not the screen changes.</param>
    /// <param name="output">Where encoded frames go. Never completed by the encoder.</param>
    public H264Encoder(
        IMFActivate encoderActivate,
        ID3D11Device device,
        ID3D11DeviceContext context,
        int width,
        int height,
        int frameRate,
        int bitrate,
        ChannelWriter<EncodedSample> output)
    {
        ArgumentNullException.ThrowIfNull(encoderActivate);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bitrate);

        _output = output;
        _converter = new Nv12Converter(device, context, width, height, frameRate);
        for (var i = 0; i < InputSlots; i++)
        {
            _inputs[i] = _converter.CreateTarget();
        }

        _frameDuration = TimeSpan.TicksPerSecond / frameRate;

        encoderActivate.ActivateObject<IMFTransform>(typeof(IMFTransform).GUID, out var transform).CheckError();
        _transform = transform!;

        // Asynchronous transforms refuse every call until unlocked.
        _transform.Attributes.Set(TransformAttributeKeys.TransformAsyncUnlock, 1u);
        _events = _transform.QueryInterface<IMFMediaEventGenerator>();

        // Lets the encoder read our textures directly.
        _deviceManager = MediaFactory.MFCreateDXGIDeviceManager();
        _deviceManager.ResetDevice(device).CheckError();
        _transform.ProcessMessage(MessageSetD3DManager, (UIntPtr)(ulong)_deviceManager.NativePointer);

        // An encoder takes its output type first, then an input type that fits it.
        using (var outputType = CreateVideoType(VideoFormatGuids.H264, frameRate))
        {
            outputType.Set(MediaTypeAttributeKeys.AvgBitrate, (uint)bitrate);
            outputType.Set(MediaTypeAttributeKeys.Mpeg2Profile, ProfileHigh);
            _transform.SetOutputType(0, outputType, 0);
        }

        // Set once the encoder knows it is producing H.264; some refuse these before that.
        // Low latency also turns off B-frames, which NVENC won't take as a separate setting.
        CodecApi.Set(
            _transform,
            (CodecApi.RateControlMode, CodecApi.RateControlCbr),
            (CodecApi.MeanBitRate, (uint)bitrate),
            (CodecApi.GopSize, (uint)frameRate),
            (CodecApi.LowLatencyMode, true));

        using (var inputType = CreateVideoType(VideoFormatGuids.NV12, frameRate))
        {
            _transform.SetInputType(0, inputType, 0);
        }

        var streamInfo = _transform.GetOutputStreamInfo(0);
        _providesSamples = (streamInfo.Flags & (int)(OutputStreamInfoFlags.OutputStreamProvidesSamples
            | OutputStreamInfoFlags.OutputStreamCanProvideSamples)) != 0;
        _outputSize = Math.Max(streamInfo.Size, 1);

        _eventThread = new Thread(RunEvents) { IsBackground = true, Name = "H.264 encoder events" };
        _eventThread.Start();

        _transform.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
        _transform.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);

        IMFMediaType CreateVideoType(Guid subtype, int rate)
        {
            var type = MediaFactory.MFCreateMediaType();
            type.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            type.Set(MediaTypeAttributeKeys.Subtype, subtype);
            type.Set(MediaTypeAttributeKeys.FrameSize, Pack(_converter.Width, _converter.Height));
            type.Set(MediaTypeAttributeKeys.FrameRate, Pack(rate, 1));
            type.Set(MediaTypeAttributeKeys.PixelAspectRatio, Pack(1, 1));
            type.Set(MediaTypeAttributeKeys.InterlaceMode, (uint)VideoInterlaceMode.Progressive);
            return type;
        }
    }

    /// <summary>
    /// Width of the encoded video.
    /// </summary>
    public int Width => _converter.Width;

    /// <summary>
    /// Height of the encoded video.
    /// </summary>
    public int Height => _converter.Height;

    /// <summary>
    /// A bitrate that keeps screen content sharp at the given size and frame rate, about
    /// 20 Mbps at 2560×1440 and 60 fps.
    /// </summary>
    public static int SuggestBitrate(int width, int height, int frameRate) =>
        (int)Math.Min(width * (long)height * frameRate * 9 / 100, int.MaxValue);

    /// <inheritdoc/>
    /// <remarks>
    /// Converts the frame on the GPU before returning, so the caller can release it right after.
    /// </remarks>
    public void Encode(CapturedFrame frame, TimeSpan timestamp)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ObjectDisposedException.ThrowIf(_disposed, this);

        _freeSlots.Wait();
        _needInput.Wait();
        ThrowIfFailed();

        try
        {
            var texture = _inputs[_nextSlot];
            _nextSlot = (_nextSlot + 1) % InputSlots;

            _converter.Convert(frame, texture);

            using var buffer = MediaFactory.MFCreateDXGISurfaceBuffer(typeof(ID3D11Texture2D).GUID, texture, 0, false);
            using var sample = MediaFactory.MFCreateSample();
            sample.AddBuffer(buffer);
            sample.SampleTime = timestamp.Ticks;
            sample.SampleDuration = _frameDuration;

            _transform.ProcessInput(0, sample, 0);
        }
        catch (SharpGen.Runtime.SharpGenException e)
        {
            _failure = e;
            ThrowIfFailed();
        }
    }

    /// <summary>
    /// Encodes the frames still inside the encoder, then releases it.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_failure is null)
        {
            try
            {
                _transform.ProcessMessage(TMessageType.MessageNotifyEndOfStream, UIntPtr.Zero);
                _transform.ProcessMessage(TMessageType.MessageCommandDrain, UIntPtr.Zero);
                _drained.Wait(TimeSpan.FromSeconds(2));
            }
            catch (SharpGen.Runtime.SharpGenException)
            {
                // Already broken; shutting down is all that's left.
            }
        }

        // Ends a GetEvent still waiting on the event thread.
        MediaFactory.MFShutdownObject(_transform);
        _eventThread.Join(TimeSpan.FromSeconds(2));

        _events.Dispose();
        _transform.Dispose();
        _deviceManager.Dispose();
        foreach (var texture in _inputs)
        {
            texture.Dispose();
        }

        _converter.Dispose();
        _needInput.Dispose();
        _freeSlots.Dispose();
        _drained.Dispose();
    }

    private void RunEvents()
    {
        try
        {
            while (true)
            {
                using var mediaEvent = _events.GetEvent(0);
                switch ((MediaEventTypes)mediaEvent.EventType)
                {
                    case MediaEventTypes.TransformNeedInput:
                        _needInput.Release();
                        break;

                    case MediaEventTypes.TransformHaveOutput:
                        ReadOutput();
                        break;

                    case MediaEventTypes.TransformDrainComplete:
                        _drained.Set();
                        return;
                }
            }
        }
        catch (Exception e) when (_disposed)
        {
            // Shut down while waiting for an event.
            _ = e;
        }
        catch (Exception e)
        {
            _failure = e;
            _drained.Set();

            // Wakes an Encode waiting for input, so it sees the failure.
            _needInput.Release();
            _freeSlots.Release();
        }
    }

    private void ReadOutput()
    {
        var buffer = new OutputDataBuffer { StreamID = 0 };
        IMFSample? ownSample = null;
        if (!_providesSamples)
        {
            ownSample = MediaFactory.MFCreateSample();
            using var memory = MediaFactory.MFCreateMemoryBuffer(_outputSize);
            ownSample.AddBuffer(memory);
            buffer.Sample = ownSample;
        }

        try
        {
            _transform.ProcessOutput(ProcessOutputFlags.None, 1, ref buffer, out _);

            using var sample = buffer.Sample!;
            using var contiguous = sample.ConvertToContiguousBuffer();
            contiguous.Lock(out var data, out _, out var length);
            byte[] bytes;
            try
            {
                bytes = new byte[length];
                Marshal.Copy(data, bytes, 0, length);
            }
            finally
            {
                contiguous.Unlock();
            }

            var isKeyframe = IsKeyframe(sample);
            _output.TryWrite(new EncodedSample(
                TimeSpan.FromTicks(sample.SampleTime),
                TimeSpan.FromTicks(sample.SampleDuration),
                isKeyframe,
                _converter.Width,
                _converter.Height,
                bytes));
        }
        finally
        {
            buffer.Events?.Dispose();
            if (ownSample is not null && !ReferenceEquals(ownSample, buffer.Sample))
            {
                ownSample.Dispose();
            }
        }

        // No B-frames: this output is for the oldest input still inside the encoder.
        _freeSlots.Release();
    }

    // Encoders may leave the flag off frames that aren't keyframes.
    private static bool IsKeyframe(IMFSample sample)
    {
        try
        {
            return sample.GetUInt32(SampleAttributeKeys.CleanPoint) != 0;
        }
        catch (SharpGen.Runtime.SharpGenException)
        {
            return false;
        }
    }

    private void ThrowIfFailed()
    {
        if (_failure is { } failure)
        {
            throw new InvalidOperationException("The encoder failed.", failure);
        }
    }

    private static ulong Pack(int high, int low) => ((ulong)(uint)high << 32) | (uint)low;
}
