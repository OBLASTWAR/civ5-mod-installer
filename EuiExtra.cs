// EuiExtra.cs -- EUI ("Enhanced User Interface", by bc1) install/remove.
//
// Downloaded at install time from our own forks' GitHub Releases --
// OBLASTWAR/enhanced-user-interface (folder UI_bc1) and
// OBLASTWAR/enhanced-user-interface-xits (folder UI_bc1_xits). Upstream EUI
// has no versioned, programmatically-fetchable source (a CivFanatics forum
// page), and its v1.28g crashes the game when a unit flag outlives its unit,
// so the forks carry v1.28g plus our fixes. Each release has one asset,
// <FolderName>.zip, whose top-level folder is FolderName.
//
// The two variants are genuinely separate DLC folders -- Assets/DLC/UI_bc1
// and Assets/DLC/UI_bc1_xits -- not the same folder under two names.
// kek-mod's own ui_check.bat recognizes both; Tournament Mod's bundled
// ui_check.bat is an external repo (catscatsforever/Civ5-Patch) we don't
// control and only ever recognizes "UI_bc1", so EUI XITS has no effect
// there today. Only one variant is ever active at a time -- installing one
// removes the other, the same "these can't coexist" rule the mod boxes use.
//
// The folder name itself is what distinguishes an installed variant. The
// installed release tag is kept in a marker file inside that folder (as the
// lobby patch does next to its DLL), so removing the folder removes the
// marker too. An EUI without a marker is upstream EUI or a hand-installed
// copy, which has the crash, so it reads as needing an update.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace KekModInstaller
{
    internal class EuiVariant
    {
        public string Id;
        public string DisplayName;
        public string FolderName;   // Assets/DLC folder this variant lands in -- "UI_bc1" / "UI_bc1_xits"
        public string Repo;         // OBLASTWAR/<Repo> -- its release asset is FolderName + ".zip"
    }

    internal static class EuiExtra
    {
        private const string Owner = "OBLASTWAR";
        private const string MarkerFile = "kekmod-eui-version.txt";

        public static readonly List<EuiVariant> Variants = new List<EuiVariant>
        {
            new EuiVariant { Id = "eui", DisplayName = "EUI", FolderName = "UI_bc1", Repo = "enhanced-user-interface" },
            new EuiVariant { Id = "eui_xits", DisplayName = "EUI XITS", FolderName = "UI_bc1_xits", Repo = "enhanced-user-interface-xits" },
        };

        public static EuiVariant ById(string id)
        {
            return Variants.FirstOrDefault(v => v.Id == id);
        }

        // Whichever variant's own folder is present, if any. Doesn't
        // distinguish "we installed it" from "the player placed it there
        // manually" -- exactly what every one of these mods' ui_check.bat
        // scripts has always assumed players would do, so a manually-placed
        // EUI is recognized and treated the same as one we installed.
        //
        // Exact folder names first (matches what Install produces), then a
        // fallback: ANY Assets/DLC folder starting with "UI" -- e.g. a
        // player who dropped in a differently-versioned or differently-
        // renamed EUI build rather than our exact "UI_bc1"/"UI_bc1_xits".
        // None of the mods this installer supports (kek-mod, Tournament Mod)
        // or Civ5 itself ship a DLC folder starting with "UI", so this is
        // safe to treat as EUI's namespace. Guess which variant by "xits"
        // appearing in the folder name; a guessed match has no marker in our
        // exact folder (see InstalledTag), so it always surfaces as needing
        // an update -- which is the whole point of recognizing it at all.
        public static string DetectInstalledVariantId(string dlcRoot)
        {
            foreach (EuiVariant v in Variants)
            {
                if (Directory.Exists(Path.Combine(dlcRoot, v.FolderName)))
                {
                    return v.Id;
                }
            }

            if (!Directory.Exists(dlcRoot))
            {
                return null;
            }
            foreach (string dir in Directory.GetDirectories(dlcRoot, "UI*"))
            {
                string name = Path.GetFileName(dir);
                return name.IndexOf("xits", StringComparison.OrdinalIgnoreCase) >= 0 ? "eui_xits" : "eui";
            }
            return null;
        }

        // Release tag this installer wrote into variant's folder, or null --
        // not installed by us (upstream EUI, hand-installed, or an installer
        // from before the forks). Never throws.
        public static string InstalledTag(string dlcRoot, EuiVariant variant)
        {
            try
            {
                string marker = Path.Combine(dlcRoot, variant.FolderName, MarkerFile);
                if (!File.Exists(marker))
                {
                    return null;
                }
                string tag = File.ReadAllText(marker).Trim();
                return tag.Length > 0 ? tag : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        // Newest published release tag -- the status worker's update check.
        public static string FetchLatestTag(EuiVariant variant)
        {
            return GitHubModSource.FetchLatestRelease(Owner, variant.Repo).TagName;
        }

        // Downloads variant's latest release and installs it, removing any
        // OTHER EUI folder first -- any "UI*" folder (see
        // DetectInstalledVariantId), not just the other known variant's
        // exact name, since only one is ever active at a time -- plus any
        // existing copy of this same variant. Nothing on disk changes until
        // the download has arrived and checked out.
        public static void Install(string dlcRoot, EuiVariant variant, Action<string> log)
        {
            log("Checking " + Owner + "/" + variant.Repo + " for the latest release...");
            GhRelease release = GitHubModSource.FetchLatestRelease(Owner, variant.Repo);
            string assetName = variant.FolderName + ".zip";
            GhAsset asset = release.Assets == null ? null : release.Assets.FirstOrDefault(
                a => string.Equals(a.Name, assetName, StringComparison.OrdinalIgnoreCase));
            if (asset == null)
            {
                throw new InvalidOperationException("Release " + release.TagName + " has no " + assetName + ".");
            }
            log("Release: " + release.TagName);
            log("Downloading " + asset.Name + "...");
            string tempZip = GitHubModSource.DownloadAsset(asset);
            try
            {
                using (ZipArchive archive = ZipFile.OpenRead(tempZip))
                {
                    bool allUnderFolder = archive.Entries.Count > 0 && archive.Entries.All(
                        en => en.FullName.StartsWith(variant.FolderName + "/", StringComparison.Ordinal));
                    if (!allUnderFolder)
                    {
                        throw new InvalidOperationException(
                            "Downloaded " + asset.Name + " doesn't contain a single " + variant.FolderName + " folder.");
                    }
                }

                foreach (string dir in Directory.GetDirectories(dlcRoot, "UI*"))
                {
                    log("Removing " + Path.GetFileName(dir) + "...");
                    Directory.Delete(dir, true);
                }

                log("Installing " + variant.DisplayName + " " + release.TagName + "...");
                ZipFile.ExtractToDirectory(tempZip, dlcRoot); // zip's own top level is already variant.FolderName
                File.WriteAllText(Path.Combine(dlcRoot, variant.FolderName, MarkerFile), release.TagName);
            }
            finally
            {
                File.Delete(tempZip);
            }
        }

        // Removes whichever EUI folder is present -- any "UI*" folder (see
        // DetectInstalledVariantId), not just the two known variants' exact
        // names, so a manually-renamed EUI is removed the same as one we
        // installed. Never throws -- a locked file shouldn't block whatever
        // triggered this.
        public static void Remove(string dlcRoot, Action<string> log)
        {
            if (!Directory.Exists(dlcRoot))
            {
                return;
            }
            foreach (string dir in Directory.GetDirectories(dlcRoot, "UI*"))
            {
                try
                {
                    log("Removing " + Path.GetFileName(dir) + "...");
                    Directory.Delete(dir, true);
                }
                catch (Exception ex)
                {
                    log(Path.GetFileName(dir) + ": couldn't remove (" + ex.Message + ")");
                }
            }
        }
    }
}
