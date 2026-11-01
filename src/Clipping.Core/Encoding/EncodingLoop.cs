using System.Threading.Channels;
using Clipping.Core.Capture;

namespace Clipping.Core.Encoding;

/// <summary>
/// Encodes captured frames into a constant-frame-rate video stream.
/// </summary>
/// <remarks>
/// Reads frames, places each on a <see cref="FramePacer"/> timeline, and encodes the repeats
/// the timeline calls for followed by the frame itself. The latest frame is kept so it can be
/// repeated: when it arrives, and as time passes while the screen is idle.
/// <para>
/// A change in frame size starts a new encoder and a new timeline. If the encoder fails, or
/// fails to start, it is dropped, and the next frame starts a new one.
/// </para>
/// <para>
/// Frame timestamps must be on the clock of the <see cref="TimeProvider"/>'s
/// <see cref="TimeProvider.GetTimestamp"/>, which is how idle time is measured.
/// </para>
/// </remarks>
public sealed class EncodingLoop : IDisposable
{
    /// <summary>
    /// How often idle time is filled with repeats of the latest frame.
    /// </summary>
    public static readonly TimeSpan FillInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// How far behind the present idle time is filled, leaving room for frames still on their
    /// way from capture.
    /// </summary>
    public static readonly TimeSpan FillHoldback = TimeSpan.FromMilliseconds(100);

    private readonly ChannelReader<CapturedFrame> _frames;
    private readonly VideoEncoderFactory _createEncoder;
    private readonly int _frameRate;
    private readonly TimeProvider _time;

    private readonly Channel<EncodedSample> _samples = Channel.CreateUnbounded<EncodedSample>(
        new UnboundedChannelOptions { SingleReader = true });

    private IVideoEncoder? _encoder;
    private int _encoderWidth;
    private int _encoderHeight;
    private FramePacer? _pacer;
    private CapturedFrame? _latest;
    private bool _disposed;

    /// <param name="frames">The captured frames. The loop disposes each one it reads.</param>
    /// <param name="createEncoder">Creates an encoder whenever the frame size changes.</param>
    /// <param name="frameRate">Frames per second of the video.</param>
    /// <param name="timeProvider">Measures idle time. Defaults to the system clock.</param>
    public EncodingLoop(
        ChannelReader<CapturedFrame> frames,
        VideoEncoderFactory createEncoder,
        int frameRate,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentNullException.ThrowIfNull(createEncoder);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frameRate);

        _frames = frames;
        _createEncoder = createEncoder;
        _frameRate = frameRate;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// The encoded video, in order. A new encoder starts with a keyframe. Completes once the
    /// loop is disposed.
    /// </summary>
    public ChannelReader<EncodedSample> Samples => _samples.Reader;

    /// <summary>
    /// Encodes frames until the frame channel completes or <paramref name="cancellationToken"/>
    /// is cancelled.
    /// </summary>
    /// <remarks>
    /// Encoding blocks while waiting for the encoder, so this occupies a thread-pool thread
    /// while it runs. Dispose the loop only after the returned task completes.
    /// </remarks>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        Task<bool>? frameReady = null;
        try
        {
            while (true)
            {
                frameReady ??= _frames.WaitToReadAsync(cancellationToken).AsTask();

                using (var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    var fillDue = Task.Delay(FillInterval, _time, idle.Token);
                    if (await Task.WhenAny(frameReady, fillDue).ConfigureAwait(false) == frameReady)
                    {
                        idle.Cancel();
                        if (!await frameReady.ConfigureAwait(false))
                        {
                            return;
                        }

                        frameReady = null;
                        while (_frames.TryRead(out var frame))
                        {
                            Process(frame);
                        }
                    }
                }

                Fill(Now());
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>
    /// Encodes the frames still inside the encoder, then completes <see cref="Samples"/>.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DropEncoder();
        _samples.Writer.TryComplete();
    }

    /// <summary>
    /// Encodes one captured frame and the repeats it calls for. Takes ownership of the frame.
    /// </summary>
    internal void Process(CapturedFrame frame)
    {
        if (_encoder is null || frame.Width != _encoderWidth || frame.Height != _encoderHeight)
        {
            DropEncoder();
            try
            {
                _encoder = _createEncoder(frame.Width, frame.Height, _frameRate, _samples.Writer);
            }
            catch (InvalidOperationException)
            {
                // The next frame tries again.
                frame.Dispose();
                return;
            }

            _encoderWidth = frame.Width;
            _encoderHeight = frame.Height;
            _pacer = new FramePacer(_frameRate);
        }

        var placement = _pacer!.Place(frame.Timestamp);
        if (placement.Time is not { } time)
        {
            frame.Dispose();
            EncodeRepeats(placement.Repeats);
            return;
        }

        if (!EncodeRepeats(placement.Repeats) || !TryEncode(frame, time))
        {
            frame.Dispose();
            return;
        }

        _latest?.Dispose();
        _latest = frame;
    }

    /// <summary>
    /// Repeats the latest frame for the idle time up to <paramref name="now"/>, minus
    /// <see cref="FillHoldback"/>.
    /// </summary>
    internal void Fill(TimeSpan now)
    {
        if (_pacer is null || _latest is null)
        {
            return;
        }

        EncodeRepeats(_pacer.FillUntil(now - FillHoldback));
    }

    private bool EncodeRepeats(IReadOnlyList<TimeSpan> times)
    {
        foreach (var time in times)
        {
            if (_latest is null || !TryEncode(_latest, time))
            {
                return false;
            }
        }

        return true;
    }

    private bool TryEncode(CapturedFrame frame, TimeSpan time)
    {
        try
        {
            _encoder!.Encode(frame, time);
            return true;
        }
        catch (InvalidOperationException)
        {
            // The encoder broke; the next frame starts a new one.
            DropEncoder();
            return false;
        }
    }

    private void DropEncoder()
    {
        _encoder?.Dispose();
        _encoder = null;
        _pacer = null;
        _latest?.Dispose();
        _latest = null;
    }

    // Frame timestamps count from the same origin as GetTimestamp.
    private TimeSpan Now() => _time.GetElapsedTime(0, _time.GetTimestamp());
}
