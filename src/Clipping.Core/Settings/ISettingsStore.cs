namespace Clipping.Core.Settings;

/// <summary>
/// Provides the user's persisted settings and the only way to change them.
/// </summary>
public interface ISettingsStore
{
    /// <summary>
    /// The current settings.
    /// </summary>
    /// <remarks>
    /// Returns an immutable snapshot that is never modified. To see later changes,
    /// read this property again or subscribe to <see cref="Changed"/>.
    /// </remarks>
    AppSettings Settings { get; }

    /// <summary>
    /// Applies a change to the current settings and persists it.
    /// </summary>
    /// <param name="change">
    /// Receives the current settings and returns the updated settings, typically
    /// <c>s =&gt; s with { ... }</c>. Must be fast and free of side effects; do not
    /// call back into the store from it.
    /// </param>
    /// <remarks>
    /// Updates are applied one at a time, each against the latest settings, so
    /// concurrent updates to different settings are never lost.
    /// <para>
    /// If saving fails, the exception propagates, <see cref="Settings"/> is left
    /// unchanged, and <see cref="Changed"/> is not raised.
    /// </para>
    /// </remarks>
    void Update(Func<AppSettings, AppSettings> change);

    /// <summary>
    /// Raised after an update has been persisted and <see cref="Settings"/> reflects it.
    /// </summary>
    /// <remarks>
    /// Raised on the thread that called <see cref="Update"/>. Handlers that touch UI
    /// must marshal to the UI thread themselves.
    /// </remarks>
    event EventHandler<SettingsChangedEventArgs>? Changed;
}
