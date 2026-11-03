using Clipping.Windows.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Clipping.Core.Tests.Windows;

public sealed class FileLoggerProviderTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "Clipping.Tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    // With no console, the log file is the only record of a retried failure.
    [Fact]
    public void Log_WritesLevelCategoryMessageAndExceptionToTodaysFile()
    {
        // Arrange
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 2, 18, 30, 0, TimeSpan.Zero));
        time.SetLocalTimeZone(TimeZoneInfo.Utc);
        var provider = new FileLoggerProvider(_directory, timeProvider: time);
        var logger = provider.CreateLogger("Clipping.Encoding");

        // Act
        logger.LogError(new InvalidOperationException("encoder lost"), "Recording failed; retrying.");
        provider.Dispose();

        // Assert
        var text = File.ReadAllText(Path.Combine(_directory, "clipping-20261102.log"));
        Assert.Contains("[Error] Clipping.Encoding: Recording failed; retrying.", text);
        Assert.Contains("System.InvalidOperationException: encoder lost", text);
    }

    // Debug and trace detail would grow the file without helping a user's bug report.
    [Fact]
    public void Log_BelowInformation_IsNotWritten()
    {
        // Arrange
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 2, 18, 30, 0, TimeSpan.Zero));
        time.SetLocalTimeZone(TimeZoneInfo.Utc);
        var provider = new FileLoggerProvider(_directory, timeProvider: time);
        var logger = provider.CreateLogger("Clipping");

        // Act
        logger.LogDebug("frame 812 paced");
        logger.LogInformation("capture started");
        provider.Dispose();

        // Assert
        var text = File.ReadAllText(Path.Combine(_directory, "clipping-20261102.log"));
        Assert.DoesNotContain("frame 812 paced", text);
        Assert.Contains("capture started", text);
    }

    // A tray app runs for days; each day's entries belong in that day's file.
    [Fact]
    public void Log_AfterMidnight_WritesToTheNewDaysFile()
    {
        // Arrange
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 2, 23, 59, 0, TimeSpan.Zero));
        time.SetLocalTimeZone(TimeZoneInfo.Utc);
        var provider = new FileLoggerProvider(_directory, timeProvider: time);
        var logger = provider.CreateLogger("Clipping");

        // Act
        logger.LogInformation("before midnight");
        time.Advance(TimeSpan.FromMinutes(2));
        logger.LogInformation("after midnight");
        provider.Dispose();

        // Assert
        var first = File.ReadAllText(Path.Combine(_directory, "clipping-20261102.log"));
        var second = File.ReadAllText(Path.Combine(_directory, "clipping-20261103.log"));
        Assert.Contains("before midnight", first);
        Assert.DoesNotContain("after midnight", first);
        Assert.Contains("after midnight", second);
    }

    // Without pruning, months of daily files would pile up in the user's AppData.
    [Fact]
    public void Log_KeepsOnlyTheNewestFiles()
    {
        // Arrange
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero));
        time.SetLocalTimeZone(TimeZoneInfo.Utc);
        var provider = new FileLoggerProvider(_directory, filesKept: 2, timeProvider: time);
        var logger = provider.CreateLogger("Clipping");

        // Act
        for (var day = 0; day < 4; day++)
        {
            logger.LogInformation("still running");
            time.Advance(TimeSpan.FromDays(1));
        }

        provider.Dispose();

        // Assert
        var files = Directory.GetFiles(_directory).Select(Path.GetFileName).Order();
        Assert.Equal(["clipping-20261103.log", "clipping-20261104.log"], files);
    }

    // A failing log must never take down the code that was reporting a failure.
    [Fact]
    public void Log_UnwritableDirectory_DoesNotThrow()
    {
        // Arrange
        Directory.CreateDirectory(Path.GetDirectoryName(_directory)!);
        File.WriteAllText(_directory, "a file where the log folder should be");
        var provider = new FileLoggerProvider(_directory);
        var logger = provider.CreateLogger("Clipping");

        try
        {
            // Act
            var failure = Record.Exception(() => logger.LogError("Recording failed; retrying."));

            // Assert
            Assert.Null(failure);
        }
        finally
        {
            provider.Dispose();
            File.Delete(_directory);
        }
    }

    // A write that fails once the file is open (a full disk, a locked region) leaves unwritten bytes
    // behind; cleaning up after it must not throw a second time.
    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void Log_WriteFailsAfterTheFileOpened_DoesNotThrow()
    {
        // Arrange
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 2, 18, 30, 0, TimeSpan.Zero));
        time.SetLocalTimeZone(TimeZoneInfo.Utc);
        var provider = new FileLoggerProvider(_directory, timeProvider: time);
        var logger = provider.CreateLogger("Clipping");
        logger.LogInformation("file opened");
        using var blocker = new FileStream(
            Path.Combine(_directory, "clipping-20261102.log"), FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        blocker.Lock(0, long.MaxValue);

        // Act
        var whileWriting = Record.Exception(() => logger.LogError("Recording failed; retrying."));
        var whileDisposing = Record.Exception(provider.Dispose);

        // Assert
        Assert.Null(whileWriting);
        Assert.Null(whileDisposing);
        blocker.Unlock(0, long.MaxValue);
    }

    // A removable or network drive can come back; logging resumes without a restart.
    [Fact]
    public void Log_DirectoryBecomesWritable_ResumesWriting()
    {
        // Arrange
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 2, 18, 30, 0, TimeSpan.Zero));
        time.SetLocalTimeZone(TimeZoneInfo.Utc);
        Directory.CreateDirectory(Path.GetDirectoryName(_directory)!);
        File.WriteAllText(_directory, "a file where the log folder should be");
        var provider = new FileLoggerProvider(_directory, timeProvider: time);
        var logger = provider.CreateLogger("Clipping");
        logger.LogInformation("while unwritable");

        // Act
        File.Delete(_directory);
        logger.LogInformation("after recovery");
        provider.Dispose();

        // Assert
        var text = File.ReadAllText(Path.Combine(_directory, "clipping-20261102.log"));
        Assert.Contains("after recovery", text);
    }
}
