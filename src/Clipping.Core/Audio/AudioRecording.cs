using Clipping.Core.Clips;
using Clipping.Core.Settings;

namespace Clipping.Core.Audio;

/// <summary>
/// Keeps recent system audio buffered alongside the video, following the audio settings.
/// </summary>
/// <remarks>
/// Retention follows <see cref="AppSettings.BufferLength"/>, plus
/// <see cref="KeyframeAllowance"/>, since the video buffer can hold up to a keyframe interval
/// more than its retention and every frame of it needs audio.
/// <para>
/// Turning <see cref="AppSettings.AudioEnabled"/> off stops capture and discards the audio
/// held, and clips are saved without an audio track. Turning it back on starts a fresh
/// history; earlier clips aren't given audio from before.
/// </para>
/// </remarks>
public sealed class AudioRecording : IClipAudio, IDisposable
{
    /// <summary>
    /// Audio kept beyond the video retention, covering the extra keyframe interval the video
    /// buffer may hold.
    /// </summary>
    public static readonly TimeSpan KeyframeAllowance = TimeSpan.FromSeconds(5);

    private readonly IAudioSource _source;
    private readonly ISettingsStore _settings;
    private readonly Lock _gate = new();
    private readonly Task _buffering;
    private volatile bool _enabled;
    private bool _disposed;

    /// <param name="source">Where the audio comes from. Owned by the recording from now on.</param>
    /// <param name="settings">Supplies retention and whether audio is on.</param>
    /// <param name="timeProvider">The clock packet timestamps use. Defaults to the system clock.</param>
    public AudioRecording(IAudioSource source, ISettingsStore settings, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(settings);

        _source = source;
        _settings = settings;
        Buffer = new RollingAudioBuffer(RetentionFor(settings.Settings), timeProvider);
        _enabled = settings.Settings.AudioEnabled;
        _settings.Changed += OnSettingsChanged;
        _buffering = BufferPacketsAsync();
        if (_enabled)
        {
            _source.Start();
        }
    }

    /// <summary>
    /// The audio held for upcoming clips.
    /// </summary>
    public RollingAudioBuffer Buffer { get; }

    /// <inheritdoc/>
    public async Task<AudioTrack?> ReadAsync(TimeSpan start, TimeSpan end, CancellationToken cancellationToken = default)
    {
        if (!_enabled)
        {
            return null;
        }

        var track = await Buffer.ReadAsync(start, end, cancellationToken).ConfigureAwait(false);

        // Turned off while waiting: the user asked for clips without audio from now on.
        return _enabled ? track : null;
    }

    private static TimeSpan RetentionFor(AppSettings settings) =>
        TimeSpan.FromSeconds(settings.BufferLength) + KeyframeAllowance;

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            Buffer.Retention = RetentionFor(e.Current);
            if (e.Current.AudioEnabled == _enabled)
            {
                return;
            }

            _enabled = e.Current.AudioEnabled;
            Buffer.Clear();
            if (_enabled)
            {
                _source.Start();
            }
            else
            {
                _source.Stop();
            }
        }
    }

    // Only this consumer adds to the buffer as captured audio arrives.
    private async Task BufferPacketsAsync()
    {
        await foreach (var packet in _source.Packets.ReadAllAsync().ConfigureAwait(false))
        {
            if (_enabled)
            {
                Buffer.Add(packet);
            }
        }
    }

    /// <summary>
    /// Stops capture and releases the source.
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _settings.Changed -= OnSettingsChanged;
        _source.Dispose();
        _buffering.GetAwaiter().GetResult();
    }
}
