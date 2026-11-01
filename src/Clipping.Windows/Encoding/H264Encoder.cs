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

    private readonly IMFActivate _activate;
    private readonly IMFTransform _transform;
    private readonly IMFMediaEventGenerator _events;
    private readonly IMFDXGIDeviceManager _deviceManager;
    private readonly Nv12Converter _converter;
    private readonly ID3D11Texture2D[] _inputs = new ID3D11Texture2D[InputSlots];
    private readonly long _frameDuration;
    private readonly bool _providesSamples;
    private readonly int _outputSize;

    private readonly ChannelWriter<EncodedSample> _output;

    // A driver stall must return control to the retry loop within a bounded interval.
    private readonly EncoderProgress _progress = new(InputSlots, TimeSpan.FromSeconds(2));
    private readonly H264ParameterSets _parameterSets = new();
    private readonly ManualResetEventSlim _drained = new();
    private readonly Thread _eventThread;

    private int _nextSlot;
    private volatile Exception? _failure;
    private volatile bool _disposed;

    /// <param name="encoderActivate">
    /// A hardware H.264 encoder on the same GPU as <paramref name="device"/>. Can be reused for
    /// the next encoder once this one is disposed.
    /// </param>
    /// <param name="device">The device the frames' textures live on.</param>
    /// <param name="context">Its immediate context.</param>
    /// <param name="width">Width of the captured frames.</param>
    /// <param name="height">Height of the captured frames.</param>
    /// <param name="frameRate">Frames per second.</param>
    /// <param name="bitrate">Bits per second, spent whether or not the screen changes.</param>
    /// <param name="output">Where encoded frames go. Never completed by the encoder.</param>
    /// <exception cref="InvalidOperationException">The encoder couldn't be started.</exception>
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
        _activate = encoderActivate;
        _frameDuration = TimeSpan.TicksPerSecond / frameRate;
        _eventThread = new Thread(RunEvents) { IsBackground = true, Name = "H.264 encoder events" };

        try
        {
            _converter = new Nv12Converter(device, context, width, height, frameRate);
            for (var i = 0; i < InputSlots; i++)
            {
                _inputs[i] = _converter.CreateTarget();
            }

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

            _eventThread.Start();

            _transform.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
            _transform.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);
        }
        catch (Exception e)
        {
            _disposed = true;
            try
            {
                Release();
            }
            catch (Exception cleanupFailure)
            {
                throw new InvalidOperationException("Couldn't start or clean up the encoder.",
                    new AggregateException(e, cleanupFailure));
            }

            throw new InvalidOperationException($"Couldn't start the encoder: {e.Message}", e);
        }

        // Input and output must describe the same dimensions and timing to negotiate successfully.
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
    public void Encode(CapturedFrame frame, TimeSpan timestamp, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ObjectDisposedException.ThrowIf(_disposed, this);

        try
        {
            _progress.WaitForInput(cancellationToken);
        }
        catch (Exception failure)
        {
            // Cancellation or a timeout must skip draining a transform that cannot make progress.
            _failure = failure;
            throw;
        }

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

        try
        {
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
        }
        finally
        {
            // Unexpected drain errors must still release GPU resources before recording retries.
            Release();
        }
    }

    // Also cleans up after a constructor that failed partway, so any field may still be unset.
    private void Release()
    {
        List<Exception> failures = [];
        if (_transform is not null)
        {
            // Ends a GetEvent still waiting on the event thread.
            Attempt(() => MediaFactory.MFShutdownObject(_transform).CheckError());
        }

        Attempt(() => EncoderProgress.JoinIfStarted(_eventThread));
        Attempt(() => _events?.Dispose());
        Attempt(() => _transform?.Dispose());

        // Reactivation must create a fresh transform after a resize or failed startup.
        Attempt(() => _activate.ShutdownObject());
        Attempt(() => _deviceManager?.Dispose());
        foreach (var texture in _inputs)
        {
            Attempt(() => texture?.Dispose());
        }

        Attempt(() => _converter?.Dispose());
        Attempt(_progress.Dispose);
        Attempt(_drained.Dispose);
        if (failures.Count > 0)
        {
            throw new AggregateException("Couldn't release all encoder resources.", failures);
        }

        // One failed native release must not strand the remaining GPU resources.
        void Attempt(Action release)
        {
            try
            {
                release();
            }
            catch (Exception failure)
            {
                failures.Add(failure);
            }
        }
    }

    // Error events carry their own failure status independently of GetEvent's return value.
    private void RunEvents()
    {
        try
        {
            while (true)
            {
                using var mediaEvent = _events.GetEvent(0);
                // GetEvent succeeding does not mean the operation reported by the event succeeded.
                EncoderProgress.CheckEvent(mediaEvent.Status.Code, (MediaEventTypes)mediaEvent.EventType == MediaEventTypes.Error);
                switch ((MediaEventTypes)mediaEvent.EventType)
                {
                    case MediaEventTypes.TransformNeedInput:
                        _progress.NeedInput();
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

            _progress.Fail(e);
        }
    }

    // Output owns the compressed bytes and codec headers needed after this encoder is retired.
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

            // Some encoders send parameter sets only in the media type or the very first GOP.
            var header = _parameterSets.Update(bytes);
            if (header is null)
            {
                using var outputType = _transform.GetOutputCurrentType(0);
                try
                {
                    header = _parameterSets.Update(outputType.GetBlob(MediaTypeAttributeKeys.MpegSequenceHeader));
                }
                catch (SharpGen.Runtime.SharpGenException e) when (e.HResult == unchecked((int)0xC00D36E6))
                {
                    // MF_E_ATTRIBUTENOTFOUND: wait for in-band SPS/PPS instead.
                }
            }

            var isKeyframe = IsKeyframe(sample);
            _output.TryWrite(new EncodedSample(
                TimeSpan.FromTicks(sample.SampleTime),
                TimeSpan.FromTicks(sample.SampleDuration),
                isKeyframe,
                _converter.Width,
                _converter.Height,
                bytes) { SequenceHeader = header });
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
        _progress.HaveOutput();
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
