#!/usr/bin/env bash
# bump-version.sh
# Met à jour la version de l'application dans Directory.Build.props (Source Unique de Vérité)
set -e

if [ -z "$1" ]; then
    echo "Usage: $0 <version> (ex: $0 1.2.0 ou $0 v1.2.0)" >&2
    exit 1
fi

NEW_VERSION="${1#v}"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
PROPS_PATH="$ROOT_DIR/Directory.Build.props"

if [ ! -f "$PROPS_PATH" ]; then
    echo "Erreur: $PROPS_PATH introuvable." >&2
    exit 1
fi

sed -i -E "s|<Version>[^<]+</Version>|<Version>$NEW_VERSION</Version>|g" "$PROPS_PATH"
sed -i -E "s|<AssemblyVersion>[^<]+</AssemblyVersion>|<AssemblyVersion>$NEW_VERSION.0</AssemblyVersion>|g" "$PROPS_PATH"
sed -i -E "s|<FileVersion>[^<]+</FileVersion>|<FileVersion>$NEW_VERSION.0</FileVersion>|g" "$PROPS_PATH"
sed -i -E "s|<InformationalVersion>[^<]+</InformationalVersion>|<InformationalVersion>$NEW_VERSION</InformationalVersion>|g" "$PROPS_PATH"

echo "Version de CassiniFlow mise à jour vers $NEW_VERSION dans Directory.Build.props !"
