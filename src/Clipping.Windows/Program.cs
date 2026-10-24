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

        Console.WriteLine(displayTarget.GetAvailableMonitors());
        
        return 0;
    }
}
