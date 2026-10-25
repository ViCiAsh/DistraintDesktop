# Distraint for Windows

Desktop port of the Android app: lock distracting programs until you've studied.

## Build and run
Needs the .NET 8 SDK on Windows 10/11.

    dotnet run
    dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true

The published `Distraint.exe` is in `bin/Release/net8.0-windows/win-x64/publish/`.
Data is stored in `%AppData%\Distraint\state.json` (delete it to rerun first-run setup).

## How the Android parts map over
| Android | Windows |
|---|---|
| package name | exe name (e.g. `chrome.exe`) |
| Accessibility service | `Core/Blocker.cs`, polls the foreground window every 500 ms |
| Usage-stats log | Blocker adds foreground seconds per app into StudyState |
| Lock overlay / popups | `OverlayWindow` (one per monitor) / `BannerWindow` |
| "Go home" | minimize / close window / terminate (Settings) |
| Device admin (uninstall protection) | typed-code exit guard + no exit during strict lock |
| SharedPreferences | JSON file |
| App picker (launcher apps) | Start Menu + running windows + Browse for .exe |

## Behaviour notes
- Closing the window hides it to the tray; the blocker keeps running. Exit is in the tray menu.
- Study apps stop counting after 15 min without keyboard/mouse. Locked apps always count.
- "Start studying" pauses when the Distraint window loses focus (same idea as Android's onPause).

## Known limits
- Task Manager can still kill the process. A watchdog second process would be the next step.
- Exclusive-fullscreen games can hide the lock cover; use borderless mode, or set the action to Terminate.
- Microsoft Store (UWP) apps aren't identified by exe name reliably.
