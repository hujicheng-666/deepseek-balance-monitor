#!/usr/bin/env bash
# Build a signed-ready macOS DMG on macOS.
# Usage: ./packaging/make-macos-dmg.sh [osx-arm64|osx-x64]
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
RID="${1:-}"
if [[ -z "$RID" ]]; then
  case "$(uname -m)" in
    arm64) RID="osx-arm64" ;;
    x86_64) RID="osx-x64" ;;
    *) echo "Unsupported macOS architecture: $(uname -m)" >&2; exit 1 ;;
  esac
fi

case "$RID" in
  osx-arm64) ARCH="arm64" ;;
  osx-x64) ARCH="x64" ;;
  *) echo "RID must be osx-arm64 or osx-x64" >&2; exit 1 ;;
esac

VERSION="2.0.0"
PUBLISH="$ROOT/.artifacts/publish/$RID"
APP="$ROOT/.artifacts/DeepSeek.app"
RELEASE="$ROOT/release"

rm -rf "$PUBLISH" "$APP"
mkdir -p "$PUBLISH" "$APP/Contents/MacOS" "$APP/Contents/Resources" "$RELEASE"

dotnet publish "$ROOT/DeepSeekMonitor.Avalonia/DeepSeekMonitor.Avalonia.csproj" \
  -c Release -r "$RID" --self-contained true -o "$PUBLISH" --nologo
cp -R "$PUBLISH/." "$APP/Contents/MacOS/"
cp "$ROOT/DeepSeekMonitor.Avalonia/Assets/whale.png" "$APP/Contents/Resources/whale.png"
chmod +x "$APP/Contents/MacOS/DeepSeekMonitor"

cat > "$APP/Contents/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
  <key>CFBundleName</key><string>DeepSeek</string>
  <key>CFBundleDisplayName</key><string>DeepSeek</string>
  <key>CFBundleIdentifier</key><string>com.deepseek.monitor</string>
  <key>CFBundleVersion</key><string>2.0.0</string>
  <key>CFBundleShortVersionString</key><string>2.0.0</string>
  <key>CFBundleExecutable</key><string>DeepSeekMonitor</string>
  <key>LSMinimumSystemVersion</key><string>11.0</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>LSApplicationCategoryType</key><string>public.app-category.utilities</string>
</dict></plist>
PLIST

# Set MACOS_SIGN_IDENTITY to sign locally before creating the DMG.
if [[ -n "${MACOS_SIGN_IDENTITY:-}" ]]; then
  codesign --force --deep --options runtime --sign "$MACOS_SIGN_IDENTITY" "$APP"
fi

DMG="$RELEASE/DeepSeek-${VERSION}-macos-${ARCH}.dmg"
rm -f "$DMG"
STAGING="$ROOT/.artifacts/dmg-root"
rm -rf "$STAGING"
mkdir -p "$STAGING"
cp -R "$APP" "$STAGING/DeepSeek.app"
ln -s /Applications "$STAGING/Applications"
hdiutil create -volname "DeepSeek" -srcfolder "$STAGING" -ov -format UDZO "$DMG"
echo "Created $DMG"
