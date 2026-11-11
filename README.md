# clipping-software

A Windows tray recorder that keeps recent hardware-encoded video in memory and saves clips on demand.

## Where changes belong

- `Clipping.Core`: capture coordination, frame pacing, recording/buffering, clip filing, preferences, and feature-facing tray contracts. No Windows runtime dependency.
- `Clipping.Windows.Support`: Windows adapter support that can compile without WinForms, WinRT, or GPU bindings. The app and tests reference the same compiled implementation. Namespaces retain their Windows feature names. `SingleInstance` still requires Windows named events at runtime.
- `Clipping.Windows`: native capture, graphics, encoding, display enumeration, tray UI, and startup composition.
- `Clipping.Core.Tests`: behavior regressions for Core and Windows.Support.

`Program` creates shared GPU and Media Foundation services before the recording. Capture stops first at shutdown; `BufferedRecording` then joins encoding, flushes the encoder, and finishes buffering. Shared native services outlive that work. `VideoMediaTypes` owns the video attributes shared by live encoding and MP4 output. `AppSettings` owns numeric preference limits and validity policies.

`SaveHotkey` keeps the save hotkey registered to `AppSettings.SelectedHotkey` through `IHotkeyService` (`RegisterHotKey` on Windows). `Rebind` is all or nothing: the new combination is registered and saved before the old one is released. Registration or saving failures leave both unchanged. If a settings notification throws after saving, both use the new combination and the exception propagates.

`HotkeyMenu` shows the save hotkey in the tray and rebinds it through `IHotkeyPrompt` (`HotkeyPrompt`, a small WinForms window, on Windows). The hotkey is suspended while the prompt is open, so the prompt can read the current combination too; it is registered again on close unless a new one replaced it.

## Build and verify

Requires the .NET 10 SDK. Run the app on Windows; cross-building is supported on macOS/Linux.

```sh
dotnet build Clipping.sln --configuration Release
dotnet test tests/Clipping.Core.Tests --configuration Release
```

On macOS, named events and file-region locking are unsupported. Run the portable portion with:

```sh
dotnet test tests/Clipping.Core.Tests --configuration Release --filter 'FullyQualifiedName!~SingleInstanceTests&FullyQualifiedName!~Log_WriteFailsAfterTheFileOpened_DoesNotThrow'
```

These exclusions leave three launch tests and one file-locking test for Windows. The tests themselves remain enabled for Windows runs. GPU capture, hardware encoder negotiation, hotkeys, tray interaction, and MP4 playback also require a Windows smoke test.
