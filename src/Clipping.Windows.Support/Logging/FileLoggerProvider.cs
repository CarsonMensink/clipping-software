using Microsoft.Extensions.Logging;

namespace Clipping.Windows.Logging;

/// <summary>
/// Writes log entries to one file per day, keeping only the newest few.
/// </summary>
/// <remarks>
/// Files are named <c>clipping-yyyyMMdd.log</c> by local date. Logging never throws: an entry
/// that cannot be written is dropped, since a failing log must not take the recorder down
/// with it.
/// </remarks>
internal sealed class FileLoggerProvider : ILoggerProvider
{
    private const string FilePrefix = "clipping-";
    private const string FileExtension = ".log";

    private readonly string _directory;
    private readonly int _filesKept;
    private readonly TimeProvider _time;
    private readonly Lock _lock = new();

    private StreamWriter? _writer;
    private DateOnly _day;
    private bool _disposed;

    /// <param name="directory">Where log files go. Created when the first entry is written.</param>
    /// <param name="filesKept">How many daily files to keep, including today's.</param>
    /// <param name="timeProvider">Dates entries and files. Defaults to the system clock.</param>
    public FileLoggerProvider(string directory, int filesKept = 7, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        ArgumentOutOfRangeException.ThrowIfLessThan(filesKept, 1);

        _directory = directory;
        _filesKept = filesKept;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    /// <summary>
    /// Closes the current file. Later entries are dropped.
    /// </summary>
    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            CloseWriter();
        }
    }

    private void Write(string category, LogLevel level, string message, Exception? exception)
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                var now = _time.GetLocalNow();
                var day = DateOnly.FromDateTime(now.DateTime);
                if (_writer is null || day != _day)
                {
                    Open(day);
                }

                _writer!.WriteLine($"{now:yyyy-MM-dd HH:mm:ss.fff zzz} [{level}] {category}: {message}");
                if (exception is not null)
                {
                    _writer.WriteLine(exception);
                }
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
            {
                // Retry the file on the next entry; the drive may come back.
                CloseWriter();
            }
        }
    }

    private void Open(DateOnly day)
    {
        CloseWriter();

        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, $"{FilePrefix}{day:yyyyMMdd}{FileExtension}");
        _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
        {
            AutoFlush = true,
        };
        _day = day;

        DeleteOldFiles();
    }

    /// <summary>
    /// Closes the current file, if any, without throwing.
    /// </summary>
    private void CloseWriter()
    {
        var writer = _writer;
        _writer = null;

        try
        {
            // Flushes first, so after a failed write it fails again on the bytes still buffered.
            writer?.Dispose();
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            // Those bytes are lost; the handle is released regardless.
        }
    }

    private void DeleteOldFiles()
    {
        var old = Directory.GetFiles(_directory, $"{FilePrefix}*{FileExtension}")
            .Order(StringComparer.OrdinalIgnoreCase)
            .SkipLast(_filesKept);

        foreach (var path in old)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
            {
                // Open elsewhere, perhaps in an editor; try again at the next roll.
            }
        }
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel is >= LogLevel.Information and < LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                provider.Write(category, logLevel, formatter(state, exception), exception);
            }
        }
    }
}
