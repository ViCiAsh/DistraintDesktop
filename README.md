# Distraint for Windows
<p align="center">
  <img src="assets/logo.png" alt="Distraint logo" width="280" />
</p>

<h3 align="center">Focus &gt; Progress &gt; Freedom</h3>

<p align="center">
  Earn your screen time by studying.
</p>

Lock distracting programs until you've studied. Distraint for Windows is the desktop companion to the Distraint Android app: pick the apps that distract you, set how much study time you owe, and those apps stay locked until you've put in the work.

## Features

- **App locking** - block any program by its executable name (e.g. `chrome.exe`, `steam.exe`) until your study goal is met.
- **Study time tracking** - time spent in your designated study apps is counted toward your goal. Time in locked apps is always counted too, so you can see where your hours go.
- **Lock overlay** - a cover window is shown on every connected monitor while a locked app is in the foreground. Banner popups handle gentler notifications.
- **Configurable lock action** - choose what happens when you open a locked app: minimize it, close the window, or terminate the process.
- **Strict mode** - while a strict lock is active, you can't exit Distraint. Outside of it, exiting requires typing a confirmation code.
- **Phone pairing and sync** - pair with the Android app so study time is shared between your phone and your PC.
- **Themes** - customizable look via the `Themes` folder.
- **System tray** - closing the window hides Distraint to the tray; the blocker keeps running in the background.
- **Installer** - an Inno Setup script (`installer.iss`) is included for building a Windows installer.

## Installation

### Download the Application

1. Go to the [Releases](https://github.com/ViCiAsh/DistraintDesktop/releases) page.
2. Download the latest application.
3. Install it and run the application as administrator.
4. Configure the Settings.

## Requirements

- Windows 10 or 11
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (to build from source)

## Build and run

Clone the repo and run it directly:

```bash
git clone https://github.com/ViCiAsh/DistraintDesktop.git
cd DistraintDesktop
dotnet run
```

To publish a single-file release build:

```bash
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true
```

The output `Distraint.exe` is in `bin/Release/net8.0-windows/win-x64/publish/`. Because the build is framework-dependent (`--self-contained false`), the .NET 8 Desktop Runtime must be installed on the machine running it.

## First run and data

The first launch has to be run with administrator privilege.

On first launch, a setup window walks you through choosing your apps and study goal. Your data is stored in:

```
%AppData%\Distraint\state.json
```

Delete this file to reset everything and run first-time setup again.

## How it works

A background blocker polls the foreground window every 500 ms. When the active program matches a locked app and your study goal isn't met, Distraint applies your chosen action (minimize, close, or terminate) and shows the lock overlay. Foreground time for each app is added to your study state as you go.

### Behaviour notes

- Closing the main window hides it to the tray. Use the tray menu to exit.
- Study apps stop counting after 15 minutes without keyboard or mouse input. Locked apps always count.
- "Start studying" pauses when the Distraint window loses focus.

### How the Android parts map over

| Android                             | Windows                                                     |
|-------------------------------------|-------------------------------------------------------------|
| Package name                        | Exe name (e.g. `chrome.exe`)                                |
| Accessibility service               | `Core/Blocker.cs`, polls the foreground window every 500 ms |
| Usage-stats log                     | Blocker adds foreground seconds per app into `StudyState`   |
| Lock overlay / popups               | `OverlayWindow` (one per monitor) / `BannerWindow`          |
| "Go home"                           | Minimize / close window / terminate (Settings)              |
| Device admin (uninstall protection) | Typed-code exit guard, no exit during strict lock           |
| SharedPreferences                   | JSON file                                                   |
| App picker (launcher apps)          | Start Menu + running windows + Browse for `.exe`            |

## Project structure

```
DistraintDesktop/
├── Core/                  # Blocker and core logic
├── Themes/                # UI themes
├── App.xaml(.cs)          # Application entry point
├── MainWindow.xaml(.cs)   # Main UI
├── SetupWindow.xaml(.cs)  # First-run setup
├── SettingsWindow.xaml(.cs)
├── PickerWindow.xaml(.cs) # App picker
├── TimeDialog.xaml(.cs)   # Study time entry
├── PairDialog.xaml(.cs)   # Phone pairing
├── CodeDialog.xaml(.cs)   # Typed-code exit guard
├── StrictWindow.xaml(.cs) # Strict mode UI
├── OverlayWindow.cs       # Per-monitor lock cover
├── BannerWindow.cs        # Popup banners
├── SyncService.cs         # Phone <-> PC sync
├── BackgroundSweeper.cs
├── Watchdog.cs
├── Styles.xaml
├── installer.iss          # Inno Setup installer script
└── Distraint.sln
```

## Known limitations

- Task Manager can still kill the Distraint process.
- Exclusive-fullscreen games can hide the lock cover. Run them in borderless windowed mode, or set the lock action to **Terminate**.
- Microsoft Store (UWP) apps can't be reliably identified by exe name.

## Contributing

ug reports and ideas are welcome. Open an [issue](https://github.com/ViCiAsh/DistraintDesktop/issues), or fork the repo and send a pull request.

## License

This project is licensed under the terms of the [LICENSE](LICENSE) file.