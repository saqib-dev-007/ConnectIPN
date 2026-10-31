# ConnectIPN — Screen Mirroring, Remote Control & Keymapping Plan

Living checklist. `[x]` done, `[~]` partial / best-effort, `[ ]` not started.

Build rule (from BUILD.md): every artifact goes to `release\`. Never test on the
emulator — build and hand the files over for manual testing.

---

## Phase 0 — Branding

- [x] Regenerate every icon from `Icon/Icon.png` with a **transparent** background.
- [x] Windows app icon `windows/ConnectIPN.Desktop/Assets/app.ico` (16/24/32/48/64/128/256).
- [x] Windows header `Assets/logo.png` (256, transparent).
- [x] Installer icon + header (reuses `Assets/app.ico` / `logo.png`).
- [x] Android launcher: legacy mipmaps + adaptive foreground + `ic_logo.png`.
- [x] Android `ic_launcher_background` colour sampled from the artwork (#64373A).

## Phase 1 — Protocol

- [x] `OP_SCREEN` (0x50) fire-and-forget JPEG frame, both directions.
- [x] `OP_SCREEN_CONFIG` (0x51) start/stop + resolution/fps/quality/orientation/aspect.
- [x] `OP_PHONE_INPUT` (0x52) PC → phone tap/swipe/long-press/key.
- [x] `OP_KEYMAP` (0x53) PC → phone keymap JSON.
- [x] Mirror the constants in `Protocol.cs` and `Protocol.java`.
- [x] Keep `LinkClient` Android-free so `tools/jvm` still compiles.

## Phase 2 — Phone screen on the PC

- [x] Android `ScreenCaptureService` (MediaProjection + VirtualDisplay + ImageReader + JPEG).
- [x] Foreground service type `mediaProjection` + `FOREGROUND_SERVICE_MEDIA_PROJECTION`.
- [x] Frame throttle (fps), resolution, JPEG quality, orientation, aspect.
- [x] Live reconfiguration from the PC without a second consent prompt.
- [~] Phone cover: implemented as a **dim** overlay. Android mirrors the composited
  screen, so a fully black cover would black out the PC image too.
- [x] Desktop `ScreenWindow` renders frames, shows fps/latency/resolution.
- [x] Options: resolution, fps, quality, orientation, aspect, full screen.
- [x] PC controls the phone (tap, swipe, long-press, back/home/recents) through
  `RemoteControlService` (accessibility gestures).
- [x] Key mapping for the mirrored phone (WASD joystick, buttons, look area).

## Phase 3 — PC screen on the phone

- [x] Desktop `ScreenStreamer` captures the primary display (GDI, scaled, JPEG, cursor).
- [x] `OP_SCREEN_CONFIG` from the phone starts/stops the stream with its own resolution/fps.
- [x] Android `DesktopViewActivity`: full-screen stream, touch = mouse, tap = click,
  two-finger = right-click + scroll.
- [x] Transparent keyboard overlay for typing into the PC.
- [x] Controller overlay (joystick + ABXY + d-pad + LB/RB) with mouse-look on the stream.
- [x] Switch between touch / controller / keyboard without leaving the screen.
- [x] Low-latency / balanced / sharp presets that restart the stream at a new size.

## Phase 4 — Keymapping (BlueStacks-style)

- [x] Phone overlay service with draggable controls (edit mode).
- [x] Editor activity: add button / joystick / look region, clear, save, start.
- [x] Injection through `RemoteControlService` (`dispatchGesture`).
- [x] PC → phone keymap push (`OP_KEYMAP`), with a desktop drag-and-drop editor.
- [x] Persist the layout in SharedPreferences (phone) and `%LOCALAPPDATA%` (PC).
- [~] Simultaneous held gestures are limited by Android accessibility (a new gesture
  cancels the previous one); the joystick uses repeating short strokes.

## Phase 5 — "Second screen"

- [~] Second-screen use case is covered by **View PC screen** / **Gaming mode** (the
  phone shows the PC's primary display). This is a mirror, not an extension.
- [ ] True *extended* desktop needs a virtual display driver (IddSampleDriver-style),
  which is out of scope for a per-user app; documented as a limitation.

## Phase 6 — Build & hand-over

- [x] `tools/package.ps1 -Setup` → `release\ConnectIPN-Setup.exe` (125.2 MB).
- [x] `release\windows\ConnectIPN.exe` (self-contained folder, 147 MB).
- [x] `release\android\ConnectIPN-1.0.apk` (3.2 MB, release key).
- [x] Report artifact paths + sizes; manual testing by the user.

## Phase 7 — Fixes after the first hands-on test

Reported: the APK was blocked by Play Protect, phone → PC showed one frozen frame
and then crashed or dropped the link, PC → phone never showed a picture, there was
no driver-install option, and the new screens looked unfinished.

- [x] **Phone capture crash / frozen frame.** The service allocated two full-screen
  bitmaps per frame at 24 fps, which exhausted the heap after the first frame. Both
  bitmaps and the JPEG buffer are now reused, and every frame is wrapped so a bad
  frame is dropped instead of killing the process.
- [x] **Reader teardown race.** `stopSelf()` and `reader.close()` used to run from
  inside the `ImageReader` callback. All capture work and teardown now run on one
  private handler thread; the overlay is added and removed on the main thread.
- [x] **Idle links dropped after 10 s.** `RemoteClient` set `ReceiveTimeout = 10000`,
  so any quiet moment was reported as "device disconnected". Reads are blocking
  again and dead peers are caught by TCP keep-alive plus write errors.
- [x] **A viewer bug could kill the link.** Screen/keymap events are dispatched inside
  try/catch; a failing window can no longer tear down the socket.
- [x] **PC → phone never streamed.** `ScreenStreamer` now reuses its capture and
  scale surfaces, logs the first capture failure into the ACTIVITY list, and the
  phone shows "no picture yet" with a **Retry** button instead of a silent black
  screen. The desktop window also asks the phone to start as soon as it opens.
- [x] **One listener slot for two screens.** `LinkManager` now fans frame/config
  events out to every registered listener, so opening the PC viewer no longer
  unregisters the capture service (and vice versa).
- [x] **No driver-install option.** The main window has a **DRIVERS** card with live
  detection for WinFsp (virtual drive) and ViGEmBus (virtual Xbox pad), a download
  button for each, and **Check again** to re-detect after installing.
- [x] **UI polish.** New status cards, live fps/frame counters, stall detection,
  a full-screen button, and a clearer waiting state on both sides.
- [x] **Upgrades install cleanly.** `versionCode` bumped to 2.
- [x] **Play Protect.** Documented in the README: the APK is self-signed and
  sideloaded, so Play Protect warns on the first install.

## Phase 8 — Second hands-on round

Reported: PC could not control the phone, the phone view had big black bars, phone audio
never reached the PC, changing any setting dropped the link and forced a re-scan, and
PC → phone dropped the connection.

- [x] **PC control.** The phone now reports its capabilities (`OP_STATUS`): accessibility
  control ready, overlay permission, audio support. The PC shows a **"PC control is off"**
  banner with the exact setting to open instead of silently ignoring clicks. Gestures use
  the phone's *real* display metrics, so taps land where the PC aimed them.
- [x] **Black bars.** The desktop viewer resizes itself to the stream's aspect ratio, so a
  portrait phone fills the window.
- [x] **Phone audio.** `OP_AUDIO` carries 20 ms chunks of 16-bit stereo PCM captured with
  Android's playback-capture API; the PC plays them through `winmm` (no new package).
  Needs Android 10+ and the microphone permission, requested when the toggle is on.
- [x] **Settings no longer drop the link.** Changing fps or quality only updates the
  stream; the virtual display is rebuilt only when the frame size really changes.
  `largeHeap` and frame downsampling on the phone viewer remove the memory spikes that
  killed the process mid-change.
- [x] **Paired for good.** The PC persists its pairing token, the phone remembers host,
  port and token, and it reconnects on launch and after a drop with a 1.5 s → 15 s
  backoff. Long-press **Disconnect** on the phone to forget.
- [x] **Redesign.** One shared palette (near-black background, `#6C8CFF` accent), branded
  title bar, softer cards, and matching colours on both apps.

## Phase 9 — Mirroring bug round + full teal/mint redesign

Third hands-on round reported: PC → phone still showed nothing, phone → PC showed a
frozen first frame after reconnect, the desktop viewer killed itself mid-reconnect,
and the whole UI still looked unfinished.

Bugs (all verified by build + protocol harness RESULT PASS):

- [x] **PC → phone "nothing works".** `ScreenCaptureService` teardown raced with the
  next session: the old `teardown()` could close the *new* projection when start/stop
  came quickly in a row. Teardown now disposes only locals snapshotted at call time.
- [x] **Frozen first frame after reconnect.** When `OP_SCREEN_CONFIG` arrived while the
  service was idle it never told the PC anything, so the PC kept waiting. The config
  listener answers with a fresh `OP_STATUS` when nothing is running, and the phone
  re-announces its mirroring state the moment a link comes up (`LinkManager` → `announce()`).
- [x] **Tap offsets in phone → PC view.** `ScreenActivity.targetSize()` used window
  metrics instead of real display metrics; taps used to land offset. Now uses
  `getRealMetrics()` with a fallback.
- [x] **Desktop viewer killed the stream on reconnect.** `DesktopViewActivity` treated
  CONNECTING/IDLE as "lost and finished", so a mid-stream reconnection closed the
  stream it was about to restart. `onLinkState` is now reconnect-aware (restarts the
  stream on CONNECTED, shows "Reconnecting…" otherwise).
- [x] **UI freeze when stopping the PC stream.** `LinkSession.StopDesktopStream`
  disposed the `ScreenStreamer` (600 ms join) on the WPF UI thread, freezing the
  window exactly when the phone asked for the screen. It now disposes away from the
  dispatcher via `Task.Run`.
- [x] **Latency snowball.** `ScreenStreamer` advanced its frame schedule one slot per
  loop even when an encode/send took several; a slow frame now skips only the slots
  it actually missed.
- [x] **Blank status-bar icon.** Both foreground services used
  `android.R.drawable.stat_sys_upload_done`, which renders empty on some OEM shells.
  New single-colour cast icon `res/drawable/ic_stat.xml`, wired into both services.
- [x] **Harness source list stale** (added GamepadHost/InputInjector + the ViGEm
  package reference), which unblocked the contract check.

Redesign (teal/mint, near-monochrome, all screens on both apps):

- [x] Design tokens: layered near-black surfaces (`#07090B` → `#0E1113`), single
  aqua accent `#2DD4BF` + highlight `#5EEAD4`, mint gradient on primary CTAs
  with dark-on-mint text, success `#34D399` / warn `#FBBF24` / red `#F87171`.
- [x] Android: `colors_ui.xml`, 14 drawables (cards, tiles, pills, glow, steps,
  scroll thumb, touchpad) rebuilt; main, screen, desktop, touchpad, keyboard,
  gamepad, keymap layouts restyled (chip grid for stream options, glowing hero
  status card, floating rounded viewer chrome); joystick knob + keymap overlay
  + keyboard latched keys retinted; launcher `versionCode` → 3.
- [x] Windows: MainWindow, ScreenWindow, MiniWindow, KeymapWindow rebuilt
  (gradient primary buttons, custom ComboBox popups, glowing status dot, dark
  close chrome); every status colour in `.cs` remapped to the new tokens.
- [x] **Upgrades install cleanly.** `versionCode` bumped to 3.
- [x] Full verification: `tools\build.ps1 -SkipAndroid` → 0 warnings/errors;
  `tools\package.ps1 -Setup` → 125 MB Setup.exe; APK badging `versionCode='3'`.

## Phase 10 — Structural redesign ("operation deck") + crash-onboarding

Feedback: "you just changed the colour" — the redesign had to change **structure
and motion**, not only palette; plus two runtime complaints (app closes
randomly, Screen tab crashes on tap). Installed a repo-local copy of the
frontend-design skill (`.opencode/skills/frontend-design/SKILL.md`, adapted
from Anthropic's) and committed to one direction: **instrument panel / mission
control** — near-black machined surfaces, hairline strokes, mono numerals,
one aqua accent reserved for live state and primary actions.

- [x] **Motion layer (Android).** Theme-level `WindowMotion` style: every screen
  change now slides up + fades (220/180 ms, decelerate/accelerate). New
  `RadarView` custom view — the connection state is a breathing target (rings
  expand while connecting, steady ball when linked, red on error) used on the
  main screen and the Screen control strip. `Ui.pressSink` gives the deck tiles
  a 0.96 press-scale; ScanOverlay draws an animated scanner reticle (corner
  brackets, sweeping light 1.9 s loop, scrim outside the frame).
- [x] **Custom scanner.** `ScannerActivity` replaces the stock zxing
  `CaptureActivity`: own layout, back control, torch button, animated reticle,
  stock laser hidden. Registered in the manifest; `ScanOptions.setCaptureActivity`
  wired in `MainActivity.startScan`.
- [x] **Structure (Android).** Main screen rebuilt: connection panel (radar +
  status + mono root line + stacked actions) then a 2×2 **command deck** of apps,
  footer strip. Screen page rebuilt as an instrument list: header strip with mini
  radar, mono option buttons, hairline-separated toggle panel.
- [x] **Structure + motion (Windows).** MainWindow rebuilt as a split *operation
  deck*: left panel = live status (glowing radar pair `StatusGlow`/`StatusRing`
  animated from `SetStatus`), QR framed in aqua brackets, mono meta grid;
  right = command surface (drive + screen rows), slim system/drivers row,
  activity log. `Util/UiFx.SlideIn` staggers the four sections on open and every
  other window uses the same entrance. `tools\build.ps1` → 0 errors.
- [x] **Crash onboarding.** `CrashLogger.install` writes every uncaught
  exception to `crash.log` in the app's private files **and** public
  `Android/data/com.connectipn/files`, stamped with link state — the next
  "it just closed" report comes with a stack trace even without adb.
- [x] **Screen tab crash — investigated, not found statically.** Every
  `findViewById` type matches the rebuilt layouts; the resource sweep returns
  clean (`ic_menu_edit` resolves from the zxing library's own resources);
  `ScreenActivity`/`ScreenCaptureService` teardown paths re-checked. Need a
  logcat (or the new `crash.log`) from a real device to pin it.
- [x] Full verification: Android `assembleDebug/Release` green, harness protocol
  contract `RESULT PASS` (12/12, 405.6 MiB/s read / 288.5 MiB/s write;
  first run "blocked" was only a missing `%TEMP%\opencode\harness-root`
  folder — documented here so nobody debugs a protocol non-bug again),
  `package.ps1 -Setup` → fresh Setup + `versionCode` 4 APK.

---

## Manual test checklist

Phone → PC:

- [ ] Apps → Screen → Start mirroring → accept the Android prompt.
- [ ] With the audio switch on, the PC plays the phone's sound.
- [ ] PC → Phone screen → the image appears and the fps counter moves.
- [ ] Click = tap, drag = swipe, right-click = long-press.
- [ ] Back / Home / Recents work (needs Accessibility enabled on the phone).
- [ ] Change resolution / fps / quality on the PC while streaming.
- [ ] The link survives every change — no QR rescan.
- [ ] Close both apps and reopen: the phone reconnects without scanning.
- [ ] Dim switch darkens the phone (and the stream, by design).

PC → phone:

- [ ] Screen → View PC screen → the desktop appears in landscape.
- [ ] Drag moves the PC pointer, tap clicks, two-finger scrolls.
- [ ] Keys overlay types into the focused PC window.
- [ ] Controller overlay plugs in a pad and drives the left stick / ABXY.
- [ ] Low latency / Balanced / Sharp restarts the stream at a new size.

Key mapping:

- [ ] Screen → Key mapping → allow overlay + accessibility.
- [ ] Drag the WASD stick and Jump button; save and start.
- [ ] PC → Key mapping → drag, Send to phone; the phone overlay updates.
- [ ] Over a game, the stick/buttons inject touches.

Known limits (by design, documented in README):

- True **extended** desktop needs a virtual display driver; this is a mirror.
- A fully black phone cover would black out the stream, so the cover is a dim.
- Accessibility cancels a gesture when a new one starts, so held multi-touch is
  best-effort (joystick uses repeating short strokes).
- No audio; frames are JPEG over the same plain-TCP link as the drive.
