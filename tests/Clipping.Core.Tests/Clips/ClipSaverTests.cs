using Clipping.Core.Clips;
using Clipping.Core.Encoding;
using Clipping.Core.Settings;
using Microsoft.Extensions.Time.Testing;

namespace Clipping.Core.Tests.Clips;

public sealed class ClipSaverTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ClipSaverTests", Guid.NewGuid().ToString("N"));
    private readonly RollingClipBuffer _buffer = new(TimeSpan.FromSeconds(30));
    private readonly FakeWriter _writer = new();
    private readonly FakeSettingsStore _settings;
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 31, 22, 16, 5, TimeSpan.Zero));

    public ClipSaverTests()
    {
        _settings = new FakeSettingsStore(new AppSettings { OutputDirectory = Path.Combine(_directory, "clips") });
        _time.SetLocalTimeZone(TimeZoneInfo.Utc);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public async Task SaveAsync_WhenBufferEmpty_FailsWithoutWriting()
    {
        var result = await CreateSaver().SaveAsync();

        Assert.False(result.Succeeded);
        Assert.NotNull(result.Error);
        Assert.Empty(_writer.Written);
    }

    [Fact]
    public async Task SaveAsync_WritesBufferToDatedFileInOutputDirectory()
    {
        FillBuffer();

        var result = await CreateSaver().SaveAsync();

        Assert.True(result.Succeeded);
        Assert.Equal(Path.Combine(_directory, "clips", "Clip 2026-10-31 22-16-05.mp4"), result.Path);
        Assert.Equal(TimeSpan.FromSeconds(2), result.Duration);
        Assert.Equal(20, Assert.Single(_writer.Written).Clip.Samples.Count);
    }

    [Fact]
    public async Task SaveAsync_CreatesMissingOutputDirectory()
    {
        FillBuffer();

        await CreateSaver().SaveAsync();

        Assert.True(Directory.Exists(Path.Combine(_directory, "clips")));
    }

    [Fact]
    public async Task SaveAsync_TwiceInSameSecond_UsesDifferentNames()
    {
        FillBuffer();
        var saver = CreateSaver();

        var first = await saver.SaveAsync();
        var second = await saver.SaveAsync();

        Assert.Equal(Path.Combine(_directory, "clips", "Clip 2026-10-31 22-16-05 (2).mp4"), second.Path);
        Assert.NotEqual(first.Path, second.Path);
    }

    [Fact]
    public async Task SaveAsync_WhenWriterFails_ReturnsFailure()
    {
        FillBuffer();
        _writer.Failure = new IOException("There is not enough space on the disk.");

        var result = await CreateSaver().SaveAsync();

        Assert.False(result.Succeeded);
        Assert.Equal("There is not enough space on the disk.", result.Error);
    }

    [Fact]
    public async Task SaveAsync_WhenOutputDirectoryInvalid_ReturnsFailure()
    {
        FillBuffer();

        // A folder can't be created where a file already is.
        Directory.CreateDirectory(_directory);
        var blocker = Path.Combine(_directory, "blocker");
        File.WriteAllText(blocker, "");
        _settings.Settings = _settings.Settings with { OutputDirectory = blocker };

        var result = await CreateSaver().SaveAsync();

        Assert.False(result.Succeeded);
        Assert.Empty(_writer.Written);
    }

    [Fact]
    public async Task SaveAsync_ConcurrentSaves_RunOneAtATime()
    {
        FillBuffer();
        var saver = CreateSaver();
        _writer.Delay = TimeSpan.FromMilliseconds(50);

        var results = await Task.WhenAll(saver.SaveAsync(), saver.SaveAsync(), saver.SaveAsync());

        Assert.Equal(1, _writer.MaxConcurrent);
        Assert.Equal(3, results.Select(r => r.Path).Distinct().Count());
    }

    [Fact]
    public void OutputDirectory_WhenNotSet_IsVideosFolder()
    {
        _settings.Settings = new AppSettings();

        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), CreateSaver().OutputDirectory);
    }

    private ClipSaver CreateSaver() => new(_buffer, _writer, _settings, _time);

    private void FillBuffer()
    {
        var frame = TimeSpan.FromTicks(TimeSpan.TicksPerSecond / 10);
        for (var i = 0; i < 20; i++)
        {
            _buffer.Add(new EncodedSample(TimeSpan.FromHours(5) + frame * i, frame, i % 10 == 0, 2560, 1440, [1]));
        }
    }

    private sealed class FakeWriter : IClipWriter
    {
        private int _active;

        public List<(ClipSnapshot Clip, string Path)> Written { get; } = [];

        public Exception? Failure { get; set; }

        public TimeSpan Delay { get; set; }

        public int MaxConcurrent { get; private set; }

        public string Extension => ".mp4";

        public async Task WriteAsync(ClipSnapshot clip, string path, CancellationToken cancellationToken = default)
        {
            MaxConcurrent = Math.Max(MaxConcurrent, Interlocked.Increment(ref _active));
            try
            {
                await Task.Delay(Delay, cancellationToken);
                if (Failure is not null)
                {
                    throw Failure;
                }

                // Like a real writer, leave a file behind so the next name has to differ.
                File.WriteAllBytes(path, []);
                lock (Written)
                {
                    Written.Add((clip, path));
                }
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }
    }

    private sealed class FakeSettingsStore(AppSettings settings) : ISettingsStore
    {
        public AppSettings Settings { get; set; } = settings;

        public event EventHandler<SettingsChangedEventArgs>? Changed
        {
            add { }
            remove { }
        }

        public void Update(Func<AppSettings, AppSettings> change) => Settings = change(Settings);
    }
}
