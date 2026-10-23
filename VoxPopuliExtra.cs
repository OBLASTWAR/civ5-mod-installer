// VoxPopuliExtra.cs -- Vox Populi (+ its EUI) install/remove.
//
// The multiplayer VP + EUI modpack from the CivFanatics "VoxPopuli Modpacks"
// thread: one "<version>_VP-EUI.zip" per release in a public Google Drive
// folder. Drive has no manifest, so the folder's embedded listing page is
// scraped for (file ID, name, date). Downloads reuse Lekmod's Drive routine.
//
// Each zip holds three sibling folders for Assets/DLC: UI_bc1 (the EUI build
// VP needs -- it replaces whatever EUI is installed), VPUI and VP_MODPACK.
// None carries a version, so the installed release tag is kept in a marker
// file inside VP_MODPACK, and removing the folder removes the marker too.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace KekModInstaller
{
    internal static class VoxPopuliExtra
    {
        public const string DisplayName = "Vox Populi";
        public const string BundledEuiNote = "bundled with VP";

        private const string FolderId = "1k6rheDL2Q38oBF9je2ADw_dgVkqaG7Ey";
        private const string MarkerDir = "VP_MODPACK";
        private const string MarkerFile = "kekmod-vp-version.txt";
        private static readonly string[] PackFolders = { "UI_bc1", "VPUI", "VP_MODPACK" };

        private class Pack
        {
            public string FileId;
            public string FileName;
        }

        // Every pack in the Drive folder, newest first.
        public static List<ModRelease> ListReleases()
        {
            string html;
            using (var client = new HttpClient())
            {
                client.DefaultRequestHeaders.Add("User-Agent", "KekModInstaller");
                html = client.GetStringAsync("https://drive.google.com/embeddedfolderview?id=" + FolderId)
                    .GetAwaiter().GetResult();
            }

            var byTag = new Dictionary<string, ModRelease>();
            var entry = new Regex("id=\"entry-([^\"]+)\".*?flip-entry-title\">([^<]*)<.*?<div>([^<]*)</div>", RegexOptions.Singleline);
            foreach (Match m in entry.Matches(html))
            {
                string name = m.Groups[2].Value.Trim();
                Match t = Regex.Match(name, @"^(\d+(?:\.\d+)*)_VP.*\.zip$", RegexOptions.IgnoreCase);
                if (!t.Success || byTag.ContainsKey(t.Groups[1].Value))
                {
                    continue;
                }
                string tag = t.Groups[1].Value;
                var mr = new ModRelease();
                mr.Tag = tag;
                mr.DisplayExtra = name.Substring(tag.Length + 1, name.Length - tag.Length - 5) + ", " + m.Groups[3].Value.Trim();
                mr.Native = new Pack { FileId = m.Groups[1].Value, FileName = name };
                byTag[tag] = mr;
            }
            if (byTag.Count == 0)
            {
                throw new InvalidOperationException("No Vox Populi packs found in the Google Drive folder.");
            }
            List<ModRelease> list = byTag.Values.ToList();
            list.Sort((a, b) => CompareTags(b.Tag, a.Tag));
            return list;
        }

        private static int CompareTags(string a, string b)
        {
            int[] av = a.Split('.').Select(p => { int n; int.TryParse(p, out n); return n; }).ToArray();
            int[] bv = b.Split('.').Select(p => { int n; int.TryParse(p, out n); return n; }).ToArray();
            for (int i = 0; i < Math.Max(av.Length, bv.Length); i++)
            {
                int x = i < av.Length ? av[i] : 0;
                int y = i < bv.Length ? bv[i] : 0;
                if (x != y)
                {
                    return x.CompareTo(y);
                }
            }
            return 0;
        }

        // null when not installed, else the installed tag ("" if unknown).
        public static string DetectInstalledTag(string dlcRoot)
        {
            string dir = Path.Combine(dlcRoot, MarkerDir);
            if (!Directory.Exists(dir))
            {
                return null;
            }
            try
            {
                string marker = Path.Combine(dir, MarkerFile);
                return File.Exists(marker) ? File.ReadAllText(marker).Trim() : "";
            }
            catch (Exception)
            {
                return "";
            }
        }

        // Installs pack tagName (null: the newest), replacing any installed VP
        // and whatever EUI is in Assets/DLC. Nothing on disk changes until the
        // download has arrived and checked out.
        public static void Install(string dlcRoot, string tagName, Action<string> log)
        {
            log("Checking the Vox Populi Drive folder for " + (tagName == null ? "the newest pack" : "version " + tagName) + "...");
            List<ModRelease> releases = ListReleases();
            ModRelease release = tagName == null ? releases[0]
                : releases.FirstOrDefault(r => string.Equals(r.Tag, tagName, StringComparison.OrdinalIgnoreCase));
            if (release == null)
            {
                throw new InvalidOperationException("Vox Populi " + tagName + " isn't in the Drive folder. Pick another version.");
            }
            var pack = (Pack)release.Native;
            log("Vox Populi: " + release.Tag + (tagName != null ? " [selected version]" : ""));
            log("Downloading " + pack.FileName + " (large, this can take a few minutes)...");

            // Scratch inside Assets (same drive, so the final move is a
            // rename) but outside Assets/DLC, so a half-extracted copy is
            // never picked up by the game.
            string scratch = Path.Combine(Directory.GetParent(dlcRoot).FullName, ".kekmod-install-" + Guid.NewGuid().ToString("N"));
            string tempZip = LekmodModSource.DownloadFromGoogleDrive(pack.FileId, log);
            try
            {
                using (ZipArchive archive = ZipFile.OpenRead(tempZip))
                {
                    var tops = new HashSet<string>(archive.Entries
                        .Select(en => en.FullName.Replace('\\', '/').Split('/')[0])
                        .Where(n => n.Length > 0), StringComparer.Ordinal);
                    if (!tops.SetEquals(PackFolders))
                    {
                        throw new InvalidOperationException("Unexpected archive layout (top level: " + string.Join(", ", tops.ToArray()) + ").");
                    }
                }
                log("Extracting...");
                Directory.CreateDirectory(scratch);
                ZipFile.ExtractToDirectory(tempZip, scratch);

                foreach (string dir in Directory.GetDirectories(dlcRoot, "UI*")) // only one EUI may be active
                {
                    log("Replacing " + Path.GetFileName(dir) + " with VP's bundled EUI...");
                    Directory.Delete(dir, true);
                }
                foreach (string name in PackFolders.Skip(1))
                {
                    string old = Path.Combine(dlcRoot, name);
                    if (Directory.Exists(old))
                    {
                        log("Removing existing " + name + "...");
                        Directory.Delete(old, true);
                    }
                }
                foreach (string name in PackFolders)
                {
                    Directory.Move(Path.Combine(scratch, name), Path.Combine(dlcRoot, name));
                }
                File.WriteAllText(Path.Combine(dlcRoot, MarkerDir, MarkerFile), release.Tag);
            }
            finally
            {
                File.Delete(tempZip);
                if (Directory.Exists(scratch))
                {
                    try { Directory.Delete(scratch, true); } catch (Exception) { }
                }
            }
            log("DONE: " + DisplayName + " " + release.Tag + " installed.");
        }

        // Removes VPUI and VP_MODPACK, plus UI_bc1 when it's the pack's own EUI
        // (no EUI marker -- one installed from here is kept). Never throws -- a
        // locked file shouldn't block whatever triggered this.
        public static void Remove(string dlcRoot, Action<string> log)
        {
            foreach (string name in PackFolders)
            {
                string dir = Path.Combine(dlcRoot, name);
                if (!Directory.Exists(dir))
                {
                    continue;
                }
                if (name == "UI_bc1" && File.Exists(Path.Combine(dir, "kekmod-eui-version.txt")))
                {
                    continue;
                }
                try
                {
                    log("Removing " + name + "...");
                    Directory.Delete(dir, true);
                }
                catch (Exception ex)
                {
                    log(name + ": couldn't remove (" + ex.Message + ")");
                }
            }
            log("DONE: " + DisplayName + " removed.");
        }
    }
}
