namespace Clipping.Core.Clips;

/// <summary>
/// The application a clip was recorded from.
/// </summary>
/// <param name="Id">
/// Identifies the application: two sources are the same application when their ids match,
/// ignoring case. Usually its publisher and product name, so one game's several executables
/// count as one application while unrelated programs that share an executable name don't.
/// </param>
/// <param name="Executable">File name of the program, such as <c>Cyberpunk2077.exe</c>.</param>
/// <param name="DisplayName">Name to show the user, such as <c>Cyberpunk 2077</c>.</param>
public sealed record ClipSource(string Id, string Executable, string DisplayName);
