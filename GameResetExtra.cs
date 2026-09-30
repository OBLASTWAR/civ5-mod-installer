// GameResetExtra.cs -- backs MainForm's REDOWNLOAD CIV button.
//
// The last-resort fix for a Civ5 install nobody can untangle: deletes the
// whole game folder so Steam has to download every file again. Neither of
// Steam's own tools gets there. Verify integrity only restores stock files
// it knows about and ignores anything extra (old mods, a second or renamed
// EUI), and Steam's uninstall only removes the files in its manifest --
// the same extras survive, so a reinstall lands right back next to them.
//
// Only the game folder goes. Documents\My Games (saves, settings) is left
// alone apart from the cached gameplay DBs, which GraphicsCacheClear
// already knows are safe to wipe. Afterwards steam://validate/8930 has
// Steam notice every file missing and download the game again; the
// appmanifest in steamapps/ still marks it installed, so no reinstall
// prompt is needed.
//
// Deletion is guarded hard (see ResolveGameFolder): the path must be the
// exact Steam layout with CivilizationV.exe inside, and junctions/symlinks
// are unlinked, never followed, so a link inside the game folder can't
// take anything outside it down too.

using System;
using System.Collections.Generic;
using System.IO;

namespace KekModInstaller
{
    internal sealed class GameResetResult
    {
        public int FilesDeleted;
        public int FilesSkipped;
        public List<string> Leftovers = new List<string>();
        public bool FolderRemoved;
    }

    internal static class GameReset
    {
        public const string SteamAppId = "8930";
        private const string GameFolderName = "Sid Meier's Civilization V";

        // Returns the full path of the game folder to delete, or null with
        // a player-facing reason. Every check has to pass -- this is the
        // only recursive delete in the installer that isn't confined to a
        // folder the installer created itself.
        public static string ResolveGameFolder(out string reason)
        {
            reason = null;
            string game = InstallerCore.TryGetCiv5GameFolder();
            if (game == null || !Directory.Exists(game))
            {
                reason = "Couldn't find a Civilization V install with Steam.";
                return null;
            }
            return ValidateGameFolder(game, out reason);
        }

        internal static string ValidateGameFolder(string game, out string reason)
        {
            reason = null;
            var dir = new DirectoryInfo(Path.GetFullPath(game));
            DirectoryInfo common = dir.Parent;
            DirectoryInfo steamapps = common == null ? null : common.Parent;
            if (!string.Equals(dir.Name, GameFolderName, StringComparison.OrdinalIgnoreCase)
                || common == null || !string.Equals(common.Name, "common", StringComparison.OrdinalIgnoreCase)
                || steamapps == null || !string.Equals(steamapps.Name, "steamapps", StringComparison.OrdinalIgnoreCase))
            {
                reason = "\"" + dir.FullName + "\" isn't a standard Steam game folder -- not deleting it.";
                return null;
            }
            if ((dir.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                reason = "\"" + dir.FullName + "\" is a link to another folder -- not deleting it. Delete the game manually.";
                return null;
            }
            if (!File.Exists(Path.Combine(dir.FullName, "CivilizationV.exe"))
                && !File.Exists(Path.Combine(dir.FullName, "CivilizationV_DX11.exe")))
            {
                reason = "\"" + dir.FullName + "\" has no CivilizationV.exe -- not deleting it.";
                return null;
            }
            return dir.FullName;
        }

        // Size of what Steam will have to download again, for the confirm
        // dialog. Best effort -- unreadable folders just count as 0.
        public static long MeasureBytes(string root)
        {
            long total = 0;
            foreach (string file in EnumerateFilesNoLinks(root))
            {
                try { total += new FileInfo(file).Length; }
                catch (Exception) { }
            }
            return total;
        }

        // True when steamapps/ still holds Civ5's appmanifest, i.e. Steam
        // still thinks the game is installed and validate will re-download
        // it. Without it, validate does nothing and install is the way in.
        public static bool SteamStillTracksGame(string gameFolder)
        {
            string steamapps = Path.GetDirectoryName(Path.GetDirectoryName(gameFolder));
            return File.Exists(Path.Combine(steamapps, "appmanifest_" + SteamAppId + ".acf"));
        }

        public static GameResetResult DeleteGameFolder(string root, Action<string> log, Action<int> progress)
        {
            var result = new GameResetResult();

            log("Listing files in " + root + " ...");
            var files = new List<string>(EnumerateFilesNoLinks(root));
            log(files.Count + " files to delete.");

            int lastPct = -1;
            for (int i = 0; i < files.Count; i++)
            {
                try
                {
                    File.SetAttributes(files[i], FileAttributes.Normal);
                    File.Delete(files[i]);
                    result.FilesDeleted++;
                }
                catch (Exception)
                {
                    result.FilesSkipped++;
                    if (result.Leftovers.Count < 10)
                    {
                        result.Leftovers.Add(files[i]);
                    }
                }
                int pct = (int)((i + 1) * 100L / files.Count);
                if (pct != lastPct)
                {
                    progress(pct);
                    lastPct = pct;
                }
            }

            // Folders deepest first. Links were never descended into above;
            // Directory.Delete on a link removes the link, not its target.
            var dirs = new List<string>(EnumerateDirsNoLinks(root));
            dirs.Sort((a, b) => b.Length.CompareTo(a.Length));
            foreach (string d in dirs)
            {
                try
                {
                    new DirectoryInfo(d).Attributes = FileAttributes.Normal;
                    Directory.Delete(d, false);
                }
                catch (Exception)
                {
                    // Still holds a skipped file -- already counted above.
                }
            }
            try
            {
                Directory.Delete(root, false);
                result.FolderRemoved = true;
            }
            catch (Exception)
            {
                result.FolderRemoved = !Directory.Exists(root);
            }
            return result;
        }

        // Manual walk instead of Directory.GetFiles(AllDirectories): that
        // one follows junctions, and this must never leave the game folder.
        private static IEnumerable<string> EnumerateFilesNoLinks(string root)
        {
            var dirs = new List<string> { root };
            foreach (KeyValuePair<string, bool> d in WalkSubdirs(root))
            {
                if (!d.Value)
                {
                    dirs.Add(d.Key);
                }
            }
            foreach (string dir in dirs)
            {
                string[] files;
                try { files = Directory.GetFiles(dir); }
                catch (Exception) { continue; }
                foreach (string f in files)
                {
                    yield return f;
                }
            }
        }

        // Every subfolder of root, links included so they can be unlinked.
        private static IEnumerable<string> EnumerateDirsNoLinks(string root)
        {
            foreach (KeyValuePair<string, bool> d in WalkSubdirs(root))
            {
                yield return d.Key;
            }
        }

        // (path, isLink) for every folder under root. A link is listed but
        // never walked into.
        private static List<KeyValuePair<string, bool>> WalkSubdirs(string root)
        {
            var found = new List<KeyValuePair<string, bool>>();
            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                string[] subdirs;
                try { subdirs = Directory.GetDirectories(pending.Pop()); }
                catch (Exception) { continue; }
                foreach (string s in subdirs)
                {
                    bool isLink = true; // unreadable attributes: don't descend
                    try { isLink = (new DirectoryInfo(s).Attributes & FileAttributes.ReparsePoint) != 0; }
                    catch (Exception) { }
                    found.Add(new KeyValuePair<string, bool>(s, isLink));
                    if (!isLink)
                    {
                        pending.Push(s);
                    }
                }
            }
            return found;
        }
    }
}
