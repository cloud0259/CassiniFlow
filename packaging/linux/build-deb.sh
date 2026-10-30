#!/usr/bin/env bash
# CassiniFlow Debian Package (.deb) Builder
set -e

VERSION="${1:-1.0.0}"
# Clean leading 'v' if present (e.g. v1.0.0 -> 1.0.0)
VERSION="${VERSION#v}"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/../.." && pwd)"
BUILD_DIR="$ROOT_DIR/staging/deb-build"
DEB_PACKAGE_DIR="$BUILD_DIR/cassiniflow_${VERSION}_amd64"
OUTPUT_DIR="$ROOT_DIR/release-artifacts"

echo "=== Génération du paquet Debian CassiniFlow v$VERSION ==="

rm -rf "$BUILD_DIR"
mkdir -p "$DEB_PACKAGE_DIR/DEBIAN"
mkdir -p "$DEB_PACKAGE_DIR/opt/cassiniflow"
mkdir -p "$DEB_PACKAGE_DIR/opt/cassiniflow/cli"
mkdir -p "$DEB_PACKAGE_DIR/usr/local/bin"
mkdir -p "$DEB_PACKAGE_DIR/usr/share/applications"
mkdir -p "$DEB_PACKAGE_DIR/usr/share/icons/hicolor/256x256/apps"
mkdir -p "$OUTPUT_DIR"

# 1. Copie des fichiers publiés Desktop et CLI
if [ -d "$ROOT_DIR/staging/linux-x64/Desktop" ]; then
    cp -r "$ROOT_DIR/staging/linux-x64/Desktop"/* "$DEB_PACKAGE_DIR/opt/cassiniflow/"
elif [ -d "$ROOT_DIR/staging/CassiniFlow-linux-x64/Desktop" ]; then
    cp -r "$ROOT_DIR/staging/CassiniFlow-linux-x64/Desktop"/* "$DEB_PACKAGE_DIR/opt/cassiniflow/"
else
    echo "Publication autonome requise avant la création du .deb..."
    dotnet publish "$ROOT_DIR/src/CassiniFlow.Desktop/CassiniFlow.Desktop.csproj" \
        -c Release -r linux-x64 --self-contained true \
        -o "$DEB_PACKAGE_DIR/opt/cassiniflow"
fi

if [ -d "$ROOT_DIR/staging/linux-x64/Cli" ]; then
    cp -r "$ROOT_DIR/staging/linux-x64/Cli"/* "$DEB_PACKAGE_DIR/opt/cassiniflow/cli/"
elif [ -d "$ROOT_DIR/staging/CassiniFlow-linux-x64/Cli" ]; then
    cp -r "$ROOT_DIR/staging/CassiniFlow-linux-x64/Cli"/* "$DEB_PACKAGE_DIR/opt/cassiniflow/cli/"
else
    dotnet publish "$ROOT_DIR/src/CassiniFlow.Cli/CassiniFlow.Cli.csproj" \
        -c Release -r linux-x64 --self-contained true \
        -o "$DEB_PACKAGE_DIR/opt/cassiniflow/cli"
fi

chmod +x "$DEB_PACKAGE_DIR/opt/cassiniflow/CassiniFlow.Desktop"
if [ -f "$DEB_PACKAGE_DIR/opt/cassiniflow/cli/CassiniFlow.Cli" ]; then
    chmod +x "$DEB_PACKAGE_DIR/opt/cassiniflow/cli/CassiniFlow.Cli"
fi

# 2. Wrappers dans /usr/local/bin
cat << 'EOF' > "$DEB_PACKAGE_DIR/usr/local/bin/cassiniflow"
#!/usr/bin/env bash
exec /opt/cassiniflow/CassiniFlow.Desktop "$@"
EOF
chmod +x "$DEB_PACKAGE_DIR/usr/local/bin/cassiniflow"

cat << 'EOF' > "$DEB_PACKAGE_DIR/usr/local/bin/cassiniflow-cli"
#!/usr/bin/env bash
exec /opt/cassiniflow/cli/CassiniFlow.Cli "$@"
EOF
chmod +x "$DEB_PACKAGE_DIR/usr/local/bin/cassiniflow-cli"

# 3. Fichier Desktop et icône
cp "$SCRIPT_DIR/cassiniflow.desktop" "$DEB_PACKAGE_DIR/usr/share/applications/cassiniflow.desktop"
cp "$ROOT_DIR/src/CassiniFlow.Desktop/Assets/icon_256x256.png" "$DEB_PACKAGE_DIR/usr/share/icons/hicolor/256x256/apps/cassiniflow.png"

# 4. Fichier de contrôle DEBIAN/control
INSTALLED_SIZE=$(du -sk "$DEB_PACKAGE_DIR" | cut -f1)

cat << EOF > "$DEB_PACKAGE_DIR/DEBIAN/control"
Package: cassiniflow
Version: $VERSION
Section: science
Priority: optional
Architecture: amd64
Installed-Size: $INSTALLED_SIZE
Maintainer: CassiniFlow Team <https://github.com/cloud0259/CassiniFlow>
Depends: libgl1, libglib2.0-0, libgomp1, ffmpeg
Description: Planetary & Astro Imaging PreProcessor
 CassiniFlow is a high-performance astronomical preprocessing and stacking
 software for planetary, lunar and solar imaging (lucky imaging).
 Features zero-allocation image pipeline, multi-point alignment stacking,
 SER/FITS native codecs, and celestial target auto-detection.
EOF

# 5. Compilation du paquet .deb avec dpkg-deb
echo "Construction du paquet avec dpkg-deb..."
dpkg-deb --build --root-owner-group "$DEB_PACKAGE_DIR" "$OUTPUT_DIR/cassiniflow_${VERSION}_amd64.deb"
# Symlink or copy to generic name
cp "$OUTPUT_DIR/cassiniflow_${VERSION}_amd64.deb" "$OUTPUT_DIR/cassiniflow-linux-x64.deb"

echo "✓ Paquet Debian généré : $OUTPUT_DIR/cassiniflow_${VERSION}_amd64.deb"

