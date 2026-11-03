using Clipping.Windows.Shell;

namespace Clipping.Core.Tests.Windows;

public sealed class SingleInstanceTests
{
    // Unique per test, so tests never collide with each other or with a running copy of the app.
    private readonly string _name = $"Clipping.Tests.{Guid.NewGuid():N}";

    // Two copies would show two icons and both capture and encode.
    [Fact]
    public void TryClaim_WhileAnotherCopyRuns_SignalsItAndIsRefused()
    {
        // Arrange
        using var running = RunningCopy.Start(_name);

        // Act
        var second = SingleInstance.TryClaim(_name, exitGrace: TimeSpan.FromMilliseconds(100));

        // Assert
        Assert.Null(second);
        Assert.True(running.SecondLaunchSeen.Wait(TimeSpan.FromSeconds(5)));
    }

    // Relaunching right after Exit, while the old copy is still shutting down, must not leave
    // nothing running.
    [Fact]
    public void TryClaim_RunningCopyExitsWithinGrace_TakesOver()
    {
        // Arrange
        using var running = RunningCopy.StartExitingOnSecondLaunch(_name);

        // Act
        using var second = SingleInstance.TryClaim(_name, exitGrace: TimeSpan.FromSeconds(5));

        // Assert
        Assert.True(running.ExitedOnSecondLaunch);
        Assert.NotNull(second);
    }

    // The signal a takeover sent to the departing copy is not a later launch; the new copy must not
    // tell the user it is "already running" about itself, yet must still hear real later launches.
    [Fact]
    public void TryClaim_AfterTakingOver_ReportsOnlyLaterLaunches()
    {
        // Arrange
        using var running = RunningCopy.StartExitingOnSecondLaunch(_name);
        using var takenOver = SingleInstance.TryClaim(_name, exitGrace: TimeSpan.FromSeconds(5))!;
        using var secondLaunches = new SemaphoreSlim(0);
        takenOver.OnSecondLaunch(() => secondLaunches.Release());

        // Act
        var reportedItself = secondLaunches.Wait(TimeSpan.FromMilliseconds(300));

        // Another thread, as a later launch is another process: a mutex lets its owner re-acquire it.
        SingleInstance? later = null;
        var launch = new Thread(() => later = SingleInstance.TryClaim(_name, exitGrace: TimeSpan.FromMilliseconds(100)));
        launch.Start();
        launch.Join();
        var reportedLater = secondLaunches.Wait(TimeSpan.FromSeconds(5));

        // Assert
        Assert.True(running.ExitedOnSecondLaunch);
        Assert.False(reportedItself);
        Assert.Null(later);
        Assert.True(reportedLater);
    }

    /// <summary>
    /// A claim held on its own thread, as a separate running copy would hold it; a mutex can only
    /// be released by the thread that owns it.
    /// </summary>
    private sealed class RunningCopy : IDisposable
    {
        private readonly ManualResetEventSlim _exit = new();
        private readonly Thread _thread;
        private volatile bool _exitedOnSecondLaunch;

        private RunningCopy(string name, bool exitOnSecondLaunch)
        {
            using var claimed = new ManualResetEventSlim();
            _thread = new Thread(() =>
            {
                using var claim = SingleInstance.TryClaim(name)!;
                if (!exitOnSecondLaunch)
                {
                    claim.OnSecondLaunch(SecondLaunchSeen.Set);
                    claimed.Set();
                    _exit.Wait();
                    return;
                }

                // Stands in for a copy that is shutting down: it no longer listens, so the signal it
                // is sent stays pending. Waiting on the event proves a later launch signalled while
                // this copy still held the claim; setting it again puts the signal back, unconsumed.
                using var signal = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\{name}.SecondLaunch");
                claimed.Set();
                if (signal.WaitOne(TimeSpan.FromSeconds(5)))
                {
                    signal.Set();
                    _exitedOnSecondLaunch = true;
                }
            });
            _thread.Start();
            claimed.Wait();
        }

        public ManualResetEventSlim SecondLaunchSeen { get; } = new();

        /// <summary>Whether the copy let go only after a later launch had signalled it.</summary>
        public bool ExitedOnSecondLaunch => _exitedOnSecondLaunch;

        /// <summary>Starts a copy that runs, and hears later launches, until disposed.</summary>
        public static RunningCopy Start(string name) => new(name, exitOnSecondLaunch: false);

        /// <summary>Starts a copy that exits as soon as a later launch signals it.</summary>
        public static RunningCopy StartExitingOnSecondLaunch(string name) => new(name, exitOnSecondLaunch: true);

        public void Dispose()
        {
            _exit.Set();
            _thread.Join();
        }
    }
}
