#!/usr/bin/env bash
# Packages Hisui PDF as a portable AppImage for Linux x86_64.
#
# Prerequisites:
#   - .NET 10 SDK
#   - curl (to download appimagetool on first run)
#   - FUSE v2 (libfuse2) for running the AppImage; not needed to build it
#
# Run on Linux or WSL2.  The dotnet publish step can also be done on Windows first.
# Usage: bash build/package-linux.sh [VERSION]
#
# Output: bin/installer/HisuiPDF-<version>-linux-x86_64.AppImage

set -euo pipefail

VERSION="${1:-1.0.0}"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(dirname "$SCRIPT_DIR")"

PUBLISH_DIR="$ROOT/bin/publish/linux-x64"
APPDIR="$ROOT/build/.staging/linux/HisuiPDF.AppDir"
OUT_DIR="$ROOT/bin/installer"
APPIMAGETOOL="$ROOT/build/tools/appimagetool-x86_64.AppImage"

# ---- 1. publish -------------------------------------------------------------

echo "Publishing linux-x64..."
dotnet publish "$ROOT/src/Hisui.Pdf.App/Hisui.Pdf.App.csproj" /p:PublishProfile=linux-x64

# ---- 2. check icon ----------------------------------------------------------

ICON_SRC="$ROOT/src/Hisui.Pdf.App/Assets/linux/HisuiPDF.png"
if [[ ! -f "$ICON_SRC" ]]; then
    echo "ERROR: Icon not found at $ICON_SRC"
    echo "Generate icons first: pwsh build/create-icons.ps1  (or PowerShell on Windows)"
    exit 1
fi

# ---- 3. AppDir layout -------------------------------------------------------

echo ""
echo "Staging AppDir..."
rm -rf "$APPDIR"
mkdir -p "$APPDIR/usr/bin"
mkdir -p "$APPDIR/usr/share/tessdata"

cp "$PUBLISH_DIR/Hisui.Pdf.App" "$APPDIR/usr/bin/"
chmod +x "$APPDIR/usr/bin/Hisui.Pdf.App"

if [[ -d "$PUBLISH_DIR/tessdata" ]]; then
    cp -r "$PUBLISH_DIR/tessdata/." "$APPDIR/usr/share/tessdata/"
fi

# AppRun launcher
cat > "$APPDIR/AppRun" << 'APPRUN'
#!/usr/bin/env sh
HERE="$(dirname "$(readlink -f "$0")")"
export TESSDATA_PREFIX="$HERE/usr/share"
exec "$HERE/usr/bin/Hisui.Pdf.App" "$@"
APPRUN
chmod +x "$APPDIR/AppRun"

# FreeDesktop .desktop entry
cat > "$APPDIR/HisuiPDF.desktop" << DESKTOP
[Desktop Entry]
Name=Hisui PDF
GenericName=PDF Editor
Comment=PDF editor: merge, split, annotate, redact, sign
Exec=Hisui.Pdf.App %F
Icon=HisuiPDF
Type=Application
Categories=Office;
MimeType=application/pdf;
Keywords=PDF;editor;viewer;
StartupNotify=true
DESKTOP

cp "$ICON_SRC" "$APPDIR/HisuiPDF.png"

# ---- 4. get appimagetool ----------------------------------------------------

if [[ ! -f "$APPIMAGETOOL" ]]; then
    echo ""
    echo "Downloading appimagetool..."
    mkdir -p "$(dirname "$APPIMAGETOOL")"
    curl -L --progress-bar -o "$APPIMAGETOOL" \
        "https://github.com/AppImage/AppImageKit/releases/download/continuous/appimagetool-x86_64.AppImage"
    chmod +x "$APPIMAGETOOL"
fi

# ---- 5. build AppImage ------------------------------------------------------

mkdir -p "$OUT_DIR"
OUT="$OUT_DIR/HisuiPDF-$VERSION-linux-x86_64.AppImage"

echo ""
echo "Building AppImage..."
ARCH=x86_64 "$APPIMAGETOOL" "$APPDIR" "$OUT"

echo ""
echo "Output: $OUT"
echo "Install: chmod +x \"$OUT\" && \"$OUT\""
