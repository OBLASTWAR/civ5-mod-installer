// ExtraModScan.cs -- finds folders under Assets/DLC and Assets/Maps that
// this installer doesn't recognize: not one of ModRegistry's own supported
// mods (or their bonus components), not an EUI variant folder (see
// EuiExtra), not stock Steam-delivered Civ5 DLC content. Surfaces them so a
// player who's accumulated a pile of half-remembered manually-installed mods
// over the years can see what's actually sitting in there and decide, per
// folder, to keep it, archive it (zipped in place, reversible), or delete it.
//
// Deliberately conservative about what counts as "stock": Civ5's own paid
// DLC content is, as far as every install we've checked shows, always named
// with a "DLC" prefix (e.g. "DLC_Y1_UnitPack") -- excluding that prefix means
// a wrong assumption here fails safe (real DLC just doesn't get listed,
// never gets suggested for deletion), rather than risking a player being
// shown their own paid content as "extra."

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace KekModInstaller
{
    internal enum ExtraFolderLocation { Dlc, Maps }

    internal class ExtraFolderInfo
    {
        public string FullPath;
        public string Name;
        public ExtraFolderLocation Location;
        public long SizeBytes;
    }

    internal static class ExtraModScan
    {
        // Stock Assets/DLC folders that don't follow the "DLC*" naming
        // convention -- confirmed against a clean Civ5 + Gods & Kings + Brave
        // New World install: "Expansion"/"Expansion2" are the two paid
        // expansions themselves, "Shared" and "Tablet" ship alongside them.
        // None of these are ever something a player installed themselves.
        private static readonly string[] KnownStockDlcFolders =
        {
            "Expansion", "Expansion2", "Shared", "Tablet",
        };

        public static List<ExtraFolderInfo> Scan()
        {
            var found = new List<ExtraFolderInfo>();
            string dlcRoot = InstallerCore.TryGetDlcFolder();
            if (dlcRoot == null || !Directory.Exists(dlcRoot))
            {
                return found;
            }

            ScanFolder(dlcRoot, ExtraFolderLocation.Dlc, found);

            string mapsFolder = Path.Combine(Path.GetDirectoryName(dlcRoot), "Maps");
            ScanFolder(mapsFolder, ExtraFolderLocation.Maps, found);

            return found;
        }

        private static void ScanFolder(string root, ExtraFolderLocation location, List<ExtraFolderInfo> found)
        {
            if (!Directory.Exists(root))
            {
                return;
            }
            foreach (string dir in Directory.GetDirectories(root))
            {
                string name = Path.GetFileName(dir);
                if (IsRecognized(name, location))
                {
                    continue;
                }

                var info = new ExtraFolderInfo();
                info.FullPath = dir;
                info.Name = name;
                info.Location = location;
                try
                {
                    info.SizeBytes = GetDirectorySize(new DirectoryInfo(dir));
                }
                catch (Exception)
                {
                    // best-effort -- an inaccessible folder still gets listed, just with size 0
                }
                found.Add(info);
            }
        }

        private static bool IsRecognized(string name, ExtraFolderLocation location)
        {
            if (location == ExtraFolderLocation.Dlc)
            {
                if (name.StartsWith("DLC", StringComparison.OrdinalIgnoreCase))
                {
                    return true; // stock Steam-delivered Civ5 DLC content
                }
                if (name.StartsWith("UI", StringComparison.OrdinalIgnoreCase))
                {
                    return true; // EuiExtra's namespace -- see EuiExtra.DetectInstalledVariantId
                }
                foreach (string stock in KnownStockDlcFolders)
                {
                    if (string.Equals(name, stock, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                foreach (ModDefinition mod in ModRegistry.All)
                {
                    if (MatchesGlob(name, mod.InstalledFolderGlob))
                    {
                        return true;
                    }
                }
                return false;
            }

            // Maps: Tournament Mod's Better Pangaea bonus is a loose .lua
            // file, not a folder (see TournamentMapExtra), so the recognized
            // folders here are kek-mod's Fish Map Script and Lekmod's Lekmap.
            return string.Equals(name, MapScriptExtra.FolderName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, LekmapExtra.FolderName, StringComparison.OrdinalIgnoreCase);
        }

        // Minimal glob support -- every InstalledFolderGlob in ModRegistry is
        // exactly "<prefix> *" (a literal prefix followed by "*"), the same
        // shape Directory.GetDirectories(root, glob) already relies on
        // elsewhere in this codebase; no need for full wildcard matching.
        private static bool MatchesGlob(string name, string glob)
        {
            if (glob.EndsWith("*"))
            {
                return name.StartsWith(glob.Substring(0, glob.Length - 1), StringComparison.OrdinalIgnoreCase);
            }
            return string.Equals(name, glob, StringComparison.OrdinalIgnoreCase);
        }

        private static long GetDirectorySize(DirectoryInfo di)
        {
            long total = 0;
            foreach (FileInfo f in di.GetFiles("*", SearchOption.AllDirectories))
            {
                total += f.Length;
            }
            return total;
        }

        // Zips the folder in place (same Assets/DLC or Assets/Maps parent)
        // and deletes the original -- rather than moving the folder
        // somewhere else, this sidesteps any question of whether Civ5's
        // loader might still pick it up from a different location: a .zip
        // file is never a directory, so nothing that scans Assets/DLC for
        // subfolders (which is how every mod this installer supports gets
        // mounted -- see ModCore's Assets/DLC/<name> layout) can ever find or
        // load it. Recoverable by hand any time -- just extract the zip back
        // into place. The archive includes the mod's own root folder as the
        // zip's single top-level entry (includeBaseDirectory: true) so
        // extracting it back into Assets/DLC or Assets/Maps reproduces the
        // original folder exactly, not its contents loose at the top level.
        //
        // If a same-named zip already exists, skips re-zipping (no
        // overwrite, no piling up timestamped copies) but still deletes the
        // folder -- the goal is getting it out of Civ5's scan path, and an
        // existing archive under this name already covers that.
        public static void Archive(ExtraFolderInfo info)
        {
            string parent = Path.GetDirectoryName(info.FullPath); // Assets/DLC or Assets/Maps
            string zipPath = Path.Combine(parent, info.Name + ".zip");
            if (!File.Exists(zipPath))
            {
                ZipFile.CreateFromDirectory(info.FullPath, zipPath, CompressionLevel.Optimal, true);
            }
            Directory.Delete(info.FullPath, true);
        }

        // Permanent -- callers must confirm with the player before calling
        // this, there is no undo.
        public static void Delete(ExtraFolderInfo info)
        {
            Directory.Delete(info.FullPath, true);
        }
    }
}
