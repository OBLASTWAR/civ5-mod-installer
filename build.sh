#!/usr/bin/env bash
# ============================================================
# KekModInstaller build script -- Linux edition of build.bat.
#
# Builds the same KekModInstaller.exe from the same *.cs, with no .NET or
# Mono install needed: it borrows Proton's bundled wine-mono, which ships
# the real Roslyn csc.exe and the .NET Framework 4.8 reference assemblies.
# Any Steam Proton install works (Experimental, 10.0, 9.0, ...).
#
#   installer/build.sh
#   PROTON_DIR=".../steamapps/common/Proton 10.0" installer/build.sh
#
# csc runs in a throwaway Wine prefix under ~/.cache/kekmod-build, never
# the Civ V prefix.
#
# Self-update: same rule as build.bat -- when publishing a new
# KekModInstaller.exe to main, bump InstallerCore.InstallerVersion in
# Installer.cs, installer/installer_version.txt AND VERSION in
# installer/linux/civ5-mod to the same new value.
# ============================================================
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
OUT="$HERE/KekModInstaller.exe"

steam_roots() {
    local d
    for d in "$HOME/.local/share/Steam" "$HOME/.steam/steam" "$HOME/.steam/root" \
             "$HOME/.var/app/com.valvesoftware.Steam/.local/share/Steam" "$HOME/snap/steam/common/.local/share/Steam"; do
        [[ -d "$d" ]] && echo "$d"
    done
}

# Every Steam library (libraryfolders.vdf) plus the roots themselves.
steam_libraries() {
    local root
    while read -r root; do
        echo "$root"
        [[ -f "$root/steamapps/libraryfolders.vdf" ]] &&
            sed -n 's/^[[:space:]]*"path"[[:space:]]*"\(.*\)"[[:space:]]*$/\1/p' "$root/steamapps/libraryfolders.vdf"
    done < <(steam_roots)
}

# A Proton dir whose wine-mono has Roslyn's csc.exe. Experimental first,
# then the newest numbered version.
find_proton() {
    local lib dir
    local -a dirs
    while read -r lib; do
        dirs=("$lib/steamapps/common/Proton - Experimental")
        mapfile -t -O 1 dirs < <(compgen -G "$lib/steamapps/common/Proton [0-9]*" | sort -rV)
        for dir in "${dirs[@]}"; do
            if [[ -x "$dir/files/bin/wine" ]] && compgen -G "$dir/files/share/wine/mono/wine-mono-*/lib/mono/4.5/csc.exe" >/dev/null; then
                echo "$dir"
                return 0
            fi
        done
    done < <(steam_libraries | awk '!seen[$0]++')
    return 1
}

PROTON="${PROTON_DIR:-}"
if [[ -z "$PROTON" ]]; then
    PROTON="$(find_proton)" || {
        echo "ERROR: no Steam Proton install with wine-mono found."
        echo "       Install any Proton from Steam (Library > Tools), or set PROTON_DIR."
        exit 1
    }
fi
WINE="$PROTON/files/bin/wine"
MONO="$(compgen -G "$PROTON/files/share/wine/mono/wine-mono-*/lib/mono" | sort -V | tail -n1 || true)"
[[ -n "$MONO" ]] || { echo "ERROR: no wine-mono in $PROTON -- is it a complete Proton install?"; exit 1; }
CSC="$MONO/4.5/csc.exe"
REFS="$MONO/4.8-api"
for f in "$WINE" "$CSC" "$REFS/mscorlib.dll"; do
    [[ -e "$f" ]] || { echo "ERROR: $f not found -- is $PROTON a complete Proton install?"; exit 1; }
done
echo "Using: $PROTON"

export WINEPREFIX="${XDG_CACHE_HOME:-$HOME/.cache}/kekmod-build/wineprefix"
export WINEDEBUG=-all
mkdir -p "$WINEPREFIX"

# Same reference set as build.bat. System.Xml is listed explicitly because
# -noconfig skips csc.rsp, which is where Windows csc picks it up.
ARGS=(-nologo -noconfig -nostdlib -target:winexe -platform:x64
      "-out:Z:$OUT.tmp" "-lib:Z:$REFS"
      -r:mscorlib.dll -r:System.dll -r:System.Core.dll -r:System.Xml.dll -r:System.Net.Http.dll
      -r:System.Runtime.Serialization.dll -r:System.IO.Compression.dll
      -r:System.IO.Compression.FileSystem.dll -r:Microsoft.CSharp.dll
      -r:System.Windows.Forms.dll -r:System.Drawing.dll)

# beat.mp3 and the two EUI zips are gitignored -- embed whichever this
# machine has, exactly like build.bat. Note the Linux civ5-mod reads the
# bundled EUI straight out of KekModInstaller.exe, so publishing a build
# without the zips breaks EUI installs there too.
add_resource() {
    if [[ -f "$HERE/$1" ]]; then
        ARGS+=("-resource:Z:$HERE/$1,KekModInstaller.$1")
    else
        echo "WARNING: $1 not found next to build.sh -- building without $2."
    fi
}
add_resource beat.mp3 "embedded music"
add_resource UI_bc1.zip "bundled EUI"
add_resource UI_bc1_xits.zip "bundled EUI XITS"

SOURCES=()
for f in "$HERE"/*.cs; do SOURCES+=("Z:$f"); done

echo
echo "=== Building KekModInstaller.exe ==="
rm -f "$OUT.tmp"
set +e
"$WINE" "Z:$CSC" "${ARGS[@]}" "${SOURCES[@]}" 2>&1 | grep -v '^ntsync\|fixme:\|^wine:'
status=${PIPESTATUS[0]}
set -e
if [[ $status -ne 0 || ! -f "$OUT.tmp" ]]; then
    rm -f "$OUT.tmp"
    echo
    echo "BUILD FAILED (exit code $status)"
    exit 1
fi
mv -f "$OUT.tmp" "$OUT"

echo
echo "BUILD SUCCEEDED"
echo "  $OUT"
