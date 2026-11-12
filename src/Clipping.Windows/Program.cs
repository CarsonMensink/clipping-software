using Clipping.Core.Capture;
using Clipping.Core.Clips;
using Clipping.Core.Display;
using Clipping.Core.Hotkeys;
using System.Diagnostics;
using Clipping.Core.Settings;
using Clipping.Core.Shell;
using Clipping.Windows.Capture;
using Clipping.Windows.Clips;
using Clipping.Windows.Display;
using Clipping.Windows.Encoding;
using Clipping.Windows.Graphics;
using Clipping.Windows.Logging;
using Clipping.Windows.Shell;
using Microsoft.Extensions.Logging;

namespace Clipping.Windows;

internal static class Program
{
    [STAThread]
    public static int Main()
    {
        // A second launch only signals the running copy, which tells the user where it is.
        using var singleInstance = SingleInstance.TryClaim();
        if (singleInstance is null)
        {
            return 0;
        }

        // Applies ApplicationHighDpiMode; must run before any Screen or window call.
        ApplicationConfiguration.Initialize();

        var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Clipping");
        var logDirectory = Path.Combine(appData, "logs");

        // Created first and disposed last, so everything below can log, including its shutdown.
        using var logFile = new FileLoggerProvider(logDirectory);
        using var loggerFactory = new LoggerFactory([logFile]);
        var log = loggerFactory.CreateLogger(typeof(Program).FullName!);

        // There is no console, so a crash would otherwise leave no trace.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            log.LogCritical(e.ExceptionObject as Exception, "Unhandled exception; the application is closing.");

        // WinForms catches exceptions thrown on the UI thread (menu handlers, posted work, window
        // hooks) before they become unhandled. Log them and keep running instead of showing its
        // dialog. Must be set before any window is created.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => log.LogError(e.Exception, "UI thread exception; continuing.");

        using var monitorSource = new ScreenMonitorSource();
        using var displayTarget = new DisplayTarget(monitorSource);

        // Disposed after the pipeline below has drained, so the tray icon goes last.
        // Notifications appear on the monitor being recorded.
        using var shell = new TrayShell(
            () => displayTarget.GetSelectedMonitor().Bounds, Path.Combine(appData, "WebView2"), loggerFactory);
        singleInstance.OnSecondLaunch(() => shell.Notifications.Show(
            "Clipping is already running", "Find it in the notification area.", NotificationKind.Info));
        shell.Menu.Add("Open log folder", MenuGroup.App, () =>
        {
            Directory.CreateDirectory(logDirectory);
            Process.Start(new ProcessStartInfo { FileName = logDirectory, UseShellExecute = true });
        });

        var store = new JsonSettingsStore(Path.Combine(appData, "settings.json"));

        // Restores the saved monitor, so it must exist before capture starts below.
        using var monitorMenu = new MonitorMenu(displayTarget, store, shell.Menu, shell.Notifications);

        // Shared by capture and encoding; a texture is only usable on the device that made it.
        using var gpuDevice = new GpuDevice();

        // Started before, and shut down after, everything that encodes.
        using var mediaFoundation = new MediaFoundationRuntime();

        using var frameSource = new FrameSource(
            store, gpuDevice.Direct3DDevice, gpuDevice.Device, gpuDevice.Context, loggerFactory.CreateLogger<FrameSource>());

        using var encoderActivate = HardwareEncoders.SelectH264(gpuDevice.AdapterVendorId);
        // Dispose before shared GPU/MF services; a retired worker keeps activation reserved until exit.
        using var encoderLifetime = new EncoderLifetime(TimeSpan.FromSeconds(2));
        using var recording = new BufferedRecording(
            frameSource.Frames,
            (width, height, frameRate, output) => new H264Encoder(
                encoderActivate,
                encoderLifetime,
                gpuDevice.Device,
                gpuDevice.Context,
                width,
                height,
                frameRate,
                H264Encoder.SuggestBitrate(width, height, frameRate),
                output),
            store,
            failure => log.LogError(failure, "Recording failed; retrying."));

        // Capture stops before recording drains, while the GPU remains available to both.
        using var captureCoordinator = new CaptureCoordinator(displayTarget, frameSource);
        captureCoordinator.Start();

        // Notes once a second which application is on the captured monitor, so clips are filed under it.
        using var clipSources = new ClipSourceTracker(
            new CapturedMonitorProbe(displayTarget), log: loggerFactory.CreateLogger<ClipSourceTracker>());

        var clipSaver = new ClipSaver(recording.Buffer, new Mp4ClipWriter(), store, clipSources);

        // Created on this thread, which runs the message loop, so hotkey presses are delivered here.
        using var hotkeys = new HotkeyService();

        var saveNotifier = new ClipSaveNotifier(shell.Notifications);

        // Saving runs asynchronously so a hotkey press never blocks the message loop.
        using var saveHotkey = new SaveHotkey(hotkeys, store, shell.Notifications, () => _ = SaveClipAsync());
        if (saveHotkey.ActiveCombination is { } combination)
        {
            log.LogInformation("Press {Combination} to save a clip.", combination);
        }

        _ = new HotkeyMenu(saveHotkey, new HotkeyPrompt(), shell.Menu);

        async Task SaveClipAsync()
        {
            saveNotifier.Notify(await clipSaver.SaveAsync());
        }

        // Pumps Windows messages, so display changes and hotkeys are delivered, until Exit is chosen.
        shell.Run();

        return 0;
    }
}
