using Clipping.Core.Capture;
using Clipping.Core.Display;
using Clipping.Core.Settings;
using Clipping.Windows.Capture;
using Clipping.Windows.Display;
using Clipping.Windows.Graphics;

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

        var store = new JsonSettingsStore(path);

        using var monitorSource = new ScreenMonitorSource();
        using var displayTarget = new DisplayTarget(monitorSource);

        // Shared by capture and encoding; a texture is only usable on the device that made it.
        using var gpuDevice = new GpuDevice();

        using var frameSource = new FrameSource(gpuDevice.Direct3DDevice);

        // Follows the selected monitor; disposed before frameSource, since it calls into it.
        using var captureCoordinator = new CaptureCoordinator(displayTarget, frameSource);
        captureCoordinator.Start();

        // Pumps Windows messages so display changes are delivered. #17 will pass the tray here.
        Application.Run();

        return 0;
    }
}
