# Islands For Windows

A native, fluid, and minimalist Dynamic Island companion for Windows 10 and Windows 11, built with C# .NET 8, WPF, and native Windows APIs (CoreAudio, WinRT, and Win32).

---

## Overview

Islands For Windows brings the fluid capsule interface concept to desktop environments. It replaces outdated Windows flyouts with an interactive status hub positioned at the top of your display, providing real-time system monitoring, hardware controls, privacy indicators, media controls, and seamless cross-device file transfer.

---

## Features

### System HUD Replacement
- **Zero-Latency Audio Monitoring**: Direct event hooks via Windows CoreAudio WASAPI (`IAudioEndpointVolumeCallback`). Catches multimedia keys, Fn shortcuts, and external app volume changes instantly.
- **Hardware Display Brightness**: Adjusts screen brightness via WMI (laptops/internal panels) and Win32 High-Level Monitor Configuration API / DXVA2 (external desktop monitors).
- **Interactive Mouse Wheel Controls**:
  - Scroll directly over the island to adjust system volume in 2% increments.
  - Hold `Alt` and scroll to adjust monitor brightness in 5% increments.
  - Click the volume icon to toggle mute.
- **Capsule Sliders**: Fully rounded track and thumb geometry with smooth value interpolation.

### Privacy Indicators & Global Mic Mute
- **Hardware Sensor Monitoring**: Queries Windows Capability Access Manager (`ConsentStore`) to track real-time access to the microphone and webcam across both Win32 desktop apps (Chrome, Discord, Zoom, OBS) and UWP/packaged apps.
- **Status Dots**:
  - Amber indicator: Microphone is actively capturing audio.
  - Green indicator: Webcam is actively streaming video.
  - Both indicators display concurrently when both devices are in use.
- **App Inspection**: Hover over any indicator to inspect which applications are accessing your hardware.
- **Global Mic Mute**: Click the microphone indicator or use the Control Center button to toggle system-wide microphone muting at the endpoint level.

### Lock Key & Input Method Notifications
- **Key State Detection**: Uses a low-level keyboard hook (`WH_KEYBOARD_LL`) to capture state changes for `Caps Lock`, `Num Lock`, and `Scroll Lock`.
- **Input Method Switching**:
  - Detects Vietnamese/English input toggles from popular IME engines (UniKey, EVKey) via `Ctrl + Shift` or `Alt + Z`.
  - Detects Windows native keyboard layout changes via `Win + Space` or `Alt + Shift`.
  - Displays transient status pill (`[VIE] Tiếng Việt` vs `[ENG] English`) with automatic 1.6-second dismissal.

### Media Player Integration
- **System Media Transport Controls (SMTC)**: Automatically captures active sessions from Spotify, YouTube (via Chrome, Edge, Brave), Apple Music, and other media players.
- **Expanded Player**: Displays high-resolution album artwork, track title, artist name, source badge, interactive timeline progress, and playback controls (`Previous`, `Play/Pause`, `Next`).
- **Live Waveform Equalizer**: Smoothly animated 3-bar visualizer with Spotify signature green accent.

### Cross-Device File Transfer
- **Direct Drag & Drop**: Drag files, photos, or documents directly onto the island from File Explorer.
- **Windows Share Target (Phone Link / Nearby Share)**:
  - Default recommended method.
  - Works anywhere (including coffee shops, schools, and offices) via Bluetooth and Microsoft Cloud Relay.
  - Completely immune to router AP Isolation and local network firewalls.
- **LocalSend API Integration**:
  - Automatic peer discovery via UDP multicast (`224.0.0.167:53317`) and local subnet scanning.
  - Automatic fallback: Routes to Windows Share if the local network restricts peer-to-peer communication.
- **Status Feedback**: Interactive transfer progress bar and success/error notifications.

### Control Center & Multitasking
- **Interactive Hub**: Clicking the island expands it into an organized control dashboard containing volume sliders, brightness sliders, battery indicators, global mic mute, and quick switches to active media playback.
- **Multi-Monitor Management**:
  - Automatically identifies primary and secondary monitors.
  - Instant screen-switch shortcut (`F9`) or multi-monitor selector in the context menu.
- **Auto-Hide Mode**:
  - Slides out of view to the top edge when idle (configurable timeout from 0.5s to 4.0s).
  - Invisible top-edge proximity trigger wakes the island upon cursor approach.
- **Window Management**:
  - Topmost overlay (`Always-On-Top`).
  - Transparent hit-testing (clicks outside the island pass directly through to background applications).
  - Excluded from `Alt + Tab` task switchers (`WS_EX_TOOLWINDOW`).

---

## Interaction Reference

| Action | Trigger | Result |
| :--- | :--- | :--- |
| **Adjust Volume** | Mouse wheel over Island | Increases / decreases master volume (±2%) |
| **Adjust Brightness** | Hold `Alt` + Mouse wheel | Increases / decreases monitor brightness (±5%) |
| **Toggle Mute** | Click Speaker icon | Toggles master audio mute state |
| **Toggle Mic Mute** | Click Mic privacy dot | Toggles global system microphone mute |
| **Expand Control Center** | Left-click on idle island | Opens Volume, Brightness, and System hub |
| **Expand Media Player** | Click 'Nhạc' button | Opens detailed album art and playback controls |
| **Collapse Island** | Click Close button or outside | Returns to compact pill |
| **Transfer File** | Drag & drop files onto Island | Initiates wireless transfer to mobile device |
| **Wake Auto-Hide** | Hover cursor at top edge | Reveals hidden island smoothly |
| **Switch Monitor** | Press `F9` | Moves the island to the next connected display |
| **Open Settings** | Press `Ctrl + ,` | Opens the configuration dialog |
| **Context Menu** | Right-click on Island | Displays quick actions, display picker, and exit |

---

## Architecture & Project Structure

```text
Dynamics_island/
├── App.xaml / App.xaml.cs             # Application lifecycle & global exception handling
├── MainWindow.xaml / .cs              # Main island capsule view, morphing engine, and HUDs
├── SettingsWindow.xaml / .cs          # Fluent dark mode configuration interface
├── DynamicIsland.csproj               # .NET 8 Windows SDK project configuration
└── Services/
    ├── BatteryService.cs              # PowerManager WinRT battery & AC status
    ├── BrightnessService.cs           # WMI & DXVA2 monitor brightness controller
    ├── LocalSendService.cs            # LocalSend HTTP API & UDP multicast client
    ├── LockKeyService.cs              # Low-level keyboard hook (Caps/Num/IME)
    ├── MediaService.cs                # GlobalSystemMediaTransportControlsSessionManager
    ├── PrivacyService.cs              # CapabilityAccessManager registry sensor monitor
    ├── SettingsService.cs             # JSON settings persistence & live sync
    ├── VolumeService.cs               # CoreAudio WASAPI endpoint & mic mute controller
    ├── Win32Helper.cs                 # Win32 P/Invoke utilities & display positioning
    └── WindowsShareService.cs         # Windows DataTransferManager share contracts
```

---

## Prerequisites

- **Operating System**: Windows 10 (version 2004 / Build 19041 or higher) or Windows 11.
- **Runtime**: [.NET 8.0 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) (included automatically with the SDK).
- **Fonts**: `Segoe Fluent Icons` (built-in on Windows 11; falls back to `Segoe MDL2 Assets` on Windows 10).

---

## Building from Source

### 1. Clone the repository
```bash
git clone https://github.com/AidanVo02/Islands-For-Windows.git
cd Islands-For-Windows
```

### 2. Restore dependencies and build
```bash
dotnet restore
dotnet build -c Release
```

### 3. Run the application
```bash
dotnet run -c Release
```

### 4. Publish standalone executable
```bash
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true
```

---

## Configuration

Settings are saved in `%APPDATA%\DynamicIsland\settings.json` and update in real-time without requiring application restarts:

```json
{
  "MonitorIndex": 0,
  "TopOffset": 3.0,
  "HorizontalOffset": 0.0,
  "AlwaysOnTop": true,
  "StartWithWindows": false,
  "AutoHide": false,
  "AutoHideDelay": 1.5,
  "IdleWidth": 190.0,
  "IdleHeight": 36.0,
  "ExpandedWidth": 400.0,
  "ExpandedHeight": 185.0,
  "CornerRadius": 18.0,
  "ThemeBackground": "#080809",
  "BorderColor": "#2A2A30",
  "EnableShadow": true,
  "ShadowOpacity": 0.6,
  "ShadowBlur": 20.0,
  "ShowClock": true,
  "Use24HourClock": true,
  "ShowBattery": true,
  "HoverAnimation": true,
  "EqualizerEnabled": true,
  "AutoSwitchMedia": true,
  "VolumeBrightnessHudEnabled": true,
  "MouseWheelVolumeEnabled": true,
  "EnablePrivacyIndicators": true,
  "EnableLockKeyNotifications": true,
  "FileTransferMethod": "WindowsShare",
  "LocalSendTargetIp": "",
  "LocalSendPort": 53317,
  "LocalSendTargetName": "",
  "LocalSendAutoDiscovery": true
}
```

---

## License

This project is licensed under the [MIT License](LICENSE).
