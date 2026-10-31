# Building ConnectIPN

**Every build goes to `release\` in the repository root. Nothing else.**

If you are an AI agent or a contributor working on this repo: run the command in
[The one command](#the-one-command), then report the paths under `release\`. Do not hand
back a path inside `bin\`, `obj\` or `android\app\build\` — those are intermediates, they
are not the build, and they get wiped by the next clean.

## The one command

```powershell
tools\package.ps1 -Setup
```

That produces the complete, shippable set:

| Artifact | What it is |
|---|---|
| `release\ConnectIPN-Setup.exe` | Per-user installer, payload embedded, ~125 MB |
| `release\windows\ConnectIPN.exe` | Self-contained app; runs on a PC with no .NET installed |
| `release\android\ConnectIPN-1.0.apk` | Release APK (signed with your keystore, or the debug key if `android/keystore.properties` is absent) |

`tools\package.ps1` without `-Setup` stops after the APK and the Windows folder.

## Faster loops

```powershell
tools\build.ps1            # quick build: release\windows-dev + release\android\*-debug.apk
tools\build.ps1 -Publish   # self-contained publish into release\windows
```

`release\windows-dev\` is deliberately separate from `release\windows\`. A framework-dependent
build dropped on top of a self-contained publish produces a folder that no longer runs on a
machine without .NET, so the two never share a directory.

## Layout

```
release\
  ConnectIPN-Setup.exe          installer (package.ps1 -Setup)
  windows\                      self-contained app (package.ps1 / build.ps1 -Publish)
  windows-dev\                  framework-dependent quick build (build.ps1)
  android\
    ConnectIPN-1.0.apk          release APK (package.ps1)
    ConnectIPN-1.0-debug.apk    debug APK (build.ps1)
```

`release\` is git-ignored. Never commit it, and never move a build out of it into the tree.

## Building a project by hand

The scripts exist so you do not have to, but if you do, still land the output in `release\`:

```powershell
# Windows, self-contained
dotnet publish windows\ConnectIPN.Desktop\ConnectIPN.Desktop.csproj ^
  -c Release -r win-x64 --self-contained true -o release\windows

# Android
cd android
.\gradlew.bat assembleRelease
Copy-Item app\build\outputs\apk\release\ConnectIPN-1.0.apk ..\release\android\ -Force
```

## Environment

- **.NET 8 SDK.** It may live in your user profile (`%USERPROFILE%\.dotnet`) rather than on
  PATH. A `dotnet` that resolves from PATH can be runtime-only and fail with
  *"No .NET SDKs were found"*. All three scripts handle this: `Resolve-Dotnet` returns the
  first candidate that answers `dotnet --list-sdks`. If you invoke `dotnet` yourself, check
  `dotnet --list-sdks` first.
- **JDK 17.** The scripts default `JAVA_HOME` to `C:\Program Files\Java\jdk-17`.
- **`android\local.properties`** must point at your SDK (`sdk.dir=...`). It is git-ignored
  and must not be committed.
- **WinFsp** is not needed to build — the .NET and native assemblies are vendored in
  `windows\vendor`. It *is* needed to mount a drive at runtime.
- If a build fails on a locked `ConnectIPN.exe`, a copy is still running (possibly
  minimised to the tray). Close it from the tray menu, or `taskkill /F /IM ConnectIPN.exe`.

## Verify before handing a build over

```powershell
Get-ChildItem release -Recurse -File -Include *.exe,*.apk |
    Select-Object FullName, @{n='MB';e={[math]::Round($_.Length/1MB,1)}}, LastWriteTime
```

Every row should have today's timestamp. If a file is missing, the step that produces it
did not run — do not report a partial build as done.
