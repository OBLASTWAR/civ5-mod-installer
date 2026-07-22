// EuiExtra.cs -- EUI ("Enhanced User Interface", by bc1) install/remove.
//
// Unlike kek-mod/Tournament Mod/Fish Map Script/Better Pangaea, there is no
// reliable versioned, programmatically-fetchable source for EUI: its
// canonical distribution is a CivFanatics forum resource page (no API, no
// stable URL scheme, rate-limits automated fetches), and the only GitHub
// mirror (vans163/ui_bc1) has no releases/tags and is frozen at a ~10-year-
// old version. So EUI (and its "XITS" font variant) are bundled directly
// with this installer as embedded resources -- see build.bat's /resource:
// switches for UI_bc1.zip / UI_bc1_xits.zip, mirroring exactly how
// RetroAudio embeds beat.mp3. Both are gitignored (large binary assets, dev
// machine only) and gracefully absent if not present at build time.
//
// The two variants are genuinely separate DLC folders -- Assets/DLC/UI_bc1
// and Assets/DLC/UI_bc1_xits, matching each zip's own top-level folder name
// exactly -- not the same folder under two names. kek-mod's own
// ui_check.bat now recognizes both (see the repo root ui_check.bat, updated
// alongside this); Tournament Mod's bundled ui_check.bat is an external
// repo (catscatsforever/Civ5-Patch) we don't control and only ever
// recognizes "UI_bc1", so EUI XITS has no effect there today. Only one
// variant is ever active at a time -- installing one removes the other, the
// same "these can't coexist" rule the mod boxes use.
//
// The folder name itself is what distinguishes an installed variant (no
// separate marker file needed): whichever of UI_bc1 / UI_bc1_xits actually
// exists tells you which one is active.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;

namespace KekModInstaller
{
    internal class EuiVariant
    {
        public string Id;
        public string DisplayName;
        public string FolderName;   // Assets/DLC folder this variant lands in -- "UI_bc1" / "UI_bc1_xits"
        public string ResourceName; // embedded resource logical name (see build.bat)
        // Hand-set, not detected -- none of these files carry a version
        // string anywhere (checked: filenames, .civ5pkg metadata, Lua
        // source, zip comment). Confirmed externally instead by diffing the
        // bundled UI_bc1.zip byte-for-byte against a known-version download
        // from CivFanatics (195/195 files identical). Null if not yet known.
        public string DisplayVersion;
    }

    internal static class EuiExtra
    {
        public static readonly List<EuiVariant> Variants = new List<EuiVariant>
        {
            new EuiVariant { Id = "eui", DisplayName = "EUI", DisplayVersion = "v1.28g", FolderName = "UI_bc1", ResourceName = "KekModInstaller.UI_bc1.zip" },
            new EuiVariant { Id = "eui_xits", DisplayName = "EUI XITS", DisplayVersion = null, FolderName = "UI_bc1_xits", ResourceName = "KekModInstaller.UI_bc1_xits.zip" },
        };

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
        // appearing in the folder name; a guessed match can never be exact
        // (see IsExactBundledMatch, which checks the precise FolderName), so
        // it always surfaces as a version mismatch rather than a clean
        // install -- which is the whole point of recognizing it at all.
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

        // Installs variant's own folder, removing any OTHER EUI folder first
        // -- any "UI*" folder (see DetectInstalledVariantId), not just the
        // other known variant's exact name, since only one is ever active at
        // a time -- plus any existing copy of this same variant. Logs and
        // returns without throwing if this installer build wasn't compiled
        // with that variant's zip embedded.
        // Returns whether the variant was actually installed -- false means
        // the "isn't bundled" no-op case, so the caller (EuiWorker_DoWork)
        // knows not to log a DONE line for an install that never happened.
        public static bool Install(string dlcRoot, EuiVariant variant, Action<string> log)
        {
            string tempZip = ExtractEmbeddedResourceToTemp(variant.ResourceName);
            if (tempZip == null)
            {
                log(variant.DisplayName + " isn't bundled with this installer build -- nothing to install.");
                return false;
            }

            foreach (string dir in Directory.GetDirectories(dlcRoot, "UI*"))
            {
                log("Removing " + Path.GetFileName(dir) + "...");
                Directory.Delete(dir, true);
            }

            log("Installing " + variant.DisplayName + "...");
            ZipFile.ExtractToDirectory(tempZip, dlcRoot); // zip's own top level is already variant.FolderName
            File.Delete(tempZip);
            return true;
        }

        // Every mod we ship is only ever tested against this exact bundled
        // EUI build -- a player who manually dropped in a newer (or older,
        // or modified) EUI themselves can silently break UI files these
        // mods rely on, since ui_check.bat only checks "does a UI_bc1
        // folder exist", not what's actually in it. This does the check
        // DetectInstalledVariantId deliberately doesn't: hashes every file
        // this build's embedded copy of variant contains and compares
        // against what's actually on disk. False (not a confirmed match) on
        // any mismatch, missing file, or if this build wasn't compiled with
        // that variant's zip embedded in the first place -- never throws.
        public static bool IsExactBundledMatch(string dlcRoot, EuiVariant variant)
        {
            try
            {
                string targetDir = Path.Combine(dlcRoot, variant.FolderName);
                if (!Directory.Exists(targetDir))
                {
                    return false;
                }

                Dictionary<string, string> bundledHashes = GetBundledFileHashes(variant);
                if (bundledHashes == null)
                {
                    return false; // this build has no embedded copy of variant to compare against
                }

                foreach (KeyValuePair<string, string> entry in bundledHashes)
                {
                    string diskPath = Path.Combine(targetDir, entry.Key.Replace('/', Path.DirectorySeparatorChar));
                    if (!File.Exists(diskPath))
                    {
                        return false;
                    }
                    if (ComputeSha256(diskPath) != entry.Value)
                    {
                        return false;
                    }
                }
                return true;
            }
            catch (Exception)
            {
                return false; // can't verify -- treat as "not a confirmed match" rather than crash detection
            }
        }

        // Reads the embedded zip without extracting it, keyed by path
        // relative to the installed folder itself (i.e. with the zip's own
        // "UI_bc1/" top-level prefix stripped) so keys line up directly with
        // what IsExactBundledMatch finds on disk. Null if this build wasn't
        // compiled with that variant's zip embedded.
        private static Dictionary<string, string> GetBundledFileHashes(EuiVariant variant)
        {
            Assembly asm = Assembly.GetExecutingAssembly();
            using (Stream resStream = asm.GetManifestResourceStream(variant.ResourceName))
            {
                if (resStream == null)
                {
                    return null;
                }

                // ZipArchive needs a seekable stream -- the manifest
                // resource stream usually is one, but copy to a MemoryStream
                // first so this doesn't depend on that implementation detail.
                using (var buffer = new MemoryStream())
                {
                    resStream.CopyTo(buffer);
                    buffer.Position = 0;

                    using (var archive = new ZipArchive(buffer, ZipArchiveMode.Read))
                    {
                        var hashes = new Dictionary<string, string>();
                        using (var sha = SHA256.Create())
                        {
                            foreach (ZipArchiveEntry entry in archive.Entries)
                            {
                                if (string.IsNullOrEmpty(entry.Name))
                                {
                                    continue; // directory entry
                                }
                                string relative = StripTopLevelFolder(entry.FullName);
                                using (Stream entryStream = entry.Open())
                                {
                                    byte[] hash = sha.ComputeHash(entryStream);
                                    hashes[relative] = Convert.ToBase64String(hash);
                                }
                            }
                        }
                        return hashes;
                    }
                }
            }
        }

        private static string StripTopLevelFolder(string zipEntryPath)
        {
            int slash = zipEntryPath.IndexOf('/');
            return slash >= 0 ? zipEntryPath.Substring(slash + 1) : zipEntryPath;
        }

        private static string ComputeSha256(string filePath)
        {
            using (var sha = SHA256.Create())
            using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read))
            {
                return Convert.ToBase64String(sha.ComputeHash(fs));
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

        private static string ExtractEmbeddedResourceToTemp(string resourceName)
        {
            Assembly asm = Assembly.GetExecutingAssembly();
            using (Stream resStream = asm.GetManifestResourceStream(resourceName))
            {
                if (resStream == null)
                {
                    return null;
                }
                string tempPath = Path.Combine(Path.GetTempPath(), "kekmod_eui_" + Guid.NewGuid().ToString("N") + ".zip");
                using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write))
                {
                    resStream.CopyTo(fs);
                }
                return tempPath;
            }
        }
    }
}
