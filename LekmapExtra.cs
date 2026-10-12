// LekmapExtra.cs -- Lekmod's own custom mapscript bundle ("Lekmap": several
// map types -- Continents, Pangaea, Archipelago, Donut, Four Corners, etc. --
// plus shared HB*.lua helper code and a config/table file). Per Lekmod's own
// docs (docs/installation.md, "Lekmap Installation"), this is a SEPARATE
// download from LEKMOD itself, dropped into Assets/Maps rather than
// Assets/DLC -- Lekmod's reference installer (LekmodInstaller/installer.py)
// doesn't automate this step at all, players do it by hand.
//
// Unlike Better Pangaea (a single loose .lua, versioned by filename) or Fish
// Map Script (its own repo with real GitHub Releases), Lekmap has no
// published release channel of any kind -- no tag, no manifest. The only
// place it actually lives is the Lekmap/ folder in Lekmod's own GitHub repo
// (confirmed: same repo LEKMOD/LEKMOD_DLL/LekmodInstaller are cloned from),
// so this fetches that folder's current contents directly via the GitHub
// Contents API -- same mechanism TournamentMapExtra already uses for Better
// Pangaea, just pulling every file in the directory instead of picking the
// single best-versioned one. Entirely best-effort and "install once, never
// auto-update" like both of those siblings: nothing here should ever fail
// the main Lekmod install.
//
// Lekmap DOES have a real version number players are told about -- LEKMOD's
// own main-page doc currently advertises "Download latest Map version
// (v5.2)" -- but that's a hand-maintained Google Doc with no stable API, not
// something this installer should scrape at runtime. It lines up exactly
// with LekmapPangaeaFractalv5.2.lua's own filename suffix (Pangaea Fractal
// being the flagship script), so DetectVersion below reads the version off
// that file instead: same regex-on-versioned-filename idiom
// TournamentMapExtra already uses for Better_Pangaea_V*.lua, and it self-
// updates whenever that file's suffix bumps without needing a new installer
// build.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.Serialization.Json;
using System.Text.RegularExpressions;

namespace KekModInstaller
{
    internal static class LekmapExtra
    {
        private const string RepoOwner = "EnormousApplePie";
        private const string RepoName = "Lekmod";
        private const string DirPath = "Lekmap";
        private const string FolderName = "Lekmap";

        // Installed as Assets/Maps/"Lekmap v6.3" etc., exactly like the
        // Lekmap zip's own folder. Civ identifies the lobby's map by its full
        // path (Assets\Maps\Lekmap v6.3\LekmapPangaeaFractalv6.3.lua), so a
        // copy under any other folder name shows up red, its custom map
        // options never arrive (Map.GetCustomOption returns nil) and map
        // generation fails at game start. Plain "Lekmap" is only used when
        // the version can't be read, and is still recognized because older
        // installer builds wrote it.
        private static string FolderFor(string version)
        {
            return version == null ? FolderName : FolderName + " " + version;
        }

        // Internal, not private: ExtraModScan uses this so its "is this an
        // extra/unrecognized folder" check never drifts out of sync with the
        // names this installer actually writes.
        internal static bool IsLekmapFolderName(string name)
        {
            return string.Equals(name, FolderName, StringComparison.OrdinalIgnoreCase)
                || (name ?? "").StartsWith(FolderName + " v", StringComparison.OrdinalIgnoreCase);
        }

        private static List<string> InstalledDirs(string mapsFolder)
        {
            if (!Directory.Exists(mapsFolder))
            {
                return new List<string>();
            }
            return Directory.GetDirectories(mapsFolder)
                .Where(dir => IsLekmapFolderName(Path.GetFileName(dir)))
                .ToList();
        }

        // Downloads every file in the repo's Lekmap/ directory into
        // Assets/Maps/"Lekmap <version>", replacing any existing Lekmap
        // folder unless it's already exactly that. Deliberately swallows every failure
        // (offline, directory listing empty/changed, write hiccup): this is
        // a bonus, not part of Lekmod itself, so it should never fail the
        // main install.
        public static void EnsureInstalled(string dlcRoot, Action<string> log)
        {
            try
            {
                string mapsFolder = MapsFolder(dlcRoot);

                log("Checking for Lekmap...");
                List<GhContentsEntry> files = FetchDirectoryListing()
                    .Where(en => !string.IsNullOrEmpty(en.DownloadUrl) && (en.Type == null || en.Type == "file"))
                    .ToList();
                if (files.Count == 0)
                {
                    log(FolderName + ": no files found upstream, skipping.");
                    return;
                }
                string upstreamVersion = VersionFromNames(files.Select(en => en.Name));
                string targetDir = Path.Combine(mapsFolder, FolderFor(upstreamVersion));
                List<string> existing = InstalledDirs(mapsFolder);
                if (existing.Count == 1
                    && Path.GetFileName(existing[0]) == Path.GetFileName(targetDir)
                    && (upstreamVersion == null || MapVersion.Same(DetectVersion(targetDir), upstreamVersion))
                    && HasCrlfLineEndings(targetDir))
                {
                    log(FolderName + (upstreamVersion == null ? "" : " " + upstreamVersion) + " already installed.");
                    return;
                }

                log("Installing " + FolderName + (upstreamVersion == null ? "" : " " + upstreamVersion)
                    + " (" + files.Count + " files)...");
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
                            File.WriteAllBytes(Path.Combine(tempDir, entry.Name), ToCrlf(data));
                        }
                    }
                    foreach (string dir in existing)
                    {
                        Directory.Delete(dir, true); // older version or folder name
                    }
                    Directory.Move(tempDir, targetDir);
                }
                finally
                {
                    try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch (Exception) { }
                }
                log(Path.GetFileName(targetDir) + " installed.");
            }
            catch (Exception ex)
            {
                log(FolderName + ": couldn't install (" + ex.Message + ")");
            }
        }

        // Upstream version, e.g. "v6.1" -- same form as DetectVersion. Null
        // if the flagship file isn't there to read it from.
        public static string FetchLatestVersion()
        {
            return VersionFromNames(FetchDirectoryListing().Select(en => en.Name));
        }

        // Removes every Lekmap folder from Assets/Maps. Never
        // throws -- a missing folder or locked file shouldn't fail the
        // overall uninstall.
        public static void Remove(string dlcRoot, Action<string> log)
        {
            try
            {
                foreach (string dir in InstalledDirs(MapsFolder(dlcRoot)))
                {
                    log("Removing " + Path.GetFileName(dir) + "...");
                    Directory.Delete(dir, true);
                }
            }
            catch (Exception ex)
            {
                log(FolderName + ": couldn't remove (" + ex.Message + ")");
            }
        }

        // The repo stores Lekmap with LF line endings, but the Lekmap zip
        // everyone else installs has CRLF. Civ compares map script bytes in
        // the MP lobby, so an LF copy shows up red against a host with the
        // zip version even when the content is identical. Converting every
        // bare LF to CRLF reproduces the zip byte-for-byte (verified against
        // "Lekmap v6.3.zip": all 22 files match).
        private static byte[] ToCrlf(byte[] data)
        {
            var output = new List<byte>(data.Length + data.Length / 20);
            for (int i = 0; i < data.Length; i++)
            {
                if (data[i] == (byte)'\n' && (i == 0 || data[i - 1] != (byte)'\r'))
                {
                    output.Add((byte)'\r');
                }
                output.Add(data[i]);
            }
            return output.ToArray();
        }

        // False for an install written by an older installer build that
        // kept the repo's LF endings (see ToCrlf) -- reinstalls it so the
        // file finally matches what other players have.
        private static bool HasCrlfLineEndings(string targetDir)
        {
            try
            {
                string flagship = Directory.GetFiles(targetDir, "LekmapPangaeaFractalv*.lua").FirstOrDefault();
                if (flagship == null)
                {
                    return true; // nothing to judge by; leave it alone
                }
                byte[] data = File.ReadAllBytes(flagship);
                int lf = Array.IndexOf(data, (byte)'\n');
                return lf < 0 || (lf > 0 && data[lf - 1] == (byte)'\r');
            }
            catch (Exception)
            {
                return true;
            }
        }

        private static readonly Regex FractalVersionPattern = new Regex(@"^LekmapPangaeaFractalv(.+)\.lua$", RegexOptions.IgnoreCase);

        // Surfaced as its own pseudo-entry in the multi-mod install state --
        // same treatment as Fish Map Script/Better Pangaea.
        public static DetectedModInstall DetectInstalled(string dlcRoot)
        {
            List<string> dirs = InstalledDirs(MapsFolder(dlcRoot));
            if (dirs.Count == 0)
            {
                return null;
            }
            var d = new DetectedModInstall();
            d.ModId = "lekmap";
            d.DisplayName = FolderName;
            d.FolderNames = dirs.Select(Path.GetFileName).ToList();
            d.VersionLabel = DetectVersion(dirs[0]);
            // An install from an older build (plain "Lekmap" folder, LF
            // endings) is unusable in MP lobbies even at the latest version;
            // a label that never matches upstream puts the UPDATE button on
            // the map row so one click reinstalls it properly.
            if (d.VersionLabel != null
                && (dirs.Count > 1 || Path.GetFileName(dirs[0]) != FolderFor(d.VersionLabel) || !HasCrlfLineEndings(dirs[0])))
            {
                d.VersionLabel += " (needs repair)";
            }
            return d;
        }

        // See the file header -- reads Lekmap's overall version off
        // LekmapPangaeaFractalv*.lua's own filename suffix, since nothing
        // else in the bundle publishes one. Null if that file isn't present
        // (an older or hand-edited install) or its name doesn't end in a
        // version.
        private static string DetectVersion(string targetDir)
        {
            try
            {
                return VersionFromNames(Directory.GetFiles(targetDir, "LekmapPangaeaFractalv*.lua").Select(Path.GetFileName));
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string VersionFromNames(IEnumerable<string> names)
        {
            foreach (string name in names)
            {
                Match m = FractalVersionPattern.Match(name ?? "");
                if (m.Success)
                {
                    return "v" + m.Groups[1].Value;
                }
            }
            return null;
        }

        private static List<GhContentsEntry> FetchDirectoryListing()
        {
            string url = "https://api.github.com/repos/" + RepoOwner + "/" + RepoName + "/contents/" + DirPath;
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
