# Civ V Mod Installer

Installs and updates Civilization V multiplayer mods (KEK Mod, Tournament Mod,
Lekmod, SnowVe), their bonus maps, EUI / EUI XITS, Vox Populi (multiplayer pack with its own EUI) and civ5-lobby-patch.

## Windows

Download [`CivVModInstaller.exe`](https://github.com/OBLASTWAR/civ5-mod-installer/releases/latest/download/CivVModInstaller.exe)
and run it. It keeps itself up to date (red **UPDATE** button).

## Linux (Steam Proton)

```sh
curl -fsSL https://github.com/OBLASTWAR/civ5-mod-installer/releases/latest/download/civ5-mod | python3 - install-self
```

Then run `civ5-mod`. `civ5-mod self-update` keeps it current.

## Building and releasing

- `./build.sh` (Linux, uses Steam Proton's wine-mono) or `build.bat` (Windows,
  .NET Framework 4.x) builds `CivVModInstaller.exe`. Put `beat.mp3` next to
  them first; it's gitignored and gets embedded.
  Local builds report version `dev`.
- **Releases come from Jenkins.** A push to `main` builds on Windows
  and publishes the next stable `v2.N` (the major is in `VERSION`) with
  `CivVModInstaller.exe`, `civ5-mod` and `version.txt` as assets. Running
  installers pick it up from `releases/latest/download/`. A push to `release`
  publishes a beta pre-release, which updaters ignore. Pushes that only touch
  `ticker.txt` or docs don't release.
- `ticker.txt` on `main` is the scrolling greetz text, fetched at startup, so
  edits go live without a release.

History before the move lives in
[OBLASTWAR/kek-mod/installer](https://github.com/OBLASTWAR/kek-mod/tree/main/installer)
(installer 2.3 and earlier). That folder keeps frozen copies so older
installers can still update.
