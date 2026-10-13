#!/usr/bin/env bash
# ============================================================
# Publishes an installer release: tags v<version> and uploads the three
# assets every running installer updates from --
#   CivVModInstaller.exe  Windows build (build.sh / build.bat output)
#   civ5-mod              Linux installer
#   version.txt           the version both compare themselves against
# Players and updaters always fetch
#   https://github.com/OBLASTWAR/civ5-mod-installer/releases/latest/download/<asset>
#
#   ./build.sh && ./release.sh
#
# Needs gh (authenticated), a clean tree with the version commit pushed,
# and beat.mp3 embedded in the build (the script refuses a build without it).
# ============================================================
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$HERE"
REPO="OBLASTWAR/civ5-mod-installer"

CS_VER="$(sed -n 's/.*private const string InstallerVersion = "\([^"]*\)";.*/\1/p' Installer.cs)"
PY_VER="$(sed -n 's/^VERSION = "\([^"]*\)"$/\1/p' linux/civ5-mod)"
[[ -n "$CS_VER" && "$CS_VER" == "$PY_VER" ]] || {
    echo "ERROR: version mismatch -- Installer.cs '$CS_VER' vs linux/civ5-mod '$PY_VER'"; exit 1; }
TAG="v$CS_VER"

[[ -f CivVModInstaller.exe ]] || { echo "ERROR: CivVModInstaller.exe not built -- run build.sh first"; exit 1; }
[[ CivVModInstaller.exe -nt Installer.cs ]] || { echo "ERROR: CivVModInstaller.exe is older than Installer.cs -- rebuild"; exit 1; }
grep -q "KekModInstaller.beat.mp3" CivVModInstaller.exe || { echo "ERROR: build has no embedded beat.mp3"; exit 1; }
[[ -z "$(git status --porcelain)" ]] || { echo "ERROR: uncommitted changes"; exit 1; }
git fetch -q origin
[[ "$(git rev-parse HEAD)" == "$(git rev-parse origin/main)" ]] || { echo "ERROR: HEAD isn't origin/main -- push first"; exit 1; }

STAGE="$(mktemp -d)"
trap 'rm -rf "$STAGE"' EXIT
cp CivVModInstaller.exe linux/civ5-mod "$STAGE/"
printf '%s\n' "$CS_VER" > "$STAGE/version.txt"

gh release create "$TAG" -R "$REPO" --target "$(git rev-parse HEAD)" --title "Civ V Mod Installer $CS_VER" \
    --notes "${NOTES:-Civ V Mod Installer $CS_VER.}" \
    "$STAGE/CivVModInstaller.exe" "$STAGE/civ5-mod" "$STAGE/version.txt"
echo "Published $TAG"
