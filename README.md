# MultiWave

MultiWave is a native Windows desktop application for monitoring Windows playback endpoints, shaping captured system audio, and forwarding that audio to selected playback endpoints. The interface uses WinUI 3 with a dark theme and orange accents.

> **Project status:** MultiWave is an active prototype. Building the app does not verify playback on your particular speakers. Multi-device timing depends on Windows, each endpoint, Bluetooth transport, the room, and microphone setup. See [Known limitations](#known-limitations) before relying on synchronization or calibration results.

## Features

- Enumerates active Windows playback endpoints through NAudio/Core Audio and refreshes when endpoint notifications arrive.
- Saves endpoint selection, Windows endpoint volume, MultiWave stream trim, delay, buffer preference, and per-device EQ settings locally.
- Captures audio from one selected Windows render endpoint using WASAPI loopback, then fans the captured stream to selected output endpoints other than the capture source.
- Converts the captured PCM/float samples to a shared float processing format, then adapts sample rate and channel count for each output endpoint.
- Provides master volume and mute, per-speaker stream trim, and Windows endpoint volume controls. Windows may reject endpoint-volume changes; the UI reports that condition.
- Provides global and per-speaker EQ, 10/31-band controls, preamp, bass/treble, and presets. Global processing runs before fan-out; speaker EQ runs in that speaker's output path.
- Provides Music/Movie output tone profiles, automatic digital headroom controls, a live spectrum analyzer based on captured samples, and stream diagnostics.
- Provides reported-latency alignment, configurable per-output delay and buffer settings, and microphone-based relative acoustic calibration.

## Requirements

- Windows 10/11 on a supported Windows App SDK platform.
- .NET 10 SDK.
- The Windows App SDK and NuGet packages declared in `MultiWave.csproj` (restored by `dotnet restore`).
- For playback, at least one active Windows render endpoint.
- For automatic acoustic calibration, at least two active selected output endpoints and an active microphone or line-in capture endpoint. Windows microphone privacy settings must permit access.

The project uses C#, .NET 10, WinUI 3 / Windows App SDK, and NAudio. It does not include a virtual audio driver.

## Download and install MultiWave

### Quick install for users

The easiest supported installation is the packaged ZIP release:

1. Open the [MultiWave Releases page](https://github.com/suchir7/MultiWave/releases).
2. Open the latest release and download `MultiWave-v1.0.0-x64.zip` under **Assets**.
3. Extract the ZIP file.
4. Open the extracted folder and right-click `Install.ps1`.
5. Select **Run with PowerShell** and follow the prompts.
6. Open the Windows Start menu, search for **MultiWave**, and launch it.

You do not need to install .NET, Visual Studio, or run `dotnet run` when using the packaged release.

### What to download

For normal users, download the packaged release. Do **not** download **Source code (zip)** or **Source code (tar.gz)** unless you are a developer.

1. Open the [MultiWave Releases page](https://github.com/suchir7/MultiWave/releases).
2. Select the newest release, currently `v1.0.0`.
3. Under **Assets**, download `MultiWave-v1.0.0-x64.zip`.
4. Extract the ZIP to a normal folder such as `Downloads\MultiWave`.
5. Do not run the application from inside the ZIP file.

The extracted folder contains:

- `Install.ps1` — installation script
- `MultiWave_1.0.0.0_x64.msix` — the application package
- `Dependencies` — required Windows App SDK packages

### Install the downloaded application

1. Open the extracted `MultiWave-v1.0.0-x64` folder.
2. Right-click `Install.ps1` and select **Run with PowerShell**.
3. If Windows blocks script execution, open PowerShell as Administrator and run:

```powershell
cd "$HOME\Downloads\MultiWave\MultiWave_1.0.0.0_x64_Test"
Set-ExecutionPolicy -Scope Process Bypass
.\Install.ps1
```

4. Follow the installation prompts. The script installs the Windows App SDK dependencies and registers the MSIX package.
5. Open the Windows Start menu, search for **MultiWave**, and launch it.

The current `v1.0.0` package is an unsigned test package. Windows may display a publisher warning or refuse installation until the package is signed and trusted. Only install it if you trust this project. A future release will include a trusted or self-signed certificate with separate certificate-installation instructions.

Do not double-click random files inside the extracted folder. Download packages only from the official
[suchir7/MultiWave Releases page](https://github.com/suchir7/MultiWave/releases).

### First-time application setup

1. Ensure Windows has at least one active playback device.
2. Launch MultiWave and press **Refresh** if the device list is empty.
3. In **Settings**, choose the Windows playback endpoint to capture.
4. Select one or more different physical playback endpoints in the device list.
5. Press **Start System Audio**.
6. Adjust master volume, per-device volume, EQ, and delay.
7. For microphone calibration, enable microphone access in **Windows Settings → Privacy & security → Microphone**, select a microphone under **Synchronization**, and choose **Measure & Calibrate**.

MultiWave saves preferences automatically for the current Windows user. To uninstall it, open **Windows Settings → Apps → Installed apps**, find **MultiWave**, open its menu, and choose **Uninstall**.

### First-time setup

1. Make sure Windows has at least one active playback device.
2. Start MultiWave and press **Refresh** if the device list is empty.
3. Open **Settings** and choose the Windows playback endpoint to capture.
4. Select one or more different physical playback endpoints in the device list.
5. Press **Start System Audio**.
6. Adjust master volume, per-device volume, EQ, and delay as needed.
7. For microphone calibration, allow microphone access in **Windows Settings → Privacy & security → Microphone**, select a microphone under **Synchronization**, and use **Measure & Calibrate**.

MultiWave saves preferences automatically for the current Windows user. To remove the application, open **Windows Settings → Apps → Installed apps**, find **MultiWave**, select the menu button, and choose **Uninstall**.

### Developers: run from source

Downloading the **Source code** ZIP from GitHub is intended for developers, not normal installation. To build and run from source, install the .NET 10 SDK, open PowerShell in the folder containing `MultiWave.csproj`, and run:

```powershell
dotnet restore .\MultiWave.csproj
dotnet build .\MultiWave.csproj
dotnet run --project .\MultiWave.csproj -p:WinAppRunDevTools=off
```

## Build and launch

Open PowerShell in the directory containing `MultiWave.csproj`:

```powershell
dotnet restore .\MultiWave.csproj
dotnet build .\MultiWave.csproj
dotnet run --project .\MultiWave.csproj
```

If `dotnet` is not on `PATH`, install the .NET 10 SDK or invoke the installed `dotnet.exe` by its full path. For WinUI development and packaged launch, this project also documents the WinApp CLI workflow in `AGENTS.md` (`winapp run`).

## First use

1. Launch MultiWave and press **Refresh** if the playback endpoint list is stale.
2. Select the physical playback endpoints that should receive the copied audio. The current capture-source endpoint is excluded from the output list to avoid feeding loopback audio back into itself.
3. Choose the **Capture source** endpoint in Settings. WASAPI loopback captures the Windows mix being rendered by that endpoint.
4. Press **Start System Audio**. Press **Stop** to stop capture and output streams.
5. Adjust master level, per-device trim, EQ, and delay as needed. Per-device delay is saved locally and applied to that stream.

### Important routing behavior

WASAPI loopback is a tap of audio being rendered by the chosen endpoint. MultiWave does not intercept or replace the original Windows route, does not mute the original endpoint, and does not change the Windows default device. With ordinary routing, audio continues to play through its normal Windows destination while MultiWave copies the captured mix to selected non-source endpoints.

Therefore, **selecting speakers in MultiWave alone does not make audio play exclusively through those speakers**. If selected-only routing is required, Windows and each application must send audio to a separate virtual capture/render route that MultiWave can capture, while the physical speakers are selected as outputs. MultiWave itself does not install or provide that route.

## Audio controls

### Endpoint selection and volume

- **Device selection** determines which active non-source playback endpoints MultiWave opens.
- **Device volume** changes the Windows endpoint's master volume. This is system-wide for that endpoint, not an app-private level. Some endpoints or drivers reject this operation; MultiWave reports the failure.
- **MultiWave trim** adjusts the individual duplicated stream without changing Windows' endpoint volume.
- **Master Volume / Mute** applies to all MultiWave output streams.

### Equalizer and effects

Global EQ and output tone processing are applied to the captured stream before it is distributed. Each output has its own endpoint conversion, DSP/EQ, volume trim, and delay path. The spectrum analyzer analyzes real captured audio; it does not generate simulated spectrum values.

**Auto Optimize** is a digital headroom action for EQ/preamp settings. It does not identify a speaker model, infer its acoustic capabilities, or automatically adjust physical speaker bass/treble hardware. Digital limiting can reduce digital clipping but cannot prevent distortion caused by an overloaded speaker or amplifier.

### Synchronization

- **Auto Sync** aligns streams using latency values reported by the Windows audio renderer. It does not measure Bluetooth acoustic latency or synchronize hardware clocks.
- **Manual delay** adds 0–500 ms to an individual output. Delay changes can be made while streams are active. Saved delay and buffer preferences are restored for matching endpoint IDs.
- **Buffer capacity** is configurable per endpoint (80–500 ms) and is used when output streams are prepared. Larger buffers may improve tolerance to scheduling variation at the cost of more buffering latency.
- Diagnostics show Windows-reported renderer latency, current software queue depth, buffer capacity, short reads, overflows, and renderer compensation when those streams are running. Unavailable values are shown as unavailable rather than estimated.

### Automatic acoustic measurement

Open **Synchronization → Measure & Calibrate**:

1. Connect and select at least two playback endpoints.
2. Choose an active microphone or line-in device in the microphone selector.
3. Place the microphone at the listening position and keep the room quiet.
4. Confirm the measurement. MultiWave briefly pauses the system-audio copy and plays a short, low-level swept tone through each selected output one at a time.
5. MultiWave detects probe arrival in the microphone capture, estimates relative arrival differences, adjusts each selected endpoint's software delay (within 0–500 ms), and saves the resulting delay preferences.

The measurement is relative to the chosen microphone position, not an absolute Bluetooth latency measurement. It depends on audible probe level, microphone placement and processing (including automatic gain control), room noise/reverberation, output buffering, and Windows capture timing. It cannot measure hardware clock drift or guarantee perfect synchronization. Listen to the result and adjust delays manually if needed. Microphone access may require enabling access for desktop apps in Windows Privacy & security settings.

## Preferences and data

Preferences are stored for the current Windows user under:

```text
%LOCALAPPDATA%\MultiWave\device-preferences.json
%LOCALAPPDATA%\MultiWave\equalizer-preferences.json
```

The device file stores selection and endpoint preferences keyed by Windows endpoint ID. The equalizer file stores global EQ, custom presets, master volume/mute, Auto Sync state, and the selected calibration microphone ID. Removing these files resets the corresponding saved preferences; close MultiWave before editing them manually.

Windows endpoint IDs can change when a device is reinstalled, paired again, or its driver changes. A device with a new ID may need to be selected and calibrated again.

## Project layout

```text
MultiWave.csproj                 WinUI 3 / .NET target and package references
App.xaml, App.xaml.cs            Application resources and startup
MainWindow.xaml(.cs)             Native application window
MainPage.xaml(.cs)               Dashboard, navigation, controls, and UI bindings
Models/                          Endpoint, EQ, preference, and diagnostics models
Services/AudioDeviceService.cs   Windows playback/capture endpoint enumeration and notifications
Services/SystemAudioCaptureService.cs
                                 WASAPI loopback capture
Services/AudioMixerService.cs    Source format conversion, global EQ, spectrum feed, fan-out
Services/AudioOutputService.cs   Per-endpoint WASAPI streams, conversion, DSP, delay, and diagnostics
Services/AudioSynchronizationService.cs
                                 Output start coordination and diagnostics polling
Services/AcousticCalibrationService.cs
                                 Microphone probe capture and relative arrival alignment
Services/*PreferencesService.cs  Local JSON persistence
```

## Troubleshooting

### No audio duplication starts

- Confirm the capture source is active and Windows is rendering audio through it.
- Check that at least one active playback endpoint is selected other than the capture source.
- Inspect the status message and diagnostics for a disconnected endpoint, rejected stream, or unavailable format.
- Some devices become unavailable while changing Bluetooth profiles. Refresh endpoints and restart the engine after the profile change.

### Audio still comes from the original speaker

That is expected for a loopback tap. MultiWave duplicates captured audio; it does not replace Windows' original playback route. Configure a separate virtual audio route externally if you need selected-only routing.

### Automatic calibration cannot detect a speaker

- Confirm the selected microphone is active, not muted, and permitted in Windows privacy settings.
- Move it near the intended listening position; reduce room noise and reverberation.
- Ensure the selected speaker is audible and its MultiWave trim/master level is not muted.
- Use at least two active outputs. The capture source is not itself an eligible duplicated output.
- If probe detection still fails or a result sounds worse, reset the affected delay and use manual listening-based calibration.

### Volume slider is rejected

The endpoint driver may not expose a writable Windows master-volume control. MultiWave reports the error and restores the displayed endpoint value where it can. The separate MultiWave trim remains an application-stream gain control.

## Testing and verification

Build verification:

```powershell
dotnet build .\MultiWave.csproj
```

Hardware validation should be performed on the target Windows machine. Test at least two real playback endpoints with known test audio, confirm each selected stream responds to its own trim and delay, stop/restart playback, disconnect/reconnect an endpoint, and run acoustic calibration with a microphone at the listening position. A successful build only verifies compilation; it does not prove a particular Bluetooth combination works or is synchronized acoustically.

## Current limitations

- MultiWave does not provide system-wide interception, exclusive selected-only routing, or a virtual audio driver.
- The source endpoint still plays its normal Windows audio. If selected as an output, it is excluded to avoid a loopback feedback path.
- Bluetooth devices may use independent clocks and variable buffering; Windows Core Audio does not give this app controls for hardware clock synchronization.
- Auto Sync uses renderer-reported timing only. Acoustic calibration is an explicit microphone-assisted relative measurement, not continuous automatic tracking.
- Automatic measurement quality is dependent on microphone and room conditions; results should be verified by listening.
- Hardware-specific playback and calibration must be validated on the actual devices. This README does not imply that all endpoint combinations have been tested.
