namespace Clipping.Core.Clips;

/// <summary>
/// Programs people commonly have open alongside a game that are known not to be games.
/// </summary>
/// <remarks>
/// Used only to tell a quick look at one of these apart from time spent in a game, so a program
/// missing from the list is no worse off than before it existed: it's counted like any other.
/// </remarks>
internal static class NonGamePrograms
{
    // Chat apps, browsers and video players, which people do sometimes mean to clip: a stream, a
    // call, a video.
    private static readonly HashSet<string> Watched = new(
        [
            "Discord.exe", "DiscordPTB.exe", "DiscordCanary.exe", "slack.exe", "Teams.exe", "ms-teams.exe",
            "Telegram.exe", "WhatsApp.exe", "Signal.exe", "Zoom.exe", "WeChat.exe", "Guilded.exe", "Element.exe",
            "TeamSpeak.exe", "ts3client_win64.exe", "mumble.exe",
            "chrome.exe", "msedge.exe", "firefox.exe", "brave.exe", "opera.exe", "vivaldi.exe",
            "vlc.exe", "mpv.exe", "mpc-hc64.exe", "Video.UI.exe", "Microsoft.Media.Player.exe",
        ],
        StringComparer.OrdinalIgnoreCase);

    // Windows' own tools, music, recording software and game launchers, which nobody means to clip.
    private static readonly HashSet<string> Utilities = new(
        [
            "explorer.exe", "SystemSettings.exe", "Taskmgr.exe", "StartMenuExperienceHost.exe", "SearchHost.exe",
            "LockApp.exe", "Spotify.exe", "AppleMusic.exe", "iTunes.exe", "wallpaper32.exe", "wallpaper64.exe",
            "obs64.exe", "Streamlabs OBS.exe", "Medal.exe", "NVIDIA app.exe", "NVIDIA Share.exe", "RadeonSoftware.exe",
            "XboxPcApp.exe", "itch.exe",
            "steam.exe", "steamwebhelper.exe", "EpicGamesLauncher.exe", "Battle.net.exe", "EADesktop.exe",
            "upc.exe", "UbisoftConnect.exe", "GalaxyClient.exe", "RiotClientServices.exe", "RiotClientUx.exe",
        ],
        StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// What kind of program <paramref name="executable"/>, a file name such as <c>Discord.exe</c>, is.
    /// </summary>
    public static NonGameKind KindOf(string executable) =>
        Watched.Contains(executable) ? NonGameKind.Watched
        : Utilities.Contains(executable) ? NonGameKind.Utility
        : NonGameKind.None;
}

/// <summary>
/// What kind of known non-game a program is.
/// </summary>
internal enum NonGameKind
{
    /// <summary>Not known to be anything but a game.</summary>
    None,

    /// <summary>A chat app or browser, which is sometimes clipped on purpose.</summary>
    Watched,

    /// <summary>A program nobody clips on purpose, such as the file manager or a game launcher.</summary>
    Utility,
}
