namespace Clipping.Windows.Shell;

/// <summary>
/// Keeps one copy of the application running per user session, and tells it when another
/// launch was turned away.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    // Longer than the running copy takes to drain and shut down after Exit.
    private static readonly TimeSpan DefaultExitGrace = TimeSpan.FromSeconds(5);

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _secondLaunch;
    private RegisteredWaitHandle? _wait;

    private SingleInstance(Mutex mutex, EventWaitHandle secondLaunch)
    {
        _mutex = mutex;
        _secondLaunch = secondLaunch;
    }

    /// <summary>
    /// Claims the right to run.
    /// </summary>
    /// <param name="name">Names the claim; copies with the same name exclude each other.</param>
    /// <param name="exitGrace">
    /// How long to wait for a running copy that may be exiting. A copy that is still running
    /// has already been signalled by then; this only delays the refused launch's own exit.
    /// </param>
    /// <returns>
    /// The claim, held until disposed on this thread; or <see langword="null"/> if another copy
    /// is running. That copy has been signalled, unless it runs with rights this launch lacks
    /// (for example, as administrator), which leaves its objects closed to this launch.
    /// </returns>
    public static SingleInstance? TryClaim(string name = "Clipping", TimeSpan? exitGrace = null)
    {
        EventWaitHandle? secondLaunch = null;
        Mutex? mutex = null;
        try
        {
            // Opened before the mutex, so by the time a later launch can see our claim we already
            // hold the event. Otherwise its signal could land on an event that is destroyed when it
            // closes its handle, before we open ours.
            secondLaunch = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\{name}.SecondLaunch");
            mutex = new Mutex(initiallyOwned: true, $@"Local\{name}.SingleInstance", out var createdNew);

            if (createdNew)
            {
                return new SingleInstance(mutex, secondLaunch);
            }

            secondLaunch.Set();

            // The running copy may be shutting down after Exit, in which case it never shows the
            // notification. Take over once it lets go rather than leave nothing running.
            if (WaitForRelease(mutex, exitGrace ?? DefaultExitGrace))
            {
                // Our own signal went to the copy that left; don't announce ourselves as a second
                // launch. A launch that signalled in the moment since then goes unreported.
                secondLaunch.Reset();
                return new SingleInstance(mutex, secondLaunch);
            }
        }
        catch (Exception failure) when (failure is UnauthorizedAccessException or WaitHandleCannotBeOpenedException)
        {
            // Another copy created these objects with rights we lack. It runs; we can't tell it.
        }

        secondLaunch?.Dispose();
        mutex?.Dispose();
        return null;
    }

    /// <summary>
    /// Runs <paramref name="handler"/> on a thread-pool thread after later launches are turned away.
    /// </summary>
    /// <remarks>
    /// At least once after one or more launches: launches close together may be reported once,
    /// and one that arrives while this copy is taking over from an exiting copy may go unreported.
    /// </remarks>
    public void OnSecondLaunch(Action handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        _wait?.Unregister(null);
        _wait = ThreadPool.RegisterWaitForSingleObject(
            _secondLaunch, (_, _) => handler(), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    /// <summary>
    /// Releases the claim. Must be called on the thread that claimed it.
    /// </summary>
    public void Dispose()
    {
        _wait?.Unregister(null);
        _secondLaunch.Dispose();
        _mutex.ReleaseMutex();
        _mutex.Dispose();
    }

    /// <returns>Whether this thread now owns the mutex.</returns>
    private static bool WaitForRelease(Mutex mutex, TimeSpan timeout)
    {
        try
        {
            return mutex.WaitOne(timeout);
        }
        catch (AbandonedMutexException)
        {
            // The holder died without releasing; ownership passed to this thread anyway.
            return true;
        }
    }
}
