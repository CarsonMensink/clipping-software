using Clipping.Core.Settings;

namespace Clipping.Windows;

internal static class Program
{
    public static int Main()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var path = Path.Combine(appData, "Clipping", "settings.json");

        ISettingsStore store = new JsonSettingsStore(path);
        
        
        return 0;
    }
}
