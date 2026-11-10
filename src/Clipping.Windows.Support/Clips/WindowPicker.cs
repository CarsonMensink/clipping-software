using System.Drawing;

namespace Clipping.Windows.Clips;

/// <summary>
/// Chooses which window a monitor is showing, from its windows in front-to-back order.
/// </summary>
/// <remarks>
/// Only what's actually visible counts: the parts of a window hidden by windows in front of it
/// are left out. The frontmost window with at least <see cref="MinimumShare"/> of the monitor
/// visible wins, so a small game window in front of a maximised browser is still the game.
/// When no window shows that much, the one showing most wins. Holds no Windows API calls, so
/// it can be tested anywhere.
/// </remarks>
internal static class WindowPicker
{
    /// <summary>The share of the monitor a window must show to win outright.</summary>
    public const double MinimumShare = 0.10;

    /// <summary>
    /// The window the monitor is showing.
    /// </summary>
    /// <param name="frontToBack">Candidate windows and their bounds, frontmost first.</param>
    /// <param name="monitor">The monitor's bounds, in the same coordinates.</param>
    /// <returns>The chosen window, or 0 if none is visible on the monitor.</returns>
    public static nint Pick(IEnumerable<(nint Window, Rectangle Bounds)> frontToBack, Rectangle monitor)
    {
        var monitorArea = (double)monitor.Width * monitor.Height;
        if (monitorArea <= 0)
        {
            return 0;
        }

        var inFront = new List<Rectangle>();
        nint best = 0;
        var bestShare = 0.0;

        foreach (var (window, bounds) in frontToBack)
        {
            var onMonitor = Rectangle.Intersect(bounds, monitor);
            if (onMonitor.Width <= 0 || onMonitor.Height <= 0)
            {
                continue;
            }

            var share = (Area(onMonitor) - CoveredArea(onMonitor, inFront)) / monitorArea;
            if (share >= MinimumShare)
            {
                return window;
            }

            if (share > bestShare)
            {
                best = window;
                bestShare = share;
            }

            inFront.Add(onMonitor);
        }

        return best;
    }

    private static double Area(Rectangle rect) => (double)rect.Width * rect.Height;

    /// <summary>
    /// How much of <paramref name="target"/> the union of <paramref name="covers"/> hides.
    /// </summary>
    /// <remarks>
    /// Splits the target into a grid along every cover's edges and adds up the covered cells.
    /// There are only ever a few dozen windows, so this is cheap.
    /// </remarks>
    private static double CoveredArea(Rectangle target, List<Rectangle> covers)
    {
        var clipped = covers
            .Select(cover => Rectangle.Intersect(cover, target))
            .Where(cover => cover.Width > 0 && cover.Height > 0)
            .ToList();
        if (clipped.Count == 0)
        {
            return 0;
        }

        var xs = clipped.SelectMany(c => new[] { c.Left, c.Right }).Append(target.Left).Append(target.Right).Distinct().Order().ToArray();
        var ys = clipped.SelectMany(c => new[] { c.Top, c.Bottom }).Append(target.Top).Append(target.Bottom).Distinct().Order().ToArray();

        var covered = 0.0;
        for (var i = 0; i + 1 < xs.Length; i++)
        {
            for (var j = 0; j + 1 < ys.Length; j++)
            {
                var cell = new Point(xs[i], ys[j]);
                if (clipped.Any(c => c.Contains(cell)))
                {
                    covered += (double)(xs[i + 1] - xs[i]) * (ys[j + 1] - ys[j]);
                }
            }
        }

        return covered;
    }
}
