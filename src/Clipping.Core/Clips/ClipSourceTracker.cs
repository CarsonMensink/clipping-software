using Clipping.Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Clipping.Core.Clips;

/// <summary>
/// Notes which application is on the captured monitor once a second, so a clip can be filed
/// under the one that was on screen for most of it.
/// </summary>
/// <remarks>
/// Checking only when the clip is saved would misfile it whenever the user tabbed out before
/// pressing the hotkey, which is common, since people react after the moment has passed.
/// <para>
/// Each check is taken to hold until the next one. Checks that found no application are left
/// out of the count, so a few failed lookups can't outvote a game. History covers the longest
/// allowed buffer, so it stays small however long the app runs.
/// </para>
/// </remarks>
public sealed class ClipSourceTracker : IClipSourceResolver, IDisposable
{
    /// <summary>How often the captured monitor is checked.</summary>
    public static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(1);

    // The buffer can hold one keyframe interval past its length, so keep some extra.
    private static readonly TimeSpan History = TimeSpan.FromSeconds(AppSettings.MaxBufferLength) + TimeSpan.FromMinutes(1);

    private readonly IClipSourceProbe _probe;
    private readonly TimeProvider _time;
    private readonly ILogger _log;
    private readonly Lock _lock = new();
    private readonly Queue<(TimeSpan At, ClipSource? Source)> _samples = new();
    private readonly ITimer _timer;
    private int _sampling;

    /// <param name="probe">Says what's on the captured monitor.</param>
    /// <param name="timeProvider">
    /// Schedules and timestamps checks, on the clock clip timestamps use. Defaults to the system clock.
    /// </param>
    /// <param name="log">Records failed checks.</param>
    public ClipSourceTracker(IClipSourceProbe probe, TimeProvider? timeProvider = null, ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(probe);

        _probe = probe;
        _time = timeProvider ?? TimeProvider.System;
        _log = log ?? NullLogger.Instance;
        _timer = _time.CreateTimer(_ => Sample(), null, TimeSpan.Zero, SampleInterval);
    }

    /// <inheritdoc/>
    public ClipSource? Resolve(TimeSpan start, TimeSpan end)
    {
        (TimeSpan At, ClipSource? Source)[] samples;
        lock (_lock)
        {
            samples = [.. _samples];
        }

        var totals = new Dictionary<string, (ClipSource Source, TimeSpan Time, TimeSpan LastSeen)>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < samples.Length; i++)
        {
            if (samples[i].Source is not { } source)
            {
                continue;
            }

            var from = samples[i].At > start ? samples[i].At : start;
            var until = i + 1 < samples.Length && samples[i + 1].At < end ? samples[i + 1].At : end;
            if (until <= from)
            {
                continue;
            }

            var time = totals.TryGetValue(source.Id, out var total) ? total.Time : TimeSpan.Zero;
            totals[source.Id] = (source, time + (until - from), until);
        }

        return totals.Count == 0
            ? null
            : totals.Values.MaxBy(total => (total.Time, total.LastSeen)).Source;
    }

    /// <summary>
    /// Stops checking.
    /// </summary>
    public void Dispose() => _timer.Dispose();

    private void Sample()
    {
        // A check that runs long is skipped over rather than queued behind.
        if (Interlocked.Exchange(ref _sampling, 1) == 1)
        {
            return;
        }

        try
        {
            var now = _time.GetElapsedTime(0, _time.GetTimestamp());

            ClipSource? source;
            try
            {
                source = _probe.Current();
            }
            catch (Exception e)
            {
                _log.LogDebug(e, "Couldn't tell which application is on the captured monitor.");
                source = null;
            }

            lock (_lock)
            {
                _samples.Enqueue((now, source));
                while (_samples.Peek().At < now - History)
                {
                    _samples.Dequeue();
                }
            }
        }
        finally
        {
            Volatile.Write(ref _sampling, 0);
        }
    }
}
