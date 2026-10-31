<p align="center">
  <img src="Icon/Icon.png" width="112" alt="ConnectIPN icon">
</p>

<h1 align="center">ConnectIPN</h1>

<p align="center">
  <strong>Turn your Android phone into a drive letter on Windows, over plain Wi-Fi.</strong><br>
  No cloud, no USB cable, no internet.
</p>

<p align="center">
  <a href="https://github.com/saqib-dev-007/ConnectIPN/releases/latest"><img alt="Download" src="https://img.shields.io/badge/download-latest%20release-2ea043?style=flat-square"></a>
  <img alt="Version" src="https://img.shields.io/badge/version-2.0-3b82f6?style=flat-square">
  <img alt="Platforms" src="https://img.shields.io/badge/platforms-Windows%20%7C%20Android-8b5cf6?style=flat-square">
  <a href="LICENSE"><img alt="License" src="https://img.shields.io/badge/license-PolyForm%20Noncommercial-f59e0b?style=flat-square"></a>
</p>

---

ConnectIPN bridges a phone and a PC that are on the same Wi-Fi network. The Windows app
shows a QR code; the Android app scans it and becomes a file server; Windows mounts the
phone's shared folder as a real drive letter through [WinFsp](https://winfsp.dev/). Because
the drive is a genuine Win32 file system, Explorer, `copy`, `robocopy`, VS Code, 7-Zip and
anything else that speaks normal file APIs work without knowing the phone exists.

## Table of contents

- [What it does](#what-it-does)
- [Features](#features)
- [Screenshots](#screenshots)
- [Requirements](#requirements)
- [Install](#install)
- [Quick start](#quick-start)
- [How it works](#how-it-works)
- [Background mode](#background-mode)
- [Building from source](#building-from-source)
- [Testing](#testing)
- [Project layout](#project-layout)
- [Troubleshooting](#troubleshooting)
- [Security and limitations](#security-and-limitations)
- [Contributing](#contributing)
- [License](#license)
- [Credits](#credits)

## What it does

```
┌──────────────────┐       QR: address, port, token       ┌──────────────────────┐
│   Android app    │ ◄──────────────────────────────────  │    Windows app       │
│                  │                                      │                      │
│   LinkClient ────┼───── TCP 47653, framed RPC ────────► │  LinkServer          │
│   FileServer     │      pipelined, 1 MiB chunks         │  RemoteClient        │
│  (java.io.File)  │                                      │  RemoteFileSystem    │
└──────────────────┘                                      └──────────┬───────────┘
                                                                     │ WinFsp
                                                                Z:\ in Explorer
```

The phone dials the PC, not the other way round, so nothing has to be installed or
configured on the phone's side of the network. The Windows side is the TCP listener and
drives every file operation.

## Features

- **A real drive letter.** `Z:\` (or any free letter) backed by WinFsp, with the file
  operations Explorer actually uses — create, read, write, flush, rename, delete-on-close,
  directory enumeration, volume info and free-space queries.
- **QR pairing, once.** The PC renders a `connectipn://pair` code carrying its LAN address,
  port and pairing token. The token is stable across restarts, so a phone that paired once
  reconnects on its own — no scanning again every launch.
- **Pipelined transport.** One TCP connection carries many in-flight requests. Reads land
  straight in the WinFsp buffer and writes go straight from it — no intermediate copies in
  the hot path. Measured at **~214 MiB/s write and ~362 MiB/s read** over loopback.
- **Your phone is also a mouse.** Once paired, the phone's **Apps** section opens a
  trackpad: drag to move the pointer, tap to left-click, two fingers to right-click, and
  the middle strip scrolls. It drives the PC through `SendInput`, so it works in any
  application, not just Explorer.
- **Screen mirroring both ways.** Send the phone screen to the PC (and click, drag or
  long-press on it), or open the PC screen on the phone with touch, a transparent
  keyboard and an Xbox-style controller overlay — the same pad the Controller screen
  uses, with gyro steering and a mouse kill-switch for gaming.
- **Wi-Fi or USB.** Pair over the network, then flip one switch to move the link onto
  the cable. The desktop detects a USB-attached phone and offers **Connect via USB**;
  USB is faster, steadier and immune to Wi-Fi drops. Unplugging falls back to Wi-Fi
  on its own.
- **Phone audio on the PC.** While mirroring, the phone's speaker mix travels with the
  picture and plays on the PC (Android 10+, where the source app allows capture).
- **Key mapping.** WASD/joystick, buttons and look areas, laid out on the PC or on the
  phone and injected through Android's accessibility gestures.
- **Stays out of the way.** Closing the window hides the app to the notification area. The
  tray icon opens a small flyout with the pairing code, or the live drive state, and can
  mount without opening the full window.
- **Runs in the background at login.** The installer can register it to start with Windows
  in tray mode, so the drive is reachable without a window on screen.
- **Nothing leaves your LAN.** No account, no cloud, no telemetry, no internet access
  required at any point.

## Screenshots

| Windows app | Tray flyout |
|---|---|
| ![Windows app](docs/windows-main.png) | ![Tray flyout](docs/windows-tray.png) |

| Android app | Installer |
|---|---|
| ![Android app](docs/android.png) | ![Installer](docs/installer.png) |

## Requirements

| Side | Needs |
|---|---|
| Windows | Windows 10 or 11, x64. [WinFsp 2.x](https://winfsp.dev/rel/) — a kernel driver that needs a one-time administrator install. |
| Android | Android 8.0 (API 26) or newer, a camera for the QR code, and the same Wi-Fi network as the PC. |
| USB mode (optional) | USB debugging on the phone, and [Android platform-tools](https://developer.android.com/tools/releases/platform-tools) (`adb`) on the PC. |
| Build only | .NET 8 SDK, JDK 17, Android SDK 34 (build-tools 34.0.0). |

The Windows build is self-contained, so the target PC does not need .NET installed. WinFsp
is the only thing that has to be added, and the app detects when it is missing and offers a
download link.

## Install

Everything is on the [latest release](https://github.com/saqib-dev-007/ConnectIPN/releases/latest)
page:

| Platform | File | Size |
|---|---|---|
| **Android 8.0+** | [ConnectIPN-2.0.apk](https://github.com/saqib-dev-007/ConnectIPN/releases/latest/download/ConnectIPN-2.0.apk) | 3.2 MB |
| **Windows 10 / 11 (x64)** | [ConnectIPN-Setup-2.0.exe](https://github.com/saqib-dev-007/ConnectIPN/releases/latest/download/ConnectIPN-Setup-2.0.exe) | 126 MB |

Install [WinFsp 2.x](https://winfsp.dev/rel/) before the Windows app — it is the driver that
makes the drive letter possible, it needs administrator rights once, and it needs a reboot.

### Windows

Download `ConnectIPN-Setup-2.0.exe` and run it. The installer is a single file and needs no .NET
runtime.

- choose the install folder (default `%LOCALAPPDATA%\Programs\ConnectIPN`),
- optionally create a **desktop shortcut**,
- optionally **start ConnectIPN when Windows starts** (it starts hidden, in tray mode),
- registers itself under **Settings → Apps** with a working uninstaller.

The install is per-user, so it never prompts for administrator rights. Only the WinFsp
driver does.

Unattended install:

```bat
ConnectIPN-Setup-2.0.exe --silent D:\Apps\ConnectIPN [/no-desktop] [/no-startup]
```

Uninstall from **Settings → Apps → ConnectIPN**, or run `ConnectIPN.exe --uninstall`.

If you would rather not install anything, publish the app to a folder and run
`ConnectIPN.exe` from there — see [Building from source](#building-from-source).

### Android

Download `ConnectIPN-2.0.apk` on the phone and open it, allowing installs from this source
when prompted. Or push it over USB:

```bash
adb install -r ConnectIPN-2.0.apk
```

Open the app and grant **All files access** when asked. Without it the drive still works,
but only the app's own folder is shared.

## Quick start

1. Install WinFsp on the PC if it is not already there — the app's **Get WinFsp** button
   opens the download page.
2. Start ConnectIPN. It listens on TCP 47653 and shows a QR code.
3. Install and open the Android app, then allow access to all files.
4. Tap **Scan QR code** and scan the code on the PC.
5. Back on the PC, pick a drive letter and press **Mount drive** — or use **Mount drive**
   in the tray flyout.
6. `Z:\` now shows the phone's shared storage. Read, write, rename and delete normally.

## How it works

### Pairing

The QR payload is `connectipn://pair?h=<ip>&p=<port>&t=<token>&v=1`. The token is a random
8-hex-character secret generated at launch. Android connects and sends `HELLO`; Windows
compares the token in constant time and closes the socket on a mismatch. The listener binds
to all interfaces but accepts nothing until `HELLO` succeeds.

### Transport

One TCP connection carries every request. Frames are length-prefixed and carry a request
id, so many operations can be in flight at once.

- `TCP_NODELAY` on both ends, 1 MiB socket buffers.
- Reads go straight from the socket into the WinFsp buffer; writes go straight from the
  WinFsp buffer into the socket.
- The phone serves requests from a 12-thread pool, so a 1 MiB read never blocks a
  `GetFileInfo` from Explorer.
- Metadata is cached for 1 s (`FileInfoTimeout`), and the drive's own attribute cache keeps
  Explorer's repeated queries off the wire.

### USB transport

The phone dials the PC, so USB cannot simply replace the address. Instead the PC opens
an adb reverse tunnel with `adb reverse tcp:47653 tcp:<its port>`: anything the phone
connects to on its own `127.0.0.1:47653` is carried down the cable into the same
listener. The PC then sends `TRANSPORT` (opcode `0x56`) telling the phone to redial, and
recognises the new connection by its loopback remote address.

The phone remembers the choice. If a USB connect fails — cable pulled, adb server
restarted — it clears the preference and falls back to the saved Wi-Fi address on the
next retry, so you never end up stranded.

The full wire format is documented in [PROTOCOL.md](PROTOCOL.md).

### The virtual drive

`RemoteFileSystem` implements the WinFsp callbacks Explorer actually uses:
`GetVolumeInfo`, `GetSecurityByName`, `Create`, `Open`, `Read`, `Write`, `Flush`,
`GetFileInfo`, `SetBasicInfo`, `SetFileSize`, `Overwrite`, `Rename`, `Cleanup`
(delete-on-close), `ReadDirectoryEntry` and `GetDirInfoByName`. Errno values from Android
map onto NTSTATUS values, so Explorer shows the usual "access denied" and "file in use"
messages instead of generic I/O errors.

### Storage access

The app serves the tree under its root folder:

- with **All files access** (`MANAGE_EXTERNAL_STORAGE`): `/storage/emulated/0`,
- without it: the app-specific folder (`Android/data/com.connectipn/files`).

Paths are resolved lexically and any `..` segment is rejected, so a client cannot escape
the shared root.

## Screen mirroring and control

Open **Apps → Screen** on the phone. There are two directions:

- **Phone → PC.** Tap **Start mirroring to PC**, accept Android's screen-capture prompt,
  then open **Phone screen** on the PC. Click to tap, drag to swipe, right-click to
  long-press, and use the toolbar for Back/Home/Recents. The PC can change resolution,
  frame rate, JPEG quality, orientation and aspect ratio while the stream runs.
- **PC → phone.** Tap **View PC screen** and the PC sends its primary display. Drag to
  move the pointer, tap to click, two fingers to right-click, and use **Keys** for a
  transparent keyboard or **Controller** for the same pad as the Controller screen:
  triggers, bumpers, d-pad, ABXY, both sticks, gyro steering, Back/Start.

The toolbar across the top of the PC view has a **quality** button, an **aspect** button
(Fit / Fill / Stretch), **Keys** and **Controller**. Tap the **▲** handle in the middle to
slide the bar away — the fps read-out and the bottom hint go with it — and a dim **▼**
brings it back. With the controller open, its **Mouse** button above Start kills the
phone-to-PC pointer, so a stray thumb cannot click anything on the desktop mid-game.

For the PC to control the phone, enable **ConnectIPN** under
**Settings → Accessibility**. The keymap overlay also needs **Display over other apps**.
Both are one-time switches; the screen-capture prompt appears once per mirroring session.

**Key mapping** (Apps → Screen → allow PC control, then **Key mapping** on the PC) places a
WASD joystick, buttons and a look region over the game. Drag to place, right-click to
remove, then **Send to phone**. The phone can edit the same layout itself.

What it deliberately does not do: H.264 (frames are JPEG), a true *extended* desktop
(that needs a virtual display driver), and a fully black phone cover — Android mirrors
the composited screen, so the **Dim** cover is visible in the stream too. Audio capture
follows Android's rules: apps that opt out of playback capture stay silent, and anything
below Android 10 cannot be captured at all.

## Background mode

Closing the main window does not quit the app. The bridge keeps running and the icon moves
to the notification area (the **hidden icons** chevron on the taskbar).

- **Left-click the tray icon** — a flyout appears in the bottom-right corner. It shows the
  pairing QR while waiting, or the connected phone and drive state once paired, with a
  **Mount drive** button that mounts the remembered letter. It dismisses when you click
  elsewhere.
- **Right-click the tray icon** — **Open ConnectIPN** (full window) and **Close program**
  (stops the listener, unmounts the drive and exits).
- Only one copy runs at a time; launching it again brings up the existing window.

The drive letter is remembered in `%LOCALAPPDATA%\ConnectIPN\settings.json`.

## Building from source

Every artifact goes to `release\` — see [BUILD.md](BUILD.md) for the full rules.

```powershell
# Everything: Windows app, Android APK, installer
tools\package.ps1 -Setup

# Just the two apps
tools\build.ps1
```

Individual steps:

```powershell
# Windows app -> windows\ConnectIPN.Desktop\bin\Release\net8.0-windows\ConnectIPN.exe
dotnet build windows\ConnectIPN.Desktop\ConnectIPN.Desktop.csproj -c Release

# Self-contained folder
dotnet publish windows\ConnectIPN.Desktop\ConnectIPN.Desktop.csproj -c Release -r win-x64 --self-contained true -o dist\windows

# Android APK -> android\app\build\outputs\apk\debug\ConnectIPN-2.0.apk
cd android
.\gradlew.bat assembleDebug
```

`android/local.properties` must point at your SDK, for example
`sdk.dir=C:\Users\you\AppData\Local\Android\Sdk`.

The WinFsp .NET and native assemblies are vendored in `windows\vendor`, so the desktop
project builds on a machine without WinFsp installed — the driver is still needed to mount.

`tools\installer.ps1` publishes the app, zips it, and embeds that zip into
`release\ConnectIPN-Setup.exe`. `tools\package.ps1` also produces the signed release APK
(signed with `android/keystore/connectipn.jks`, configured through `android/keystore.properties`;
without those files it falls back to the debug key so the build stays installable).

Both app icons are generated from `Icon/Icon.png` by `tools/IconGen`: a multi-resolution
`Assets/app.ico` for Windows, a PNG logo for in-app headers, and mipmap/adaptive icons for
Android. The adaptive foreground is scaled into the 68 dp safe zone so no launcher mask
crops the artwork.

## Testing

```bash
# Cross-language protocol test: Java device side against the C# PC side
javac -d .tmp/javabuild android/app/src/main/java/com/connectipn/{Protocol,FileServer,LinkClient}.java tools/jvm/TestClientMain.java
java -cp .tmp/javabuild TestClientMain 127.0.0.1 9021 TESTTOKEN .tmp/testroot &
dotnet run --project tools/Harness -c Release
```

It checks directory creation, a 32 MiB write/read with SHA-256 verification, rename, mtime,
listing, delete and `statfs`, and prints throughput.

`tools/MountTest` goes further: it mounts a real `Z:` against the Java device side and
exercises the drive through the Win32 API (create, 8 MiB write/read, size, delete). Run it
with the Java client running, same as above. Renaming or deleting a file that the server
still holds open only works when the server is Android/Linux. On a Windows test host the
rename step reports `SKIP`, and the delete-on-close steps print `deleted ...: False`: the
phone-side delete is refused with `EACCES`, but WinFsp's `Cleanup` callback cannot return an
error, so Win32 `DeleteFile` has already reported success.

`tools/WinFspProbe` is a small diagnostic that confirms the WinFsp driver can be reached;
it prints `0xC000000E` (`STATUS_NO_SUCH_DEVICE`) when the driver is missing.

## Project layout

```
windows/ConnectIPN.Desktop/     WPF app: QR, listener, RPC client, WinFsp file system
  Protocol/Protocol.cs          framing, scalars, attributes
  Net/RemoteClient.cs           pipelined RPC, zero-copy read/write paths
  Net/LinkServer.cs             listener + port selection
  Fs/RemoteFileSystem.cs        WinFsp callbacks
  Fs/DriveMount.cs              mount/unmount lifecycle
  App/LinkSession.cs            listener, pairing and mount state shared by both windows
  App/TrayIcon.cs               notification-area icon and menu
  App/Uninstaller.cs            --uninstall cleanup
  MiniWindow.xaml               bottom-right flyout: QR code or live drive state
  ScreenWindow.xaml             phone screen on the PC + touch control
  KeymapWindow.xaml             keymap editor, pushes JSON to the phone
  Net/ScreenStreamer.cs         captures the desktop for the phone
  Net/AdbBridge.cs              adb reverse tunnel behind USB mode
android/app/src/main/java/com/connectipn/
  Protocol.java                 wire format (mirrors the C# side)
  LinkClient.java               socket, handshake, worker pool
  FileServer.java               file operations on java.io.File
  MainActivity.java             scanner, permissions, UI
  ScreenActivity.java           mirror phone / open PC screen, stream options
  ScreenCaptureService.java     MediaProjection + JPEG encoder
  DesktopViewActivity.java      PC screen, touch, keyboard and controller overlays
  RemoteControlService.java     accessibility gestures (PC controls the phone)
  KeymapOverlayView.java        keymap drawing + touch injection
  KeymapService.java            keeps the keymap on top of other apps
  LinkService.java              foreground service that keeps the link alive
installer/ConnectIPN.Setup/     Windows setup program (payload embedded at build time)
tools/Harness                   cross-language protocol test
tools/MountTest                 end-to-end test through a real mounted drive
tools/WinFspProbe               driver reachability check
tools/IconGen                   icon generation
docs/                           screenshots used by this README
PROTOCOL.md                     wire protocol specification
CONTRIBUTING.md                 how to build, test and submit a change
LICENSE                         PolyForm Noncommercial 1.0.0
```

## Troubleshooting

| Symptom | Fix |
|---|---|
| Mount fails with `0xC000000E` | WinFsp driver is not installed. Run the WinFsp installer as administrator and reboot. |
| "WinFsp is not installed" banner | Same as above — install WinFsp, then restart the app. |
| Phone cannot connect | Same network? Guest Wi-Fi and AP isolation block device-to-device traffic. Windows Firewall must allow TCP 47653 for `ConnectIPN.exe` on private networks. |
| QR shows the wrong adapter | Pick another entry in the **Adapter** list; the code regenerates. |
| Drive disappears | The phone dropped the socket (screen off plus aggressive battery saver). Keep the app open or exempt it from battery optimisation. |
| Explorer shows stale sizes | Metadata is cached for 1 s; press F5. |
| Very slow transfers | 2.4 GHz Wi-Fi or a weak signal. The protocol itself saturates a 5 GHz link. |
| Tray icon missing | It may be in the hidden-icons chevron; drag it onto the taskbar to pin it. |
| Play Protect blocks the APK | The APK is self-signed and sideloaded, so Play Protect warns on the first install. Tap **More details → Install anyway**, or turn off *Scan apps with Play Protect* in the Play Store settings while you test. |
| The phone does not reconnect by itself | The pairing is remembered after one successful scan. If it still asks, open the phone app and leave it on the main screen for a moment — it retries every few seconds. Long-press **Disconnect** to forget the PC on purpose. |
| Taps from the PC do nothing | The phone has not granted control yet. Open **Screen → Allow PC control** and enable ConnectIPN under Installed services; the PC shows a banner until it does. |
| Mirroring shows one frame and stops | Install the current build on both sides. The old capture path allocated two full-screen bitmaps per frame and ran out of memory after the first frame. |
| "No picture from the PC yet" on the phone | The PC must be running and connected. Tap **Retry**; the desktop logs *Sending this PC's screen at …* in its ACTIVITY list when the stream starts. |
| PC cannot tap or swipe the phone | Enable the ConnectIPN accessibility service: **Screen → Allow PC control**, then turn it on in Settings. The Screen page shows whether it is on. |
| Controller overlay does nothing | ViGEmBus is not installed. Open the **DRIVERS** card in the desktop app, download ViGEmBus, install it, then press **Check again**. |
| Screen stream is laggy | Lower the resolution and fps on the sender, or pick **Low latency** on the phone. 5 GHz Wi-Fi matters more than any setting. |
| **Connect via USB** never appears | The PC cannot find `adb`. Install Android platform-tools, or check that `adb devices` lists the phone as `device` — `unauthorized` means you have not accepted the "Allow USB debugging?" prompt on the phone. |
| USB button is there but the link stays on Wi-Fi | The tunnel could not be opened. The panel says why; usually USB debugging was revoked. Replug the cable and accept the prompt. |
| Phone keeps dropping back to Wi-Fi after unplugging | Expected. The phone clears its USB preference and redials the saved Wi-Fi address on the next retry. |
| Taps on the PC view do nothing while gaming | The controller's **Mouse** button is set to **Mouse off**. Tap it back to **Mouse on**, or close the controller. |

## Security and limitations

What the design gives you:

- Pairing requires the token from the QR code, and the token changes every launch.
- The listener binds to all interfaces but only accepts connections that pass `HELLO`.
- The phone exposes exactly one shared root; `..` traversal is rejected.
- Nothing is sent anywhere except between the two devices on your LAN.

What it deliberately does not do:

- **No encryption.** Traffic is plain TCP on your local network. Do not use it on untrusted
  or public Wi-Fi, and do not expose port 47653 to the internet.
- **One phone at a time**, one shared folder, one mount.
- **Windows is the only host.** There is no macOS or Linux client.
- **No partial or sparse file support**, and no file locking across the link.
- **WinFsp is a kernel driver.** Installing it needs administrator rights; the app itself
  does not.

## Contributing

Bug reports, fixes and improvements are welcome — see [CONTRIBUTING.md](CONTRIBUTING.md)
for how to build, test and submit a change. By opening a pull request you agree to license
your contribution under the same terms as the project.

## License

**PolyForm Noncommercial License 1.0.0** — see [LICENSE](LICENSE).

Required Notice: Copyright 2026 Saqib Mengal

In plain terms:

- ✅ **Use it** for any noncommercial purpose — personal projects, study, hobby use,
  research, charities and other noncommercial organisations.
- ✅ **Modify it**, fork it, and share your changes, as long as you keep the licence and the
  notice above with every copy.
- ❌ **Do not sell it**, bundle it into a paid product, or use it as part of a commercial
  offering.

This is a *source-available* licence, not an open-source one: the noncommercial restriction
means it cannot meet the OSI definition. If you want to use ConnectIPN commercially,
contact the author for a separate licence.

## Credits

Built by **Saqib Mengal**.

[Instagram](https://www.instagram.com/saqib_mengal_007) ·
[GitHub](https://github.com/saqib-dev-007) ·
[LinkedIn](https://www.linkedin.com/in/saqib-mengal-76855b389/) ·
[Patreon](https://www.patreon.com/cw/SaqibMengal) ·
[saqibmengal7@gmail.com](mailto:saqibmengal7@gmail.com)

Thanks to the [WinFsp](https://winfsp.dev/) project for the file-system framework and to
the [ZXing](https://github.com/journeyapps/zxing-android-embedded) project for the Android
QR scanner.
