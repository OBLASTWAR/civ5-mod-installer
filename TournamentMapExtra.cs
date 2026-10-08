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

        // Drops the latest Better_Pangaea_V*.lua into Assets/Maps, replacing
        // any older Better_Pangaea_V*.lua already there. Deliberately
        // swallows every failure (offline, directory listing empty/changed,
        // write hiccup): this is a bonus, not part of Tournament Mod itself,
        // so it should never fail the main install.
        public static void EnsureInstalled(string dlcRoot, Action<string> log)
        {
            try
            {
                string mapsFolder = MapsFolder(dlcRoot);

                log("Checking for the latest Better Pangaea map...");
                GhContentsEntry latest = FindLatestUpstream();
                if (latest == null)
                {
                    log("Better Pangaea: no map file found upstream, skipping.");
                    return;
                }

                if (File.Exists(Path.Combine(mapsFolder, latest.Name)))
                {
                    log(latest.Name + " already installed.");
                }
                else
                {
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

                foreach (string old in FindLocalFiles(mapsFolder))
                {
                    if (!string.Equals(Path.GetFileName(old), latest.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        log("Removing old " + Path.GetFileName(old) + "...");
                        File.Delete(old);
                    }
                }
            }
            catch (Exception ex)
            {
                log("Better Pangaea: couldn't install (" + ex.Message + ")");
            }
        }

        // Latest upstream version, e.g. "V5.4" -- same form as
        // DetectInstalled's VersionLabel. Null if none found.
        public static string FetchLatestVersion()
        {
            GhContentsEntry latest = FindLatestUpstream();
            return latest == null ? null : "V" + NamePattern.Match(latest.Name).Groups[1].Value;
        }

        // Removes whichever Better_Pangaea_V*.lua is present in Assets/Maps.
        // Never throws -- a missing file or locked handle shouldn't fail the
        // overall uninstall.
        public static void Remove(string dlcRoot, Action<string> log)
        {
            try
            {
                foreach (string existing in FindLocalFiles(MapsFolder(dlcRoot)))
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
            return FindLocalFiles(mapsFolder).FirstOrDefault();
        }

        private static string[] FindLocalFiles(string mapsFolder)
        {
            if (!Directory.Exists(mapsFolder))
            {
                return new string[0];
            }
            return Directory.GetFiles(mapsFolder, "Better_Pangaea_V*.lua");
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
