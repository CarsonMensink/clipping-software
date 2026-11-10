using System.Threading.Channels;
using Clipping.Core.Capture;
using Clipping.Core.Clips;
using Clipping.Core.Encoding;
using Clipping.Core.Settings;

namespace Clipping.Core.Tests.Clips;

public sealed class BufferedRecordingTests
{
    // The last hardware output may arrive only during shutdown and must remain available to save.
    [Fact]
    public async Task Dispose_RetainsSamplesFlushedByEncoderAndReturnsFrame()
    {
        // Arrange
        var frames = Channel.CreateUnbounded<CapturedFrame>();
        var settings = new MemorySettings();
        var encoded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var owner = new FrameOwner();
        using var recording = new BufferedRecording(frames.Reader,
            (width, height, rate, output) => new FlushEncoder(output, encoded), settings, _ => { });
        frames.Writer.TryWrite(new CapturedFrame(TimeSpan.FromSeconds(1), 1920, 1080, 0, owner));
        await encoded.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Act
        recording.Dispose();

        // Assert
        var sample = Assert.Single(recording.Buffer.Snapshot().Samples);
        Assert.Equal(new byte[] { 1, 2, 3 }, sample.Data);
        Assert.True(sample.IsKeyframe);
        Assert.True(owner.Returned);
    }

    // Closed recordings must stop following preferences while the next recording owns them.
    [Fact]
    public void SettingsChanges_UpdateRetentionUntilRecordingIsDisposed()
    {
        // Arrange
        var frames = Channel.CreateUnbounded<CapturedFrame>();
        var settings = new MemorySettings();
        using var recording = new BufferedRecording(frames.Reader,
            (_, _, _, _) => throw new InvalidOperationException("No frames expected."), settings, _ => { });

        // Act
        settings.Update(s => s with { BufferLength = 90 });
        var activeRetention = recording.Buffer.Retention;
        recording.Dispose();
        settings.Update(s => s with { BufferLength = 120 });

        // Assert
        Assert.Equal(TimeSpan.FromSeconds(90), activeRetention);
        Assert.Equal(activeRetention, recording.Buffer.Retention);
    }

    // Delayed publication models hardware that emits its final frame only when drained.
    private sealed class FlushEncoder(ChannelWriter<EncodedSample> output, TaskCompletionSource encoded) : IVideoEncoder
    {
        private EncodedSample? _sample;
        public int Width => 1920;
        public int Height => 1080;

        // Capture the promised output before notifying the test that shutdown can begin.
        public void Encode(CapturedFrame frame, TimeSpan timestamp, CancellationToken cancellationToken = default)
        {
            _sample = new EncodedSample(timestamp, TimeSpan.FromSeconds(1.0 / 60), true, Width, Height, [1, 2, 3]);
            encoded.TrySetResult();
        }

        // Publication during disposal exercises the ordering required by real encoders.
        public void Dispose()
        {
            if (_sample is { } sample)
            {
                output.TryWrite(sample);
            }
        }
    }

    private sealed class FrameOwner : IDisposable
    {
        public bool Returned { get; private set; }
        public void Dispose() => Returned = true;
    }

    private sealed class MemorySettings : ISettingsStore
    {
        public AppSettings Settings { get; private set; } = new();
        public event EventHandler<SettingsChangedEventArgs>? Changed;

        // Publish preference changes without involving disk in recording lifecycle tests.
        public void Update(Func<AppSettings, AppSettings> change)
        {
            var previous = Settings;
            Settings = change(previous);
            Changed?.Invoke(this, new SettingsChangedEventArgs(previous, Settings));
        }
    }
}
