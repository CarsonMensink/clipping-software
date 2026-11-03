namespace Clipping.Core.Shell;

/// <summary>
/// The application's lifetime as the tray shell runs it.
/// </summary>
public interface IAppShell
{
    /// <summary>
    /// Runs the message loop until the application exits.
    /// </summary>
    /// <remarks>
    /// Must be called on the thread that created the shell. Returns after <see cref="RequestExit"/>
    /// or the tray's Exit entry; the caller then shuts down whatever it started.
    /// </remarks>
    void Run();

    /// <summary>
    /// Asks <see cref="Run"/> to return.
    /// </summary>
    /// <remarks>Callable from any thread. Messages already queued are handled first.</remarks>
    void RequestExit();
}
