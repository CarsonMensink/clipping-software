using Clipping.Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Clipping.Core.Clips;

/// <summary>
/// Notes which application is on the captured monitor once a second, so a clip can be filed
/// under the one that held most of its last stretch.
/// </summary>
/// <remarks>
/// Checking only when the clip is saved would misfile it whenever the user tabbed out before
/// pressing the hotkey, which is common, since people react after the moment has passed. Counting
/// the whole clip equally misfiles long buffers the other way: four minutes of one game followed by
/// a minute of another would outvote the game the user actually clipped.
/// <para>
/// So every second of the last <see cref="RecentWindow"/> counts in full, and older time counts
/// less the further back it was, halving every <see cref="HalfLife"/>. A clip is usually filed
/// under whatever held most of its last 30 seconds, but older time still counts, so a tab-out has
/// to last a while before it wins, and when recent checks found nothing, earlier ones decide.
/// </para>
/// <para>
/// Time in a chat app or browser counts for <see cref="WatchedWeight"/> of that, so tabbing out to
/// reply to a message or look something up doesn't take the clip from the game on either side.
/// But people do clip a stream, call or video on purpose. So if one is on screen in the last
/// <see cref="SaveGrace"/> of the clip, the user is taken to be watching it, and chat apps and
/// browsers are discounted between two stretches of play in the same app, or a switch to a
/// different app played for at least 15 seconds. The earlier stretch must last at least 15
/// seconds, or the later one must start within two minutes of it ending. Watching before or
/// after these stretches counts in full. A stretch needs
/// at least five identified seconds; failed checks neither break it nor add to its duration.
/// </para>
/// <para>
/// Time in a utility such as the file manager or a game launcher always counts for only
/// <see cref="UtilityWeight"/>, so a brief visit rarely takes the clip from another app.
/// </para>
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

    /// <summary>How much of the end of a clip counts in full.</summary>
    public static readonly TimeSpan RecentWindow = TimeSpan.FromSeconds(30);

    /// <summary>How far before <see cref="RecentWindow"/> time on screen counts half as much.</summary>
    public static readonly TimeSpan HalfLife = TimeSpan.FromSeconds(15);

    /// <summary>How much time in a chat app or browser counts while the user isn't away from games.</summary>
    public const double WatchedWeight = 0.1;

    /// <summary>How much time in a utility, such as the file manager or a game launcher, counts.</summary>
    public const double UtilityWeight = 0.01;

    /// <summary>
    /// How close to the end of a clip a chat app or browser has to be on screen for the user to be
    /// taken to be watching it rather than playing.
    /// </summary>
    public static readonly TimeSpan SaveGrace = TimeSpan.FromSeconds(5);

    /// <summary>How long the user has to stay in an application for it to count as having gone back to it.</summary>
    public static readonly TimeSpan PlayingStretch = TimeSpan.FromSeconds(5);

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

        // The stretch of each check that falls within the clip, oldest first.
        var spans = new List<(ClipSource Source, TimeSpan From, TimeSpan Until)>();
        for (var i = 0; i < samples.Length; i++)
        {
            var from = samples[i].At > start ? samples[i].At : start;
            var until = i + 1 < samples.Length && samples[i + 1].At < end ? samples[i + 1].At : end;
            if (samples[i].Source is { } source && until > from)
            {
                spans.Add((source, from, until));
            }
        }

        if (spans.Count == 0)
        {
            return null;
        }

        var discounted = Played(spans, end);

        var totals = new Dictionary<string, (ClipSource Source, double Weight, TimeSpan LastSeen)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (source, from, until) in spans)
        {
            var weight = Weight(from, until, end) * NonGamePrograms.KindOf(source.Executable) switch
            {
                NonGameKind.Utility => UtilityWeight,
                NonGameKind.Watched when discounted.Any(window => from >= window.From && from < window.Until) => WatchedWeight,
                _ => 1,
            };

            var total = totals.TryGetValue(source.Id, out var sum) ? sum.Weight : 0;
            totals[source.Id] = (source, total + weight, until);
        }

        return totals.Values.MaxBy(total => (total.Weight, total.LastSeen)).Source;
    }

    /// <summary>Intervals in which watching is treated as a break between stretches of play.</summary>
    private static List<(TimeSpan From, TimeSpan Until)> Played(
        List<(ClipSource Source, TimeSpan From, TimeSpan Until)> spans, TimeSpan end)
    {
        if (!spans.Any(span => span.Until > end - SaveGrace &&
            NonGamePrograms.KindOf(span.Source.Executable) == NonGameKind.Watched))
        {
            return [(TimeSpan.MinValue, TimeSpan.MaxValue)];
        }

        // Missing checks are absent from spans: they neither break nor extend a run.
        var runs = new List<(string Id, TimeSpan From, TimeSpan Until, TimeSpan Duration)>();
        string? app = null;
        var first = TimeSpan.Zero;
        var last = TimeSpan.Zero;
        var duration = TimeSpan.Zero;
        foreach (var (source, from, until) in spans)
        {
            var playable = NonGamePrograms.KindOf(source.Executable) == NonGameKind.None;
            if (!playable || !string.Equals(app, source.Id, StringComparison.OrdinalIgnoreCase))
            {
                if (app is not null && duration >= PlayingStretch)
                {
                    runs.Add((app, first, last, duration));
                }

                app = playable ? source.Id : null;
                first = from;
                duration = TimeSpan.Zero;
            }

            duration += until - from;
            last = until;
        }

        if (app is not null && duration >= PlayingStretch)
        {
            runs.Add((app, first, last, duration));
        }

        var windows = new List<(TimeSpan From, TimeSpan Until)>();
        for (var q = 1; q < runs.Count; q++)
        {
            for (var p = 0; p < q; p++)
            {
                if ((string.Equals(runs[p].Id, runs[q].Id, StringComparison.OrdinalIgnoreCase) ||
                     runs[q].Duration >= TimeSpan.FromSeconds(15)) &&
                    (runs[p].Duration >= TimeSpan.FromSeconds(15) ||
                     runs[q].From - runs[p].Until <= TimeSpan.FromSeconds(120)))
                {
                    // The earliest qualifying predecessor covers all later predecessors too.
                    windows.Add((runs[p].Until, runs[q].From));
                    break;
                }
            }
        }

        return windows;
    }

    /// <summary>
    /// How much the time from <paramref name="from"/> to <paramref name="until"/> counts in a clip
    /// ending at <paramref name="end"/>, in seconds at full weight.
    /// </summary>
    internal static double Weight(TimeSpan from, TimeSpan until, TimeSpan end) =>
        WeightWithin(end - from) - WeightWithin(end - until);

    /// <summary>
    /// How much all the time less than <paramref name="age"/> before the end of a clip counts, in
    /// seconds at full weight.
    /// </summary>
    private static double WeightWithin(TimeSpan age)
    {
        if (age <= RecentWindow)
        {
            return age.TotalSeconds;
        }

        // The integral of 2^(-t / HalfLife) for t from 0 to age - RecentWindow.
        var halfLives = (age - RecentWindow) / HalfLife;
        return RecentWindow.TotalSeconds + HalfLife.TotalSeconds / Math.Log(2) * (1 - Math.Pow(2, -halfLives));
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
