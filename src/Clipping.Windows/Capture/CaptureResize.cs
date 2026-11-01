namespace Clipping.Windows.Capture;

internal static class CaptureResize
{
    // Failed resizing keeps the old resources available and disposes any uncommitted replacement.
    public static void Replace<T>(ref T? current, Func<T> create, Action recreate) where T : class, IDisposable
    {
        var replacement = create();
        try
        {
            recreate();
        }
        catch
        {
            replacement.Dispose();
            throw;
        }

        var previous = current;
        current = replacement;
        previous?.Dispose();
    }
}
