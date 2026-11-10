# clipping-software

A Windows tray recorder that keeps recent hardware-encoded video in memory and saves clips on demand.

## Where changes belong

- `Clipping.Core`: capture coordination, frame pacing, recording/buffering, clip filing, preferences, and feature-facing tray contracts. No Windows runtime dependency.
- `Clipping.Windows.Support`: Windows adapter support that can compile without WinForms, WinRT, or GPU bindings. The app and tests reference the same compiled implementation. Namespaces retain their Windows feature names. `SingleInstance` still requires Windows named events at runtime.
- `Clipping.Windows`: native capture, graphics, encoding, display enumeration, tray UI, and startup composition.
- `Clipping.Core.Tests`: behavior regressions for Core and Windows.Support.

`Program` creates shared GPU and Media Foundation services before the recording. Capture stops first at shutdown; `BufferedRecording` then joins encoding, flushes the encoder, and finishes buffering. Shared native services outlive that work. `VideoMediaTypes` owns the video attributes shared by live encoding and MP4 output. `AppSettings` owns numeric preference limits and validity policies.

The temporary save hotkey still reads its combination at startup, pending the hotkey feature.

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
