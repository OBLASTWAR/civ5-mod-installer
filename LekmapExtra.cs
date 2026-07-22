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
        // Internal, not private: ExtraModScan reads this directly so its
        // "is this an extra/unrecognized folder" check never drifts out of
        // sync with the one place this name is actually defined.
        internal const string FolderName = "Lekmap";

        // Downloads every file in the repo's Lekmap/ directory into
        // Assets/Maps/Lekmap if that folder isn't already there. Deliberately
        // swallows every failure (offline, directory listing empty/changed,
        // write hiccup): this is a bonus, not part of Lekmod itself, so it
        // should never fail the main install.
        public static void EnsureInstalled(string dlcRoot, Action<string> log)
        {
            try
            {
                string mapsFolder = MapsFolder(dlcRoot);
                string targetDir = Path.Combine(mapsFolder, FolderName);
                if (Directory.Exists(targetDir))
                {
                    log(FolderName + " already installed, skipping.");
                    return;
                }

                log("Checking for Lekmap...");
                List<GhContentsEntry> entries = FetchDirectoryListing();
                List<GhContentsEntry> files = entries
                    .Where(en => !string.IsNullOrEmpty(en.DownloadUrl) && (en.Type == null || en.Type == "file"))
                    .ToList();
                if (files.Count == 0)
                {
                    log(FolderName + ": no files found upstream, skipping.");
                    return;
                }

                log("Installing " + FolderName + " (" + files.Count + " files)...");
                Directory.CreateDirectory(targetDir);
                using (var client = new HttpClient())
                {
                    client.DefaultRequestHeaders.Add("User-Agent", "KekModInstaller");
                    foreach (GhContentsEntry entry in files)
                    {
                        byte[] data = client.GetByteArrayAsync(entry.DownloadUrl).GetAwaiter().GetResult();
                        File.WriteAllBytes(Path.Combine(targetDir, entry.Name), data);
                    }
                }
                log(FolderName + " installed.");
            }
            catch (Exception ex)
            {
                log(FolderName + ": couldn't install (" + ex.Message + ")");
            }
        }

        // Removes the Lekmap folder from Assets/Maps if present. Never
        // throws -- a missing folder or locked file shouldn't fail the
        // overall uninstall.
        public static void Remove(string dlcRoot, Action<string> log)
        {
            try
            {
                string targetDir = Path.Combine(MapsFolder(dlcRoot), FolderName);
                if (Directory.Exists(targetDir))
                {
                    log("Removing " + FolderName + "...");
                    Directory.Delete(targetDir, true);
                }
            }
            catch (Exception ex)
            {
                log(FolderName + ": couldn't remove (" + ex.Message + ")");
            }
        }

        private static readonly Regex FractalVersionPattern = new Regex(@"^LekmapPangaeaFractalv(.+)\.lua$", RegexOptions.IgnoreCase);

        // Surfaced as its own pseudo-entry in the multi-mod install state --
        // same treatment as Fish Map Script/Better Pangaea.
        public static DetectedModInstall DetectInstalled(string dlcRoot)
        {
            string targetDir = Path.Combine(MapsFolder(dlcRoot), FolderName);
            if (!Directory.Exists(targetDir))
            {
                return null;
            }
            var d = new DetectedModInstall();
            d.ModId = "lekmap";
            d.DisplayName = FolderName;
            d.FolderNames = new List<string> { FolderName };
            d.VersionLabel = DetectVersion(targetDir);
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
                string file = Directory.GetFiles(targetDir, "LekmapPangaeaFractalv*.lua").FirstOrDefault();
                if (file == null)
                {
                    return null;
                }
                Match m = FractalVersionPattern.Match(Path.GetFileName(file));
                return m.Success ? "v" + m.Groups[1].Value : null;
            }
            catch (Exception)
            {
                return null;
            }
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
