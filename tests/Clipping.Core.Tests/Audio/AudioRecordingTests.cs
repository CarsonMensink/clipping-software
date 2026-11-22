using System.Threading.Channels;
using Clipping.Core.Audio;
using Clipping.Core.Settings;
using Microsoft.Extensions.Time.Testing;

namespace Clipping.Core.Tests.Audio;

public sealed class AudioRecordingTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "AudioRecordingTests", Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _time = new();
    private readonly FakeAudioSource _source = new();
    private readonly JsonSettingsStore _settings;

    public AudioRecordingTests()
    {
        Directory.CreateDirectory(_directory);
        _settings = new JsonSettingsStore(Path.Combine(_directory, "settings.json"));
    }

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task ReadAsync_WhenEnabled_ReturnsTheCapturedAudioForTheClip()
    {
        // Arrange
        using var recording = new AudioRecording(_source, _settings, _time);
        var now = Now();
        await _source.PlayAsync(new AudioPacket(now - TimeSpan.FromMilliseconds(10), AudioFormat.Default, Tone(480)));
        await WaitUntilBufferedAsync(recording);

        // Act
        var track = await recording.ReadAsync(now - TimeSpan.FromMilliseconds(10), now);

        // Assert
        Assert.True(_source.Running);
        Assert.NotNull(track);
        Assert.Equal(Tone(480), track.Samples);
    }

    // Audio can be disabled, producing a video-only clip (#9).
    [Fact]
    public async Task TurningAudioOff_StopsCaptureAndClipsHaveNoAudio()
    {
        // Arrange
        using var recording = new AudioRecording(_source, _settings, _time);

        // Act
        _settings.Update(s => s with { AudioEnabled = false });
        var track = await recording.ReadAsync(Now() - TimeSpan.FromSeconds(1), Now());

        // Assert
        Assert.False(_source.Running);
        Assert.Null(track);
    }

    [Fact]
    public void Constructor_WhenAudioIsOff_DoesNotCapture()
    {
        // Arrange
        _settings.Update(s => s with { AudioEnabled = false });

        // Act
        using var recording = new AudioRecording(_source, _settings, _time);

        // Assert
        Assert.False(_source.Running);
    }

    // Audio from before it was turned off must not reappear in later clips.
    [Fact]
    public async Task TurningAudioBackOn_ResumesCaptureWithoutEarlierAudio()
    {
        // Arrange
        using var recording = new AudioRecording(_source, _settings, _time);
        await _source.PlayAsync(new AudioPacket(Now(), AudioFormat.Default, Tone(480)));
        await WaitUntilBufferedAsync(recording);

        // Act
        _settings.Update(s => s with { AudioEnabled = false });
        _settings.Update(s => s with { AudioEnabled = true });

        // Assert
        Assert.True(_source.Running);
        Assert.Equal(TimeSpan.Zero, recording.Buffer.RetainedDuration);
    }

    // Every frame the video buffer holds, up to a keyframe interval past retention, needs audio.
    [Fact]
    public void Retention_FollowsTheBufferLengthSettingWithKeyframeAllowance()
    {
        // Arrange
        using var recording = new AudioRecording(_source, _settings, _time);

        // Act
        _settings.Update(s => s with { BufferLength = 60 });

        // Assert
        Assert.Equal(TimeSpan.FromSeconds(60) + AudioRecording.KeyframeAllowance, recording.Buffer.Retention);
    }

    [Fact]
    public void Dispose_ReleasesTheSource()
    {
        // Arrange
        var recording = new AudioRecording(_source, _settings, _time);

        // Act
        recording.Dispose();

        // Assert
        Assert.True(_source.Disposed);
    }

    private TimeSpan Now() => _time.GetElapsedTime(0, _time.GetTimestamp());

    private static short[] Tone(int frames) =>
        Enumerable.Range(0, frames * 2).Select(i => (short)(i % 100)).ToArray();

    private static async Task WaitUntilBufferedAsync(AudioRecording recording)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (recording.Buffer.RetainedDuration == TimeSpan.Zero && DateTime.UtcNow < deadline)
        {
            await Task.Delay(5);
        }
    }

    private sealed class FakeAudioSource : IAudioSource
    {
        private readonly Channel<AudioPacket> _packets = Channel.CreateUnbounded<AudioPacket>();

        public bool Running { get; private set; }

        public bool Disposed { get; private set; }

        public ChannelReader<AudioPacket> Packets => _packets.Reader;

        public ValueTask PlayAsync(AudioPacket packet) => _packets.Writer.WriteAsync(packet);

        public void Start() => Running = true;

        public void Stop() => Running = false;

        public void Dispose()
        {
            Running = false;
            Disposed = true;
            _packets.Writer.TryComplete();
        }
    }
}
