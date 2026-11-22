# clipping-software

A Windows tray recorder that keeps recent hardware-encoded video in memory and saves clips on demand.

## Where changes belong

- `Clipping.Core`: capture coordination, frame pacing, recording/buffering, clip filing, preferences, and feature-facing tray contracts. No Windows runtime dependency.
- `Clipping.Windows.Support`: Windows adapter support that can compile without WinForms, WinRT, or GPU bindings. The app and tests reference the same compiled implementation. Namespaces retain their Windows feature names. `SingleInstance` still requires Windows named events at runtime.
- `Clipping.Windows`: native capture, graphics, encoding, display enumeration, tray UI, and startup composition.
- `Clipping.UI`: the web UI (Vite + React + TypeScript), shown in WebView2. One HTML page per surface; `overlay.html` draws notifications.
- `Clipping.Core.Tests`: behavior regressions for Core and Windows.Support.

`Program` creates shared GPU and Media Foundation services before the recording. Capture stops first at shutdown; `BufferedRecording` then joins encoding, flushes the encoder, and finishes buffering. Shared native services outlive that work. `VideoMediaTypes` owns the video attributes shared by live encoding and MP4 output. `AppSettings` owns numeric preference limits and validity policies.

`SaveHotkey` keeps the save hotkey registered to `AppSettings.SelectedHotkey` through `IHotkeyService` (`RegisterHotKey` on Windows). `Rebind` is all or nothing: the new combination is registered and saved before the old one is released. Registration or saving failures leave both unchanged. If a settings notification throws after saving, both use the new combination and the exception propagates.

`HotkeyMenu` shows the save hotkey in the tray and rebinds it through `IHotkeyPrompt` (`HotkeyPrompt`, a small WinForms window, on Windows). The hotkey is suspended while the prompt is open, so the prompt can read the current combination too; it is registered again on close unless a new one replaced it.

`INotificationService` is implemented by `OverlayNotifications`: the `overlay.html` page in WebView2, composed through DirectComposition into a topmost, click-through, non-activating window that is excluded from screen capture, so it never appears in clips. It shows on the recorded monitor and is hidden when no card is showing. It covers windowed, borderless, and most fullscreen games, but not true exclusive fullscreen. Host and page talk through the JSON messages in `OverlayProtocol` and `bridge.ts`.

## Build and verify

Requires the .NET 10 SDK and Node.js with npm; building `Clipping.Windows` runs `npm ci` and `npm run build` in `src/Clipping.UI` and copies its `dist/` to `ui/` beside the exe. Pass `-p:SkipUiBuild=true` to build without Node; the app then shows no notifications. Run the app on Windows (it needs the WebView2 runtime, which ships with Windows 11); cross-building is supported on macOS/Linux.

To work on the UI with hot reload, run `npm run dev` in `src/Clipping.UI` and start the app with `CLIPPING_UI_DEV_URL=http://localhost:5173`.

```sh
dotnet build Clipping.sln --configuration Release
dotnet test tests/Clipping.Core.Tests --configuration Release
```

On macOS, named events and file-region locking are unsupported. Run the portable portion with:

```sh
dotnet test tests/Clipping.Core.Tests --configuration Release --filter 'FullyQualifiedName!~SingleInstanceTests&FullyQualifiedName!~Log_WriteFailsAfterTheFileOpened_DoesNotThrow'
```

These exclusions leave three launch tests and one file-locking test for Windows. The tests themselves remain enabled for Windows runs. GPU capture, hardware encoder negotiation, hotkeys, tray interaction, overlay notifications, and MP4 playback also require a Windows smoke test.
