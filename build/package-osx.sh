#!/usr/bin/env bash
# Packages Hisui PDF as a .app bundle wrapped in a .dmg disk image for macOS x64.
#
# Prerequisites:
#   - .NET 10 SDK
#   - hdiutil (built-in on macOS)
#   - Optional: codesign + xcrun notarytool (Apple Developer account) for Gatekeeper clearance
#
# Run on macOS. The dotnet publish step can also be done on another platform first.
# Usage: bash build/package-osx.sh [VERSION]
#
# Output: bin/installer/HisuiPDF-<version>-osx-x64.dmg

set -euo pipefail

VERSION="${1:-1.0.0}"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(dirname "$SCRIPT_DIR")"

PUBLISH_DIR="$ROOT/bin/publish/osx-x64"
STAGING="$ROOT/build/.staging/osx"
APP_NAME="Hisui PDF"
APP_BUNDLE="$STAGING/$APP_NAME.app"
CONTENTS="$APP_BUNDLE/Contents"
OUT_DIR="$ROOT/bin/installer"

# ---- 1. publish -------------------------------------------------------------

echo "Publishing osx-x64..."
dotnet publish "$ROOT/src/Hisui.Pdf.App/Hisui.Pdf.App.csproj" /p:PublishProfile=osx-x64

# ---- 2. .app bundle ---------------------------------------------------------

echo ""
echo "Building .app bundle..."
rm -rf "$APP_BUNDLE"
mkdir -p "$CONTENTS/MacOS"
mkdir -p "$CONTENTS/Resources/tessdata"

cp "$PUBLISH_DIR/Hisui.Pdf.App" "$CONTENTS/MacOS/"
chmod +x "$CONTENTS/MacOS/Hisui.Pdf.App"

if [[ -d "$PUBLISH_DIR/tessdata" ]]; then
    cp -r "$PUBLISH_DIR/tessdata/." "$CONTENTS/Resources/tessdata/"
fi

# App icon (.icns)
ICNS="$ROOT/src/Hisui.Pdf.App/Assets/osx/AppIcon.icns"
if [[ -f "$ICNS" ]]; then
    cp "$ICNS" "$CONTENTS/Resources/AppIcon.icns"
else
    echo "WARNING: $ICNS not found — app will have a generic icon."
    echo "Create it on macOS:"
    echo "  mkdir AppIcon.iconset"
    echo "  sips -z 512 512 icon-1024.png --out AppIcon.iconset/icon_512x512.png"
    echo "  iconutil -c icns AppIcon.iconset -o src/Hisui.Pdf.App/Assets/osx/AppIcon.icns"
fi

# Info.plist
cat > "$CONTENTS/Info.plist" << PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key>
    <string>Hisui PDF</string>
    <key>CFBundleDisplayName</key>
    <string>Hisui PDF</string>
    <key>CFBundleIdentifier</key>
    <string>com.filardo.hisuipdf</string>
    <key>CFBundleVersion</key>
    <string>$VERSION</string>
    <key>CFBundleShortVersionString</key>
    <string>$VERSION</string>
    <key>CFBundleExecutable</key>
    <string>Hisui.Pdf.App</string>
    <key>CFBundleIconFile</key>
    <string>AppIcon</string>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>NSHighResolutionCapable</key>
    <true/>
    <key>NSPrincipalClass</key>
    <string>NSApplication</string>
    <key>LSMinimumSystemVersion</key>
    <string>12.0</string>
    <key>CFBundleDocumentTypes</key>
    <array>
        <dict>
            <key>CFBundleTypeName</key>
            <string>PDF Document</string>
            <key>CFBundleTypeExtensions</key>
            <array><string>pdf</string></array>
            <key>CFBundleTypeMIMETypes</key>
            <array><string>application/pdf</string></array>
            <key>CFBundleTypeRole</key>
            <string>Editor</string>
            <key>LSHandlerRank</key>
            <string>Alternate</string>
        </dict>
    </array>
</dict>
</plist>
PLIST

# ---- 3. optional codesign ---------------------------------------------------
# Uncomment and set CODESIGN_IDENTITY to sign the bundle (requires Apple Developer account):
#
# CODESIGN_IDENTITY="Developer ID Application: Emanuele Filardo (TEAMID)"
# codesign --deep --force --options runtime \
#     --entitlements "$ROOT/build/entitlements.plist" \
#     --sign "$CODESIGN_IDENTITY" \
#     "$APP_BUNDLE"
#
# After signing, notarize with:
#   xcrun notarytool submit HisuiPDF-$VERSION-osx-x64.dmg \
#       --apple-id you@email.com --team-id TEAMID --password "@keychain:AC_PASSWORD" --wait
#   xcrun stapler staple "bin/installer/HisuiPDF-$VERSION-osx-x64.dmg"

# ---- 4. DMG -----------------------------------------------------------------

mkdir -p "$OUT_DIR"
OUT="$OUT_DIR/HisuiPDF-$VERSION-osx-x64.dmg"

echo ""
echo "Creating DMG..."
hdiutil create \
    -volname "$APP_NAME $VERSION" \
    -srcfolder "$APP_BUNDLE" \
    -ov -format UDZO \
    "$OUT"

echo ""
echo "Output: $OUT"
echo ""
echo "Note: without Apple code signing, Gatekeeper blocks the app on first launch."
echo "Users can bypass it with:  xattr -dr com.apple.quarantine \"$APP_BUNDLE\""
