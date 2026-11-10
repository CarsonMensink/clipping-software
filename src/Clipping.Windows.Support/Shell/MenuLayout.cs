using Clipping.Core.Shell;

namespace Clipping.Windows.Shell;

/// <summary>
/// Decides the order of a menu's entries and where separators go.
/// </summary>
internal static class MenuLayout
{
    /// <summary>
    /// Returns the visible entries ordered by group, keeping insertion order within a group,
    /// with <see langword="null"/> marking a separator between groups.
    /// </summary>
    /// <param name="entries">Every entry, in the order it was added.</param>
    public static IReadOnlyList<T?> Arrange<T>(IEnumerable<(MenuGroup Group, T Entry, bool Visible)> entries)
        where T : class
    {
        var arranged = new List<T?>();
        MenuGroup? previous = null;

        // OrderBy is stable, so entries in one group stay in the order they were added.
        foreach (var (group, entry, _) in entries.Where(e => e.Visible).OrderBy(e => e.Group))
        {
            if (previous is not null && previous != group)
            {
                arranged.Add(null);
            }

            arranged.Add(entry);
            previous = group;
        }

        return arranged;
    }
}
