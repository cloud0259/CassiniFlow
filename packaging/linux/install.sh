#!/usr/bin/env bash
# CassiniFlow Linux Installer Script
set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
APP_NAME="cassiniflow"
DISPLAY_NAME="CassiniFlow"

SYSTEM_INSTALL=false
if [ "$1" = "--system" ] || [ "$EUID" -eq 0 ]; then
    SYSTEM_INSTALL=true
fi

if [ "$SYSTEM_INSTALL" = true ]; then
    TARGET_DIR="/opt/cassiniflow"
    BIN_DIR="/usr/local/bin"
    DESKTOP_DIR="/usr/share/applications"
    ICON_DIR="/usr/share/icons/hicolor/256x256/apps"
    echo "=== Installation système de $DISPLAY_NAME dans $TARGET_DIR ==="
else
    TARGET_DIR="$HOME/.local/share/cassiniflow"
    BIN_DIR="$HOME/.local/bin"
    DESKTOP_DIR="$HOME/.local/share/applications"
    ICON_DIR="$HOME/.local/share/icons/hicolor/256x256/apps"
    echo "=== Installation utilisateur de $DISPLAY_NAME dans $TARGET_DIR ==="
fi

mkdir -p "$TARGET_DIR" "$BIN_DIR" "$DESKTOP_DIR" "$ICON_DIR"

echo "Copie des fichiers de l'application..."
if [ -d "$SCRIPT_DIR/Desktop" ]; then
    cp -r "$SCRIPT_DIR/Desktop"/* "$TARGET_DIR/"
elif [ -f "$SCRIPT_DIR/CassiniFlow.Desktop" ]; then
    cp -r "$SCRIPT_DIR"/* "$TARGET_DIR/"
else
    echo "Erreur : binaire CassiniFlow.Desktop introuvable dans le répertoire d'installation." >&2
    exit 1
fi

if [ -d "$SCRIPT_DIR/Cli" ]; then
    mkdir -p "$TARGET_DIR/cli"
    cp -r "$SCRIPT_DIR/Cli"/* "$TARGET_DIR/cli/"
fi

chmod +x "$TARGET_DIR/CassiniFlow.Desktop"
if [ -f "$TARGET_DIR/cli/CassiniFlow.Cli" ]; then
    chmod +x "$TARGET_DIR/cli/CassiniFlow.Cli"
fi

# Création du script wrapper
WRAPPER="$BIN_DIR/$APP_NAME"
cat << 'EOF' > "$WRAPPER"
#!/usr/bin/env bash
TARGET_DIR_PLACEHOLDER
exec "$APP_INSTALL_DIR/CassiniFlow.Desktop" "$@"
EOF
sed -i "s|TARGET_DIR_PLACEHOLDER|APP_INSTALL_DIR=\"$TARGET_DIR\"|g" "$WRAPPER"
chmod +x "$WRAPPER"

# CLI wrapper
if [ -f "$TARGET_DIR/cli/CassiniFlow.Cli" ]; then
    CLI_WRAPPER="$BIN_DIR/${APP_NAME}-cli"
    cat << 'EOF' > "$CLI_WRAPPER"
#!/usr/bin/env bash
TARGET_DIR_PLACEHOLDER
exec "$APP_INSTALL_DIR/cli/CassiniFlow.Cli" "$@"
EOF
    sed -i "s|TARGET_DIR_PLACEHOLDER|APP_INSTALL_DIR=\"$TARGET_DIR\"|g" "$CLI_WRAPPER"
    chmod +x "$CLI_WRAPPER"
fi

# Icône
if [ -f "$TARGET_DIR/Assets/icon_256x256.png" ]; then
    cp "$TARGET_DIR/Assets/icon_256x256.png" "$ICON_DIR/$APP_NAME.png"
elif [ -f "$SCRIPT_DIR/icon_256x256.png" ]; then
    cp "$SCRIPT_DIR/icon_256x256.png" "$ICON_DIR/$APP_NAME.png"
fi

# Fichier .desktop
DESKTOP_FILE="$DESKTOP_DIR/$APP_NAME.desktop"
if [ -f "$SCRIPT_DIR/cassiniflow.desktop" ]; then
    cp "$SCRIPT_DIR/cassiniflow.desktop" "$DESKTOP_FILE"
elif [ -f "$TARGET_DIR/cassiniflow.desktop" ]; then
    cp "$TARGET_DIR/cassiniflow.desktop" "$DESKTOP_FILE"
fi

if command -v update-desktop-database >/dev/null 2>&1; then
    update-desktop-database "$DESKTOP_DIR" 2>/dev/null || true
fi

echo "✓ Installation de $DISPLAY_NAME terminée avec succès !"
echo "Vous pouvez lancer l'application avec la commande : $APP_NAME"

