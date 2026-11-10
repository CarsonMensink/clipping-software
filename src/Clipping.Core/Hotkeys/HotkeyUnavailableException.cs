namespace Clipping.Core.Hotkeys;

/// <summary>
/// A key combination could not be registered.
/// </summary>
/// <remarks>
/// <see cref="Exception.Message"/> is written for the user and says what to do next.
/// </remarks>
public sealed class HotkeyUnavailableException : Exception
{
    /// <param name="combination">The combination that was requested.</param>
    /// <param name="message">What went wrong and what the user can do, shown as is.</param>
    public HotkeyUnavailableException(string combination, string message)
        : base(message)
    {
        Combination = combination;
    }

    /// <summary>
    /// The combination that was requested.
    /// </summary>
    public string Combination { get; }
}
