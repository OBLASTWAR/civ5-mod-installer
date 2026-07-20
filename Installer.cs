// KekModInstaller -- pulls the latest published kek-mod build from GitHub
// Releases and drops it into the local Civ V DLC folder. Small WinForms GUI:
// pick a channel/build, click Install.
//
// Mirrors, on the client side, what stage.sh/deploy.sh already do on the dev
// machine: same "KEK Mod v<version>" folder naming, same "replace only this
// version's own folder, leave others alone" rule, same ui_check.bat-last step.
//
// Deliberately written against C# 5 syntax (no string interpolation, no
// null-conditional operators, no pattern-matching `is` declarations): the
// .NET Framework's bundled csc.exe doesn't support anything newer, and that's
// the one compiler guaranteed present without installing extra tooling.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Microsoft.Win32;

namespace KekModInstaller
{
    [DataContract]
    internal class GhAsset
    {
        [DataMember(Name = "name")]
        public string Name { get; set; }

        [DataMember(Name = "browser_download_url")]
        public string BrowserDownloadUrl { get; set; }
    }

    [DataContract]
    internal class GhRelease
    {
        [DataMember(Name = "tag_name")]
        public string TagName { get; set; }

        [DataMember(Name = "prerelease")]
        public bool Prerelease { get; set; }

        [DataMember(Name = "assets")]
        public List<GhAsset> Assets { get; set; }
    }

    internal class InstallOptions
    {
        public bool WantBeta;
        public bool WantDev;
        // Exact release tag to install (e.g. "v1.4"), from the VERSION
        // dropdown. null/empty = automatic: newest per WantBeta.
        public string TagName;
    }

    internal class InstallResult
    {
        public string FolderName;
        public string TargetDir;
    }

    // Persists the settings-page opt-ins across launches. Both off by
    // default: regular players shouldn't see beta as an option at all
    // unless they've deliberately dug into Settings for it, and a dev build
    // that accidentally starts pulling from the LAN test server is the kind
    // of surprise nobody wants -- see MainForm.UpdateAutoVersionLabel and
    // SettingsForm.
    internal static class SettingsManager
    {
        private const string RegistryKey = @"HKEY_CURRENT_USER\Software\KekModInstaller";
        private const string ShowBetaValueName = "ShowBetaBuilds";
        private const string UseDevValueName = "UseDevBuild";
        private const string MutedValueName = "Muted";

        public static bool GetShowBeta()
        {
            object value = Registry.GetValue(RegistryKey, ShowBetaValueName, 0);
            return value is int && (int)value != 0;
        }

        public static void SetShowBeta(bool show)
        {
            Registry.SetValue(RegistryKey, ShowBetaValueName, show ? 1 : 0, RegistryValueKind.DWord);
        }

        // INTERNAL_BUILD-only in practice (SettingsForm only shows the
        // Prod/Dev radios there), but the getter/setter pair is harmless to
        // keep unconditional -- simpler than threading #if into this class.
        public static bool GetUseDev()
        {
            object value = Registry.GetValue(RegistryKey, UseDevValueName, 0);
            return value is int && (int)value != 0;
        }

        public static void SetUseDev(bool useDev)
        {
            Registry.SetValue(RegistryKey, UseDevValueName, useDev ? 1 : 0, RegistryValueKind.DWord);
        }

        // Unmuted by default -- the mute button only needs to remember an
        // explicit "turn it off", not force music on for someone who never
        // touched the button.
        public static bool GetMuted()
        {
            object value = Registry.GetValue(RegistryKey, MutedValueName, 0);
            return value is int && (int)value != 0;
        }

        public static void SetMuted(bool muted)
        {
            Registry.SetValue(RegistryKey, MutedValueName, muted ? 1 : 0, RegistryValueKind.DWord);
        }
    }

    // Core install logic, UI-agnostic: reports progress through a plain
    // Action<string> so both the WinForms log box and (if ever needed again)
    // a console could drive it the same way.
    internal static class InstallerCore
    {
        private const string RepoOwner = "OBLASTWAR";
        private const string RepoName = "kek-mod";

        // Bonus map script, pulled in alongside kek-mod if the player
        // doesn't already have it -- separate repo, separate release cadence
        // (no dev/prod, no beta channel, just "latest"). Uninstall removes
        // it too, since the installer is the one that put it there and
        // players expect "uninstall" to clean up everything it added.
        //
        // FolderName is deliberately "Fish Map Script" -- the exact name
        // already used by existing community installs -- NOT the
        // "pangea-stratbal" repo's own label. Renaming it would make players
        // who already have it look like they're missing a "different" map
        // and get a redundant second copy installed alongside their real one.
        private const string MapScriptRepoOwner = "OBLASTWAR";
        private const string MapScriptRepoName = "pangea-stratbal";
        private const string MapScriptFolderName = "Fish Map Script";

        // Branch the scrolling greetz ticker is read from at startup, so the
        // message can be edited on GitHub without shipping a new installer
        // build. release/2.x is the canonical release line (one merge per
        // feature); its installer/ticker.txt is the live copy to edit.
        private const string TickerBranch = "release/2.x";

        // Never throws -- a missing/offline/renamed file just means the
        // caller keeps whatever default ticker text it already has.
        public static string TryFetchTickerText()
        {
            string url = "https://raw.githubusercontent.com/" + RepoOwner + "/" + RepoName
                + "/" + TickerBranch + "/installer/ticker.txt";
            try
            {
                using (var client = new HttpClient())
                {
                    client.DefaultRequestHeaders.Add("User-Agent", "KekModInstaller");
                    string text = client.GetStringAsync(url).GetAwaiter().GetResult();
                    text = text.Trim();
                    return text.Length == 0 ? null : text;
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static InstallResult Run(InstallOptions options, Action<string> log, Action<int> progress)
        {
            progress(0);
            GhRelease release = string.IsNullOrEmpty(options.TagName)
                ? FetchTargetRelease(options.WantBeta)
                : FetchReleaseByTag(options.TagName);
            log("Release: " + release.TagName + (release.Prerelease ? " (prerelease)" : "")
                + (string.IsNullOrEmpty(options.TagName) ? "" : " [selected version]"));

            GhAsset asset = PickAsset(release, options.WantDev);
            log("Asset: " + asset.Name);
            progress(10);

            log("Downloading...");
            string zipPath = DownloadAsset(asset);
            progress(50);

            string dlcRoot = LocateCiv5DlcFolder(log);
            log("Civ V DLC folder: " + dlcRoot);
            progress(55);

            string folderName = "KEK Mod " + release.TagName; // tag "v1.5-beta8" -> "KEK Mod v1.5-beta8"
            string targetDir = Path.Combine(dlcRoot, folderName);

            if (Directory.Exists(targetDir))
            {
                log("Removing existing " + folderName + "...");
                Directory.Delete(targetDir, true);
            }
            progress(60);

            log("Extracting...");
            ZipFile.ExtractToDirectory(zipPath, dlcRoot);
            File.Delete(zipPath);
            progress(75);

            RunUiCheck(targetDir, log);
            progress(90);

            // Clean up other versions only after the new one is confirmed in
            // place -- if anything above failed, whatever was already working
            // stays untouched instead of leaving the user with nothing.
            RemoveOtherVersions(dlcRoot, folderName, log);
            progress(97);

            EnsureMapScriptInstalled(dlcRoot, log);
            progress(100);

            log("Done: " + folderName);

            var result = new InstallResult();
            result.FolderName = folderName;
            result.TargetDir = targetDir;
            return result;
        }

        // Deletes every "KEK Mod v*" folder under dlcRoot except keepFolderName.
        private static void RemoveOtherVersions(string dlcRoot, string keepFolderName, Action<string> log)
        {
            foreach (string dir in Directory.GetDirectories(dlcRoot, "KEK Mod v*"))
            {
                string name = Path.GetFileName(dir);
                if (!string.Equals(name, keepFolderName, StringComparison.OrdinalIgnoreCase))
                {
                    log("Removing old version " + name + "...");
                    Directory.Delete(dir, true);
                }
            }
        }

        // Removes every installed "KEK Mod v*" folder plus the Fish Map
        // Script folder installed alongside it. Used by the Uninstall
        // button -- unlike Run(), there's no "new version" to keep.
        public static void Uninstall(Action<string> log, Action<int> progress)
        {
            progress(0);
            string dlcRoot = TryAutoLocateCiv5DlcFolder();
            if (dlcRoot == null || !Directory.Exists(dlcRoot))
            {
                throw new InvalidOperationException("Couldn't locate the Civilization V DLC folder.");
            }

            string[] dirs = Directory.GetDirectories(dlcRoot, "KEK Mod v*");
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

            RemoveMapScript(dlcRoot, log);

            log("Uninstall complete.");
            progress(100);
        }

        // Removes the Fish Map Script folder from Assets/Maps if present.
        // Mirrors the path derivation in EnsureMapScriptInstalled. Never
        // throws -- a missing folder or locked file shouldn't fail the
        // overall uninstall.
        private static void RemoveMapScript(string dlcRoot, Action<string> log)
        {
            try
            {
                string assetsFolder = Path.GetDirectoryName(dlcRoot); // DLC's parent
                string mapsFolder = Path.Combine(assetsFolder, "Maps");
                string targetDir = Path.Combine(mapsFolder, MapScriptFolderName);
                if (Directory.Exists(targetDir))
                {
                    log("Removing " + MapScriptFolderName + "...");
                    Directory.Delete(targetDir, true);
                }
            }
            catch (Exception ex)
            {
                log(MapScriptFolderName + ": couldn't remove (" + ex.Message + ")");
            }
        }

        // GitHub returns releases newest-first. WantBeta takes the newest release
        // of any kind; otherwise the newest one that isn't a prerelease.
        private static GhRelease FetchTargetRelease(bool wantBeta)
        {
            List<GhRelease> releases = FetchReleases(RepoOwner, RepoName);
            GhRelease chosen = wantBeta ? releases[0] : releases.FirstOrDefault(r => !r.Prerelease);
            if (chosen == null)
            {
                throw new InvalidOperationException(
                    "No stable (non-prerelease) release found. Check \"Include beta versions\" to install the latest beta instead.");
            }
            return chosen;
        }

        // VERSION-dropdown support: every published release, newest first.
        // Same fetch the auto path uses; the UI shows tags and hands the
        // chosen one back via InstallOptions.TagName.
        public static List<GhRelease> ListAvailableReleases()
        {
            return FetchReleases(RepoOwner, RepoName);
        }

        private static GhRelease FetchReleaseByTag(string tagName)
        {
            List<GhRelease> releases = FetchReleases(RepoOwner, RepoName);
            GhRelease chosen = releases.FirstOrDefault(
                r => string.Equals(r.TagName, tagName, StringComparison.OrdinalIgnoreCase));
            if (chosen == null)
            {
                throw new InvalidOperationException(
                    "Release " + tagName + " no longer exists on GitHub. Pick another version.");
            }
            return chosen;
        }

        private static List<GhRelease> FetchReleases(string owner, string repo)
        {
            string url = "https://api.github.com/repos/" + owner + "/" + repo + "/releases?per_page=50";
            using (var client = new HttpClient())
            {
                client.DefaultRequestHeaders.Add("User-Agent", "KekModInstaller");
                client.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");

                byte[] body = client.GetByteArrayAsync(url).GetAwaiter().GetResult();
                var serializer = new DataContractJsonSerializer(typeof(List<GhRelease>));
                using (var stream = new MemoryStream(body))
                {
                    var releases = (List<GhRelease>)serializer.ReadObject(stream);
                    if (releases == null || releases.Count == 0)
                    {
                        throw new InvalidOperationException(
                            "No releases found for " + owner + "/" + repo + ". Has anything been published yet?");
                    }
                    return releases;
                }
            }
        }

        // Downloads and drops MapScriptFolderName into Assets/Maps if it
        // isn't already there. Deliberately swallows every failure (offline,
        // repo/release missing, extraction hiccup): this is a bonus, not
        // part of kek-mod itself, so it should never fail the main install.
        // Takes dlcRoot (already resolved by Run(), possibly via the manual
        // FolderBrowserDialog fallback) rather than re-deriving it via the
        // auto-only TryAutoLocateCiv5DlcFolder -- otherwise this would
        // silently skip for anyone who had to browse to a non-Steam-standard
        // Civ5 install.
        private static void EnsureMapScriptInstalled(string dlcRoot, Action<string> log)
        {
            try
            {
                string assetsFolder = Path.GetDirectoryName(dlcRoot); // DLC's parent

                string mapsFolder = Path.Combine(assetsFolder, "Maps");
                string targetDir = Path.Combine(mapsFolder, MapScriptFolderName);
                if (Directory.Exists(targetDir))
                {
                    log(MapScriptFolderName + " already installed, skipping.");
                    return;
                }

                log("Installing " + MapScriptFolderName + "...");
                GhRelease release = FetchReleases(MapScriptRepoOwner, MapScriptRepoName)[0]; // newest
                GhAsset asset = release.Assets != null ? release.Assets.FirstOrDefault() : null;
                if (asset == null)
                {
                    log(MapScriptFolderName + ": release " + release.TagName + " has no assets, skipping.");
                    return;
                }

                string zipPath = DownloadAsset(asset);
                Directory.CreateDirectory(mapsFolder);
                ZipFile.ExtractToDirectory(zipPath, mapsFolder);
                File.Delete(zipPath);
                log(MapScriptFolderName + " " + release.TagName + " installed.");
            }
            catch (Exception ex)
            {
                log(MapScriptFolderName + ": couldn't install (" + ex.Message + ")");
            }
        }

        public static string DescribeLatestAvailable(bool wantBeta)
        {
            GhRelease release = FetchTargetRelease(wantBeta);
            // "KEK Mod " prefix matches DetectInstalledVersions' folder-name
            // format (e.g. "KEK Mod v1.4") so [INSTALLED] and [LATEST] read
            // as directly comparable at a glance instead of two different
            // formats for the same version.
            return "KEK Mod " + release.TagName + (release.Prerelease ? " (beta)" : "");
        }

        private static GhAsset PickAsset(GhRelease release, bool wantDev)
        {
            // gh CLI sanitizes spaces in uploaded asset filenames (observed:
            // "kekmod prod 1.5.zip" -> "kekmod.prod.1.5.zip" on GitHub), so
            // match "prod"/"dev" as a whole word regardless of the separator
            // (space, dot, dash, underscore) around it.
            string word = wantDev ? "dev" : "prod";
            var pattern = new Regex("\\b" + word + "\\b", RegexOptions.IgnoreCase);
            GhAsset asset = null;
            if (release.Assets != null)
            {
                asset = release.Assets.FirstOrDefault(a => pattern.IsMatch(a.Name));
            }
            if (asset == null)
            {
                string found = release.Assets == null
                    ? ""
                    : string.Join(", ", release.Assets.Select(a => a.Name).ToArray());
                throw new InvalidOperationException(
                    "Release " + release.TagName + " has no asset matching \"" + word + "\". Assets found: " + found);
            }
            return asset;
        }

        private static string DownloadAsset(GhAsset asset)
        {
            string tempPath = Path.Combine(Path.GetTempPath(), "kekmod_" + Guid.NewGuid().ToString("N") + ".zip");
            using (var client = new HttpClient())
            {
                client.DefaultRequestHeaders.Add("User-Agent", "KekModInstaller");
                byte[] data = client.GetByteArrayAsync(asset.BrowserDownloadUrl).GetAwaiter().GetResult();
                File.WriteAllBytes(tempPath, data);
            }
            return tempPath;
        }

        // Finds the Steam library that actually contains Civ5 -- it may not be
        // the main Steam install (external drives, multiple libraries).
        private const string Civ5RelPath = "steamapps\\common\\Sid Meier's Civilization V";

        // Silent variant for the startup status check: no dialog, just null if
        // nothing was found. The interactive prompt is reserved for Install,
        // where failing to locate Civ5 should actually stop the user.
        // Public wrapper for the "Open install folder" button, which should
        // always point somewhere useful regardless of whether an
        // install/uninstall has run yet this session.
        public static string TryGetDlcFolder()
        {
            return TryAutoLocateCiv5DlcFolder();
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

        // Startup status check: what's already on disk, and what's the newest
        // thing published (whichever channel). Never throws -- the caller
        // shows "couldn't check" rather than blocking the form on failure.
        public static List<string> DetectInstalledVersions()
        {
            var found = new List<string>();
            string dlcRoot = TryAutoLocateCiv5DlcFolder();
            if (dlcRoot != null && Directory.Exists(dlcRoot))
            {
                foreach (string dir in Directory.GetDirectories(dlcRoot, "KEK Mod v*"))
                {
                    found.Add(Path.GetFileName(dir));
                }
            }
            found.Sort();
            return found;
        }

        // Runs on the background thread; must not touch the main form. A small
        // modal FolderBrowserDialog is safe to show from any STA thread.
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

        private static void RunUiCheck(string extractedDir, Action<string> log)
        {
            string batPath = Path.Combine(extractedDir, "ui_check.bat");
            if (!File.Exists(batPath))
            {
                log("ui_check.bat not found in the extracted folder, skipping");
                return;
            }

            log("Running ui_check.bat...");
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
                    log("ui_check.bat finished successfully.");
                }
                else
                {
                    log("ui_check.bat failed with exit code " + proc.ExitCode + ":");
                    log(output.ToString());
                }
            }
        }
    }

    // Looping background track via the classic MCI trick (winmm.dll,
    // mciSendString "open ... type mpegvideo" / "play ... repeat") -- plays
    // MP3s with zero extra assembly references, which matters here since
    // build.bat's csc.exe invocation only links the handful of Framework
    // DLLs already on every Windows box.
    internal static class RetroAudio
    {
        [DllImport("winmm.dll", CharSet = CharSet.Unicode, EntryPoint = "mciSendStringW")]
        private static extern int MciSendString(string command, StringBuilder returnBuffer, int returnLength, IntPtr callback);

        private const string Alias = "kekbeat";
        private const string BeatResourceName = "KekModInstaller.beat.mp3";

        // Embedded via build.bat's /resource: switch rather than shipped as
        // a loose file next to the exe -- so a cloned repo or a downloaded
        // release actually has music instead of silently finding nothing.
        // MCI can only open a real file path (no stream support), so this
        // extracts to %TEMP% first. Reuses whatever's already there if the
        // write fails (e.g. a second instance launched while the first's MCI
        // handle still has that exact temp file open).
        public static void PlayEmbeddedLooped()
        {
            string tempPath = Path.Combine(Path.GetTempPath(), "kekmod_beat.mp3");
            try
            {
                Assembly asm = Assembly.GetExecutingAssembly();
                using (Stream resStream = asm.GetManifestResourceStream(BeatResourceName))
                {
                    if (resStream == null)
                    {
                        return; // not embedded in this build -- nothing to play
                    }
                    using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write))
                    {
                        resStream.CopyTo(fs);
                    }
                }
            }
            catch (Exception)
            {
                if (!File.Exists(tempPath))
                {
                    return; // couldn't extract and nothing to fall back to
                }
            }
            PlayLooped(tempPath);
        }

        public static void PlayLooped(string path)
        {
            if (!File.Exists(path))
            {
                return;
            }
            MciSendString("close " + Alias, null, 0, IntPtr.Zero); // clear any stale handle, ignore failure
            int rc = MciSendString("open \"" + path + "\" type mpegvideo alias " + Alias, null, 0, IntPtr.Zero);
            if (rc == 0)
            {
                MciSendString("play " + Alias + " repeat", null, 0, IntPtr.Zero);
            }
        }

        // Volume is 0-1000; muting this way (instead of stop/start) keeps the
        // loop's playback position, so unmuting doesn't restart the beat.
        public static void SetMuted(bool muted)
        {
            MciSendString("setaudio " + Alias + " volume to " + (muted ? "0" : "1000"), null, 0, IntPtr.Zero);
        }

        public static void Stop()
        {
            MciSendString("close " + Alias, null, 0, IntPtr.Zero);
        }
    }

    internal class MainForm : Form
    {
        // Beta opt-in (both builds) and Prod/Dev (INTERNAL_BUILD only) both
        // live in Settings now, not as boxes on the main form -- see
        // SettingsForm. Keeps the two builds' main windows identical.
        // VERSION dropdown: index 0 = automatic (newest per the Settings
        // beta opt-in); the rest mirror _versionTags (offset by one) as
        // populated by the startup status check.
        private ComboBox _cmbVersion;
        private readonly List<string> _versionTags = new List<string>();
        // TopShift reserves room for the title banner; BottomStrip reserves
        // room for the greetz ticker + mute button. Late-90s/early-2000s
        // "cracktro" theme: black background, neon green terminal text,
        // magenta box-art borders -- see MakeRetroBox/RetroButton.
        private const int TopShift = 74;
        private const int BottomStrip = 30;
        // Internal, not private: SettingsForm reuses the same palette and
        // chrome (MakeRetroBox, RetroButton) so the settings page reads as
        // part of the same "cracktro" theme instead of a bolted-on stock
        // WinForms dialog.
        internal static readonly Color ThemeGreen = Color.FromArgb(0, 255, 65);
        internal static readonly Color ThemeMagenta = Color.FromArgb(255, 0, 190);
        internal static readonly Color ThemeRed = Color.FromArgb(255, 60, 60);

        private Label _lblTitle;
        private Label _lblSubtitle;
        private Panel _dividerLine;
        private Label _lblInstalled;
        private Label _lblLatest;
        private RetroButton _btnInstall;
        private RetroButton _btnUninstall;
        private RetroButton _btnOpenFolder;
        private RetroButton _btnMute;
        private RetroButton _btnSettings;
        private Panel _tickerViewport;
        private Label _lblTicker;
        private RetroTextBox _txtLog;
        private ProgressBar _progress;
        private Label _lblStatus;
        private BackgroundWorker _worker;
        private BackgroundWorker _statusWorker;
        private BackgroundWorker _uninstallWorker;
        private Timer _tickerTimer;
        private Timer _cursorTimer;
        private bool _hasInstalledVersions;
        private bool _muted;
        private string _statusBase = "SYSTEM READY";
        private bool _cursorOn;

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        private const int PBM_SETBARCOLOR = 0x0409;
        private const int PBM_SETBKCOLOR = 0x2001;

        // PBM_SETBARCOLOR/PBM_SETBKCOLOR are silently ignored once Visual
        // Styles own the ProgressBar's rendering (Application.EnableVisualStyles
        // is on) -- SetWindowTheme(hwnd, "", "") strips theming from just this
        // control first, dropping it back to the classic renderer that
        // actually honors the color messages.
        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        private static extern int SetWindowTheme(IntPtr hWnd, string pszSubAppName, string pszSubIdList);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadCursorFromFile(string fileName);

        private static Cursor TryLoadCivCursor(string fileName)
        {
            try
            {
                string path = InstallerCore.TryGetCivCursorPath(fileName);
                if (path == null)
                {
                    return null;
                }
                IntPtr hCursor = LoadCursorFromFile(path);
                return hCursor == IntPtr.Zero ? null : new Cursor(hCursor);
            }
            catch (Exception)
            {
                return null; // keep whatever cursor the caller already had
            }
        }

        public MainForm()
        {
            Text = "KEK-MOD // LOADER";
            // ClientSize, not Width/Height: the latter includes the title bar
            // and borders, which shrinks the usable area every control below
            // is positioned against and clips the bottom row.
            ClientSize = new Size(560, 572 + TopShift + BottomStrip);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            BackColor = Color.Black;

            // Civ5's own cursors, if we can find them -- a nice bit of
            // nostalgia. Pointer.ani cascades from the form down to every
            // child that hasn't set its own explicit Cursor (which is why
            // RetroButton no longer forces Cursors.Hand). Deliberately NOT
            // `new Cursor(path)`: that constructor only parses static .cur
            // data and throws "Image format is not valid" on Pointer.ani,
            // which is a real RIFF-based animated cursor -- confirmed by
            // hand. Windows' own LoadCursorFromFile loads both formats
            // correctly, so P/Invoke it directly and wrap the handle.
            Cursor civPointer = TryLoadCivCursor("Pointer.ani");
            if (civPointer != null)
            {
                Cursor = civPointer;
            }

            _lblTitle = new Label();
            _lblTitle.SetBounds(10, 4, 400, 34);
            _lblTitle.Text = "KEK MOD";
            _lblTitle.Font = new Font("Consolas", 20F, FontStyle.Bold);
            _lblTitle.ForeColor = ThemeGreen;
            _lblTitle.BackColor = Color.Black;

            _lblSubtitle = new Label();
            _lblSubtitle.SetBounds(12, 40, 500, 16);
            _lblSubtitle.Text = "-=[ INSTALLER ]=-  RELEASED BY DEMOCRACIV";
            _lblSubtitle.Font = new Font("Consolas", 8.5F, FontStyle.Italic | FontStyle.Bold);
            _lblSubtitle.ForeColor = ThemeMagenta;
            _lblSubtitle.BackColor = Color.Black;

            _dividerLine = new Panel();
            _dividerLine.SetBounds(0, 60, 560, 2);
            _dividerLine.BackColor = ThemeMagenta;

            // Gear icon, top-right of the title bar -- the only way into
            // Settings (beta opt-in; also Prod/Dev on INTERNAL_BUILD -- see
            // SettingsForm). Deliberately not labelled "BETA" or anything
            // that would advertise the option to players who haven't gone
            // looking for it.
            _btnSettings = new RetroButton();
            _btnSettings.SetBounds(522, 6, 26, 22);
            _btnSettings.Text = "⚙"; // gear
            _btnSettings.Font = new Font("Segoe UI", 10F, FontStyle.Bold);
            _btnSettings.TextOffsetY = -2; // see RetroButton.TextOffsetY
            _btnSettings.Click += BtnSettings_Click;

            _lblInstalled = new Label();
            _lblInstalled.SetBounds(12, 10 + TopShift, 532, 18);
            _lblInstalled.Text = "[INSTALLED] checking...";
            _lblInstalled.Font = new Font("Consolas", 9F, FontStyle.Bold);
            _lblInstalled.ForeColor = ThemeGreen;
            _lblInstalled.BackColor = Color.Black;

            _lblLatest = new Label();
            _lblLatest.SetBounds(12, 30 + TopShift, 532, 18);
            _lblLatest.Text = "[LATEST]    checking...";
            _lblLatest.Font = new Font("Consolas", 9F, FontStyle.Bold);
            _lblLatest.ForeColor = ThemeGreen;
            _lblLatest.BackColor = Color.Black;

            // Full-width VERSION row -- Prod/Dev (internal builds) and the
            // beta opt-in both live in Settings now, so this row has no box
            // above it in either build; lets players install any published
            // release, not just the newest.
            var pnlVersion = MakeRetroBox("VERSION", 12, 132 + TopShift, 532, 48);

            _cmbVersion = new ComboBox();
            _cmbVersion.SetBounds(12, 18, 320, 22);
            _cmbVersion.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbVersion.FlatStyle = FlatStyle.Flat;
            _cmbVersion.BackColor = Color.Black;
            _cmbVersion.ForeColor = ThemeGreen;
            _cmbVersion.Font = new Font("Consolas", 9F, FontStyle.Bold);
            _cmbVersion.Items.Add("Latest (auto)"); // real text set by UpdateAutoVersionLabel below
            _cmbVersion.SelectedIndex = 0;
            pnlVersion.Controls.Add(_cmbVersion);

            _btnInstall = new RetroButton();
            _btnInstall.Text = "INSTALL";
            _btnInstall.SetBounds(12, 188 + TopShift, 110, 32);
            _btnInstall.Click += BtnInstall_Click;

            _btnUninstall = new RetroButton();
            _btnUninstall.Text = "UNINSTALL";
            _btnUninstall.SetBounds(130, 188 + TopShift, 110, 32);
            _btnUninstall.Enabled = false; // enabled once the status check finds something
            _btnUninstall.Click += BtnUninstall_Click;

            _progress = new ProgressBar();
            // Inset 1px inside a magenta-bordered panel -- a plain black bar
            // on a black form is otherwise invisible at 0%, before any fill
            // color would show.
            var progressBorder = new Panel();
            progressBorder.SetBounds(12, 228 + TopShift, 532, 20);
            progressBorder.BackColor = ThemeMagenta;

            _progress.SetBounds(1, 1, 530, 18);
            _progress.Style = ProgressBarStyle.Blocks;
            _progress.Minimum = 0;
            _progress.Maximum = 100;
            _progress.Value = 0;
            IntPtr progressHandle = _progress.Handle; // force creation so the P/Invoke calls have a real HWND
            SetWindowTheme(progressHandle, "", "");
            SendMessage(progressHandle, PBM_SETBARCOLOR, IntPtr.Zero, (IntPtr)ColorTranslator.ToWin32(ThemeGreen));
            SendMessage(progressHandle, PBM_SETBKCOLOR, IntPtr.Zero, (IntPtr)ColorTranslator.ToWin32(Color.Black));
            progressBorder.Controls.Add(_progress);

            _lblStatus = new Label();
            _lblStatus.SetBounds(12, 256 + TopShift, 532, 20);
            _lblStatus.Font = new Font("Consolas", 9F, FontStyle.Bold);
            _lblStatus.ForeColor = ThemeGreen;
            _lblStatus.BackColor = Color.Black;

            // Derived from ClientSize rather than a hand-tuned height, so the
            // log box and the Open Install Folder button below it never
            // overlap regardless of window height.
            int bottomStripY = ClientSize.Height - BottomStrip;
            int openFolderHeight = 28;
            int openFolderY = bottomStripY - 8 - openFolderHeight;
            int logTop = 280 + TopShift;
            int logHeight = openFolderY - 8 - logTop;

            _txtLog = new RetroTextBox();
            _txtLog.SetBounds(12, logTop, 532, logHeight);
            _txtLog.Multiline = true;
            _txtLog.ReadOnly = true;
            _txtLog.ScrollBars = ScrollBars.Vertical;
            _txtLog.Font = new Font("Consolas", 8.5F);
            _txtLog.BackColor = Color.Black;
            _txtLog.ForeColor = ThemeGreen;
            _txtLog.BorderStyle = BorderStyle.FixedSingle;
            _txtLog.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            // Civ5's own Edit cursor for the log console specifically --
            // thematically fitting for a text area, and an explicit
            // per-control Cursor plus the MouseMove override below is what it
            // actually takes to beat the native Edit control's built-in
            // I-beam (WM_SETCURSOR alone wasn't enough -- confirmed live).
            Cursor civEdit = TryLoadCivCursor("Edit.ani");
            if (civEdit != null)
            {
                _txtLog.Cursor = civEdit;
            }

            _btnOpenFolder = new RetroButton();
            _btnOpenFolder.Text = "OPEN INSTALL FOLDER";
            _btnOpenFolder.SetBounds(12, openFolderY, 170, openFolderHeight);
            _btnOpenFolder.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            _btnOpenFolder.Click += BtnOpenFolder_Click;

            _tickerViewport = new Panel();
            _tickerViewport.SetBounds(12, bottomStripY, 498, 20);
            _tickerViewport.BackColor = Color.Black;
            _tickerViewport.BorderStyle = BorderStyle.FixedSingle;
            _tickerViewport.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;

            // Default/fallback text -- shown until (and unless) the startup
            // status check pulls a fresher copy from installer/ticker.txt on
            // GitHub (see InstallerCore.TryFetchTickerText).
            _lblTicker = new Label();
            _lblTicker.AutoSize = true;
            _lblTicker.Font = new Font("Consolas", 8.5F, FontStyle.Bold);
            _lblTicker.ForeColor = ThemeGreen;
            _lblTicker.BackColor = Color.Black;
            _lblTicker.Text = "*** GREETZ TO ALL KEK-MOD PLAYERS OUT THERE ***  FUCK ALL THE HATING ASSES, RESYNCS, QUITTERS, UNSTABALIZERS ***  -=[ DEMOCRACIV ]=- ***    ";
            _lblTicker.Location = new Point(_tickerViewport.Width, 1);
            _tickerViewport.Controls.Add(_lblTicker);

            // Restore last session's mute state up front -- _muted drives
            // both this button's look and RetroAudio.SetMuted below, once
            // the track actually starts playing at the end of the ctor.
            _muted = SettingsManager.GetMuted();

            _btnMute = new RetroButton();
            _btnMute.SetBounds(522, bottomStripY, 26, 22);
            _btnMute.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            _btnMute.Text = _muted ? "✕" : "♪"; // X : eighth note
            _btnMute.ForeColor = _muted ? ThemeRed : ThemeGreen;
            _btnMute.Font = new Font("Segoe UI", 10F, FontStyle.Bold);
            _btnMute.Click += BtnMute_Click;

            Controls.Add(_lblTitle);
            Controls.Add(_lblSubtitle);
            Controls.Add(_dividerLine);
            Controls.Add(_lblInstalled);
            Controls.Add(_lblLatest);
            Controls.Add(pnlVersion);
            Controls.Add(_btnInstall);
            Controls.Add(_btnUninstall);
            Controls.Add(progressBorder);
            Controls.Add(_lblStatus);
            Controls.Add(_txtLog);
            Controls.Add(_btnOpenFolder);
            Controls.Add(_tickerViewport);
            Controls.Add(_btnMute);
            Controls.Add(_btnSettings);

            UpdateAutoVersionLabel();

            _worker = new BackgroundWorker();
            _worker.WorkerReportsProgress = true;
            _worker.DoWork += Worker_DoWork;
            _worker.ProgressChanged += Worker_ProgressChanged;
            _worker.RunWorkerCompleted += Worker_RunWorkerCompleted;

            _uninstallWorker = new BackgroundWorker();
            _uninstallWorker.WorkerReportsProgress = true;
            _uninstallWorker.DoWork += UninstallWorker_DoWork;
            _uninstallWorker.ProgressChanged += Worker_ProgressChanged;
            _uninstallWorker.RunWorkerCompleted += UninstallWorker_RunWorkerCompleted;

            _statusWorker = new BackgroundWorker();
            _statusWorker.DoWork += StatusWorker_DoWork;
            _statusWorker.RunWorkerCompleted += StatusWorker_RunWorkerCompleted;
            _statusWorker.RunWorkerAsync();

            _tickerTimer = new Timer();
            _tickerTimer.Interval = 30;
            _tickerTimer.Tick += TickerTimer_Tick;
            _tickerTimer.Start();

            _cursorTimer = new Timer();
            _cursorTimer.Interval = 500;
            _cursorTimer.Tick += CursorTimer_Tick;
            _cursorTimer.Start();
            SetStatus(_statusBase);

            FormClosing += MainForm_FormClosing;

            RetroAudio.PlayEmbeddedLooped();
            if (_muted)
            {
                RetroAudio.SetMuted(true); // restore last session's mute state
            }
        }

        // Draws a magenta box with the title "cut into" the top border, e.g.
        // +--[ CHANNEL ]---------+   -- the classic scene-intro/keygen box
        // art look, hand-painted since GroupBox's native border ignores
        // theme colors even with visual styles off. Static (doesn't touch
        // instance state) so SettingsForm can reuse it too.
        internal static Panel MakeRetroBox(string title, int x, int y, int w, int h)
        {
            var panel = new Panel();
            panel.SetBounds(x, y, w, h);
            panel.BackColor = Color.Black;
            var titleFont = new Font("Consolas", 8F, FontStyle.Bold);
            panel.Paint += (s, e) =>
            {
                using (var pen = new Pen(ThemeMagenta))
                {
                    e.Graphics.DrawRectangle(pen, 0, 0, panel.Width - 1, panel.Height - 1);
                }
                string label = " " + title + " ";
                SizeF sz = e.Graphics.MeasureString(label, titleFont);
                e.Graphics.FillRectangle(Brushes.Black, 8, 0, sz.Width, sz.Height);
                using (var brush = new SolidBrush(ThemeMagenta))
                {
                    e.Graphics.DrawString(label, titleFont, brush, 8, -1);
                }
            };
            return panel;
        }

        // Fully self-painted button: FlatStyle.Flat's built-in disabled-text
        // renderer ignores custom ForeColor and falls back to a system gray
        // that's unreadable against black (confirmed live -- UNINSTALL's
        // label went fully invisible while disabled). Owner-drawing sidesteps
        // that entirely and keeps enabled/disabled/hover all under our
        // control, matching the hand-painted box borders elsewhere.
        // Internal, not private: SettingsForm builds its OK/Cancel buttons
        // from this too, for the same hand-drawn look as the rest of the app.
        internal class RetroButton : Button
        {
            public RetroButton()
            {
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
                FlatStyle = FlatStyle.Flat;
                FlatAppearance.BorderSize = 0; // border is hand-drawn in OnPaint
                BackColor = Color.Black;
                ForeColor = ThemeGreen;
                Font = new Font("Consolas", 9F, FontStyle.Bold);
                // Deliberately no explicit Cursor here (was Cursors.Hand) --
                // leave it unset so the Civ5 pointer set on the form cascades
                // down to buttons too, instead of every clickable control
                // fighting the theme with a generic Windows hand icon.
            }

            // Nonzero only for glyphs whose visible ink isn't centered
            // within its own font's ascent+descent box -- TextFormatFlags.
            // VerticalCenter centers that whole box, not the ink, so a glyph
            // like the settings gear (measured live: 5px clear above the
            // ink, 2px below, in a 21px-tall button) still reads as
            // off-center even with NoPadding. Positive shifts the glyph down.
            internal int TextOffsetY;

            private bool _hover;

            protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
            protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
            protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

            protected override void OnPaint(PaintEventArgs pe)
            {
                Color border = Enabled ? ThemeMagenta : Color.FromArgb(90, 0, 65);
                Color text = Enabled ? ForeColor : Color.FromArgb(0, 100, 40);
                Color bg = (_hover && Enabled) ? Color.FromArgb(25, 25, 25) : Color.Black;

                pe.Graphics.Clear(bg);
                using (var pen = new Pen(border))
                {
                    pe.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
                }
                // NoPadding matters here: without it, DrawText pads the
                // layout rect by a few pixels on the left/top before
                // centering inside it, which reads as fine on wide text
                // (INSTALL, UNINSTALL) but visibly off-center on the square
                // single-glyph buttons (gear, mute) -- confirmed live, the
                // gear sat noticeably high-left of its box without this flag.
                Rectangle textRect = ClientRectangle;
                if (TextOffsetY != 0)
                {
                    textRect.Offset(0, TextOffsetY);
                }
                TextRenderer.DrawText(pe.Graphics, Text, Font, textRect, text,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
        }

        // Plain TextBox.Cursor is overridden by the native Win32 Edit
        // control's own class cursor (always an I-beam over the text area,
        // regardless of what the managed Cursor property says) -- confirmed
        // live, the custom cursor showed everywhere except the log box even
        // after intercepting WM_SETCURSOR. The Edit control apparently calls
        // SetCursor() directly from its own WM_MOUSEMOVE handling, a path
        // WM_SETCURSOR interception doesn't see -- so re-assert ours on every
        // MouseMove too, AFTER the base handling has already run its course,
        // so ours is the last word.
        private class RetroTextBox : TextBox
        {
            private const int WM_SETCURSOR = 0x0020;

            protected override void WndProc(ref Message m)
            {
                if (m.Msg == WM_SETCURSOR)
                {
                    Cursor.Current = Cursor;
                    m.Result = (IntPtr)1;
                    return;
                }
                base.WndProc(ref m);
            }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                base.OnMouseMove(e);
                Cursor.Current = Cursor;
            }
        }

        // Internal, not private: SettingsForm's Prod/Dev radios (internal
        // builds only) use this too, for the same look as everything else.
        internal static void StyleRetroRadio(RadioButton r)
        {
            r.ForeColor = ThemeGreen;
            r.BackColor = Color.Black;
            r.Font = new Font("Consolas", 8.5F);
        }

        // All status-line updates go through here so the blinking terminal
        // cursor (CursorTimer_Tick) always reflects the current text instead
        // of freezing mid-blink on whatever was last set directly.
        private void SetStatus(string text)
        {
            _statusBase = text;
            _lblStatus.Text = "> " + _statusBase + (_cursorOn ? "_" : " ");
        }

        private void CursorTimer_Tick(object sender, EventArgs e)
        {
            _cursorOn = !_cursorOn;
            _lblStatus.Text = "> " + _statusBase + (_cursorOn ? "_" : " ");
        }

        private void TickerTimer_Tick(object sender, EventArgs e)
        {
            _lblTicker.Left -= 2;
            if (_lblTicker.Right < 0)
            {
                _lblTicker.Left = _tickerViewport.Width;
            }
        }

        private void BtnMute_Click(object sender, EventArgs e)
        {
            _muted = !_muted;
            RetroAudio.SetMuted(_muted);
            _btnMute.Text = _muted ? "✕" : "♪"; // X : eighth note
            _btnMute.ForeColor = _muted ? ThemeRed : ThemeGreen;
            SettingsManager.SetMuted(_muted);
        }

        // Beta builds are opt-in and hidden from the main UI until the
        // player has gone into Settings and turned them on -- there's no
        // separate CHANNEL selector any more, so the Settings checkbox is
        // the single source of truth for "does this install want beta
        // releases" (BtnInstall_Click reads it directly). The VERSION
        // dropdown and [LATEST] line are filtered the same way in
        // StatusWorker_DoWork/RunWorkerCompleted so a hidden beta tag can't
        // leak through there either.
        //
        // Index 0's wording is the only thing here that depends on the
        // setting -- reassigning by index (not Add/Remove) keeps
        // SelectedIndex, and thus whatever the user already picked, intact.
        private void UpdateAutoVersionLabel()
        {
            bool showBeta = SettingsManager.GetShowBeta();
            _cmbVersion.Items[0] = showBeta
                ? "Latest (auto -- newest, incl. beta)"
                : "Latest (auto -- newest stable)";
        }

        private void BtnSettings_Click(object sender, EventArgs e)
        {
            using (var dlg = new SettingsForm())
            {
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    UpdateAutoVersionLabel();
                    // Re-run the startup check so the VERSION dropdown and
                    // [LATEST] line immediately reflect the new setting
                    // instead of waiting for the next launch.
                    if (!_statusWorker.IsBusy && !_worker.IsBusy && !_uninstallWorker.IsBusy)
                    {
                        _lblLatest.Text = "[LATEST]    checking...";
                        _statusWorker.RunWorkerAsync();
                    }
                }
            }
        }

        private void MainForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            _tickerTimer.Stop();
            _cursorTimer.Stop();
            RetroAudio.Stop();
        }

        // Startup check: what's installed already, what's newest on GitHub.
        // Both can fail independently (no Civ5 found; offline) -- neither
        // should block the form, so failures just show as "couldn't check".
        private void StatusWorker_DoWork(object sender, DoWorkEventArgs e)
        {
            string installedText;
            bool anyInstalled = false;
            try
            {
                List<string> installed = InstallerCore.DetectInstalledVersions();
                anyInstalled = installed.Count > 0;
                installedText = installed.Count == 0
                    ? "[INSTALLED] none detected"
                    : "[INSTALLED] " + string.Join(", ", installed.ToArray());
            }
            catch (Exception ex)
            {
                installedText = "[INSTALLED] couldn't check (" + ex.Message + ")";
            }

            bool showBeta = SettingsManager.GetShowBeta();

            string latestText;
            try
            {
                latestText = "[LATEST]    " + InstallerCore.DescribeLatestAvailable(showBeta);
            }
            catch (Exception ex)
            {
                latestText = "[LATEST]    couldn't check (" + ex.Message + ")";
            }

            // Best-effort refresh of the greetz ticker from GitHub; a null
            // here just means StatusWorker_RunWorkerCompleted leaves the
            // default text already on screen alone.
            string tickerText = InstallerCore.TryFetchTickerText();

            // Version list for the VERSION dropdown -- best-effort too: null
            // leaves the dropdown with just its "Latest (auto)" entry.
            // Prerelease tags are stripped out entirely when beta builds
            // aren't enabled in Settings, so a hidden beta can't be picked
            // by tag even though it's technically published.
            List<GhRelease> releases = null;
            try
            {
                releases = InstallerCore.ListAvailableReleases();
                if (!showBeta)
                {
                    releases = releases.Where(r => !r.Prerelease).ToList();
                }
            }
            catch (Exception)
            {
                // offline / rate-limited -- auto mode still works at install
                // time (Run() does its own fetch and reports its own error).
            }

            e.Result = new object[] { installedText, latestText, anyInstalled, tickerText, releases };
        }

        private void StatusWorker_RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            if (e.Error != null)
            {
                return; // leave the "checking..." placeholders rather than alarm the user
            }
            var result = (object[])e.Result;
            _lblInstalled.Text = (string)result[0];
            _lblLatest.Text = (string)result[1];
            _hasInstalledVersions = (bool)result[2];
            string tickerText = (string)result[3];

            // Fill the VERSION dropdown, preserving whatever the user already
            // picked (by tag) if this ever re-runs.
            var releases = (List<GhRelease>)result[4];
            if (releases != null && releases.Count > 0)
            {
                string keepTag = _cmbVersion.SelectedIndex > 0
                    ? _versionTags[_cmbVersion.SelectedIndex - 1] : null;
                while (_cmbVersion.Items.Count > 1)
                {
                    _cmbVersion.Items.RemoveAt(1);
                }
                _versionTags.Clear();
                foreach (GhRelease rel in releases)
                {
                    _versionTags.Add(rel.TagName);
                    _cmbVersion.Items.Add(rel.TagName + (rel.Prerelease ? "  [beta]" : "  [stable]"));
                }
                int keepIdx = keepTag == null ? -1 : _versionTags.IndexOf(keepTag);
                _cmbVersion.SelectedIndex = keepIdx >= 0 ? keepIdx + 1 : 0;
            }
            if (tickerText != null)
            {
                _lblTicker.Text = tickerText;
                _lblTicker.Location = new Point(_tickerViewport.Width, 1); // restart the scroll cleanly
            }
            // Only touch the button if nothing else is currently running --
            // don't re-enable it mid-install/uninstall.
            if (!_worker.IsBusy && !_uninstallWorker.IsBusy)
            {
                _btnUninstall.Enabled = _hasInstalledVersions;
            }
        }

        private void BtnInstall_Click(object sender, EventArgs e)
        {
            _txtLog.Clear();
            SetControlsEnabled(false);
            _progress.Value = 0;
            SetStatus("INSTALLING MOD FILES...");

            var options = new InstallOptions();
            options.WantBeta = SettingsManager.GetShowBeta();
            options.TagName = _cmbVersion.SelectedIndex > 0
                ? _versionTags[_cmbVersion.SelectedIndex - 1] : null;
#if INTERNAL_BUILD
            options.WantDev = SettingsManager.GetUseDev();
#else
            options.WantDev = false;
#endif
            _worker.RunWorkerAsync(options);
        }

        private void Worker_DoWork(object sender, DoWorkEventArgs e)
        {
            var options = (InstallOptions)e.Argument;
            var worker = (BackgroundWorker)sender;
            int lastPercent = 0;
            Action<string> log = msg => worker.ReportProgress(lastPercent, msg);
            Action<int> progress = pct => { lastPercent = pct; worker.ReportProgress(pct, null); };
            e.Result = InstallerCore.Run(options, log, progress);
        }

        private void Worker_ProgressChanged(object sender, ProgressChangedEventArgs e)
        {
            _progress.Value = e.ProgressPercentage;
            if (e.UserState != null)
            {
                _txtLog.AppendText((string)e.UserState + Environment.NewLine);
            }
        }

        private void Worker_RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            SetControlsEnabled(true);

            if (e.Error != null)
            {
                SetStatus("FAILED: " + e.Error.Message);
                _txtLog.AppendText(Environment.NewLine + "ERROR: " + e.Error.Message + Environment.NewLine);
                return;
            }

            var result = (InstallResult)e.Result;
            SetStatus("[INSTALLED] " + result.FolderName);

            // Refresh the "Installed" line so it reflects what's actually on
            // disk now, without needing a restart.
            if (!_statusWorker.IsBusy)
            {
                _lblInstalled.Text = "[INSTALLED] checking...";
                _statusWorker.RunWorkerAsync();
            }
        }

        private void BtnOpenFolder_Click(object sender, EventArgs e)
        {
            string dlcFolder = InstallerCore.TryGetDlcFolder();
            if (dlcFolder != null && Directory.Exists(dlcFolder))
            {
                Process.Start("explorer.exe", "\"" + dlcFolder + "\"");
            }
            else
            {
                MessageBox.Show(
                    this,
                    "Couldn't find a Civilization V install with Steam. Install the mod first, or locate Civilization V manually via Install.",
                    "Open install folder",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
        }

        private void BtnUninstall_Click(object sender, EventArgs e)
        {
            DialogResult confirm = MessageBox.Show(
                this,
                "Remove all installed KEK Mod versions and the Fish Map Script from your Civilization V install?",
                "Uninstall KEK Mod",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);
            if (confirm != DialogResult.Yes)
            {
                return;
            }

            _txtLog.Clear();
            SetControlsEnabled(false);
            _progress.Value = 0;
            SetStatus("REMOVING KEK-MOD FILES...");

            _uninstallWorker.RunWorkerAsync();
        }

        private void UninstallWorker_DoWork(object sender, DoWorkEventArgs e)
        {
            var worker = (BackgroundWorker)sender;
            int lastPercent = 0;
            Action<string> log = msg => worker.ReportProgress(lastPercent, msg);
            Action<int> progress = pct => { lastPercent = pct; worker.ReportProgress(pct, null); };
            InstallerCore.Uninstall(log, progress);
        }

        private void UninstallWorker_RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            SetControlsEnabled(true);

            if (e.Error != null)
            {
                SetStatus("FAILED: " + e.Error.Message);
                _txtLog.AppendText(Environment.NewLine + "ERROR: " + e.Error.Message + Environment.NewLine);
                return;
            }

            SetStatus("UNINSTALLED.");

            if (!_statusWorker.IsBusy)
            {
                _lblInstalled.Text = "[INSTALLED] checking...";
                _statusWorker.RunWorkerAsync();
            }
        }

        private void SetControlsEnabled(bool enabled)
        {
            _cmbVersion.Enabled = enabled;
            _btnInstall.Enabled = enabled;
            _btnUninstall.Enabled = enabled && _hasInstalledVersions;
        }
    }

    // Small modal dialog reached only via MainForm's gear button. "Enable
    // beta builds" is the sole way to surface beta tags in the VERSION
    // dropdown and have Install/[LATEST] consider them (see
    // MainForm.UpdateAutoVersionLabel and BtnInstall_Click). INTERNAL_BUILD
    // adds a second, Prod/Dev, section below it -- moved here from a BUILD
    // box that used to sit on the main form, so the two builds' main
    // windows now look identical and only this dialog differs between them.
    internal class SettingsForm : Form
    {
        private CheckBox _chkShowBeta;
#if INTERNAL_BUILD
        private RadioButton _rbProd;
        private RadioButton _rbDev;
#endif

        public SettingsForm()
        {
            Text = "KEK-MOD // SETTINGS";
#if INTERNAL_BUILD
            ClientSize = new Size(320, 230);
#else
            ClientSize = new Size(320, 150);
#endif
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            BackColor = Color.Black;

            Panel pnlBeta = MainForm.MakeRetroBox("BETA", 12, 12, 296, 60);

            _chkShowBeta = new CheckBox();
            _chkShowBeta.Text = "Enable beta builds";
            _chkShowBeta.SetBounds(12, 22, 260, 20);
            _chkShowBeta.ForeColor = MainForm.ThemeGreen;
            _chkShowBeta.BackColor = Color.Black;
            _chkShowBeta.Font = new Font("Consolas", 8.5F);
            _chkShowBeta.Checked = SettingsManager.GetShowBeta();
            pnlBeta.Controls.Add(_chkShowBeta);

#if INTERNAL_BUILD
            Panel pnlBuild = MainForm.MakeRetroBox("BUILD", 12, 80, 296, 70);

            _rbProd = new RadioButton();
            _rbProd.Text = "Prod (recommended)";
            _rbProd.SetBounds(12, 22, 260, 20);
            _rbProd.Checked = !SettingsManager.GetUseDev();
            MainForm.StyleRetroRadio(_rbProd);

            _rbDev = new RadioButton();
            _rbDev.Text = "Dev (internal test server)";
            _rbDev.SetBounds(12, 44, 260, 20);
            _rbDev.Checked = SettingsManager.GetUseDev();
            MainForm.StyleRetroRadio(_rbDev);

            pnlBuild.Controls.Add(_rbProd);
            pnlBuild.Controls.Add(_rbDev);
#endif

            var lblHint = new Label();
#if INTERNAL_BUILD
            lblHint.SetBounds(12, 158, 296, 32);
#else
            lblHint.SetBounds(12, 80, 296, 32);
#endif
            lblHint.Text = "Shows beta tags in VERSION and includes\r\nthem in Install/[LATEST]. Off by default.";
            lblHint.Font = new Font("Consolas", 7.5F);
            lblHint.ForeColor = MainForm.ThemeMagenta;
            lblHint.BackColor = Color.Black;

            var btnOk = new MainForm.RetroButton();
            btnOk.Text = "OK";
#if INTERNAL_BUILD
            btnOk.SetBounds(126, 194, 90, 26);
#else
            btnOk.SetBounds(126, 116, 90, 26);
#endif
            btnOk.Click += BtnOk_Click;

            var btnCancel = new MainForm.RetroButton();
            btnCancel.Text = "CANCEL";
#if INTERNAL_BUILD
            btnCancel.SetBounds(220, 194, 88, 26);
#else
            btnCancel.SetBounds(220, 116, 88, 26);
#endif
            btnCancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };

            Controls.Add(pnlBeta);
#if INTERNAL_BUILD
            Controls.Add(pnlBuild);
#endif
            Controls.Add(lblHint);
            Controls.Add(btnOk);
            Controls.Add(btnCancel);

            AcceptButton = btnOk;
        }

        private void BtnOk_Click(object sender, EventArgs e)
        {
            SettingsManager.SetShowBeta(_chkShowBeta.Checked);
#if INTERNAL_BUILD
            SettingsManager.SetUseDev(_rbDev.Checked);
#endif
            DialogResult = DialogResult.OK;
            Close();
        }
    }

    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            // Set once, before any HttpClient call (including the startup
            // status check) -- .NET Framework doesn't reliably default to
            // TLS 1.2, which GitHub's API requires.
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }
}
