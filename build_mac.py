"""Package the published Avalonia launcher into signed macOS .app bundles + zips.

  python build_mac.py
Needs: build/mac-osx-arm64 and build/mac-osx-x64 (dotnet publish output), rcodesign, Pillow.
Output: dist/mac/OkakiiLauncher-mac-arm64.zip and OkakiiLauncher-mac-x64.zip
"""
import os, plistlib, shutil, subprocess, sys, zipfile
from PIL import Image

ROOT = os.path.dirname(os.path.abspath(__file__))
RCODESIGN = os.environ.get("RCODESIGN") or os.path.expanduser(r"~\tools\rcodesign\apple-codesign-0.29.0-i686-pc-windows-msvc\rcodesign.exe")
VERSION = "4"
APP_NAME = "Okakii.app"
EXE = "OkakiiLauncher"

INFO = {
    "CFBundleName": "Okakii",
    "CFBundleDisplayName": "Okakii",
    "CFBundleIdentifier": "lol.okakii.launcher",
    "CFBundleExecutable": EXE,
    "CFBundleIconFile": "AppIcon",
    "CFBundlePackageType": "APPL",
    "CFBundleShortVersionString": f"{VERSION}.0",
    "CFBundleVersion": VERSION,
    "LSMinimumSystemVersion": "12.0",  # .NET 8 floor
    "NSHighResolutionCapable": True,
    "CFBundleURLTypes": [{"CFBundleURLName": "Okakii Protocol", "CFBundleURLSchemes": ["okakiiclient"]}],
}


def make_icns(dst):
    src = Image.open(os.path.join(ROOT, "assets", "logo.png")).convert("RGBA")
    side = max(src.size)
    canvas = Image.new("RGBA", (side, side), (0, 0, 0, 0))
    canvas.paste(src, ((side - src.width) // 2, (side - src.height) // 2))
    canvas.resize((1024, 1024), Image.LANCZOS).save(dst, sizes=[(16, 16), (32, 32), (64, 64), (128, 128), (256, 256), (512, 512), (1024, 1024)])


def zip_app(app_dir, out_zip):
    base = os.path.dirname(app_dir)
    with zipfile.ZipFile(out_zip, "w", zipfile.ZIP_DEFLATED) as z:
        for dirpath, _, files in os.walk(app_dir):
            for f in files:
                full = os.path.join(dirpath, f)
                rel = os.path.relpath(full, base).replace(os.sep, "/")
                info = zipfile.ZipInfo(rel)
                info.create_system = 3  # unix, so the mode bits below are honoured by Archive Utility/ditto
                execable = rel.endswith(f"/MacOS/{EXE}") or f.endswith(".dylib")
                info.external_attr = ((0o100755 if execable else 0o100644) << 16)
                info.compress_type = zipfile.ZIP_DEFLATED
                with open(full, "rb") as fh:
                    z.writestr(info, fh.read())


def build(rid, label):
    pub = os.path.join(ROOT, "build", f"mac-{rid}")
    if not os.path.isdir(pub):
        sys.exit(f"missing {pub}: run dotnet publish -r {rid} first")
    stage = os.path.join(ROOT, "build", f"app-{label}")
    shutil.rmtree(stage, ignore_errors=True)
    app = os.path.join(stage, APP_NAME)
    macos = os.path.join(app, "Contents", "MacOS")
    res = os.path.join(app, "Contents", "Resources")
    shutil.copytree(pub, macos, ignore=shutil.ignore_patterns("*.pdb"))
    os.makedirs(res)
    make_icns(os.path.join(res, "AppIcon.icns"))
    with open(os.path.join(app, "Contents", "Info.plist"), "wb") as fh:
        plistlib.dump(INFO, fh)
    # Ad-hoc signature: Apple Silicon refuses to run unsigned code at all.
    subprocess.run([RCODESIGN, "sign", app], check=True, capture_output=True)
    out = os.path.join(ROOT, "dist", "mac")
    os.makedirs(out, exist_ok=True)
    zpath = os.path.join(out, f"OkakiiLauncher-mac-{label}.zip")
    zip_app(app, zpath)
    print(f"{label}: {zpath} ({os.path.getsize(zpath) // 1048576} MB)")


if __name__ == "__main__":
    build("osx-arm64", "arm64")
    build("osx-x64", "x64")
