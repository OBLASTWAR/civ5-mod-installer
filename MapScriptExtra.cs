// MapScriptExtra.cs -- the "Fish Map Script" bonus install, extracted
// verbatim from the original single-mod InstallerCore. Not really "part of"
// kek-mod so much as a separate always-on extra that happens to only apply
// there -- pulled in alongside kek-mod if the player doesn't already have it,
// own repo, own release cadence (no dev/prod, no beta channel, just
// "latest"), entirely best-effort (swallows every failure).

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;

namespace KekModInstaller
{
    internal static class MapScriptExtra
    {
        // FolderName is deliberately "Fish Map Script" -- the exact name
        // already used by existing community installs -- NOT the
        // "pangea-stratbal" repo's own label. Renaming it would make players
        // who already have it look like they're missing a "different" map
        // and get a redundant second copy installed alongside their real one.
        private const string RepoOwner = "OBLASTWAR";
        private const string RepoName = "pangea-stratbal";
        // Internal, not private: ExtraModScan reads this directly so its
        // "is this an extra/unrecognized folder" check never drifts out of
        // sync with the one place this name is actually defined.
        internal const string FolderName = "Fish Map Script";

        // Downloads and drops FolderName into Assets/Maps if it isn't already
        // there. Deliberately swallows every failure (offline, repo/release
        // missing, extraction hiccup): this is a bonus, not part of any mod
        // itself, so it should never fail the main install.
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

                log("Installing " + FolderName + "...");
                List<GhRelease> releases = GitHubModSource.FetchReleases(RepoOwner, RepoName);
                GhRelease release = releases[0]; // newest
                GhAsset asset = release.Assets != null ? release.Assets.FirstOrDefault() : null;
                if (asset == null)
                {
                    log(FolderName + ": release " + release.TagName + " has no assets, skipping.");
                    return;
                }

                string zipPath = GitHubModSource.DownloadAsset(asset);
                Directory.CreateDirectory(mapsFolder);
                ZipFile.ExtractToDirectory(zipPath, mapsFolder);
                File.Delete(zipPath);
                log(FolderName + " " + release.TagName + " installed.");
            }
            catch (Exception ex)
            {
                log(FolderName + ": couldn't install (" + ex.Message + ")");
            }
        }

        // Removes the Fish Map Script folder from Assets/Maps if present.
        // Never throws -- a missing folder or locked file shouldn't fail the
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

        // Surfaced as its own pseudo-entry in the multi-mod [INSTALLED] line
        // -- it isn't a "mod" in the ModRegistry sense, but it's a visible
        // extra players installed and should show up the same way.
        public static DetectedModInstall DetectInstalled(string dlcRoot)
        {
            string targetDir = Path.Combine(MapsFolder(dlcRoot), FolderName);
            if (!Directory.Exists(targetDir))
            {
                return null;
            }
            var d = new DetectedModInstall();
            d.ModId = "fishmapscript";
            d.DisplayName = FolderName;
            d.FolderNames = new List<string> { FolderName };
            d.VersionLabel = DetectVersion(targetDir);
            return d;
        }

        // The folder name itself is always just "Fish Map Script" regardless
        // of version, unlike a mod's own versioned folder name -- but the
        // release's .modinfo file is named e.g. "VFishMapScriptv1.0.modinfo",
        // so the version can be read off of that instead. Null if no
        // .modinfo file is present or its name doesn't end in a version.
        private static string DetectVersion(string targetDir)
        {
            try
            {
                string modinfo = Directory.GetFiles(targetDir, "*.modinfo").FirstOrDefault();
                if (modinfo == null)
                {
                    return null;
                }
                Match m = Regex.Match(Path.GetFileNameWithoutExtension(modinfo), @"(\d+(?:\.\d+)*)$");
                return m.Success ? "v" + m.Groups[1].Value : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string MapsFolder(string dlcRoot)
        {
            string assetsFolder = Path.GetDirectoryName(dlcRoot); // DLC's parent
            return Path.Combine(assetsFolder, "Maps");
        }
    }
}
