// ModCore.cs -- mod-agnostic install orchestration: the IModSource/
// ModDefinition abstraction that lets InstallerCore.Run/Uninstall/
// DetectAllInstalled work the same way regardless of which mod (and which
// distribution mechanism -- GitHub Releases, Google Drive) is selected.
//
// Installer.cs keeps everything about the installer PROGRAM itself
// (self-update, ticker, UI) -- this file is everything about the MODS it
// installs and where Civ5 itself lives on disk. InstallerCore is declared
// `partial` and split across both files for that reason.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Microsoft.Win32;

namespace KekModInstaller
{
    // Distribution-agnostic "one installable release" -- what InstallerCore
    // and MainForm need regardless of whether it came from GitHub's releases
    // API or (eventually) lekmod's Google Drive manifest. Native holds the
    // underlying GhRelease (or a future manifest entry type) for the owning
    // IModSource's own use when actually downloading it.
    internal class ModRelease
    {
        public string Tag;
        public bool Prerelease;

        // Extra display text shown in the VERSION dropdown alongside the tag
        // (e.g. a release date/size). Null for mods with nothing to add.
        public string DisplayExtra;

        public object Native;
    }

    // Whether a mod has any concept of "beta"/prerelease builds at all. Only
    // kek-mod's GitHub Releases carry a real prerelease flag today --
    // Settings' "Enable beta builds" checkbox has zero effect for a mod whose
    // policy is None.
    internal enum BetaPolicy
    {
        None,
        OptIn,
    }

    // Where a mod's release payloads come from and how to fetch them. One
    // implementation per distribution mechanism -- GitHubModSource covers any
    // mod that's plain GitHub Releases (kek-mod today).
    internal interface IModSource
    {
        // Every published release, newest first.
        List<ModRelease> ListReleases();

        // Resolves options.TagName (if set) or "newest per options.WantBeta"
        // to one concrete release. Throws InvalidOperationException with a
        // user-facing message on failure.
        ModRelease ResolveRelease(InstallOptions options);

        // Downloads the release's payload to a local temp zip file.
        string DownloadRelease(ModRelease release, InstallOptions options, Action<string> log);
    }

    // Static metadata + hooks describing one supported mod. InstallerCore's
    // Run/Uninstall/DetectAllInstalled are entirely driven by this.
    internal class ModDefinition
    {
        public string Id;
        public string DisplayName;

        // Directory.GetDirectories() glob matched against Assets/DLC to find
        // this mod's installed folder(s), e.g. "KEK Mod v*".
        public string InstalledFolderGlob;

        public IModSource Source;

        // Resolved release tag -> final Assets/DLC folder name, e.g.
        // "v1.5-beta8" -> "KEK Mod v1.5-beta8".
        public Func<string, string> MakeFolderName;

        // True: the downloaded zip's own top-level directory already IS the
        // final folder name, so it can be extracted straight into Assets/DLC
        // (kek-mod today). False: the zip's top-level directory has a fixed,
        // unversioned name (see ZipSourceDirPrefix) and must be located then
        // moved/renamed into the version-suffixed destination.
        public bool ZipTopLevelIsFinalFolderName;

        // Only meaningful when ZipTopLevelIsFinalFolderName is false: the
        // fixed-name prefix to search for inside the extracted archive.
        public string ZipSourceDirPrefix;

        // Extra step run after extraction/placement but before ui_check.bat.
        // Null for mods with nothing extra to do.
        public Action<string, InstallOptions, Action<string>> PostExtractStep;

        // Whether this mod's own extracted folder ships a ui_check.bat to
        // run post-install.
        public bool RunsUiCheck;

        public BetaPolicy Beta;

        // Optional bonus component installed/removed alongside this mod but
        // not really "part of" it -- e.g. kek-mod's Fish Map Script,
        // Tournament Mod's Better Pangaea map. All five null for mods with
        // nothing extra. ExtraDisplayName/ExtraModId are static UI metadata
        // (shown as a sub-row under this mod's box even before anything's
        // installed); ExtraModId must match whatever DetectExtraInstalled's
        // returned DetectedModInstall.ModId uses, so the UI can look up
        // install state from DetectAllInstalled's results without calling
        // the detector a second time itself.
        public string ExtraDisplayName;
        public string ExtraModId;
        public Action<string, Action<string>> EnsureExtraInstalled; // (dlcRoot, log)
        public Action<string, Action<string>> RemoveExtra;          // (dlcRoot, log)
        public Func<string, DetectedModInstall> DetectExtraInstalled; // (dlcRoot) -> entry or null
    }

    internal static class ModRegistry
    {
        public static readonly ModDefinition KekMod = new ModDefinition
        {
            Id = "kekmod",
            DisplayName = "KEK Mod",
            InstalledFolderGlob = "KEK Mod v*",
            Source = new GitHubModSource("OBLASTWAR", "kek-mod", GitHubModSource.PickSoleAsset, true),
            MakeFolderName = tag => "KEK Mod " + tag, // tag "v1.5-beta8" -> "KEK Mod v1.5-beta8"
            ZipTopLevelIsFinalFolderName = true,
            ZipSourceDirPrefix = null,
            PostExtractStep = null,
            RunsUiCheck = true,
            Beta = BetaPolicy.OptIn,
            ExtraDisplayName = "Fish Map Script",
            ExtraModId = "fishmapscript",
            EnsureExtraInstalled = MapScriptExtra.EnsureInstalled,
            RemoveExtra = MapScriptExtra.Remove,
            DetectExtraInstalled = MapScriptExtra.DetectInstalled,
        };

        // Immo's Patch / "Tournament Mod". catscatsforever/Civ5-Patch is the
        // canonical upstream with actual published releases -- the
        // ImmoSS/Civ5-Patch repo it's more commonly linked from is a
        // personal fork with no releases of its own; confirmed by reading
        // ImmoSS's own bundled updater script, which pulls from
        // catscatsforever too. Confirmed by downloading and unzipping a real
        // release (12.2a): the zip's top-level directory is already named
        // "Tournament Mod V12.2a" -- same shape as kek-mod's own zips.
        //
        // Its own installer (update.ps1) never installs the Better Pangaea
        // map -- it isn't even bundled into the release zip, only living
        // loose in the repo's git tree -- so it's wired up here as a bonus
        // component the same way kek-mod's Fish Map Script is (see
        // TournamentMapExtra).
        public static readonly ModDefinition TournamentMod = new ModDefinition
        {
            Id = "tournament",
            DisplayName = "Tournament Mod",
            InstalledFolderGlob = "Tournament Mod V*",
            Source = new GitHubModSource("catscatsforever", "Civ5-Patch", GitHubModSource.PickSoleAsset, false),
            MakeFolderName = tag => "Tournament Mod V" + tag, // tag "12.2a" -> "Tournament Mod V12.2a"
            ZipTopLevelIsFinalFolderName = true,
            ZipSourceDirPrefix = null,
            PostExtractStep = null,
            RunsUiCheck = true,
            Beta = BetaPolicy.None,
            ExtraDisplayName = "Better Pangaea",
            ExtraModId = "betterpangaea",
            EnsureExtraInstalled = TournamentMapExtra.EnsureInstalled,
            RemoveExtra = TournamentMapExtra.Remove,
            DetectExtraInstalled = TournamentMapExtra.DetectInstalled,
        };

        // Lekmod. No published GitHub Releases at all -- distributed as a
        // Google Drive file per version, tracked by a JSON manifest Lekmod
        // publishes in its own repo (see LekmodModSource). Confirmed by
        // downloading a real version through the reference installer: the
        // zip's top-level directory is the fixed, unversioned name "LEKMOD"
        // (not "LEKMOD_v34.15"), so it needs the same "find and rename"
        // handling as Tournament Mod's own zip layout would if it weren't
        // already versioned -- ZipTopLevelIsFinalFolderName = false here.
        // Ships its own ui_check.bat (auto-detects EUI the same way kek-mod's
        // and Tournament Mod's do, by checking for a sibling UI_bc1 folder),
        // so RunsUiCheck = true. Its bonus map component -- Lekmap, Lekmod's
        // own custom mapscript bundle -- is wired up the same way kek-mod's
        // Fish Map Script and Tournament Mod's Better Pangaea are (see
        // LekmapExtra).
        public static readonly ModDefinition Lekmod = new ModDefinition
        {
            Id = "lekmod",
            DisplayName = "Lekmod",
            InstalledFolderGlob = "LEKMOD_v*",
            Source = new LekmodModSource(),
            MakeFolderName = tag => "LEKMOD_" + tag, // tag "v34.15" -> "LEKMOD_v34.15"
            ZipTopLevelIsFinalFolderName = false,
            ZipSourceDirPrefix = "LEKMOD",
            PostExtractStep = null,
            RunsUiCheck = true,
            Beta = BetaPolicy.None,
            ExtraDisplayName = "Lekmap",
            ExtraModId = "lekmap",
            EnsureExtraInstalled = LekmapExtra.EnsureInstalled,
            RemoveExtra = LekmapExtra.Remove,
            DetectExtraInstalled = LekmapExtra.DetectInstalled,
        };

        public static readonly List<ModDefinition> All = new List<ModDefinition> { KekMod, TournamentMod, Lekmod };

        public static ModDefinition ById(string id)
        {
            return All.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));
        }
    }

    internal class InstallOptions
    {
        public bool WantBeta;
        // Exact release tag to install (e.g. "v1.4"), from the VERSION
        // dropdown. null/empty = automatic: newest per WantBeta.
        public string TagName;
    }

    internal class InstallResult
    {
        public string ModId;
        public string FolderName;
        public string TargetDir;
    }

    // One mod's detected install state, surfaced by DetectAllInstalled().
    internal class DetectedModInstall
    {
        public string ModId;
        public string DisplayName;
        public List<string> FolderNames;
        // Human-readable installed version, if the detector could determine
        // one (e.g. "v1.0", "V5.4") -- null if not (mods derive this from
        // their own FolderNames instead; this is mainly for bonus
        // components like Fish Map Script, whose folder name doesn't change
        // between versions the way a mod's own does).
        public string VersionLabel;
    }

    internal static partial class InstallerCore
    {
        public static InstallResult Run(ModDefinition mod, InstallOptions options, Action<string> log, Action<int> progress)
        {
            progress(0);
            ModRelease release = mod.Source.ResolveRelease(options);
            log(mod.DisplayName + " release: " + release.Tag + (release.Prerelease ? " (prerelease)" : "")
                + (string.IsNullOrEmpty(options.TagName) ? "" : " [selected version]"));
            progress(10);

            log("Downloading...");
            string zipPath = mod.Source.DownloadRelease(release, options, log);
            progress(50);

            string dlcRoot = LocateCiv5DlcFolder(log);
            log("Civ V DLC folder: " + dlcRoot);
            progress(55);

            string folderName = mod.MakeFolderName(release.Tag);
            string targetDir = Path.Combine(dlcRoot, folderName);

            if (Directory.Exists(targetDir))
            {
                log("Removing existing " + folderName + "...");
                Directory.Delete(targetDir, true);
            }
            progress(60);

            log("Extracting...");
            ExtractAndLocate(zipPath, dlcRoot, targetDir, mod);
            File.Delete(zipPath);
            progress(75);

            if (mod.PostExtractStep != null)
            {
                mod.PostExtractStep(targetDir, options, log);
            }
            progress(82);

            if (mod.RunsUiCheck)
            {
                RunUiCheck(mod.DisplayName, targetDir, log);
            }
            progress(90);

            // Clean up other versions of THIS mod only after the new one is
            // confirmed in place -- if anything above failed, whatever was
            // already working stays untouched instead of leaving the user
            // with nothing. Other mods' folders are never touched.
            RemoveOtherVersions(dlcRoot, mod.InstalledFolderGlob, folderName, log);
            progress(97);

            if (mod.EnsureExtraInstalled != null)
            {
                mod.EnsureExtraInstalled(dlcRoot, log);
            }
            progress(100);

            log("DONE: " + folderName + " installed.");

            var result = new InstallResult();
            result.ModId = mod.Id;
            result.FolderName = folderName;
            result.TargetDir = targetDir;
            return result;
        }

        // Extracts zipPath, branching on whether the zip's own top-level
        // directory already matches the final folder name or needs to be
        // located and moved into place.
        private static void ExtractAndLocate(string zipPath, string dlcRoot, string targetDir, ModDefinition mod)
        {
            if (mod.ZipTopLevelIsFinalFolderName)
            {
                ZipFile.ExtractToDirectory(zipPath, dlcRoot);
                return;
            }

            string scratch = Path.Combine(Path.GetTempPath(), "modinstall_" + Guid.NewGuid().ToString("N"));
            ZipFile.ExtractToDirectory(zipPath, scratch);
            try
            {
                string found = FindDirStartingWith(scratch, mod.ZipSourceDirPrefix);
                if (found == null)
                {
                    throw new InvalidOperationException(mod.DisplayName + " folder not found in downloaded archive.");
                }
                Directory.Move(found, targetDir);
            }
            finally
            {
                try { Directory.Delete(scratch, true); } catch (Exception) { } // best-effort cleanup
            }
        }

        private static string FindDirStartingWith(string root, string prefix)
        {
            foreach (string dir in Directory.GetDirectories(root, prefix + "*", SearchOption.AllDirectories))
            {
                return dir;
            }
            return null;
        }

        // Re-runs mod's own ui_check.bat against whatever's already
        // installed, without downloading or extracting anything -- used
        // when EUI presence changes (installed/removed/switched) after the
        // mod itself is already in place, so its own UI-file selection
        // (ui_check.bat inspects Assets/DLC/UI_bc1 itself, see EuiExtra)
        // catches up with the new EUI state without a full reinstall. A
        // no-op if the mod isn't currently installed or doesn't ship its
        // own ui_check.bat.
        public static void RerunUiCheck(ModDefinition mod, Action<string> log)
        {
            if (!mod.RunsUiCheck)
            {
                log(mod.DisplayName + " doesn't use ui_check.bat, nothing to re-run.");
                return;
            }
            string dlcRoot = TryAutoLocateCiv5DlcFolder();
            if (dlcRoot == null)
            {
                log("Couldn't locate the Civilization V DLC folder, skipping ui_check.bat re-run.");
                return;
            }
            string[] dirs = Directory.GetDirectories(dlcRoot, mod.InstalledFolderGlob);
            if (dirs.Length == 0)
            {
                return; // mod isn't installed -- nothing to re-check, and nothing worth logging
            }
            RunUiCheck(mod.DisplayName, dirs[0], log);
        }

        // modLabel is prefixed onto every log line -- matters when several
        // mods' ui_check.bat runs land in the same log stream back to back
        // (e.g. RerunUiCheck looping over every installed mod after an EUI
        // change), so it's clear which mod each line is about.
        private static void RunUiCheck(string modLabel, string extractedDir, Action<string> log)
        {
            string batPath = Path.Combine(extractedDir, "ui_check.bat");
            if (!File.Exists(batPath))
            {
                log(modLabel + ": ui_check.bat not found in the extracted folder, skipping");
                return;
            }

            log(modLabel + ": running ui_check.bat...");
            var psi = new ProcessStartInfo();
            psi.FileName = batPath;
            psi.WorkingDirectory = extractedDir;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;

            // ui_check.bat's xcopy/FINDSTR chain is extremely verbose and
            // none of it is useful to a player watching the installer, so
            // it's captured (draining it is required to avoid deadlocking
            // WaitForExit() once the OS pipe buffer fills) but only surfaced
            // if the script actually fails.
            var output = new StringBuilder();
            using (var proc = new Process())
            {
                proc.StartInfo = psi;
                proc.OutputDataReceived += (s, e) => { if (e.Data != null) output.AppendLine(e.Data); };
                proc.ErrorDataReceived += (s, e) => { if (e.Data != null) output.AppendLine(e.Data); };
                proc.Start();
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
                proc.WaitForExit();

                if (proc.ExitCode == 0)
                {
                    log(modLabel + ": ui_check.bat finished successfully.");
                }
                else
                {
                    log(modLabel + ": ui_check.bat failed with exit code " + proc.ExitCode + ":");
                    log(output.ToString());
                }
            }
        }

        // Deletes every folder matching folderGlob under dlcRoot except
        // keepFolderName -- scoped to one mod's own glob so installing a new
        // version of one mod never touches a coexisting different mod's
        // folder.
        private static void RemoveOtherVersions(string dlcRoot, string folderGlob, string keepFolderName, Action<string> log)
        {
            foreach (string dir in Directory.GetDirectories(dlcRoot, folderGlob))
            {
                string name = Path.GetFileName(dir);
                if (!string.Equals(name, keepFolderName, StringComparison.OrdinalIgnoreCase))
                {
                    log("Removing old version " + name + "...");
                    Directory.Delete(dir, true);
                }
            }
        }

        // Removes every installed folder matching mod's glob. Scoped to one
        // mod at a time -- uninstalling kek-mod, say, must never touch a
        // coexisting Tournament Mod install.
        public static void Uninstall(ModDefinition mod, Action<string> log, Action<int> progress)
        {
            progress(0);
            string dlcRoot = TryAutoLocateCiv5DlcFolder();
            if (dlcRoot == null || !Directory.Exists(dlcRoot))
            {
                throw new InvalidOperationException("Couldn't locate the Civilization V DLC folder.");
            }

            string[] dirs = Directory.GetDirectories(dlcRoot, mod.InstalledFolderGlob);
            if (dirs.Length == 0)
            {
                log("Nothing to uninstall.");
            }
            else
            {
                for (int i = 0; i < dirs.Length; i++)
                {
                    log("Removing " + Path.GetFileName(dirs[i]) + "...");
                    Directory.Delete(dirs[i], true);
                    progress(10 + (80 * (i + 1) / dirs.Length));
                }
            }

            if (mod.RemoveExtra != null)
            {
                mod.RemoveExtra(dlcRoot, log);
            }

            log("DONE: " + mod.DisplayName + " uninstalled.");
            progress(100);
        }

        // Startup status check across EVERY supported mod at once (not just
        // whichever one is selected in the UI) -- Assets/DLC scan per mod's
        // glob, plus the Fish Map Script's own folder since it's a visible
        // extra players installed even though it isn't "a mod" in the
        // ModRegistry sense. Never throws -- the caller shows "couldn't
        // check" rather than blocking the form on failure.
        public static List<DetectedModInstall> DetectAllInstalled()
        {
            var found = new List<DetectedModInstall>();
            string dlcRoot = TryAutoLocateCiv5DlcFolder();
            if (dlcRoot == null || !Directory.Exists(dlcRoot))
            {
                return found;
            }

            foreach (ModDefinition mod in ModRegistry.All)
            {
                string[] dirs = Directory.GetDirectories(dlcRoot, mod.InstalledFolderGlob);
                if (dirs.Length > 0)
                {
                    List<string> names = dirs.Select(Path.GetFileName).ToList();
                    names.Sort();
                    var d = new DetectedModInstall();
                    d.ModId = mod.Id;
                    d.DisplayName = mod.DisplayName;
                    d.FolderNames = names;
                    found.Add(d);
                }

                if (mod.DetectExtraInstalled != null)
                {
                    DetectedModInstall extra = mod.DetectExtraInstalled(dlcRoot);
                    if (extra != null)
                    {
                        found.Add(extra);
                    }
                }
            }

            return found;
        }

        // Known Civ5 executable names (process name, no ".exe") -- installing
        // or uninstalling into Assets/DLC while the game has files open can
        // corrupt the install, so MainForm blocks on this at startup and
        // before each Install/Uninstall. Deliberately an explicit list rather
        // than a "StartsWith(CivilizationV)" check -- that would also match
        // Civilization VI's "CivilizationVI" process, a different game
        // entirely that isn't a reason to block.
        private static readonly string[] Civ5ProcessNames =
        {
            "CivilizationV",       // base DX9 build
            "CivilizationV_DX11",
            "CivilizationV_Tablet",
            "CivilizationV_Vulkan",
        };

        public static bool IsCiv5Running()
        {
            foreach (Process p in Process.GetProcesses())
            {
                using (p)
                {
                    foreach (string name in Civ5ProcessNames)
                    {
                        if (string.Equals(p.ProcessName, name, StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }
                }
            }
            return false;
        }

        // Kills every running Civ5 process outright (Process.Kill, not a
        // graceful WM_CLOSE) -- backs MainForm's LAUNCH CIV button doubling
        // as FORCE CLOSE when the game is already running. Any unsaved game
        // progress is lost; the caller confirms with the user before calling
        // this.
        public static void ForceCloseCiv5()
        {
            foreach (Process p in Process.GetProcesses())
            {
                using (p)
                {
                    foreach (string name in Civ5ProcessNames)
                    {
                        if (string.Equals(p.ProcessName, name, StringComparison.OrdinalIgnoreCase))
                        {
                            try
                            {
                                p.Kill();
                                p.WaitForExit(5000);
                            }
                            catch (InvalidOperationException)
                            {
                                // Already exited between enumeration and Kill.
                            }
                            break;
                        }
                    }
                }
            }
        }

        // Finds the Steam library that actually contains Civ5 -- it may not
        // be the main Steam install (external drives, multiple libraries).
        private const string Civ5RelPath = "steamapps\\common\\Sid Meier's Civilization V";

        // Silent variant for the startup status check and the "Open install
        // folder" button: no dialog, just null if nothing was found.
        public static string TryGetDlcFolder()
        {
            return TryAutoLocateCiv5DlcFolder();
        }

        // The game's install root (the folder holding CivilizationV.exe and
        // CivilizationV_DX11.exe) -- backs MainForm's direct-exe LAUNCH
        // DX9/DX11 buttons.
        public static string TryGetCiv5GameFolder()
        {
            string assetsFolder = TryGetAssetsFolder();
            return assetsFolder == null ? null : Path.GetDirectoryName(assetsFolder);
        }

        // Points at one of the player's OWN already-installed Civ5 cursors
        // under Assets/UI/Cursors (e.g. "Pointer.ani", "Edit.ani"), never
        // bundled/redistributed by us -- same principle as running
        // ui_check.bat from their own extracted mod folder rather than
        // shipping a copy of it.
        public static string TryGetCivCursorPath(string fileName)
        {
            string assetsFolder = TryGetAssetsFolder();
            if (assetsFolder == null)
            {
                return null;
            }
            string cursorPath = Path.Combine(assetsFolder, "UI", "Cursors", fileName);
            return File.Exists(cursorPath) ? cursorPath : null;
        }

        // Civ5's Assets/ folder (DLC's parent) -- shared by anything that
        // needs to reach outside Assets/DLC, e.g. Assets/UI/Cursors or
        // Assets/Maps.
        private static string TryGetAssetsFolder()
        {
            string dlcFolder = TryAutoLocateCiv5DlcFolder();
            return dlcFolder == null ? null : Path.GetDirectoryName(dlcFolder);
        }

        private static string TryAutoLocateCiv5DlcFolder()
        {
            foreach (string libraryPath in EnumerateSteamLibraries())
            {
                string candidate = Path.Combine(libraryPath, Civ5RelPath);
                if (Directory.Exists(candidate))
                {
                    return Path.Combine(candidate, "Assets", "DLC");
                }
            }
            return null;
        }

        private static string LocateCiv5DlcFolder(Action<string> log)
        {
            string auto = TryAutoLocateCiv5DlcFolder();
            if (auto != null)
            {
                return auto;
            }

            log("Couldn't auto-detect a Steam library containing Civilization V.");
            string manual = PromptForCiv5Path();
            if (string.IsNullOrEmpty(manual) || !Directory.Exists(manual))
            {
                throw new InvalidOperationException("No valid Civilization V folder given.");
            }
            return Path.Combine(manual, "Assets", "DLC");
        }

        // Runs on the background thread; must not touch the main form. A
        // small modal FolderBrowserDialog is safe to show from any STA
        // thread.
        private static string PromptForCiv5Path()
        {
            string result = null;
            using (var dialog = new FolderBrowserDialog())
            {
                dialog.Description = "Select your Sid Meier's Civilization V install folder";
                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    result = dialog.SelectedPath;
                }
            }
            return result;
        }

        private static IEnumerable<string> EnumerateSteamLibraries()
        {
            string steamPath = GetSteamInstallPath();
            if (steamPath == null)
            {
                yield break;
            }

            // The main Steam install is always an implicit library.
            yield return steamPath;

            string vdfPath = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdfPath))
            {
                yield break;
            }

            string vdf = File.ReadAllText(vdfPath);
            MatchCollection matches = Regex.Matches(vdf, "\"path\"\\s*\"([^\"]+)\"");
            foreach (Match m in matches)
            {
                yield return m.Groups[1].Value.Replace("\\\\", "\\");
            }
        }

        private static string GetSteamInstallPath()
        {
            string[] keys =
            {
                @"HKEY_CURRENT_USER\Software\Valve\Steam",
                @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam",
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Valve\Steam",
            };
            string[] valueNames = { "SteamPath", "InstallPath" };

            for (int i = 0; i < keys.Length; i++)
            {
                for (int j = 0; j < valueNames.Length; j++)
                {
                    object value = Registry.GetValue(keys[i], valueNames[j], null);
                    string s = value as string;
                    if (s != null && Directory.Exists(s))
                    {
                        return s.Replace('/', '\\');
                    }
                }
            }
            return null;
        }
    }
}
