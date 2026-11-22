using System.Runtime.InteropServices;
using System.Threading.Channels;
using Clipping.Core.Audio;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Clipping.Windows.Audio;

/// <summary>
/// Captures what the default output device plays, with WASAPI loopback.
/// </summary>
/// <remarks>
/// Windows converts the device's mix to <see cref="AudioFormat.Default"/> (resampling and
/// downmixing as needed), so everything downstream sees one format whatever the device.
/// <para>
/// Each packet is timed by the performance counter reading WASAPI took when the device played
/// its first frame. That is the clock Windows.Graphics.Capture stamps frames with, so audio and
/// video line up without either being timed on arrival.
/// </para>
/// <para>
/// Capture runs on its own thread, polling every <see cref="PollInterval"/>. If the device fails
/// or is removed, capture reopens the current default device after <see cref="RetryDelay"/>,
/// for as long as it is running. Following a change of default device while the old one still
/// works is left for later.
/// </para>
/// </remarks>
internal sealed class LoopbackAudioSource : IAudioSource
{
    /// <summary>How often captured audio is collected from the device.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(10);

    /// <summary>How long to wait before reopening a device that failed.</summary>
    public static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(1);

    // How much audio WASAPI holds for us between polls.
    private static readonly TimeSpan DeviceBuffer = TimeSpan.FromMilliseconds(200);

    private readonly AudioFormat _format = AudioFormat.Default;
    private readonly ILogger _log;
    private readonly Lock _gate = new();

    // About ten seconds of packets at the usual 10 ms device period.
    private readonly Channel<AudioPacket> _packets = Channel.CreateBounded<AudioPacket>(
        new BoundedChannelOptions(1000) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = true });

    private Thread? _thread;
    private ManualResetEventSlim? _stop;
    private bool _disposed;

    public LoopbackAudioSource(ILogger<LoopbackAudioSource> log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log;
    }

    /// <inheritdoc/>
    public ChannelReader<AudioPacket> Packets => _packets.Reader;

    /// <inheritdoc/>
    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_thread is not null)
            {
                return;
            }

            var stop = new ManualResetEventSlim();
            _stop = stop;

            // A new thread is in the multithreaded apartment, which WASAPI's objects are free in.
            _thread = new Thread(() => Run(stop)) { IsBackground = true, Name = "Audio capture" };
            _thread.Start();
        }
    }

    /// <inheritdoc/>
    public void Stop()
    {
        Thread? thread;
        ManualResetEventSlim? stop;
        lock (_gate)
        {
            thread = _thread;
            stop = _stop;
            _thread = null;
            _stop = null;
        }

        if (thread is null)
        {
            return;
        }

        stop!.Set();
        thread.Join();
        stop.Dispose();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        Stop();
        _packets.Writer.TryComplete();
    }

    // Keeps capturing until stopped, reopening the device whenever it fails.
    private void Run(ManualResetEventSlim stop)
    {
        var failing = false;
        while (!stop.IsSet)
        {
            try
            {
                Capture(stop, () =>
                {
                    if (failing)
                    {
                        failing = false;
                        _log.LogInformation("Audio capture resumed.");
                    }
                });
            }
            catch (Exception e) when (e is COMException or InvalidOperationException or ArgumentException)
            {
                // Logged once per outage rather than once a second while there's no device.
                if (!failing)
                {
                    failing = true;
                    _log.LogWarning(e, "Audio capture failed; clips will be silent until it recovers. Retrying.");
                }
            }

            stop.Wait(RetryDelay);
        }
    }

    // Captures from the current default output device until stopped or it fails.
    private void Capture(ManualResetEventSlim stop, Action started)
    {
        using var devices = new MMDeviceEnumerator();
        using var device = devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
        using var client = device.CreateAudioClient();

        // Asks Windows to convert the mix to our format, whatever the device runs at.
        var flags = AudioClientStreamFlags.Loopback | AudioClientStreamFlags.AutoConvertPcm | AudioClientStreamFlags.SrcDefaultQuality;
        client.Initialize(AudioClientShareMode.Shared, flags, DeviceBuffer.Ticks, 0,
            new WaveFormat(_format.SampleRate, 16, _format.Channels), Guid.Empty);

        var capture = client.AudioCaptureClient;
        client.Start();
        try
        {
            _log.LogInformation("Capturing audio from {Device}.", device.FriendlyName);
            started();

            TimeSpan? expected = null;
            while (!stop.Wait(PollInterval))
            {
                while (capture.GetNextPacketSize() > 0)
                {
                    var data = capture.GetBuffer(out var frames, out var bufferFlags, out _, out var qpcPosition);
                    try
                    {
                        var packet = Read(data, frames, bufferFlags, qpcPosition, expected);
                        expected = packet.End;
                        _packets.Writer.TryWrite(packet);
                    }
                    finally
                    {
                        capture.ReleaseBuffer(frames);
                    }
                }
            }
        }
        finally
        {
            client.Stop();
        }
    }

    private AudioPacket Read(IntPtr data, int frames, AudioClientBufferFlags flags, long qpcPosition, TimeSpan? expected)
    {
        var samples = new short[frames * _format.Channels];

        // A silent packet's buffer may hold anything; the zeros already are silence.
        if ((flags & AudioClientBufferFlags.Silent) == 0)
        {
            Marshal.Copy(data, samples, 0, samples.Length);
        }

        // The position is in 100 ns units of the performance counter, as TimeSpan ticks are.
        // Without one, continue straight on from the previous packet.
        var timestamp = (flags & AudioClientBufferFlags.TimestampError) == 0
            ? TimeSpan.FromTicks(qpcPosition)
            : expected ?? Now() - _format.DurationOf(frames);

        return new AudioPacket(timestamp, _format, samples);
    }

    // The same clock as frame timestamps: elapsed time since the performance counter started.
    private static TimeSpan Now() => TimeProvider.System.GetElapsedTime(0, TimeProvider.System.GetTimestamp());
}
