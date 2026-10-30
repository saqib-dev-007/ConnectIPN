# Contributing to ConnectIPN

Thanks for taking the time to look at the project. Bug reports, fixes, documentation
improvements and new ideas are all welcome.

Before you start, one thing to be clear about: ConnectIPN is released under the
[PolyForm Noncommercial License 1.0.0](LICENSE). You are free to use, study, modify and share
it for any **noncommercial** purpose. You are **not** allowed to sell it or fold it into a paid
product. Contributions are accepted on the same terms — see [Licensing](#licensing) below.

## Contents

- [Ways to contribute](#ways-to-contribute)
- [Reporting a bug](#reporting-a-bug)
- [Development setup](#development-setup)
- [Building](#building)
- [Testing](#testing)
- [Code style](#code-style)
- [Changing the wire protocol](#changing-the-wire-protocol)
- [Submitting a pull request](#submitting-a-pull-request)
- [Licensing](#licensing)

## Ways to contribute

| I want to… | Do this |
|---|---|
| Report something broken | Open an issue with the [bug template](#reporting-a-bug) |
| Ask a question | Open an issue titled `question: …` |
| Fix a bug or add a feature | Open a pull request, one logical change per PR |
| Improve the docs | PR against `README.md`, `PROTOCOL.md` or this file — these are always welcome |
| Translate or proofread | Same as docs |

For anything larger than a small fix, open an issue first so we can agree on the approach
before you spend time on it.

## Reporting a bug

A useful report contains:

1. **What you did** — the exact steps, from launching the app.
2. **What you expected** and **what actually happened**.
3. **The log** — the Windows app writes a line-by-line trace in the main window's log pane
   and to the tray tooltip. Copy the lines around the failure, including any `0x…` NTSTATUS
   code.
4. **Your setup** — Windows version, WinFsp version, Android version and phone model, and how
   the two machines are connected (same router? 2.4 or 5 GHz? VPN?).
5. **Screenshots** if the problem is visual.

Please do not paste a whole disk dump. A dozen relevant log lines beat a thousand irrelevant
ones.

## Development setup

### Windows

- **Windows 10 1809 or newer**, x64.
- **.NET SDK 8.0** — https://dotnet.microsoft.com/download/dotnet/8.0
- **WinFsp 2.1 or newer** — https://winfsp.dev/rel/. Required to actually mount a drive.
  The .NET bindings are vendored in `windows/vendor`, so the project compiles without it,
  but `tools/WinFspProbe` will print `0xC000000E` (`STATUS_NO_SUCH_DEVICE`) and mounting
  will fail until the driver is installed.
- **PowerShell 5.1 or newer** for the build scripts.

### Android

- **JDK 17** and the **Android SDK** with platform 34 and build-tools 34.
- Create `android/local.properties` pointing at your SDK:

  ```properties
  sdk.dir=C:\Users\you\AppData\Local\Android\Sdk
  ```

  This file is git-ignored and must not be committed.

- A physical device is strongly recommended. The emulator has no real storage to share and
  its virtual camera cannot scan the QR code.

The app targets API 34 and supports Android 8.0 (API 26) and newer.

### Optional

- **ADB** on `PATH` for installing and logcat.
- No Inno Setup or WiX is required — the installer is an ordinary WPF project in
  `installer/ConnectIPN.Setup` that embeds the published app as a zip payload.

## Building

From the repository root:

```powershell
# Windows app + Android APK
tools\build.ps1

# Everything, including release\ConnectIPN-Setup.exe
tools\package.ps1 -Setup
```

Individual projects:

```powershell
# Desktop app
dotnet build windows\ConnectIPN.Desktop\ConnectIPN.Desktop.csproj -c Release

# Self-contained folder (no .NET runtime needed on the target machine)
dotnet publish windows\ConnectIPN.Desktop\ConnectIPN.Desktop.csproj -c Release ^
  -r win-x64 --self-contained true -o dist\windows

# Android
cd android
.\gradlew.bat assembleDebug
```

If the build fails with a file-lock error on `ConnectIPN.exe`, a copy of the app is still
running — including one minimised to the tray. Close it from the tray menu (**Close program**)
or run `taskkill /F /IM ConnectIPN.exe` before rebuilding.

Release APKs are signed with `android/keystore/connectipn.jks` via
`android/keystore.properties`. **Neither file is in the repository and neither should ever be
committed.** Without them the Gradle build falls back to the debug key so the APK stays
installable — which is what you want for local testing.

## Testing

There is no unit-test project. Testing is done with three small harnesses in `tools/`, and
they are the fastest way to check a change.

### 1. Cross-language protocol test

This runs the real Java device side against the real C# PC side over loopback and checks
directory creation, a 32 MiB write and read with SHA-256 verification, rename, mtime,
listing, delete and `statfs`, then prints throughput.

```bash
javac -d .tmp/javabuild \
  android/app/src/main/java/com/connectipn/{Protocol,FileServer,LinkClient}.java \
  tools/jvm/TestClientMain.java
java -cp .tmp/javabuild TestClientMain 127.0.0.1 9021 TESTTOKEN .tmp/testroot &
dotnet run --project tools/Harness -c Release
```

A passing run ends with `RESULT PASS`.

### 2. Mount test

Exercises a real mounted drive through the Win32 API: create, an 8 MiB write and read, size
check, delete. Start the Java client as above, then:

```bash
dotnet run --project tools/MountTest -c Release
```

Two results are environment-dependent and are not failures:

- The **rename** step prints `SKIP` when the test host is Windows, because Windows refuses to
  rename a file the server still holds open.
- The **delete-on-close** steps print `deleted …: False`. WinFsp's `Cleanup` callback cannot
  return an error, so Win32 `DeleteFile` reports success even when the phone-side unlink was
  refused with `EACCES`.

### 3. Driver probe

```bash
dotnet run --project tools/WinFspProbe -c Release
```

Confirms the WinFsp driver is reachable before you blame your own code for a mount failure.

### Manual checklist

Before opening a PR that touches the transfer path or the file system, please check:

- [ ] QR code appears, and the phone connects after scanning it.
- [ ] `Z:` mounts, and Explorer lists the shared folder.
- [ ] Copy a file larger than 100 MiB to the drive, then copy it back and compare hashes.
- [ ] Create, rename and delete a file from Explorer.
- [ ] Close the window — the app keeps running in the tray and the drive stays mounted.
- [ ] **Close program** from the tray menu exits cleanly and unmounts the drive.

## Code style

Match the file you are editing; there is no formatter config to argue with.

**C#**

- .NET 8, nullable reference types enabled, file-scoped namespaces.
- Four-space indent, braces on their own line, `_camelCase` for private fields.
- `async`/`await` for anything that can block; the RPC reader runs on its own thread by
  design, because responses must be matched by id as they arrive.
- Keep the hot paths allocation-free. `Span<byte>`, `ArrayPool<byte>` and the `ref struct`
  readers exist for a reason — do not replace them with `byte[]` copies.

**Java**

- Java 17, four-space indent, `mCamelCase` is **not** used — plain `camelCase` fields.
- Do file I/O on the worker pool, never on the main thread.
- Close what you open; `FileChannel` and `RandomAccessFile` handles are the usual leaks.

**XAML**

- Keep templates in `App.xaml`-level resources when they are reused.
- Dark theme only — new controls must not fall back to the light system colours.

**Commits**

Short imperative subject line (`Fix mount point normalisation`), blank line, then a body
explaining *why* if the change is not obvious.

## Changing the wire protocol

`PROTOCOL.md` is the contract between the two apps. If you touch framing, opcodes, error
values or the handshake:

1. Update `PROTOCOL.md` **and** both implementations
   (`windows/ConnectIPN.Desktop/Protocol/Protocol.cs` and
   `android/app/src/main/java/com/connectipn/Protocol.java`) in the same PR.
2. Keep the changes backward-compatible where you can. The handshake already carries a
   version number — bump it and reject mismatches explicitly rather than silently
   misinterpreting a frame.
3. Do not raise `MaxFrame` (8 MiB) without a reason. Large payloads are chunked at 1 MiB for
   a reason: one oversized frame kills the link.
4. Run the cross-language test. A protocol change that passes on one side only is not done.

## Submitting a pull request

1. Fork the repository and branch from `main`: `git checkout -b fix/stale-file-size`.
2. Keep the PR focused. Separate refactors from behaviour changes — a reviewable 150-line PR
   gets merged; a 2000-line one does not.
3. Describe **what** changed and **why**, and say how you tested it. Paste the relevant
   harness output.
4. Do not commit build output (`bin/`, `obj/`, `release/`, `.tmp/`), `local.properties`,
   keystores, or IDE folders. `.gitignore` already covers these — check `git status` before
   you commit.
5. Do not reformat untouched code. It makes the diff unreadable and will be asked to be
   reverted.

A maintainer will review, ask questions if needed, and merge. If a PR goes quiet for a while,
a polite nudge on the thread is fine.

## Licensing

ConnectIPN is licensed under the **PolyForm Noncommercial License 1.0.0** — the full text is
in [LICENSE](LICENSE).

Required Notice: Copyright 2026 Saqib Mengal

### What this means for contributors

- **You keep the copyright** on what you write. You are not asked to sign a CLA or assign
  anything.
- **Inbound = outbound.** By opening a pull request you agree to license your contribution
  under the same PolyForm Noncommercial terms as the rest of the project. This is what makes
  it legal for the project to ship your code, and it is stated in the README as well.
- **Do not contribute code you do not have the right to license.** If you copied it from a
  GPL, MIT, Apache or proprietary project, say so in the PR — some licences are compatible
  with a noncommercial release and some are not, and the answer depends on the details.
- **No commercial use.** Your contribution may not be sold or bundled into a paid product by
  anyone, including you. If you want to use ConnectIPN commercially, contact the author about
  a separate licence.

### Why not an OSI open-source licence?

Because the intent is explicitly "anyone may use this, nobody may sell it". Every OSI-approved
licence permits commercial use, so none of them express that. PolyForm Noncommercial is
*source-available*: the code is public, forkable and modifiable, but the commercial restriction
stays. That is a deliberate choice, not an oversight — please do not open PRs asking to
relicense it.

## Questions

Open an issue, or reach the author:

- Instagram — [@saqib_mengal_007](https://www.instagram.com/saqib_mengal_007)
- GitHub — [@saqib-dev-007](https://github.com/saqib-dev-007)
- LinkedIn — [Saqib Mengal](https://www.linkedin.com/in/saqib-mengal-76855b389/)
- Email — [saqibmengal7@gmail.com](mailto:saqibmengal7@gmail.com)
