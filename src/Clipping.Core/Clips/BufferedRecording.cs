using System.Threading.Channels;
using Clipping.Core.Capture;
using Clipping.Core.Encoding;
using Clipping.Core.Settings;

namespace Clipping.Core.Clips;

/// <summary>
/// Owns encoding and buffering as one recording, including settings updates and ordered shutdown.
/// Stop capture before disposing; the shared graphics services must outlive this recording.
/// </summary>
public sealed class BufferedRecording : IDisposable
{
    private readonly ISettingsStore _settings;
    private readonly EncodingLoop _loop;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _encoding;
    private readonly Task _buffering;
    private bool _disposed;

    // A recording always has a consumer before encoding starts publishing samples.
    public BufferedRecording(
        ChannelReader<CapturedFrame> frames,
        VideoEncoderFactory createEncoder,
        ISettingsStore settings,
        Action<Exception> reportFailure)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(reportFailure);
        _settings = settings;
        _loop = new EncodingLoop(frames, createEncoder, settings.Settings.FrameRate);
        _loop.Failed += reportFailure;
        Buffer = new RollingClipBuffer(TimeSpan.FromSeconds(settings.Settings.BufferLength));
        _settings.Changed += OnSettingsChanged;
        _buffering = BufferSamplesAsync();
        _encoding = Task.Run(() => _loop.RunAsync(_stop.Token));
    }

    public RollingClipBuffer Buffer { get; }

    // Saving and retention changes share the same buffer for the lifetime of this recording.
    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e) =>
        Buffer.Retention = TimeSpan.FromSeconds(_settings.Settings.BufferLength);

    // Only this consumer mutates the buffer as encoded video arrives.
    private async Task BufferSamplesAsync()
    {
        await foreach (var sample in _loop.Samples.ReadAllAsync().ConfigureAwait(false))
        {
            Buffer.Add(sample);
        }
    }

    // Flush the encoder before completing its output so no final samples are lost or awaited forever.
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _settings.Changed -= OnSettingsChanged;
        _stop.Cancel();
        try
        {
            _encoding.GetAwaiter().GetResult();
        }
        finally
        {
            try
            {
                _loop.Dispose();
                _buffering.GetAwaiter().GetResult();
            }
            finally
            {
                _stop.Dispose();
            }
        }
    }
}
