using Clipping.Core.Display;
using Clipping.Core.Settings;
using Clipping.Windows.Display;

namespace Clipping.Windows;

internal static class Program
{
    [STAThread]
    public static int Main()
    {
        // Applies ApplicationHighDpiMode; must run before any Screen or window call.
        ApplicationConfiguration.Initialize();

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var path = Path.Combine(appData, "Clipping", "settings.json");

        ISettingsStore store = new JsonSettingsStore(path);

        using var monitorSource = new ScreenMonitorSource();
        using var displayTarget = new DisplayTarget(monitorSource);

        // Pumps Windows messages so display changes are delivered. #17 will pass the tray here.
        Application.Run();

        return 0;
    }
}
