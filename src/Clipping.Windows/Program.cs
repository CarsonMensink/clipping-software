using Clipping.Core.Capture;
using Clipping.Core.Display;
using Clipping.Core.Encoding;
using Clipping.Core.Settings;
using Clipping.Windows.Capture;
using Clipping.Windows.Display;
using Clipping.Windows.Encoding;
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

        // Started before, and shut down after, everything that encodes.
        using var mediaFoundation = new MediaFoundationRuntime();

        using var frameSource = new FrameSource(store, gpuDevice.Direct3DDevice, gpuDevice.Device, gpuDevice.Context);

        // Follows the selected monitor; disposed before frameSource, since it calls into it.
        using var captureCoordinator = new CaptureCoordinator(displayTarget, frameSource);
        captureCoordinator.Start();

        using var encoderActivate = FindEncoder(gpuDevice);
        using var encodingLoop = new EncodingLoop(
            frameSource.Frames,
            (width, height, frameRate, output) => new H264Encoder(
                encoderActivate,
                gpuDevice.Device,
                gpuDevice.Context,
                width,
                height,
                frameRate,
                H264Encoder.SuggestBitrate(width, height, frameRate),
                output),
            store.Settings.FrameRate);

        using var stopEncoding = new CancellationTokenSource();
        var encoding = Task.Run(() => encodingLoop.RunAsync(stopEncoding.Token));

        // Pumps Windows messages so display changes are delivered. #17 will pass the tray here.
        Application.Run();

        stopEncoding.Cancel();
        encoding.Wait();

        return 0;
    }

    /// <summary>
    /// The best hardware H.264 encoder on the GPU that captures, so it can read its textures.
    /// </summary>
    private static Vortice.MediaFoundation.IMFActivate FindEncoder(GpuDevice gpuDevice)
    {
        var encoders = HardwareEncoders.FindH264(gpuDevice.AdapterVendorId);
        if (encoders.Count == 0)
        {
            throw new InvalidOperationException("The GPU has no hardware H.264 encoder.");
        }

        foreach (var spare in encoders.Skip(1))
        {
            spare.Dispose();
        }

        return encoders[0];
    }
}
