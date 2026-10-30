#!/usr/bin/env bash
# CassiniFlow Linux Uninstaller Script
set -e

APP_NAME="cassiniflow"
DISPLAY_NAME="CassiniFlow"

SYSTEM_UNINSTALL=false
if [ "$1" = "--system" ] || [ "$EUID" -eq 0 ]; then
    SYSTEM_UNINSTALL=true
fi

if [ "$SYSTEM_UNINSTALL" = true ]; then
    TARGET_DIR="/opt/cassiniflow"
    BIN_DIR="/usr/local/bin"
    DESKTOP_DIR="/usr/share/applications"
    ICON_DIR="/usr/share/icons/hicolor/256x256/apps"
    echo "=== Désinstallation système de $DISPLAY_NAME ==="
else
    TARGET_DIR="$HOME/.local/share/cassiniflow"
    BIN_DIR="$HOME/.local/bin"
    DESKTOP_DIR="$HOME/.local/share/applications"
    ICON_DIR="$HOME/.local/share/icons/hicolor/256x256/apps"
    echo "=== Désinstallation utilisateur de $DISPLAY_NAME ==="
fi

echo "Suppression des fichiers..."
rm -rf "$TARGET_DIR"
rm -f "$BIN_DIR/$APP_NAME"
rm -f "$BIN_DIR/${APP_NAME}-cli"
rm -f "$DESKTOP_DIR/$APP_NAME.desktop"
rm -f "$ICON_DIR/$APP_NAME.png"

if command -v update-desktop-database >/dev/null 2>&1; then
    update-desktop-database "$DESKTOP_DIR" 2>/dev/null || true
fi

echo "✓ $DISPLAY_NAME a été désinstallé avec succès."

