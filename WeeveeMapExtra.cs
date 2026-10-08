// WeeveeMapExtra.cs -- SnowVe's map script, "Weevee Map" (brunotho's
// Weevee-vX.Y.Z-Map GitHub repo): one "Weevee - vX.Y.Z.lua" map plus the
// DEF*W*.lua helpers it include()s by name, all loose in the repo root. No
// releases or tags -- the version lives only in that map file's name.
//
// Two things make this unlike Lekmap/Better Pangaea's "install once, never
// update":
//
//  * The repo is RENAMED on every version bump (its own VERSION-BUMP.txt:
//    repo name = "Weevee-vX.Y.Z-Map"), so it's addressed by GitHub's numeric
//    repository ID, which survives renames, instead of owner/name.
//
//  * Players coordinate on an exact map version, and include() resolves
//    DEF*W*.lua by file name across all of Assets/Maps -- two copies side by
//    side (an old play copy next to a new one) means whichever the game
//    finds first wins. So EnsureInstalled replaces an out-of-date copy and
//    removes every other Weevee copy. It installs into
//    "Weevee-vX.Y.Z-Map-main", the exact folder GitHub's "Download ZIP"
//    gives players who install it by hand, so those are recognized as-is.
//    Folders holding a .git directory are never touched: that's the map
//    author's own working copy ("Weevee-v11.08-Map-main", frozen name),
//    which can have unpushed work in it.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.Serialization.Json;
using System.Text.RegularExpressions;

namespace KekModInstaller
{
    internal static class WeeveeMapExtra
    {
        // brunotho/Weevee-v11.0.16-Map as of 2026-10; stable across renames.
        private const long RepoId = 1354734621;
        internal const string DisplayName = "Weevee Map";
        // Internal, not private: ExtraModScan matches Assets/Maps folders
        // against this too.
        internal const string FolderGlob = "Weevee-v*-Map*";

        private static readonly Regex MapFilePattern = new Regex(@"^Weevee - v(.+)\.lua$", RegexOptions.IgnoreCase);

        // Installs the upstream version unless a copy of that same version
        // is already in place, then clears out every other (non-git) copy.
        // Best-effort like every other bonus map: never fails the SnowVe
        // install itself.
        public static void EnsureInstalled(string dlcRoot, Action<string> log)
        {
            try
            {
                string mapsFolder = MapsFolder(dlcRoot);

                log("Checking for " + DisplayName + "...");
                List<GhContentsEntry> files = FetchDirectoryListing()
                    .Where(en => !string.IsNullOrEmpty(en.DownloadUrl) && (en.Type == null || en.Type == "file")
                        && en.Name.EndsWith(".lua", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                string version = VersionFromNames(files.Select(en => en.Name));
                if (version == null)
                {
                    log(DisplayName + ": no \"Weevee - v*.lua\" found upstream, skipping.");
                    return;
                }

                string folderName = "Weevee-v" + version + "-Map-main";
                string current = InstalledFolders(mapsFolder)
                    .FirstOrDefault(d => string.Equals(DetectVersion(d), version, StringComparison.OrdinalIgnoreCase));
                if (current != null)
                {
                    log(DisplayName + " v" + version + " already installed (" + Path.GetFileName(current) + ").");
                }
                else
                {
                    log("Installing " + DisplayName + " v" + version + " (" + files.Count + " files)...");
                    string targetDir = Path.Combine(mapsFolder, folderName);
                    string tempDir = Path.Combine(mapsFolder, ".kekmod-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(tempDir);
                    try
                    {
                        using (var client = new HttpClient())
                        {
                            client.DefaultRequestHeaders.Add("User-Agent", "KekModInstaller");
                            foreach (GhContentsEntry entry in files)
                            {
                                byte[] data = client.GetByteArrayAsync(entry.DownloadUrl).GetAwaiter().GetResult();
                                File.WriteAllBytes(Path.Combine(tempDir, entry.Name), data);
                            }
                        }
                        if (Directory.Exists(targetDir))
                        {
                            Directory.Delete(targetDir, true); // same name, wrong contents
                        }
                        Directory.Move(tempDir, targetDir);
                    }
                    finally
                    {
                        try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch (Exception) { }
                    }
                    current = targetDir;
                    log(DisplayName + " v" + version + " installed.");
                }

                foreach (string dir in InstalledFolders(mapsFolder))
                {
                    if (!string.Equals(dir, current, StringComparison.OrdinalIgnoreCase) && !IsGitCheckout(dir))
                    {
                        log("Removing old " + Path.GetFileName(dir) + "...");
                        Directory.Delete(dir, true);
                    }
                }
            }
            catch (Exception ex)
            {
                log(DisplayName + ": couldn't install (" + ex.Message + ")");
            }
        }

        // Upstream version, e.g. "v11.0.16" -- same form as DetectInstalled's
        // VersionLabel. Null if no map file is found.
        public static string FetchLatestVersion()
        {
            string version = VersionFromNames(FetchDirectoryListing().Select(en => en.Name));
            return version == null ? null : "v" + version;
        }

        private static string VersionFromNames(IEnumerable<string> names)
        {
            return names.Select(n => MapFilePattern.Match(n ?? ""))
                .Where(m => m.Success).Select(m => m.Groups[1].Value).FirstOrDefault();
        }

        public static void Remove(string dlcRoot, Action<string> log)
        {
            try
            {
                foreach (string dir in InstalledFolders(MapsFolder(dlcRoot)))
                {
                    if (IsGitCheckout(dir))
                    {
                        log("Leaving " + Path.GetFileName(dir) + " (git working copy).");
                        continue;
                    }
                    log("Removing " + Path.GetFileName(dir) + "...");
                    Directory.Delete(dir, true);
                }
            }
            catch (Exception ex)
            {
                log(DisplayName + ": couldn't remove (" + ex.Message + ")");
            }
        }

        public static DetectedModInstall DetectInstalled(string dlcRoot)
        {
            List<string> dirs = InstalledFolders(MapsFolder(dlcRoot));
            if (dirs.Count == 0)
            {
                return null;
            }
            var d = new DetectedModInstall();
            d.ModId = "weeveemap";
            d.DisplayName = DisplayName;
            d.FolderNames = dirs.Select(Path.GetFileName).ToList();
            // Newest wins if several are lying around (a git working copy
            // alongside an installed one).
            string version = dirs.Select(DetectVersion).Where(v => v != null)
                .OrderByDescending(v => v, Comparer<string>.Create(CompareVersions)).FirstOrDefault();
            d.VersionLabel = version != null ? "v" + version : null;
            return d;
        }

        // Only folders that actually hold a Weevee map file -- the glob
        // alone could match something unrelated.
        private static List<string> InstalledFolders(string mapsFolder)
        {
            if (!Directory.Exists(mapsFolder))
            {
                return new List<string>();
            }
            return Directory.GetDirectories(mapsFolder, FolderGlob)
                .Where(d => DetectVersion(d) != null).OrderBy(d => d).ToList();
        }

        private static string DetectVersion(string dir)
        {
            try
            {
                foreach (string file in Directory.GetFiles(dir, "Weevee - v*.lua"))
                {
                    Match m = MapFilePattern.Match(Path.GetFileName(file));
                    if (m.Success)
                    {
                        return m.Groups[1].Value;
                    }
                }
            }
            catch (Exception) { }
            return null;
        }

        private static bool IsGitCheckout(string dir)
        {
            return Directory.Exists(Path.Combine(dir, ".git")) || File.Exists(Path.Combine(dir, ".git"));
        }

        // "11.0.16" vs "11.08": numeric per dot-separated part, missing
        // parts count as 0, non-numeric parts as 0.
        private static int CompareVersions(string a, string b)
        {
            string[] ap = a.Split('.'), bp = b.Split('.');
            for (int i = 0; i < Math.Max(ap.Length, bp.Length); i++)
            {
                int x = 0, y = 0;
                if (i < ap.Length) int.TryParse(ap[i], out x);
                if (i < bp.Length) int.TryParse(bp[i], out y);
                if (x != y)
                {
                    return x.CompareTo(y);
                }
            }
            return 0;
        }

        private static List<GhContentsEntry> FetchDirectoryListing()
        {
            string url = "https://api.github.com/repositories/" + RepoId + "/contents/";
            using (var client = new HttpClient())
            {
                client.DefaultRequestHeaders.Add("User-Agent", "KekModInstaller");
                client.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
                byte[] body = client.GetByteArrayAsync(url).GetAwaiter().GetResult();
                var serializer = new DataContractJsonSerializer(typeof(List<GhContentsEntry>));
                using (var stream = new MemoryStream(body))
                {
                    var entries = (List<GhContentsEntry>)serializer.ReadObject(stream);
                    return entries ?? new List<GhContentsEntry>();
                }
            }
        }

        private static string MapsFolder(string dlcRoot)
        {
            string assetsFolder = Path.GetDirectoryName(dlcRoot); // DLC's parent
            return Path.Combine(assetsFolder, "Maps");
        }
    }
}
