// LobbyPatchExtra.cs -- civ5-lobby-patch (OBLASTWAR/civ5-lobby-patch)
// install/remove, backing MainForm's NETWORK MODS box.
//
// Unlike every other component here it doesn't live under Assets at all:
// it's a proxy steam_api.dll in the game's root folder (next to
// CivilizationV.exe). The game's own steam_api.dll is renamed to
// steam_api_orig.dll, and the proxy forwards every export to it except
// SteamMatchmaking, whose lobby list it hooks to drop Steam's distance
// filter so lobbies show up worldwide. Works unchanged under Proton: the
// game's steam_api.dll is a plain Windows DLL there too.
//
// Distributed as GitHub Releases with a single steam_api.dll asset -- no
// zip. The installed release tag is kept in a small marker file next to
// it, since a DLL this small carries no version string of its own.
//
// "Installed" means steam_api_orig.dll exists AND steam_api.dll differs
// from it. Steam's "verify integrity" restores the stock steam_api.dll but
// leaves our extra steam_api_orig.dll behind -- the two are then
// byte-identical, which correctly reads as not installed, and installing
// again just overwrites steam_api.dll.

using System;
using System.IO;
using System.Linq;

namespace KekModInstaller
{
    internal sealed class LobbyPatchState
    {
        public bool Installed;
        public string Tag; // installed release tag, null if unknown/not installed
    }

    internal static class LobbyPatch
    {
        public const string DisplayName = "Lobby Patch";
        private const string Owner = "OBLASTWAR";
        private const string Repo = "civ5-lobby-patch";
        private const string AssetName = "steam_api.dll";
        private const string ActiveDll = "steam_api.dll";
        private const string OrigDll = "steam_api_orig.dll";
        private const string MarkerFile = "civ5-lobby-patch.txt";

        public static LobbyPatchState Detect(string gameFolder)
        {
            var state = new LobbyPatchState();
            string active = Path.Combine(gameFolder, ActiveDll);
            string orig = Path.Combine(gameFolder, OrigDll);
            if (!File.Exists(active) || !File.Exists(orig) || FilesEqual(active, orig))
            {
                return state;
            }
            state.Installed = true;
            string marker = Path.Combine(gameFolder, MarkerFile);
            if (File.Exists(marker))
            {
                string tag = File.ReadAllText(marker).Trim();
                state.Tag = tag.Length > 0 ? tag : null;
            }
            return state;
        }

        // Newest published release tag -- the status worker's update check.
        public static string FetchLatestTag()
        {
            return GitHubModSource.FetchLatestRelease(Owner, Repo).TagName;
        }

        public static void Install(string gameFolder, Action<string> log)
        {
            string active = Path.Combine(gameFolder, ActiveDll);
            string orig = Path.Combine(gameFolder, OrigDll);

            log("Checking " + Owner + "/" + Repo + " for the latest release...");
            GhRelease release = GitHubModSource.FetchLatestRelease(Owner, Repo);
            GhAsset asset = release.Assets == null ? null : release.Assets.FirstOrDefault(
                a => string.Equals(a.Name, AssetName, StringComparison.OrdinalIgnoreCase));
            if (asset == null)
            {
                throw new InvalidOperationException("Release " + release.TagName + " has no " + AssetName + ".");
            }
            log("Release: " + release.TagName);
            log("Downloading " + asset.Name + "...");
            string temp = GitHubModSource.DownloadAsset(asset);
            try
            {
                byte[] dll = File.ReadAllBytes(temp);
                if (dll.Length < 2 || dll[0] != 'M' || dll[1] != 'Z')
                {
                    throw new InvalidOperationException("Downloaded " + asset.Name + " isn't a Windows DLL.");
                }

                // Back up the game's own steam_api.dll exactly once. If the
                // backup already exists, whatever is at steam_api.dll now is
                // either an older proxy or Steam's restored stock copy --
                // overwriting either is correct.
                if (!File.Exists(orig))
                {
                    if (!File.Exists(active))
                    {
                        throw new InvalidOperationException(
                            ActiveDll + " not found in the game folder -- verify Civilization V's files in Steam first.");
                    }
                    if (File.Exists(Path.Combine(gameFolder, MarkerFile)))
                    {
                        // Our marker without a backup: steam_api.dll is
                        // probably our proxy, and renaming it to _orig would
                        // make it load itself. Only Steam can restore the
                        // real one.
                        throw new InvalidOperationException(
                            OrigDll + " is missing. Verify Civilization V's files in Steam, then install again.");
                    }
                    log("Backing up " + ActiveDll + " -> " + OrigDll);
                    File.Move(active, orig);
                }

                log("Installing " + ActiveDll + "...");
                File.WriteAllBytes(active, dll);
                File.WriteAllText(Path.Combine(gameFolder, MarkerFile), release.TagName);
            }
            finally
            {
                File.Delete(temp);
            }
            log("DONE: " + DisplayName + " " + release.TagName + " installed.");
        }

        public static void Remove(string gameFolder, Action<string> log)
        {
            string active = Path.Combine(gameFolder, ActiveDll);
            string orig = Path.Combine(gameFolder, OrigDll);
            string marker = Path.Combine(gameFolder, MarkerFile);

            if (!File.Exists(orig))
            {
                log("No " + OrigDll + " backup -- nothing to restore.");
            }
            else
            {
                log("Restoring the game's own " + ActiveDll + "...");
                if (File.Exists(active))
                {
                    File.Delete(active);
                }
                File.Move(orig, active);
            }
            if (File.Exists(marker))
            {
                File.Delete(marker);
            }
            log("DONE: " + DisplayName + " removed.");
        }

        private static bool FilesEqual(string a, string b)
        {
            var fa = new FileInfo(a);
            var fb = new FileInfo(b);
            return fa.Length == fb.Length && File.ReadAllBytes(a).SequenceEqual(File.ReadAllBytes(b));
        }
    }
}
