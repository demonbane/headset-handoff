# Headset Handoff

A lightweight Windows tray app that switches audio outputs when your wireless headset connects or disconnects.

Turn your headset on and Windows switches to it. Turn it off and Windows switches back to your chosen speakers. Headset Handoff reads the receiver's wireless connection status directly, so SteelSeries GG and Sonar do not need to run.

## Supported hardware

Currently tested with the **SteelSeries Arctis Nova Elite receiver, USB ID `1038:2270`**, on 64-bit Windows. Other models and receiver variants are not yet supported.

Requirements:

- 64-bit Windows with .NET Framework 4.8.
- A supported USB receiver and two available Windows playback devices.
- A writable folder for the executable, settings, and local log.

No administrator rights, audio drivers, or additional packages are required. The app has no mixer, telemetry, or network requests.

## Build and run

Clone the repository, then run this from its root in PowerShell:

```powershell
.\source\build.ps1 -Test
.\HeadsetHandoff.exe
```

The build uses the C# compiler included with Windows' .NET Framework. It creates `HeadsetHandoff.exe` in the repository root and runs the included checks when `-Test` is specified. Exit the app before rebuilding it.

Find the **blue headphone icon** in the notification area, possibly under the hidden-icons arrow. Right-click it and select:

1. **When headset connects** → your headset's playback output.
2. **When headset disconnects** → your speakers or other fallback output.
3. **Automatic switching** → leave enabled.

The app waits until both outputs are selected before changing Windows' defaults. It saves selections in a local `settings.json`, which Git ignores. The optional `settings.example.json` contains the structure with no machine-specific identifiers; copying it is not required.

## Tray controls

| Control | Purpose |
| --- | --- |
| Automatic switching | Pause or resume automatic output changes |
| When headset connects | Choose the connected-headset output |
| When headset disconnects | Choose the fallback output |
| Also switch call audio | Make the default communications playback output follow the headset too |
| Apply current headset state | Reapply the output after a manual selection |
| Start when I sign in | Enable or disable quiet startup for your Windows account |
| View activity log | Open the local diagnostic log |
| Exit | Stop switching and leave the current Windows output selected |

Microphone selection is never changed. Call playback is left alone unless **Also switch call audio** is enabled.

Start at sign-in is off by default. Keep the executable in the same folder after enabling it; disable that option before moving or deleting the app. You can also stop the app with:

```powershell
.\HeadsetHandoff.exe --exit
```

Startup runs when you **sign in**, not every time Windows wakes. If launched from a terminal or automation tool that would terminate its child processes when it closes, Headset Handoff relaunches through Windows' local process broker so it can keep running independently. Normal desktop and sign-in launches stay in the original process. If the broker cannot start the independent copy, the reason is logged and the original copy continues running.

## Switching behavior

- Listens for wireless connection events and queries receiver status every three seconds.
- Waits for a state to persist for 1.5 seconds before changing the output.
- Allows five seconds for firmware initialization when opening a receiver connection.
- Switches Windows' console and multimedia playback defaults, then reads them back to verify the result.
- Applies the output once per confirmed state change, so it does not continually override manual choices.
- Retries every five seconds if the selected output is temporarily unavailable.
- Leaves the current output alone when status is invalid, missing, or stale. Confirmed receiver removal selects the fallback.
- Attempts to recover after USB errors and waits for fresh receiver status after Windows resumes.

Apps that explicitly choose an audio device may ignore Windows' default. Some apps require reopening their audio stream. Other automatic output switchers can compete with Headset Handoff.

## Local files and privacy

`settings.json` contains your selected Windows audio endpoint IDs and preferences. `HeadsetHandoff.log` records connection transitions, timestamps, verified output selections, and errors. The log rotates at approximately 1 MB. These files and settings backups are excluded from Git.

The app only sends a receiver status request. It does not change headset EQ, sidetone, firmware, or other receiver settings.

## Development and validation

Source layout:

- `source/App.cs`: tray interface, settings, startup, and switching coordination.
- `source/Receiver.cs`: receiver monitoring, status parsing, and debounce.
- `source/HidNative.cs`: Windows USB HID access.
- `source/Audio.cs`: Windows audio endpoint enumeration and default selection.
- `source/Tests.cs`: protocol and state-transition regression checks.
- `source/ProcessLifetime.cs`: independent launch when a process runner imposes a kill-on-close lifetime.
- `source/LifetimeTests.cs`: bounded child-process tests that reproduce launcher shutdown and verify survival after the fix.
- `source/AudioProbe.cs`: read-only inspection of available playback devices and Windows defaults.

The initial hardware validation confirmed both switching directions with GG and Sonar stopped, approximately 1.6–1.8 seconds after the receiver reported the change. Already-connected startup, controlled exit/restart, and duplicate-launch prevention were also checked. The 25 automated checks cover status parsing, malformed reports, Bluetooth isolation, unknown states, debounce, transient connections, telemetry loss/recovery, and launch policy. Additional Windows integration tests reproduce a child being terminated when its launcher job closes, then verify that an independent child survives and can still exit normally.

Physical USB unplug/replug, Windows sleep/resume, and startup at sign-in have not yet been exercised. Offline and cable-charging status mappings are based on the protocol reference; the live states observed during initial testing were standby and connected.

## Protocol and Windows API references

The implementation is independently written. Protocol field definitions are based on the [Arctis Sound Manager Nova Elite profile](https://github.com/loteran/Arctis-Sound-Manager/blob/main/src/arctis_sound_manager/devices/nova_elite.yaml) and verified against receiver reports:

- `01 B0` status reply: radio connection byte at offset 14.
- `07 B5` connection event: radio connection byte at offset 4.
- `8` means connected; `1`, `2`, and `4` mean offline, cable charging, and standby respectively. Other values are treated as indeterminate.

Additional references:

- [Windows HID API](https://learn.microsoft.com/en-us/windows-hardware/drivers/hid/hid-api)
- [Windows MMDevice API](https://learn.microsoft.com/en-us/windows/win32/coreaudio/mmdevice-api)
- [NAudio IMMDeviceCollection declaration](https://github.com/naudio/NAudio/blob/master/NAudio.Wasapi/CoreAudioApi/Interfaces/IMMDeviceCollection.cs)
- [IPolicyConfig interface](https://github.com/tartakynov/audioswitch/blob/master/IPolicyConfig.h)

Default-output changes use the undocumented Windows `IPolicyConfig::SetDefaultEndpoint` interface. A future Windows release could change it; all switch operations check the result and verify Windows' reported defaults.
