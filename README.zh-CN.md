# MuteButton

[English](README.md)

一个轻量的 Windows 11 系统托盘小工具：**鼠标中键点击任务栏音量图标**即可切换默认播放设备的静音状态。

## 功能特点

- **仅作用于音量图标**：仅在点击任务栏音量图标时拦截中键（按下与抬起），防止系统弹出“快速设置”面板；音量图标上的滚轮调音量以及屏幕其他位置的中键功能均保持原样。
- **无硬编码坐标、无静态多语言词表**：
  - 启用 `PerMonitorV2` DPI 感知，结合 Win32 任务栏窗口校验（`Shell_TrayWnd` / `Shell_SecondaryTrayWnd`）与原生 COM UI Automation（`CUIAutomation8`）穿透 Win11 XAML 任务栏。
  - 通过 CoreAudio 动态读取当前默认输出设备的 `PKEY_Device_FriendlyName` 进行跨语言识别，并在无音频设备或不同任务栏布局下依次以 `SystemTray.OmniButtonRight` 和 `SystemTray.AccentButton`（当 `OmniButtonRight` 不存在时）兜底。
- **极简驻留**：托盘右键菜单仅保留 `Exit` 退出选项。

## 使用方法

1. 从 [Releases](../../releases) 页面下载编译好的 `MuteButton.exe`（或自行从源码编译）。
2. 双击运行 `MuteButton.exe`。
3. 在任务栏右下角的系统音量图标上点击鼠标中键，即可切换静音/取消静音。
4. 如需退出，右键点击托盘区的 MuteButton 图标并选择 `Exit`。

## 从源码构建

需要 Windows 环境与 [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)。

```powershell
# 直接运行
dotnet run

# 发布独立单文件可执行程序
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true
```

## 开源协议

[MIT](LICENSE)
