// TournamentMapExtra.cs -- the "Better Pangaea" map script Tournament Mod's
// own official installer (update.ps1) never installs: it isn't bundled into
// the release zip at all (confirmed by unzipping a real release), only
// living loose in the repo's git tree, versioned only by filename -- e.g.
// Better_Pangaea_V4.1.lua, V5.2b.lua, V5.4.lua -- since there's no Releases
// API or tag for a loose file. "Latest" is derived by listing the repo's
// Maps/ directory and picking whichever entry parses to the highest version,
// using the same MAJOR.MINOR[letter] scheme Tournament Mod's own release
// tags use (see GitHubModSource's tag comparisons for the sibling case).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text.RegularExpressions;

namespace KekModInstaller
{
    [DataContract]
    internal class GhContentsEntry
    {
        [DataMember(Name = "name")]
        public string Name { get; set; }

        [DataMember(Name = "download_url")]
        public string DownloadUrl { get; set; }

        // "file" or "dir" -- only used by LekmapExtra, which (unlike
        // TournamentMapExtra) pulls every entry in the directory rather than
        // picking one, so it needs to skip a subdirectory if one ever shows
        // up. Optional: DataContractJsonSerializer leaves this null for any
        // payload that omits it, which is fine everywhere else that ignores it.
        [DataMember(Name = "type")]
        public string Type { get; set; }
    }

    internal static class TournamentMapExtra
    {
        private const string RepoOwner = "ImmoSS";
        private const string RepoName = "Civ5-Patch";
        private const string MapsPath = "Maps";
        private static readonly Regex NamePattern = new Regex(@"^Better_Pangaea_V(.+)\.lua$", RegexOptions.IgnoreCase);

        // Downloads and drops the latest Better_Pangaea_V*.lua into
        // Assets/Maps if no such file is already there. Deliberately
        // swallows every failure (offline, directory listing empty/changed,
        // write hiccup): this is a bonus, not part of Tournament Mod itself,
        // so it should never fail the main install. Mirrors MapScriptExtra's
        // "install once, never auto-update an existing copy" behavior --
        // whatever version got installed first stays until manually removed.
        public static void EnsureInstalled(string dlcRoot, Action<string> log)
        {
            try
            {
                string mapsFolder = MapsFolder(dlcRoot);
                string existing = FindLocalFile(mapsFolder);
                if (existing != null)
                {
                    log(Path.GetFileName(existing) + " already installed, skipping.");
                    return;
                }

                log("Checking for the latest Better Pangaea map...");
                GhContentsEntry latest = FindLatestUpstream();
                if (latest == null)
                {
                    log("Better Pangaea: no map file found upstream, skipping.");
                    return;
                }

                log("Installing " + latest.Name + "...");
                using (var client = new HttpClient())
                {
                    client.DefaultRequestHeaders.Add("User-Agent", "KekModInstaller");
                    byte[] data = client.GetByteArrayAsync(latest.DownloadUrl).GetAwaiter().GetResult();
                    Directory.CreateDirectory(mapsFolder);
                    File.WriteAllBytes(Path.Combine(mapsFolder, latest.Name), data);
                }
                log(latest.Name + " installed.");
            }
            catch (Exception ex)
            {
                log("Better Pangaea: couldn't install (" + ex.Message + ")");
            }
        }

        // Removes whichever Better_Pangaea_V*.lua is present in Assets/Maps.
        // Never throws -- a missing file or locked handle shouldn't fail the
        // overall uninstall.
        public static void Remove(string dlcRoot, Action<string> log)
        {
            try
            {
                string existing = FindLocalFile(MapsFolder(dlcRoot));
                if (existing != null)
                {
                    log("Removing " + Path.GetFileName(existing) + "...");
                    File.Delete(existing);
                }
            }
            catch (Exception ex)
            {
                log("Better Pangaea: couldn't remove (" + ex.Message + ")");
            }
        }

        // Surfaced as its own pseudo-entry in the multi-mod [INSTALLED] line
        // -- same treatment as MapScriptExtra's Fish Map Script.
        public static DetectedModInstall DetectInstalled(string dlcRoot)
        {
            string existing = FindLocalFile(MapsFolder(dlcRoot));
            if (existing == null)
            {
                return null;
            }
            string fileName = Path.GetFileName(existing);
            var d = new DetectedModInstall();
            d.ModId = "betterpangaea";
            d.DisplayName = "Better Pangaea";
            d.FolderNames = new List<string> { fileName };
            Match m = NamePattern.Match(fileName);
            d.VersionLabel = m.Success ? "V" + m.Groups[1].Value : null;
            return d;
        }

        // Matches by prefix/glob rather than an exact filename, since
        // whichever version got installed (possibly an older one than
        // upstream's current latest) still needs to be found for
        // Remove/DetectInstalled.
        private static string FindLocalFile(string mapsFolder)
        {
            if (!Directory.Exists(mapsFolder))
            {
                return null;
            }
            return Directory.GetFiles(mapsFolder, "Better_Pangaea_V*.lua").FirstOrDefault();
        }

        // Lists the repo's Maps/ directory via the GitHub Contents API and
        // returns whichever Better_Pangaea_V*.lua entry parses to the
        // highest version -- there's no Releases API for a loose file, so
        // "latest" has to be derived from the directory listing itself.
        private static GhContentsEntry FindLatestUpstream()
        {
            string url = "https://api.github.com/repos/" + RepoOwner + "/" + RepoName + "/contents/" + MapsPath;
            List<GhContentsEntry> entries;
            using (var client = new HttpClient())
            {
                client.DefaultRequestHeaders.Add("User-Agent", "KekModInstaller");
                client.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
                byte[] body = client.GetByteArrayAsync(url).GetAwaiter().GetResult();
                var serializer = new DataContractJsonSerializer(typeof(List<GhContentsEntry>));
                using (var stream = new MemoryStream(body))
                {
                    entries = (List<GhContentsEntry>)serializer.ReadObject(stream);
                }
            }
            if (entries == null)
            {
                return null;
            }

            GhContentsEntry best = null;
            int[] bestVersion = null;
            string bestSuffix = null;
            foreach (GhContentsEntry entry in entries)
            {
                Match m = NamePattern.Match(entry.Name ?? "");
                if (!m.Success)
                {
                    continue;
                }
                int[] version;
                string suffix;
                ParseMapVersion(m.Groups[1].Value, out version, out suffix);
                if (best == null || CompareVersion(version, suffix, bestVersion, bestSuffix) > 0)
                {
                    best = entry;
                    bestVersion = version;
                    bestSuffix = suffix;
                }
            }
            return best;
        }

        // "5.2b" -> ([5,2], "b"); "5.4" -> ([5,4], ""). Falls back to an
        // empty version (sorts lowest) for anything that doesn't parse, so a
        // weirdly-named file can't crash the comparison, just never wins it.
        private static void ParseMapVersion(string raw, out int[] version, out string suffix)
        {
            Match m = Regex.Match(raw, @"^(\d+(?:\.\d+)*)([a-zA-Z]*)$");
            if (!m.Success)
            {
                version = new int[0];
                suffix = raw;
                return;
            }
            string[] parts = m.Groups[1].Value.Split('.');
            version = new int[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                int.TryParse(parts[i], out version[i]);
            }
            suffix = m.Groups[2].Value;
        }

        private static int CompareVersion(int[] a, string aSuffix, int[] b, string bSuffix)
        {
            int len = Math.Max(a.Length, b.Length);
            for (int i = 0; i < len; i++)
            {
                int av = i < a.Length ? a[i] : 0;
                int bv = i < b.Length ? b[i] : 0;
                if (av != bv)
                {
                    return av.CompareTo(bv);
                }
            }
            return string.CompareOrdinal(aSuffix ?? "", bSuffix ?? "");
        }

        private static string MapsFolder(string dlcRoot)
        {
            string assetsFolder = Path.GetDirectoryName(dlcRoot); // DLC's parent
            return Path.Combine(assetsFolder, "Maps");
        }
    }
}
