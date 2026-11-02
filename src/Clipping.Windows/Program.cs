using Clipping.Core.Capture;
using Clipping.Core.Clips;
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

        // Recording failures must be visible even while the message loop keeps running.
        encodingLoop.Failed += failure => Console.Error.WriteLine($"Recording failed; retrying: {failure}");

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

        // TEMP: the SelectedHotkey setting saves a clip and prints the result. #10 replaces this with the real hotkey service.
        using var saveHotkey = new TempSaveHotkey(store.Settings.SelectedHotkey, async () =>
        {
            var result = await clipSaver.SaveAsync();
            Console.WriteLine(result.Succeeded
                ? $"TEMP: saved {result.Duration.TotalSeconds:0.0} s to {result.Path}"
                : $"TEMP: save failed: {result.Error}");
        });

        // Pumps Windows messages so display changes are delivered. #17 will pass the tray here.
        Application.Run();

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

        public TempSaveHotkey(string combination, Func<Task> onPressed)
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
                Console.WriteLine($"TEMP: couldn't register {combination}; it may be invalid or owned by another app.");
                return;
            }

            Console.WriteLine($"TEMP: press {combination} to save a clip");
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
