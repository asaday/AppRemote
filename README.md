# App Remote

App Remote lets you view and control one selected Windows window from a phone on the same local network. Select the window on your PC, then open the QR code in Safari on iPhone or Chrome on Android. No phone app is required.

Video uses H.264 over WebRTC when available, with JPEG over WebSocket as a fallback. The Windows app shows which video transport is active.

![App Remote Windows app showing window selection and phone connection panel](docs/images/app-remote-windows.png)

*Windows app preview. Window names and connection details are illustrative.*

## Download

Download `AppRemote-v1.0.0-win-x64.zip` from the [latest release](https://github.com/asaday/AppRemote/releases/latest). Extract the whole `AppRemote` folder and run `AppRemote.exe`. The release includes the .NET runtime, so the .NET SDK is not needed to run it.

## Features

- Select any eligible Windows window to stream
- Tap, drag, scroll, and type from a phone
- Connect with a QR code or the **Copy URL** button
- Add a shortcut with an icon to the iPhone Home Screen
- One phone connection at a time

## Requirements

- Windows 10 version 2004 or later, or Windows 11 (x64)
- A phone on the same local network (Safari on iPhone or Chrome on Android)
- [FFmpeg (Shared) 8.1](https://github.com/GyanD/codexffmpeg/releases/tag/8.1) for H.264 video

Without FFmpeg, the app can still stream JPEG frames. On Windows, you can install the shared FFmpeg build with:

```powershell
winget install --id Gyan.FFmpeg.Shared --version 8.1 --exact
```

## Build and run

Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0), then run this from the repository root:

```powershell
.\build.ps1
```

This publishes the app to `dist/AppRemote/`. The equivalent command is:

```powershell
dotnet publish src/AppRemote/AppRemote.csproj -c Release -r win-x64 --self-contained false -o dist/AppRemote
```

This local build requires the [.NET 10 Desktop Runtime and ASP.NET Core Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) on the PC that runs it. The .NET 10 SDK includes both.

Close App Remote before rebuilding it. To move the app to another PC, copy the entire `dist/AppRemote/` directory, not only `AppRemote.exe`.

1. Start `dist/AppRemote/AppRemote.exe`.
2. If Windows Firewall prompts you, allow access on **private networks**.
3. Select a window and click **Control this window**.
4. Open the QR code on a phone connected to the same network. You can also use **Copy URL** and open the address manually.

The server listens on TCP port `8765`; WebRTC video also uses UDP. On iPhone, use Safari's **Add to Home Screen** action to launch from an icon. If an existing shortcut keeps an old icon, remove it and add it again.

## Phone controls

| Gesture | Windows input |
| --- | --- |
| Tap | Left click |
| Drag | Hold and move the left mouse button |
| Long press | Right click |
| Move two fingers vertically | Scroll |
| Tap with two fingers | Open the keyboard |

The video keeps the window's aspect ratio. Black margins are outside the control area.

## Connection and limitations

- The eight-character key in the connection URL stays the same across restarts. URLs using the older, longer key remain valid. If the PC's LAN IP address changes, scan the QR code again to get a new URL.
- The secret used to derive the key is stored at `%LOCALAPPDATA%\AppRemote\pairing.key` for the current Windows user.
- Capture continues when the selected window is behind another window. Phone input brings the selected window to the foreground first. If Windows denies that focus change, the input cannot be delivered.
- Elevated windows, UAC prompts, and minimized windows are outside the supported control path. Some apps may have a small offset between the captured image and pointer coordinates.
- The connection page and control channel use HTTP and WebSocket on the LAN. Anyone with the connection URL on that network can control the selected window, so use App Remote on a trusted private network. Chrome on Android may not offer full PWA installation for an HTTP page.

## Development check

Run the integration check in an active Windows desktop session, with a window open in the foreground and TCP port `8765` free:

```powershell
dotnet run --project tests/CaptureSmoke/CaptureSmoke.csproj
```

It checks web and icon delivery, key authentication, JPEG/WebSocket frames, and H.264/WebRTC frames. Check phone rendering and interaction on a device separately.
