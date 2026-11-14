# Okakii Launcher

The launcher for [Okakii](https://okakii.lol). Press **Play** on a game at okakii.lol and the
launcher installs (or updates) the right client and joins you into the server.

<p align="center"><img src="assets/logo.png" width="96" alt="Okakii"></p>

## Download

Grab the latest build from the [Releases](../../releases/latest) page or from
[okakii.lol/download](https://okakii.lol/download).

| Platform | File |
|---|---|
| Windows 10/11 (64-bit) | `OkakiiLauncher.exe` |
| macOS 12+, Apple Silicon (M1 and newer) | `OkakiiLauncher-mac-arm64.zip` |
| macOS 12+, Intel | `OkakiiLauncher-mac-x64.zip` |

### Windows
Run `OkakiiLauncher.exe` once. It installs itself and registers the `okakiiclient://` link, so
**Play** buttons on the site open it. It keeps itself up to date.

### macOS
1. Unzip and move **Okakii.app** to your Applications folder.
2. Open it once with **right-click → Open** (the app isn't notarized, so a normal double-click
   is blocked the first time). If macOS says it's damaged, run `xattr -cr /Applications/Okakii.app`
   in Terminal and try again.
3. Press **Play** on a game at okakii.lol.

The Mac launcher supports games that use the **2021** client.

## What it does

1. Reads the game and join ticket from the `okakiiclient://join?place=…&ticket=…&year=…` link.
2. Downloads the client for that year if it's missing or out of date
   (`/download/OkakiiClient<year>.version`, `-mac` on macOS).
3. Starts the client and joins the server.

On Windows the launcher also starts Okakii's anti-cheat and keeps watching it while you play.

## Building

### Windows (`windows/`)
.NET 8 SDK, Windows Forms.
```
cd windows
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o ../dist/windows
```
Release builds embed the obfuscated anti-cheat package, which isn't part of this repository. Point
`GuardPublishZip` in `OkakiiLauncher.csproj` at your own build of it.

### macOS (`mac/`)
.NET 8 SDK + [Avalonia](https://avaloniaui.net/), buildable from any OS.
```
cd mac
dotnet publish -c Release -r osx-arm64 --self-contained -p:UseAppHost=true -o ../build/mac-osx-arm64
dotnet publish -c Release -r osx-x64   --self-contained -p:UseAppHost=true -o ../build/mac-osx-x64
cd .. && python build_mac.py
```
`build_mac.py` assembles `Okakii.app` (Info.plist with the `okakiiclient` URL scheme, icon), signs it
ad hoc with [rcodesign](https://github.com/indygreg/apple-platform-rs) (set `RCODESIGN` to its path)
and writes the zips to `dist/mac/`. Needs Python with Pillow.
