using Clipping.Core.Capture;
using Clipping.Core.Clips;
using Clipping.Core.Display;
using Clipping.Core.Encoding;
using System.Diagnostics;
using Clipping.Core.Settings;
using Clipping.Core.Shell;
using Clipping.Windows.Capture;
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

        // Disposed after the pipeline below has drained, so the tray icon goes last.
        using var shell = new TrayShell();
        singleInstance.OnSecondLaunch(() => shell.Notifications.Show(
            "Clipping is already running", "Find it in the notification area.", NotificationKind.Info));
        shell.Menu.Add("Open log folder", MenuGroup.App, () =>
        {
            Directory.CreateDirectory(logDirectory);
            Process.Start(new ProcessStartInfo { FileName = logDirectory, UseShellExecute = true });
        });

        var store = new JsonSettingsStore(Path.Combine(appData, "settings.json"));

        using var monitorSource = new ScreenMonitorSource();
        using var displayTarget = new DisplayTarget(monitorSource);

        // Shared by capture and encoding; a texture is only usable on the device that made it.
        using var gpuDevice = new GpuDevice();

        // Started before, and shut down after, everything that encodes.
        using var mediaFoundation = new MediaFoundationRuntime();

        using var frameSource = new FrameSource(
            store, gpuDevice.Direct3DDevice, gpuDevice.Device, gpuDevice.Context, loggerFactory.CreateLogger<FrameSource>());

        // Follows the selected monitor; disposed before frameSource, since it calls into it.
        using var captureCoordinator = new CaptureCoordinator(displayTarget, frameSource);
        captureCoordinator.Start();

        using var encoderActivate = FindEncoder(gpuDevice);
        // Dispose before shared GPU/MF services; a retired worker keeps activation reserved until exit.
        using var encoderLifetime = new EncoderLifetime(TimeSpan.FromSeconds(2));
        using var encodingLoop = new EncodingLoop(
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
            store.Settings.FrameRate);

        // Recording failures must be recorded even while the message loop keeps running.
        encodingLoop.Failed += failure => log.LogError(failure, "Recording failed; retrying.");

        using var stopEncoding = new CancellationTokenSource();
        var encoding = Task.Run(() => encodingLoop.RunAsync(stopEncoding.Token));

        // Holds the last BufferLength seconds of encoded video, following the setting as it changes.
        var clipBuffer = new RollingClipBuffer(TimeSpan.FromSeconds(store.Settings.BufferLength));
        store.Changed += (_, _) => clipBuffer.Retention = TimeSpan.FromSeconds(store.Settings.BufferLength);
        var buffering = Task.Run(async () =>
        {
            await foreach (var sample in encodingLoop.Samples.ReadAllAsync())
            {
                clipBuffer.Add(sample);
            }
        });

        var clipSaver = new ClipSaver(clipBuffer, new Mp4ClipWriter(), store);

        // TEMP: the SelectedHotkey setting saves a clip and shows the result. #10 replaces this with the
        // real hotkey service, and #12 owns the save notification.
        using var saveHotkey = new TempSaveHotkey(store.Settings.SelectedHotkey, log, async () =>
        {
            var result = await clipSaver.SaveAsync();
            if (result.Succeeded)
            {
                shell.Notifications.Show(
                    "TEMP: clip saved", $"{result.Duration.TotalSeconds:0.0} s to {result.Path}", NotificationKind.Info);
            }
            else
            {
                shell.Notifications.Show("TEMP: save failed", result.Error!, NotificationKind.Error);
            }
        });

        // Pumps Windows messages, so display changes and hotkeys are delivered, until Exit is chosen.
        shell.Run();

        stopEncoding.Cancel();
        encoding.Wait();
        encodingLoop.Dispose();
        buffering.Wait();

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

    // TEMP: a bare hotkey for testing saves, read once at startup. Remove once #10 adds the hotkey service.
    private sealed class TempSaveHotkey : NativeWindow, IDisposable
    {
        private const int WM_HOTKEY = 0x0312;
        private const uint MOD_ALT = 0x0001;
        private const uint MOD_CONTROL = 0x0002;
        private const uint MOD_SHIFT = 0x0004;
        private const uint MOD_WIN = 0x0008;
        private const uint MOD_NOREPEAT = 0x4000;

        private readonly Func<Task> _onPressed;

        public TempSaveHotkey(string combination, ILogger log, Func<Task> onPressed)
        {
            _onPressed = onPressed;
            CreateHandle(new CreateParams());

            // "Alt+P", "Ctrl+Shift+S": modifiers, then a key named as in System.Windows.Forms.Keys.
            var modifiers = MOD_NOREPEAT;
            var key = Keys.None;
            foreach (var part in combination.Split('+', StringSplitOptions.TrimEntries))
            {
                switch (part.ToUpperInvariant())
                {
                    case "ALT": modifiers |= MOD_ALT; break;
                    case "CTRL" or "CONTROL": modifiers |= MOD_CONTROL; break;
                    case "SHIFT": modifiers |= MOD_SHIFT; break;
                    case "WIN": modifiers |= MOD_WIN; break;
                    default: Enum.TryParse(part, ignoreCase: true, out key); break;
                }
            }

            if (key == Keys.None || !RegisterHotKey(Handle, 1, modifiers, (uint)key))
            {
                log.LogWarning("TEMP: couldn't register {Combination}; it may be invalid or owned by another app.", combination);
                return;
            }

            log.LogInformation("TEMP: press {Combination} to save a clip", combination);
        }

        public void Dispose()
        {
            UnregisterHotKey(Handle, 1);
            DestroyHandle();
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY)
            {
                _ = _onPressed();
            }

            base.WndProc(ref m);
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr window, int id);
    }
}
