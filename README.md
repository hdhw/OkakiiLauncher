# Okakii Launcher (Windows + macOS) - NOT SHIPPED

Classic-style launcher window (white dialog, logo, "Connecting to Okakii ...", green progress bar).

## Windows - `dist/windows/OkakiiLauncher.exe`
Same behaviour as the shipping launcher (BubbaBlox/Program.cs): okakiiclient:// registration,
self-update, per-year client download/refresh, guard + watchdog, `-a/-j/-t` launch. Only the window
changed. `LauncherVersion = 4`.

- **Look at it without installing anything:** `OkakiiLauncher.exe --preview` (window only) or
  `OkakiiLauncher.exe --snapshot out.png`.
- **Real test:** run it once with no arguments. Like the current launcher, it copies itself to
  `%LOCALAPPDATA%\Okakii\OkakiiLauncher.exe` (replacing the installed one on THIS PC only), then
  press Play on the site. To go back, re-run the old launcher from okakii.lol/download.
- Shipping later = upload the exe to `/download/OkakiiLauncher.exe` and bump
  `/download/OkakiiLauncher.version` to `4` (installed launchers then update themselves).

Build: `cd windows && dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o ../dist/windows`
(embeds the guard zip from `Downloads\BubbaBlox\BubbaBlox\OkakiiAntiCheat\publish`).

## macOS - `dist/mac/OkakiiLauncher-mac-arm64.zip` (Apple Silicon) / `-x64.zip` (Intel)
Avalonia app `Okakii.app`, ad-hoc signed. Registers `okakiiclient://` via Info.plist, so the
existing Play button works unchanged. Only year 2021 is supported (other years show a message).
Installs the client to `~/Library/Application Support/Okakii/Clients/2021/RobloxPlayer.app`
(ditto unzip, `xattr -cr`, ad-hoc `codesign`) and starts it with `-ticket/-authURL/-scriptURL`.
No guard on Mac.

Test on a Mac:
1. Unzip, move `Okakii.app` to /Applications.
2. First open: right-click -> Open (it isn't notarized), or `xattr -cr /Applications/Okakii.app`.
3. Press Play on a 2021 game at okakii.lol.

The launcher needs these on the server first (staged in `dist/server/`, not uploaded):
`/download/OkakiiClient2021-mac.zip` + `/download/OkakiiClient2021-mac.version`.
The zip is the 0.461.0.416397 Mac player (version-bd209e654cfd4bb4) patched by
`Downloads/okakii-mac/patch_mac2021.py` (roblox.com -> okakii.lol, join-script keys -> PrivateKey2020).

Build: `cd mac && dotnet publish -c Release -r osx-arm64 --self-contained -p:UseAppHost=true -o ../build/mac-osx-arm64`
(same for osx-x64), then `python build_mac.py`.
