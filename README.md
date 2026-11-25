# MuteButton

[简体中文](README.zh-CN.md)

A lightweight Windows 11 system tray utility that lets you **middle-click the taskbar volume icon** to toggle mute on your default audio playback device.

## Features

- **Scoped strictly to the volume icon**: Intercepts middle-click (`WM_MBUTTONDOWN` / `WM_MBUTTONUP`) only on the taskbar volume icon so Windows does not open Quick Settings. Mouse wheel scrolling on the volume icon and middle-clicks elsewhere work as usual.
- **Zero static language keywords or hard-coded coordinates**:
  - Uses `PerMonitorV2` DPI awareness, Win32 taskbar checks (`Shell_TrayWnd` / `Shell_SecondaryTrayWnd`), and native COM UI Automation (`CUIAutomation8`) to inspect the Windows 11 XAML taskbar element under the cursor.
  - Identifies the volume button dynamically via CoreAudio `PKEY_Device_FriendlyName` on the default render endpoint (works across system languages), with structural fallbacks to `SystemTray.OmniButtonRight` and `SystemTray.AccentButton` (when `OmniButtonRight` is absent).
- **Minimal tray footprint**: Runs quietly in the notification area with a single `Exit` menu item.

## Usage

1. Download `MuteButton.exe` from the [Releases](../../releases) page (or build from source).
2. Run `MuteButton.exe`.
3. Middle-click the Windows 11 taskbar volume icon to mute/unmute.
4. Right-click the MuteButton tray icon and choose `Exit` to quit.

## Build from Source

Requires [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) on Windows.

```powershell
# Run directly
dotnet run

# Publish standalone single-file executable
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true
```

## License

[MIT](LICENSE)
