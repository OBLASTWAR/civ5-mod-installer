// KekModInstaller -- pulls the latest published mod build from GitHub or
// Google Drive and drops it into the local Civ V DLC folder. Small WinForms
// GUI: pick a mod/channel/build, click Install.
//
// Split across a few files: this one (Installer.cs) is the installer PROGRAM
// itself -- UI (MainForm/SettingsForm), retro theme/audio, and the
// installer's own self-update + greetz-ticker fetch (both pinned to
// OBLASTWAR/kek-mod's main branch regardless of which mod is selected, since
// that's just where this installer program is hosted). Everything about the
// MODS it can install -- the ModDefinition/IModSource abstraction, and where
// Civ5 itself lives on disk -- is in ModCore.cs, GitHubModSource.cs, and
// MapScriptExtra.cs. InstallerCore is declared `partial` and split across
// Installer.cs and ModCore.cs for that reason.
//
// Mirrors, on the client side, what stage.sh/deploy.sh already do on the dev
// machine for kek-mod: same "KEK Mod v<version>" folder naming, same
// "replace only this version's own folder, leave others alone" rule, same
// ui_check.bat-last step.
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
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace KekModInstaller
{
    // Persists the settings-page opt-ins across launches. Off by default:
    // regular players shouldn't see beta as an option at all unless they've
    // deliberately dug into Settings for it.
    internal static class SettingsManager
    {
        private const string RegistryKey = @"HKEY_CURRENT_USER\Software\KekModInstaller";
        private const string ShowBetaValueName = "ShowBetaBuilds";
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

    // Core logic for the installer PROGRAM itself: self-update and the
    // greetz ticker. Declared `partial` -- the other half of this class
    // (ModCore.cs) holds everything mod-agnostic: install/uninstall/detect,
    // driven entirely by a ModDefinition, plus where Civ5 lives on disk.
    // Reports progress through a plain Action<string> so both the WinForms
    // log box and (if ever needed again) a console could drive it the same
    // way.
    internal static partial class InstallerCore
    {
        // Repo this installer program itself is published from -- used only
        // for the self-update and ticker fetches below, NOT for fetching any
        // mod's own releases (see GitHubModSource for that).
        private const string RepoOwner = "OBLASTWAR";
        private const string RepoName = "kek-mod";

        // Branch the scrolling greetz ticker is read from at startup, so the
        // message can be edited on GitHub without shipping a new installer
        // build. Points at main, not a release/* branch -- release branches
        // are short-lived (merged into main and deleted once a version
        // ships), so a ticker fetch pinned to one would start 404ing the
        // moment that branch goes away.
        private const string TickerBranch = "main";

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

        // This build's own version -- bump alongside installer/installer_version.txt
        // (main branch) whenever KekModInstaller.exe is rebuilt for a release, so
        // running copies out in the wild notice the newer one via
        // TryFetchLatestInstallerVersion below. Unrelated to the mod's own
        // version (release tags like "v1.5-beta8") -- this is the installer
        // program's own version.
        private const string InstallerVersion = "1.4";

        public static string GetInstallerVersion()
        {
            return InstallerVersion;
        }

        // Same fetch pattern as TryFetchTickerText -- never throws, null just
        // means the caller can't tell whether a newer installer exists and
        // should leave the UPDATE button hidden.
        public static string TryFetchLatestInstallerVersion()
        {
            string url = "https://raw.githubusercontent.com/" + RepoOwner + "/" + RepoName
                + "/" + TickerBranch + "/installer/installer_version.txt";
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

        // System.Version requires at least a major.minor pair -- "1" alone
        // fails to parse where "1.0" doesn't, so pad single-component
        // versions before comparing. Falls back to a plain string-inequality
        // check if either side isn't a well-formed dotted-number version, so
        // a hand-typed installer_version.txt typo still surfaces as "some
        // update is available" rather than silently hiding the button.
        public static bool IsNewerVersion(string remote, string local)
        {
            Version remoteVersion, localVersion;
            if (Version.TryParse(PadVersion(remote), out remoteVersion)
                && Version.TryParse(PadVersion(local), out localVersion))
            {
                return remoteVersion > localVersion;
            }
            return !string.Equals(remote, local, StringComparison.OrdinalIgnoreCase);
        }

        private static string PadVersion(string v)
        {
            return v.IndexOf('.') < 0 ? v + ".0" : v;
        }

        // Downloads this build's own exe (same repo path the running copy was
        // itself published from -- "installer/KekModInstaller.exe" or
        // "installer/KekModInstaller.Internal.exe", whichever this process
        // is) and replaces it in place, then relaunches. A running Windows
        // exe can't overwrite its own file (still locked/mapped), so the
        // swap happens via a short-lived helper batch script: this process
        // downloads the new exe to %TEMP%, writes a .bat that waits a couple
        // seconds for this process to exit, copies the new exe over the old
        // one, relaunches it, then deletes itself. Caller must exit the app
        // (Application.Exit) right after this returns.
        public static void DownloadAndApplySelfUpdate()
        {
            string exePath = Assembly.GetExecutingAssembly().Location;
            string exeName = Path.GetFileName(exePath);
            string url = "https://raw.githubusercontent.com/" + RepoOwner + "/" + RepoName
                + "/" + TickerBranch + "/installer/" + exeName;

            string newExePath = Path.Combine(Path.GetTempPath(), "kekmod_installer_update_" + Guid.NewGuid().ToString("N") + ".exe");
            using (var client = new HttpClient())
            {
                client.DefaultRequestHeaders.Add("User-Agent", "KekModInstaller");
                byte[] data = client.GetByteArrayAsync(url).GetAwaiter().GetResult();
                File.WriteAllBytes(newExePath, data);
            }

            // "ping -n 3" instead of "timeout" -- timeout needs an attached
            // console to read from and fails ("INPUT redirection is not
            // supported") when launched hidden/redirected the way this batch
            // is; the old ping-as-sleep trick works unconditionally.
            string batPath = Path.Combine(Path.GetTempPath(), "kekmod_installer_update_" + Guid.NewGuid().ToString("N") + ".bat");
            string batContent =
                "@echo off\r\n" +
                "ping 127.0.0.1 -n 3 >nul\r\n" +
                "copy /y \"" + newExePath + "\" \"" + exePath + "\" >nul\r\n" +
                "del \"" + newExePath + "\"\r\n" +
                "start \"\" \"" + exePath + "\"\r\n" +
                "del \"%~f0\"\r\n";
            File.WriteAllText(batPath, batContent);

            var psi = new ProcessStartInfo();
            psi.FileName = "cmd.exe";
            psi.Arguments = "/c \"" + batPath + "\"";
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.WindowStyle = ProcessWindowStyle.Hidden;
            Process.Start(psi);
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
        // Beta opt-in lives in Settings now, not as a box on the main form
        // -- see SettingsForm.

        // One bordered box per ModRegistry.All entry, each titled with that
        // mod's own name -- leaves room for a sub-row per mod (its bonus
        // component, e.g. kek-mod's Fish Map Script) inside the same box,
        // which a single shared "MOD" box with plain rows couldn't express.
        // Install state is shown via a small dot indicator + name-text color
        // (UpdateModRowStyles) and via the box's own border color; each box
        // carries its own INSTALL/UNINSTALL/VERSION buttons on the right --
        // there's no separate "select a mod first" step.
        private class ModRowControls
        {
            public Panel Box;
            public Label Indicator;
            public Label NameLabel;
            public Label ExtraIndicator; // null if this mod has no bonus component
            public Label ExtraNameLabel; // null if this mod has no bonus component
            public RetroButton InstallButton;
            public RetroButton UninstallButton;
            public RetroButton VersionButton;
        }
        private readonly List<ModRowControls> _modRows = new List<ModRowControls>();
        // True while an Install/Uninstall/EUI-change worker is running --
        // every per-box button is force-disabled during that window instead
        // of relying solely on Control.Enabled bookkeeping scattered
        // elsewhere.
        private bool _actionInProgress;
        private readonly HashSet<string> _installedModIds = new HashSet<string>();
        // Version currently on disk for whichever mods are installed (e.g.
        // "v1.4", "12.2a") -- shown next to the mod's name in its box. Keyed
        // by mod id; a mod missing from here just isn't installed.
        private readonly Dictionary<string, string> _installedVersionByModId = new Dictionary<string, string>();
        // Same idea for a mod's bonus component (e.g. Fish Map Script,
        // Better Pangaea) -- keyed by ModDefinition.ExtraModId. Only
        // populated when the detector could actually determine a version
        // (see DetectedModInstall.VersionLabel); a mod's extra missing from
        // here just shows its name with no version suffix.
        private readonly Dictionary<string, string> _installedExtraVersionByModId = new Dictionary<string, string>();
        // Mods where what's installed isn't what auto-install would pick
        // right now (see IsUpdateAvailable) -- turns that mod's INSTALL
        // button into an UPDATE button, see UpdateModRowStyles.
        private readonly HashSet<string> _updateAvailableModIds = new HashSet<string>();

        // EUI section: same per-box-buttons treatment as the mod boxes --
        // each EuiVariant gets its own box with INSTALL/REMOVE buttons (no
        // VERSION button: this is a single bundled copy we ship ourselves,
        // not something with published releases to choose between).
        private class EuiRowControls
        {
            public Panel Box;
            public Label Indicator;
            public Label NameLabel;
            public RetroButton InstallButton;
            public RetroButton RemoveButton;
        }
        private readonly List<EuiRowControls> _euiRows = new List<EuiRowControls>();
        private string _installedEuiVariantId; // null if no EUI installed
        // True if _installedEuiVariantId is set but EuiExtra.IsExactBundledMatch
        // says what's actually on disk isn't byte-identical to what this
        // build bundles for that variant -- e.g. the player manually
        // installed a newer/older/modified EUI themselves. Every mod we ship
        // is only tested against our exact bundled copy, so this matters.
        private bool _installedEuiVersionMismatch;
        private BackgroundWorker _euiWorker;
        // Per-mod version choice from that mod's own VERSION button/popup --
        // absent (or not in the dictionary) means "Latest (auto)".
        private readonly Dictionary<string, string> _selectedVersionTagByModId = new Dictionary<string, string>();
        // Set only while an install-triggered uninstall (see
        // OnModInstallClick) of a conflicting mod is running, so
        // UninstallWorker_RunWorkerCompleted knows to proceed straight into
        // installing this mod with these options afterward, instead of just
        // refreshing the status display.
        private ModDefinition _pendingInstallMod;
        private InstallOptions _pendingInstallOptions;
        private const int ModRowHeight = 24;
        // TopShift reserves room for the title banner; BottomStrip reserves
        // room for the greetz ticker + mute button. Late-90s/early-2000s
        // "cracktro" theme: black background, neon green terminal text,
        // magenta box-art borders -- see MakeRetroBox/RetroButton.
        private const int TopShift = 60;
        private const int BottomStrip = 30;
        // Internal, not private: SettingsForm reuses the same palette and
        // chrome (MakeRetroBox, RetroButton) so the settings page reads as
        // part of the same "cracktro" theme instead of a bolted-on stock
        // WinForms dialog.
        internal static readonly Color ThemeGreen = Color.FromArgb(0, 255, 65);
        internal static readonly Color ThemeMagenta = Color.FromArgb(255, 0, 190);
        internal static readonly Color ThemeRed = Color.FromArgb(255, 60, 60);
        // Dim/"off" indicator color -- matches RetroButton's disabled-text
        // color so "not installed" reads consistently across the UI.
        internal static readonly Color ThemeDim = Color.FromArgb(0, 100, 40);

        private Label _lblTitle;
        private Label _lblTitleTag;
        private Panel _dividerLine;
        // Every mod's release list, fetched once at startup (see
        // StatusWorker_DoWork) and reused for every VERSION-dropdown
        // refresh afterward -- switching mods, toggling the beta setting,
        // or finishing an install/uninstall/EUI change never re-fetches,
        // so casual clicking around can't run into GitHub's unauthenticated
        // 60-requests/hour rate limit. A mod missing from this dictionary
        // means its startup fetch failed (offline/rate-limited/etc.); that
        // mod's VERSION popup just shows "Latest (auto)" alone and Install
        // still works via its own live fetch at install time regardless.
        private Dictionary<string, List<ModRelease>> _releasesByModId = new Dictionary<string, List<ModRelease>>();
        private RetroButton _btnOpenFolder;
        private RetroButton _btnScanExtras;
        private RetroButton _btnClearGfx;
        private RetroButton _btnLaunchDx9;
        private RetroButton _btnLaunchDx11;
        private RetroButton _btnMute;
        private RetroButton _btnSettings;
        private RetroButton _btnUpdate;
        private string _latestInstallerVersion;
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
        private ToolTip _tooltip;
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
            Text = "CIV V MOD INSTALLER";
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            BackColor = Color.Black;

            _tooltip = new ToolTip();

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
            _lblTitle.Text = "CIV V MOD INSTALLER";
            _lblTitle.Font = new Font("Consolas", 14F, FontStyle.Bold);
            _lblTitle.ForeColor = ThemeGreen;
            _lblTitle.BackColor = Color.Black;
            _lblTitle.AutoSize = true;
            _lblTitle.Location = new Point(10, 4);

            _lblTitleTag = new Label();
            _lblTitleTag.Text = "-=[ v" + InstallerCore.GetInstallerVersion() + " @OBLAST ]=-";
            _lblTitleTag.Font = new Font("Consolas", 8.5F, FontStyle.Italic | FontStyle.Bold);
            _lblTitleTag.ForeColor = ThemeMagenta;
            _lblTitleTag.BackColor = Color.Black;
            _lblTitleTag.AutoSize = true;
            // AutoSize on both means no fixed box to clip against; follows
            // the title's actual rendered size/right edge, vertically
            // centered on it, rather than a hand-guessed offset.
            _lblTitleTag.Location = new Point(
                _lblTitle.Right + 160,
                _lblTitle.Top + (_lblTitle.Height - _lblTitleTag.PreferredSize.Height) / 2);

            _dividerLine = new Panel();
            _dividerLine.SetBounds(0, 44, 560, 2);
            _dividerLine.BackColor = ThemeMagenta;

            // Gear icon, top-right of the title bar -- the only way into
            // Settings (beta opt-in -- see SettingsForm). Deliberately not
            // labelled "BETA" or anything that would advertise the option
            // to players who haven't gone looking for it.
            _btnSettings = new RetroButton();
            _btnSettings.SetBounds(522, 6, 26, 22);
            _btnSettings.Text = "⚙"; // gear
            _btnSettings.Font = new Font("Segoe UI", 10F, FontStyle.Bold);
            _btnSettings.TextOffsetY = -2; // see RetroButton.TextOffsetY
            _btnSettings.Click += BtnSettings_Click;

            // Hidden until the startup status check finds a newer
            // installer_version.txt on GitHub than InstallerCore.InstallerVersion
            // -- see StatusWorker_DoWork/RunWorkerCompleted. Sits just left of
            // the gear so it doesn't need its own row.
            _btnUpdate = new RetroButton();
            _btnUpdate.SetBounds(452, 6, 66, 22);
            _btnUpdate.Text = "UPDATE";
            _btnUpdate.Font = new Font("Consolas", 7.5F, FontStyle.Bold);
            _btnUpdate.ForeColor = ThemeMagenta;
            _btnUpdate.Visible = false;
            _btnUpdate.Click += BtnUpdate_Click;

            // One bordered box per supported mod (ModRegistry.All), each
            // titled with that mod's own name, containing a status indicator
            // + name plus its own INSTALL/UNINSTALL/VERSION buttons (see
            // BuildModBox) -- no separate "select a mod first" step, and a
            // sub-row for its bonus component (if any -- e.g. kek-mod's Fish
            // Map Script) with its own indicator. Separate boxes (rather than
            // one shared "MOD" box with plain rows) so a mod's sub-items
            // visually belong to it. Boxes stack starting where the old MOD
            // dropdown used to sit; modShift (how far everything below it
            // gets pushed down) is however much vertical space this stack
            // ends up needing, computed as a side effect of building it.
            //
            // A fast local detection pass (no network) seeds _installedModIds
            // / _installedVersionByModId / _installedEuiVariantId (and
            // _updateAvailableModIds, once the async status check below has
            // populated _releasesByModId to compare against) so the indicator
            // dots and per-box button states painted below reflect real state
            // immediately instead of starting blank.
            DetectInstalledState();
            // After the window itself is visible, not blocking startup.
            Shown += (s, e) =>
            {
                WarnIfCiv5RunningAtLaunch();
                WarnIfConflictingModsInstalled();
                WarnIfMissingMapForInstalledMods();
                WarnIfTournamentModXitsConflict();
            };

            const int outerPadTop = 18;    // room for the outer box's own title cutout
            const int outerPadSide = 8;
            const int outerPadBottom = 8;
            const int innerWidth = 532 - outerPadSide * 2;

            // GAME MODS -- one bordered box per ModRegistry.All entry,
            // nested inside a single outer "GAME MODS" box so kek-mod and
            // Tournament Mod read as one group rather than two unrelated
            // top-level boxes.
            int gameModsY = 10 + TopShift; // now the first content row -- [INSTALLED] used to sit here before it was removed
            int innerY = outerPadTop;
            var modBoxes = new List<Panel>();
            for (int i = 0; i < ModRegistry.All.Count; i++)
            {
                Panel box = BuildModBox(ModRegistry.All[i], outerPadSide, innerY, innerWidth);
                modBoxes.Add(box);
                innerY += box.Height + 8;
            }
            int gameModsHeight = innerY - 8 + outerPadBottom;
            var gameModsBox = MakeRetroBox("GAME MODS", 12, gameModsY, 532, gameModsHeight);
            foreach (Panel box in modBoxes) { gameModsBox.Controls.Add(box); }
            Controls.Add(gameModsBox);
            UpdateModRowStyles();

            // INTERFACE MODS -- same nested-box treatment for EUI/EUI XITS.
            // Clicking a variant's box installs it (replacing the other, if
            // present -- both share Assets/DLC/UI_bc1) or removes EUI
            // entirely if that box is already the active one.
            int interfaceModsY = gameModsY + gameModsHeight + 8;
            innerY = outerPadTop;
            var euiBoxes = new List<Panel>();
            for (int i = 0; i < EuiExtra.Variants.Count; i++)
            {
                Panel box = BuildEuiVariantBox(EuiExtra.Variants[i], outerPadSide, innerY, innerWidth);
                euiBoxes.Add(box);
                innerY += box.Height + 8;
            }
            int interfaceModsHeight = innerY - 8 + outerPadBottom;
            var interfaceModsBox = MakeRetroBox("INTERFACE MODS", 12, interfaceModsY, 532, interfaceModsHeight);
            foreach (Panel box in euiBoxes) { interfaceModsBox.Controls.Add(box); }
            Controls.Add(interfaceModsBox);
            UpdateEuiRowStyles();

            int modShift = (interfaceModsY + interfaceModsHeight + 8) - (132 + TopShift);

            // ClientSize, not Width/Height: the latter includes the title bar
            // and borders, which shrinks the usable area every control below
            // is positioned against and clips the bottom row. Set here, not
            // at the top of the constructor, since it depends on modShift.
            // "476" (was "572") accounts for the shared VERSION box + shared
            // INSTALL/UNINSTALL row no longer existing -- every mod/EUI
            // variant now carries its own action buttons directly on its box
            // instead, so progress/status/log start right after INTERFACE
            // MODS with no extra shared row in between.
            ClientSize = new Size(560, 476 + TopShift + BottomStrip + modShift);

            _progress = new ProgressBar();
            // Inset 1px inside a magenta-bordered panel -- a plain black bar
            // on a black form is otherwise invisible at 0%, before any fill
            // color would show.
            var progressBorder = new Panel();
            progressBorder.SetBounds(12, 132 + TopShift + modShift, 532, 20);
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
            _lblStatus.SetBounds(12, 160 + TopShift + modShift, 532, 20);
            _lblStatus.Font = new Font("Consolas", 9F, FontStyle.Bold);
            _lblStatus.ForeColor = ThemeGreen;
            _lblStatus.BackColor = Color.Black;

            // Derived from ClientSize rather than a hand-tuned height, so the
            // log box and the Open Install Folder button below it never
            // overlap regardless of window height.
            int bottomStripY = ClientSize.Height - BottomStrip;
            int openFolderHeight = 28;
            int openFolderY = bottomStripY - 8 - openFolderHeight;
            int logTop = 184 + TopShift + modShift;
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

            // Three buttons fill this row -- was five with the DX9/DX11
            // launch buttons, but those are hidden for now (see their
            // Controls.Add call below), so the remaining three stretch to
            // fill the freed-up width instead of leaving a gap.
            int rowGap = 4;
            int rowWidth = _txtLog.Width;
            int btnWidth = (rowWidth - 2 * rowGap) / 3;
            int btnWidth1 = rowWidth - 2 * (btnWidth + rowGap); // remainder to the last button

            _btnOpenFolder = new RetroButton();
            _btnOpenFolder.Text = "OPEN DLC FOLDER";
            _btnOpenFolder.SetBounds(12, openFolderY, btnWidth, openFolderHeight);
            _btnOpenFolder.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            _btnOpenFolder.Click += BtnOpenFolder_Click;

            // Scans Assets/DLC and Assets/Maps for folders this installer
            // doesn't recognize (see ExtraModScan) -- players who've picked
            // up a pile of half-remembered manually-installed mods over the
            // years can review and Keep/Archive/Delete each one.
            _btnScanExtras = new RetroButton();
            _btnScanExtras.Text = "VERIFY MODS";
            _btnScanExtras.SetBounds(12 + btnWidth + rowGap, openFolderY, btnWidth, openFolderHeight);
            _btnScanExtras.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            _btnScanExtras.Click += BtnScanExtras_Click;

            // Wipes GPU shader caches + Civ5's cached DBs -- the fix for
            // the "black map with smearing icons" bug (see
            // GraphicsCacheExtra.cs for the story).
            _btnClearGfx = new RetroButton();
            _btnClearGfx.Text = "CLEAR GFX CACHE";
            _btnClearGfx.SetBounds(12 + 2 * (btnWidth + rowGap), openFolderY, btnWidth1, openFolderHeight);
            _btnClearGfx.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            _btnClearGfx.Click += BtnClearGfx_Click;

            _btnLaunchDx9 = new RetroButton();
            _btnLaunchDx9.Text = "LAUNCH DX9";
            _btnLaunchDx9.SetBounds(358, openFolderY, 88, openFolderHeight);
            _btnLaunchDx9.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            _btnLaunchDx9.Click += BtnLaunchDx9_Click;

            _btnLaunchDx11 = new RetroButton();
            _btnLaunchDx11.Text = "LAUNCH DX11";
            _btnLaunchDx11.SetBounds(450, openFolderY, LaunchDx11Width, openFolderHeight);
            _btnLaunchDx11.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            _btnLaunchDx11.Click += BtnLaunchDx11_Click;
            UpdateLaunchCivButtons();

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
            _lblTicker.Text = "*** GREETINGS FELLOW CIV ADDICTS ***  FUCK ALL THE HATING ASSES, RESYNCS, QUITTERS, UNSTABALIZERS ***  -=[ OBLAST ]=- ***    ";
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
            Controls.Add(_lblTitleTag);
            Controls.Add(_dividerLine);
            // GAME MODS/INTERFACE MODS (and everything nested inside them)
            // were already added to Controls above -- each is a
            // locally-scoped variable, unlike the controls below which stay
            // in scope for this batch.
            Controls.Add(progressBorder);
            Controls.Add(_lblStatus);
            Controls.Add(_txtLog);
            Controls.Add(_btnOpenFolder);
            Controls.Add(_btnScanExtras);
            Controls.Add(_btnClearGfx);
            // LAUNCH DX9/DX11 buttons hidden for release -- direct-exe launch
            // (see LaunchCiv) still isn't reliably forcing the renderer on
            // every machine tested 2026-07-22. Code kept intact for when
            // that gets debugged; just not added to Controls so they never
            // render or receive clicks.
            // Controls.Add(_btnLaunchDx9);
            // Controls.Add(_btnLaunchDx11);
            Controls.Add(_tickerViewport);
            Controls.Add(_btnMute);
            Controls.Add(_btnSettings);
            Controls.Add(_btnUpdate);

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

            _euiWorker = new BackgroundWorker();
            _euiWorker.WorkerReportsProgress = true;
            _euiWorker.DoWork += EuiWorker_DoWork;
            _euiWorker.ProgressChanged += Worker_ProgressChanged;
            _euiWorker.RunWorkerCompleted += EuiWorker_RunWorkerCompleted;

            _statusWorker = new BackgroundWorker();
            _statusWorker.DoWork += StatusWorker_DoWork;
            _statusWorker.RunWorkerCompleted += StatusWorker_RunWorkerCompleted;
            _statusWorker.RunWorkerAsync(); // fetches every mod's release list once -- see StatusWorker_DoWork

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

        // Builds one mod's box (status indicator + name on the left,
        // INSTALL/UNINSTALL/VERSION buttons on the right, plus an optional
        // sub-item row underneath for its bonus component), and appends to
        // _modRows. Returns the box unattached to any parent -- the caller
        // (constructor) adds it wherever it belongs, nested inside the GAME
        // MODS container. Each button acts on this specific mod directly --
        // there's no separate "select a mod first" step.
        private Panel BuildModBox(ModDefinition mod, int x, int y, int width)
        {
            bool hasExtra = mod.ExtraDisplayName != null;
            int boxHeight = 18 + (hasExtra ? 2 : 1) * ModRowHeight + 6;

            var box = MakeRetroBox(mod.DisplayName.ToUpperInvariant(), x, y, width, boxHeight,
                () => _installedModIds.Contains(mod.Id) ? ThemeGreen : ThemeMagenta);

            var row = new ModRowControls();
            row.Box = box;

            var indicator = new Label();
            indicator.SetBounds(12, 16, 20, 22);
            indicator.Font = new Font("Consolas", 11F, FontStyle.Bold);
            indicator.TextAlign = ContentAlignment.MiddleCenter;
            indicator.BackColor = Color.Black;
            box.Controls.Add(indicator);
            row.Indicator = indicator;

            // Three small buttons, right-aligned: INSTALL, UNINSTALL, VERSION.
            const int btnW = 68;
            const int btnH = 22;
            const int btnGap = 6;
            int btnsX = width - 12 - (btnW * 3 + btnGap * 2);

            var installBtn = new RetroButton();
            installBtn.Text = "INSTALL";
            installBtn.Font = new Font("Consolas", 7F, FontStyle.Bold);
            installBtn.SetBounds(btnsX, 16, btnW, btnH);
            installBtn.Click += (s, e) => OnModInstallClick(mod);
            box.Controls.Add(installBtn);
            row.InstallButton = installBtn;

            var uninstallBtn = new RetroButton();
            uninstallBtn.Text = "UNINSTALL";
            uninstallBtn.Font = new Font("Consolas", 7F, FontStyle.Bold);
            uninstallBtn.SetBounds(btnsX + (btnW + btnGap), 16, btnW, btnH);
            uninstallBtn.Enabled = false; // enabled once install-state detection finds this mod on disk
            uninstallBtn.Click += (s, e) => OnModUninstallClick(mod);
            box.Controls.Add(uninstallBtn);
            row.UninstallButton = uninstallBtn;

            var versionBtn = new RetroButton();
            versionBtn.Text = "VERSION";
            versionBtn.Font = new Font("Consolas", 7F, FontStyle.Bold);
            versionBtn.SetBounds(btnsX + (btnW + btnGap) * 2, 16, btnW, btnH);
            versionBtn.Click += (s, e) => OnModVersionClick(mod);
            box.Controls.Add(versionBtn);
            row.VersionButton = versionBtn;

            var nameLabel = new Label();
            nameLabel.SetBounds(34, 18, btnsX - 4 - 34, 20);
            nameLabel.Text = mod.DisplayName;
            nameLabel.Font = new Font("Consolas", 9F, FontStyle.Bold);
            nameLabel.ForeColor = ThemeGreen;
            nameLabel.BackColor = Color.Black;
            box.Controls.Add(nameLabel);
            row.NameLabel = nameLabel;

            if (hasExtra)
            {
                // Same x-indentation as the main row above (not indented
                // further) -- just a second line, not a nested tree.
                var extraIndicator = new Label();
                extraIndicator.SetBounds(12, 16 + ModRowHeight, 20, 22);
                extraIndicator.Font = new Font("Consolas", 11F, FontStyle.Bold); // matches the main row's indicator (see BuildModBox/BuildEuiVariantBox) so both dots render the same size
                extraIndicator.TextAlign = ContentAlignment.MiddleCenter;
                extraIndicator.BackColor = Color.Black;
                box.Controls.Add(extraIndicator);
                row.ExtraIndicator = extraIndicator;

                var extraLabel = new Label();
                extraLabel.SetBounds(34, 18 + ModRowHeight, width - 34 - 12, 20);
                extraLabel.Text = mod.ExtraDisplayName;
                extraLabel.Font = new Font("Consolas", 8.5F);
                extraLabel.ForeColor = ThemeGreen;
                extraLabel.BackColor = Color.Black;
                box.Controls.Add(extraLabel);
                row.ExtraNameLabel = extraLabel;
            }

            _modRows.Add(row);
            return box;
        }

        // Same idea as BuildModBox, one EUI variant per box (status
        // indicator + name, INSTALL/REMOVE buttons -- no VERSION button:
        // this is a single bundled copy we ship ourselves, not something
        // with published releases to choose between). Appends to _euiRows.
        private Panel BuildEuiVariantBox(EuiVariant variant, int x, int y, int width)
        {
            int boxHeight = 18 + ModRowHeight + 6;
            var box = MakeRetroBox(variant.DisplayName.ToUpperInvariant(), x, y, width, boxHeight,
                () =>
                {
                    if (variant.Id != _installedEuiVariantId) { return ThemeMagenta; }
                    return _installedEuiVersionMismatch ? ThemeRed : ThemeGreen;
                });

            var indicator = new Label();
            indicator.SetBounds(12, 16, 20, 22);
            indicator.Font = new Font("Consolas", 11F, FontStyle.Bold);
            indicator.TextAlign = ContentAlignment.MiddleCenter;
            indicator.BackColor = Color.Black;
            box.Controls.Add(indicator);

            const int btnW = 68;
            const int btnH = 22;
            const int btnGap = 6;
            int btnsX = width - 12 - (btnW * 2 + btnGap);

            var row = new EuiRowControls();
            row.Box = box;
            row.Indicator = indicator;

            var installBtn = new RetroButton();
            installBtn.Text = "INSTALL";
            installBtn.Font = new Font("Consolas", 7F, FontStyle.Bold);
            installBtn.SetBounds(btnsX, 16, btnW, btnH);
            installBtn.Click += (s, e) => OnEuiInstallClick(variant);
            box.Controls.Add(installBtn);
            row.InstallButton = installBtn;

            var removeBtn = new RetroButton();
            removeBtn.Text = "UNINSTALL";
            removeBtn.Font = new Font("Consolas", 7F, FontStyle.Bold);
            removeBtn.SetBounds(btnsX + (btnW + btnGap), 16, btnW, btnH);
            removeBtn.Enabled = false; // enabled once install-state detection finds this variant active
            removeBtn.Click += (s, e) => OnEuiRemoveClick(variant);
            box.Controls.Add(removeBtn);
            row.RemoveButton = removeBtn;

            var nameLabel = new Label();
            nameLabel.SetBounds(34, 18, btnsX - 8 - 34, 20);
            // Hand-set version (see EuiVariant.DisplayVersion), not tied to
            // install state the way mods' version display is -- this
            // describes what's bundled, so it shows regardless of whether
            // this particular variant is currently installed. UpdateEuiRowStyles
            // appends a mismatch warning to this base text when applicable.
            nameLabel.Text = EuiBaseLabel(variant);
            nameLabel.Font = new Font("Consolas", 9F, FontStyle.Bold);
            nameLabel.ForeColor = ThemeGreen;
            nameLabel.BackColor = Color.Black;
            box.Controls.Add(nameLabel);
            row.NameLabel = nameLabel;

            _euiRows.Add(row);

            return box;
        }

        // Draws a magenta box with the title "cut into" the top border, e.g.
        // +--[ CHANNEL ]---------+   -- the classic scene-intro/keygen box
        // art look, hand-painted since GroupBox's native border ignores
        // theme colors even with visual styles off. Static (doesn't touch
        // instance state) so SettingsForm can reuse it too.
        internal static Panel MakeRetroBox(string title, int x, int y, int w, int h)
        {
            return MakeRetroBox(title, x, y, w, h, null);
        }

        // Overload used by the per-mod boxes: borderColor is re-queried on
        // every repaint (not just once at creation) so a box's border can
        // change color live -- e.g. bright green while that mod is the
        // active one -- just by calling Invalidate() on it, no need to
        // recreate the box. Null keeps the plain always-magenta look above.
        internal static Panel MakeRetroBox(string title, int x, int y, int w, int h, Func<Color> borderColor)
        {
            var panel = new Panel();
            panel.SetBounds(x, y, w, h);
            panel.BackColor = Color.Black;
            var titleFont = new Font("Consolas", 8F, FontStyle.Bold);
            Func<Color> colorFn = borderColor ?? (() => ThemeMagenta);
            panel.Paint += (s, e) =>
            {
                Color color = colorFn();
                using (var pen = new Pen(color))
                {
                    e.Graphics.DrawRectangle(pen, 0, 0, panel.Width - 1, panel.Height - 1);
                }
                string label = " " + title + " ";
                SizeF sz = e.Graphics.MeasureString(label, titleFont);
                e.Graphics.FillRectangle(Brushes.Black, 8, 0, sz.Width, sz.Height);
                using (var brush = new SolidBrush(color))
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

            // Null = default ThemeMagenta. Set by UpdateModRowStyles/
            // UpdateEuiRowStyles to ThemeGreen once the mod/variant is
            // installed, matching the box's own border-color state.
            private Color? _borderColor;
            internal Color? BorderColor
            {
                get { return _borderColor; }
                set { _borderColor = value; Invalidate(); }
            }

            private bool _hover;

            protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
            protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
            protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

            // Dims toward the two known theme colors' pre-tuned disabled
            // shades, so a pink-state button dims to dim pink and a
            // green-state button dims to dim green -- previously this was a
            // single hardcoded dim-green regardless of which color the
            // button actually was, so disabled buttons on not-installed
            // (pink) rows still showed green text.
            private static Color DimForDisabled(Color c)
            {
                if (c == ThemeGreen) { return Color.FromArgb(0, 100, 40); }
                if (c == ThemeMagenta) { return Color.FromArgb(90, 0, 65); }
                return Color.FromArgb(c.R * 35 / 100, c.G * 35 / 100, c.B * 35 / 100);
            }

            protected override void OnPaint(PaintEventArgs pe)
            {
                Color borderEnabled = _borderColor ?? ThemeMagenta;
                Color border = Enabled ? borderEnabled : DimForDisabled(borderEnabled);
                Color text = Enabled ? ForeColor : DimForDisabled(ForeColor);
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
            UpdateLaunchCivButtons();
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

        // Refreshes each mod box's indicator dot + name-text color (bright
        // green if _installedModIds says that mod -- or its bonus component
        // -- is on disk, pink/magenta otherwise), the box's own border color
        // (same install-state signal), and each button's enabled state
        // (UNINSTALL only makes sense once something's actually installed).
        // Called after construction and after every status refresh.
        private void UpdateModRowStyles()
        {
            const string filled = "●"; // ●
            const string hollow = "○"; // ○

            for (int i = 0; i < ModRegistry.All.Count; i++)
            {
                ModDefinition mod = ModRegistry.All[i];
                ModRowControls row = _modRows[i];

                bool installed = _installedModIds.Contains(mod.Id);
                row.Indicator.Text = installed ? filled : hollow;
                row.Indicator.ForeColor = installed ? ThemeGreen : ThemeMagenta;
                row.NameLabel.ForeColor = installed ? ThemeGreen : ThemeMagenta;

                string installedVersion;
                row.NameLabel.Text = (installed && _installedVersionByModId.TryGetValue(mod.Id, out installedVersion))
                    ? mod.DisplayName + " (" + installedVersion + ")"
                    : mod.DisplayName;

                bool updateAvailable = _updateAvailableModIds.Contains(mod.Id);

                // Relabeled rather than disabled once installed -- INSTALL
                // stays a valid action against an already-installed mod (it's
                // how a corrupted/incomplete install gets fixed, or how a
                // different VERSION pick gets applied), just worded for what
                // it'll actually do at that point. When a newer release
                // exists it becomes UPDATE instead -- same click handler
                // (OnModInstallClick), just relabeled/recolored so the row
                // itself is the call to action instead of a separate passive
                // badge. No extra confirmation on click: OnModInstallClick's
                // only popup path is for switching to a DIFFERENT, conflicting
                // mod, which can't apply when updating the mod already
                // installed.
                row.InstallButton.Text = updateAvailable ? "UPDATE" : (installed ? "REINSTALL" : "INSTALL");
                if (!_actionInProgress)
                {
                    row.InstallButton.Enabled = true;
                    row.UninstallButton.Enabled = installed;
                    row.VersionButton.Enabled = true;
                }

                // Border green once installed (matches the box's own
                // border), text pink while not -- see RetroButton.BorderColor.
                // UPDATE overrides both to red so it stands out over the
                // ordinary installed styling.
                Color? btnBorder = installed ? ThemeGreen : (Color?)null;
                Color btnText = installed ? ThemeGreen : ThemeMagenta;
                row.InstallButton.BorderColor = updateAvailable ? ThemeRed : btnBorder;
                row.InstallButton.ForeColor = updateAvailable ? ThemeRed : btnText;
                row.UninstallButton.BorderColor = btnBorder;
                row.UninstallButton.ForeColor = btnText;
                row.VersionButton.BorderColor = btnBorder;
                row.VersionButton.ForeColor = btnText;

                if (row.ExtraIndicator != null)
                {
                    bool extraInstalled = mod.ExtraModId != null && _installedModIds.Contains(mod.ExtraModId);
                    row.ExtraIndicator.Text = extraInstalled ? filled : hollow;
                    row.ExtraIndicator.ForeColor = extraInstalled ? ThemeGreen : ThemeMagenta;
                    row.ExtraNameLabel.ForeColor = extraInstalled ? ThemeGreen : ThemeMagenta;

                    string extraVersion;
                    row.ExtraNameLabel.Text = (extraInstalled && mod.ExtraModId != null
                        && _installedExtraVersionByModId.TryGetValue(mod.ExtraModId, out extraVersion))
                        ? mod.ExtraDisplayName + " (" + extraVersion + ")"
                        : mod.ExtraDisplayName;
                }

                row.Box.Invalidate(); // re-runs the box's borderColor delegate against the (possibly new) install state
            }
        }

        // Same idea as UpdateModRowStyles for the EUI boxes: INSTALL is only
        // enabled for the variant that ISN'T currently active, REMOVE only
        // for the one that is.
        private void UpdateEuiRowStyles()
        {
            for (int i = 0; i < EuiExtra.Variants.Count; i++)
            {
                EuiVariant variant = EuiExtra.Variants[i];
                bool active = variant.Id == _installedEuiVariantId;
                // Present but NOT byte-identical to what this build bundles
                // (see EuiExtra.IsExactBundledMatch) -- e.g. the player
                // manually installed a different EUI version. Every mod we
                // ship is only tested against our exact bundled copy, so
                // this gets its own warning treatment, not just "installed".
                bool mismatch = active && _installedEuiVersionMismatch;

                _euiRows[i].Indicator.Text = !active ? "○" : (mismatch ? "⚠" : "●");
                _euiRows[i].Indicator.ForeColor = !active ? ThemeMagenta : (mismatch ? ThemeRed : ThemeGreen);
                _euiRows[i].NameLabel.ForeColor = !active ? ThemeMagenta : (mismatch ? ThemeRed : ThemeGreen);
                // Mismatch means the installed copy is NOT confirmed to be
                // DisplayVersion -- showing that version number alongside
                // "MISMATCH" would misleadingly imply we know what's on disk.
                _euiRows[i].NameLabel.Text = mismatch
                    ? variant.DisplayName + " -- VERSION MISMATCH"
                    : EuiBaseLabel(variant);

                // Relabeled rather than disabled once active -- re-running
                // Install over an already-installed variant is always a
                // valid (idempotent) action: it's how a mismatched copy gets
                // fixed, and even a confirmed exact match can be force-
                // reinstalled if a player suspects local corruption.
                _euiRows[i].InstallButton.Text = active ? "REINSTALL" : "INSTALL";
                if (!_actionInProgress)
                {
                    _euiRows[i].InstallButton.Enabled = true;
                    _euiRows[i].RemoveButton.Enabled = active;
                }

                // Same border-green/text-pink state coloring as the mod
                // boxes -- see UpdateModRowStyles.
                Color? euiBtnBorder = active ? ThemeGreen : (Color?)null;
                Color euiBtnText = active ? ThemeGreen : ThemeMagenta;
                _euiRows[i].InstallButton.BorderColor = euiBtnBorder;
                _euiRows[i].InstallButton.ForeColor = euiBtnText;
                _euiRows[i].RemoveButton.BorderColor = euiBtnBorder;
                _euiRows[i].RemoveButton.ForeColor = euiBtnText;

                _euiRows[i].Box.Invalidate(); // re-runs the box's borderColor delegate against the (possibly new) installed variant
            }
        }

        private static string EuiBaseLabel(EuiVariant variant)
        {
            return variant.DisplayVersion != null
                ? variant.DisplayName + " (" + variant.DisplayVersion + ")"
                : variant.DisplayName;
        }

        private void BtnSettings_Click(object sender, EventArgs e)
        {
            // Beta on/off is read live by each mod's own VERSION popup
            // (OnModVersionClick) every time it's opened, so there's nothing
            // to refresh there. But it also feeds IsUpdateAvailable (the
            // same beta-aware pick auto-install would make), so toggling it
            // can flip a mod between INSTALL/REINSTALL and UPDATE --
            // refresh local state to pick that up. Local-only, so cheap
            // enough to run even if the checkbox didn't actually change.
            using (var dlg = new SettingsForm())
            {
                dlg.ShowDialog(this);
            }
            RefreshLocalState();
        }

        // Downloads the new installer exe and relaunches -- see
        // InstallerCore.DownloadAndApplySelfUpdate for how a running exe
        // manages to replace itself. Blocked mid-install/uninstall so the
        // helper batch script's file swap can't race a Worker still reading
        // from disk.
        private void BtnUpdate_Click(object sender, EventArgs e)
        {
            if (_worker.IsBusy || _uninstallWorker.IsBusy)
            {
                return;
            }

            try
            {
                _btnUpdate.Enabled = false;
                SetStatus("DOWNLOADING UPDATE...");
                InstallerCore.DownloadAndApplySelfUpdate();
                Application.Exit();
            }
            catch (Exception ex)
            {
                _btnUpdate.Enabled = true;
                SetStatus("UPDATE FAILED: " + ex.Message);
            }
        }

        private void MainForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            _tickerTimer.Stop();
            _cursorTimer.Stop();
            RetroAudio.Stop();
        }

        // Runs ONCE at startup: every mod's release list (the only
        // GitHub-API-rate-limited part of this), plus the ticker and
        // self-update check. Nothing else ever re-triggers this -- see
        // RefreshLocalState for what runs instead when the player switches
        // mods, toggles beta, or finishes an install/uninstall/EUI change.
        private void StatusWorker_DoWork(object sender, DoWorkEventArgs e)
        {
            var releasesByModId = new Dictionary<string, List<ModRelease>>();
            foreach (ModDefinition mod in ModRegistry.All)
            {
                try
                {
                    releasesByModId[mod.Id] = mod.Source.ListReleases();
                }
                catch (Exception)
                {
                    // offline/rate-limited/etc -- that mod's VERSION dropdown
                    // just stays on "Latest (auto)"; Install still works via
                    // its own live fetch and reports its own error if this
                    // was a real outage rather than a one-off hiccup.
                }
            }

            // Best-effort refresh of the greetz ticker from GitHub; a null
            // here just means StatusWorker_RunWorkerCompleted leaves the
            // default text already on screen alone.
            string tickerText = InstallerCore.TryFetchTickerText();

            // Best-effort self-update check; a null here just means
            // StatusWorker_RunWorkerCompleted leaves the UPDATE button hidden.
            string latestInstallerVersion = InstallerCore.TryFetchLatestInstallerVersion();

            e.Result = new object[] { releasesByModId, tickerText, latestInstallerVersion };
        }

        private void StatusWorker_RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            if (e.Error != null)
            {
                return; // leave the "checking..." placeholder rather than alarm the user
            }
            var result = (object[])e.Result;
            _releasesByModId = (Dictionary<string, List<ModRelease>>)result[0];

            string tickerText = (string)result[1];
            if (tickerText != null)
            {
                _lblTicker.Text = tickerText;
                _lblTicker.Location = new Point(_tickerViewport.Width, 1); // restart the scroll cleanly
            }

            string latestInstallerVersion = (string)result[2];
            if (latestInstallerVersion != null
                && InstallerCore.IsNewerVersion(latestInstallerVersion, InstallerCore.GetInstallerVersion()))
            {
                _latestInstallerVersion = latestInstallerVersion;
                _btnUpdate.Visible = true;
            }
            else
            {
                _btnUpdate.Visible = false;
            }

            RefreshLocalState();
        }

        // Refreshes everything that can be derived from already-cached data
        // or a fast local disk scan: the per-mod-box/EUI-box indicators,
        // text colors, border colors, update-available badges, and button
        // enabled-states. Never touches the network -- called after
        // finishing an install/uninstall/EUI change, none of which need a
        // fresh GitHub fetch since installing/uninstalling doesn't change
        // what releases exist upstream.
        private void RefreshLocalState()
        {
            DetectInstalledState();
            UpdateModRowStyles();
            UpdateEuiRowStyles();
        }

        // Fast local disk scan (no network): populates _installedModIds,
        // _installedVersionByModId, and _installedEuiVariantId, and returns
        // the [INSTALLED] line text (or an error message) as a side effect
        // of the same DetectAllInstalled() call. Callable before the mod/EUI
        // boxes exist (the constructor's initial detection pass) since it
        // doesn't touch them itself -- callers that need the boxes repainted
        // call UpdateModRowStyles/UpdateEuiRowStyles afterward.
        private void DetectInstalledState()
        {
            try
            {
                List<DetectedModInstall> detected = InstallerCore.DetectAllInstalled();

                _installedModIds.Clear();
                _installedVersionByModId.Clear();
                _installedExtraVersionByModId.Clear();
                _updateAvailableModIds.Clear();
                foreach (DetectedModInstall d in detected)
                {
                    _installedModIds.Add(d.ModId);
                    ModDefinition mod = ModRegistry.ById(d.ModId);
                    if (mod != null && d.FolderNames.Count > 0)
                    {
                        string installedFolderName = d.FolderNames[0];
                        _installedVersionByModId[d.ModId] = ExtractVersionSuffix(mod, installedFolderName);
                        if (IsUpdateAvailable(mod, installedFolderName))
                        {
                            _updateAvailableModIds.Add(mod.Id);
                        }
                    }
                    else if (d.VersionLabel != null)
                    {
                        // A bonus component (e.g. "fishmapscript",
                        // "betterpangaea") rather than a real ModRegistry
                        // entry -- ExtraModId on whichever mod owns it
                        // matches d.ModId, see UpdateModRowStyles.
                        _installedExtraVersionByModId[d.ModId] = d.VersionLabel;
                    }
                }

                string dlcRoot = InstallerCore.TryGetDlcFolder();
                _installedEuiVariantId = dlcRoot == null ? null : EuiExtra.DetectInstalledVariantId(dlcRoot);
                _installedEuiVersionMismatch = false;
                if (dlcRoot != null && _installedEuiVariantId != null)
                {
                    EuiVariant installedVariant = EuiExtra.Variants.First(v => v.Id == _installedEuiVariantId);
                    _installedEuiVersionMismatch = !EuiExtra.IsExactBundledMatch(dlcRoot, installedVariant);
                }
            }
            catch (Exception)
            {
                // best-effort -- next status refresh (or the next time
                // something changes) tries again
            }
        }

        // Was previously a Program.Main gate before MainForm even existed --
        // moved here so the window itself is visible first instead of the
        // warning being the first thing a player sees. Cancel just dismisses
        // the warning and leaves the already-visible main window (the
        // "loader") up rather than killing the installer entirely -- there's
        // nothing else useful to do here, so no point tearing the whole app
        // down over it. Force Close kills immediately, no extra confirm --
        // Civ5RunningForm's own label is the warning.
        private void WarnIfCiv5RunningAtLaunch()
        {
            while (InstallerCore.IsCiv5Running())
            {
                DialogResult result;
                using (var dlg = new Civ5RunningForm())
                {
                    result = dlg.ShowDialog(this);
                }
                if (result == DialogResult.Cancel)
                {
                    return;
                }
                if (result == DialogResult.Abort) // Force Close sentinel
                {
                    InstallerCore.ForceCloseCiv5();
                }
                // Retry (or a completed force close) falls through to the
                // while-check above, which re-polls IsCiv5Running.
            }
        }

        // These mods are mutually exclusive (see OnModInstallClick's own
        // "conflicting" check) -- the installer itself never creates this
        // state, since installing one always uninstalls any other first. So
        // finding more than one already on disk at startup means it got here
        // some other way (an older installer version, manual folder copies,
        // etc.), and it's a broken install: both ship their own
        // CvGameCore_Expansion2.dll/Override set, so having both in
        // Assets/DLC at once can crash or desync rather than just one
        // "winning." Surfaced as a blocking warning at launch, not just the
        // per-box color, since that's easy to miss and the actual cause
        // (two conflicting DLC folders) isn't obvious from in-game symptoms
        // alone.
        private void WarnIfConflictingModsInstalled()
        {
            List<ModDefinition> installed = ModRegistry.All.Where(m => _installedModIds.Contains(m.Id)).ToList();
            if (installed.Count < 2)
            {
                return;
            }

            string names = string.Join(" and ", installed.Select(m => m.DisplayName).ToArray());
            MessageBox.Show(
                this,
                names + " are both installed. They can't run together -- uninstall one.",
                "Conflicting mods installed",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }

        // Tournament Mod's own published ui_check.bat (catscatsforever/Civ5-
        // Patch, confirmed against a real download) hardcodes every EUI
        // check to the literal path "UI_bc1\..." -- no %euifolder% variable,
        // no awareness that "UI_bc1_xits" exists at all. With only EUI XITS
        // installed (no plain EUI alongside it), every one of those checks
        // comes back "not found", so Tournament Mod silently reverts nearly
        // its whole UI override set to vanilla while EUI XITS's own base
        // screens stay active underneath -- a broken half-vanilla/half-EUI
        // mashup (confirmed 2026-07-21 against a real deployed install:
        // blank top bar, unclickable units, split production/purchase
        // popups). This isn't something we can fix from our side (it's
        // upstream Tournament Mod content, not our installer or kek-mod's
        // own script), so just warn instead of silently producing a broken
        // game. Only checks Tournament Mod -- kek-mod's own XITS-aware
        // %euifolder% fix (see ui_check.bat) just hasn't shipped in a
        // release yet, so it isn't a permanent incompatibility the same way.
        private void WarnIfTournamentModXitsConflict()
        {
            if (_installedEuiVariantId != "eui_xits" || !_installedModIds.Contains(ModRegistry.TournamentMod.Id))
            {
                return;
            }

            MessageBox.Show(
                this,
                "Tournament Mod and EUI XITS are not compatible.",
                "Tournament Mod isn't compatible with EUI XITS",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }

        // Shared by OnModInstallClick (installing Tournament Mod while EUI
        // XITS is already active) and OnEuiInstallClick (installing EUI
        // XITS while Tournament Mod is already installed) -- same
        // incompatibility as WarnIfTournamentModXitsConflict, just caught
        // before the action instead of after. Defaults to blocking (Yes/No,
        // not just OK) since this is about to actively create the broken
        // combination rather than merely reporting one already on disk.
        private bool ConfirmTournamentModXitsConflict()
        {
            DialogResult confirm = MessageBox.Show(
                this,
                "Tournament Mod and EUI XITS are not compatible. Install anyway?",
                "Tournament Mod isn't compatible with EUI XITS",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);
            return confirm == DialogResult.Yes;
        }

        // EnsureExtraInstalled normally adds a mod's bonus map alongside it
        // (see InstallerCore.Run), but that's best-effort and silently
        // swallows failures (offline at install time, upstream repo/release
        // missing, etc. -- see MapScriptExtra/TournamentMapExtra), so a mod
        // can end up installed without it and nothing ever surfaces that.
        // ExtraModId's presence in _installedModIds is the same signal
        // UpdateModRowStyles already uses for that sub-row's indicator dot.
        private void WarnIfMissingMapForInstalledMods()
        {
            List<string> missing = ModRegistry.All
                .Where(m => m.ExtraModId != null && _installedModIds.Contains(m.Id) && !_installedModIds.Contains(m.ExtraModId))
                .Select(m => m.DisplayName + " is missing its " + m.ExtraDisplayName)
                .ToList();
            if (missing.Count == 0)
            {
                return;
            }

            MessageBox.Show(
                this,
                string.Join("\n", missing) + ".\n\nReinstall to add it.",
                "Missing bonus map",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }

        // Backs the VERIFY MODS button (BtnScanExtras_Click) -- runs every
        // check this installer already knows how to make, most of which
        // otherwise only ever surface as a launch-time popup (see
        // WarnIfCiv5RunningAtLaunch/WarnIfConflictingModsInstalled/
        // WarnIfTournamentModXitsConflict/WarnIfMissingMapForInstalledMods
        // above) or a small badge/border color (EUI version mismatch, the
        // update-available triangle). Assumes RefreshLocalState() was just
        // called, same as those callers assume.
        private List<CheckResult> RunAllChecks()
        {
            var results = new List<CheckResult>();

            bool civRunning = InstallerCore.IsCiv5Running();
            results.Add(new CheckResult(!civRunning,
                civRunning ? "Civilization V is currently running." : "Civilization V is not running."));

            List<ModDefinition> installedMods = ModRegistry.All.Where(m => _installedModIds.Contains(m.Id)).ToList();
            results.Add(installedMods.Count >= 2
                ? new CheckResult(false, string.Join(" and ", installedMods.Select(m => m.DisplayName).ToArray())
                    + " are both installed -- they can't run together.")
                : new CheckResult(true, "No conflicting mods installed."));

            bool xitsConflict = _installedEuiVariantId == "eui_xits" && _installedModIds.Contains(ModRegistry.TournamentMod.Id);
            results.Add(new CheckResult(!xitsConflict, xitsConflict
                ? "Tournament Mod and EUI XITS are installed together -- not compatible."
                : "No Tournament Mod / EUI XITS conflict."));

            List<string> missingMaps = ModRegistry.All
                .Where(m => m.ExtraModId != null && _installedModIds.Contains(m.Id) && !_installedModIds.Contains(m.ExtraModId))
                .Select(m => m.DisplayName + " is missing its " + m.ExtraDisplayName)
                .ToList();
            results.Add(missingMaps.Count > 0
                ? new CheckResult(false, string.Join("; ", missingMaps) + ".")
                : new CheckResult(true, "No missing bonus maps."));

            if (_installedEuiVariantId == null)
            {
                results.Add(new CheckResult(true, "No EUI variant installed."));
            }
            else
            {
                EuiVariant installedVariant = EuiExtra.Variants.First(v => v.Id == _installedEuiVariantId);
                string label = EuiBaseLabel(installedVariant);
                results.Add(new CheckResult(!_installedEuiVersionMismatch, _installedEuiVersionMismatch
                    ? label + " doesn't exactly match this installer's bundled copy."
                    : label + " matches this installer's bundled copy exactly."));
            }

            List<string> updates = ModRegistry.All
                .Where(m => _updateAvailableModIds.Contains(m.Id))
                .Select(m => m.DisplayName)
                .ToList();
            results.Add(updates.Count > 0
                ? new CheckResult(false, "Update available for: " + string.Join(", ", updates.ToArray()) + ".")
                : new CheckResult(true, "All installed mods are up to date."));

            return results;
        }

        // Whether mod's auto-install pick (respecting the current beta
        // setting, same rule InstallerCore.Run would use) resolves to a
        // different folder name than what's actually installed. Compares
        // folder names directly rather than parsing/comparing version
        // strings -- e.g. stripping "KEK Mod v" from "KEK Mod v1.4" loses
        // the "v", so re-deriving a tag from that stripped text to compare
        // against a release's raw tag ("v1.4") would be fragile. Requires
        // _releasesByModId to already have that mod's release list cached
        // (the one-time startup fetch) -- returns false if it doesn't, so
        // this never claims an update is available it can't actually back up.
        private bool IsUpdateAvailable(ModDefinition mod, string installedFolderName)
        {
            List<ModRelease> releases;
            if (!_releasesByModId.TryGetValue(mod.Id, out releases) || releases.Count == 0)
            {
                return false;
            }

            bool showBeta = SettingsManager.GetShowBeta();
            ModRelease latest = (mod.Beta == BetaPolicy.OptIn && !showBeta)
                ? releases.FirstOrDefault(r => !r.Prerelease)
                : releases.FirstOrDefault();
            if (latest == null)
            {
                return false;
            }

            string latestFolderName = mod.MakeFolderName(latest.Tag);
            return !string.Equals(installedFolderName, latestFolderName, StringComparison.OrdinalIgnoreCase);
        }

        // Strips a mod's InstalledFolderGlob prefix (everything before the
        // "*", e.g. "KEK Mod v" out of "KEK Mod v*") from an actual folder
        // name to get just the version part -- "KEK Mod v1.4" -> "1.4",
        // "Tournament Mod V12.2a" -> "12.2a". Falls back to the whole folder
        // name if it doesn't start with the expected prefix (shouldn't
        // happen since DetectAllInstalled found it via that same glob, but
        // cheap insurance against ever showing an empty string).
        private static string ExtractVersionSuffix(ModDefinition mod, string folderName)
        {
            string prefix = mod.InstalledFolderGlob.Substring(0, mod.InstalledFolderGlob.Length - 1);
            if (folderName.Length > prefix.Length
                && folderName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return folderName.Substring(prefix.Length);
            }
            return folderName;
        }

        // Opens a small themed popup listing mod's releases (from the cache
        // fetched once at startup -- no network here) and stores whichever
        // tag the player picks for the next time they click that mod's own
        // INSTALL button. Null/"Latest (auto)" clears the override.
        private void OnModVersionClick(ModDefinition mod)
        {
            if (_actionInProgress)
            {
                return;
            }

            List<ModRelease> releases;
            _releasesByModId.TryGetValue(mod.Id, out releases);
            string currentTag;
            _selectedVersionTagByModId.TryGetValue(mod.Id, out currentTag);

            using (var dlg = new VersionPickerForm(mod, releases, SettingsManager.GetShowBeta(), currentTag))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK)
                {
                    return;
                }
                string tag = dlg.GetSelectedTag();
                if (tag == null)
                {
                    _selectedVersionTagByModId.Remove(mod.Id);
                }
                else
                {
                    _selectedVersionTagByModId[mod.Id] = tag;
                }
                SetStatus(mod.DisplayName + " version set to " + (tag ?? "auto"));

                // Picking a version is the install action itself -- no
                // separate INSTALL/REINSTALL click needed afterward. Reuses
                // OnModInstallClick as-is, which already reads the tag just
                // stored above via _selectedVersionTagByModId.
                OnModInstallClick(mod);
            }
        }

        // These mods are all total-conversion-style DLC packages (own DLL,
        // own Override set) -- running two at once in Assets/DLC isn't a
        // supported combination, so installing one while a DIFFERENT one is
        // already on disk must uninstall the old one first rather than
        // leaving both in place.
        private void OnModInstallClick(ModDefinition mod)
        {
            if (_actionInProgress)
            {
                return;
            }

            var options = new InstallOptions();
            options.WantBeta = SettingsManager.GetShowBeta();
            string tag;
            options.TagName = _selectedVersionTagByModId.TryGetValue(mod.Id, out tag) ? tag : null;

            if (mod.Id == ModRegistry.TournamentMod.Id && _installedEuiVariantId == "eui_xits"
                && !ConfirmTournamentModXitsConflict())
            {
                return;
            }

            List<ModDefinition> conflicting = ModRegistry.All
                .Where(m => m.Id != mod.Id && _installedModIds.Contains(m.Id))
                .ToList();

            if (conflicting.Count > 0)
            {
                string names = string.Join(", ", conflicting.Select(m => m.DisplayName).ToArray());
                DialogResult confirm = MessageBox.Show(
                    this,
                    "Installing " + mod.DisplayName + " will uninstall " + names
                        + " first -- these mods can't run side by side in Civilization V. Continue?",
                    "Install " + mod.DisplayName,
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning);
                if (confirm != DialogResult.Yes)
                {
                    return;
                }

                _pendingInstallMod = mod;
                _pendingInstallOptions = options;
                _txtLog.Clear();
                SetControlsEnabled(false);
                _progress.Value = 0;
                SetStatus("REMOVING " + names.ToUpperInvariant() + "...");
                _uninstallWorker.RunWorkerAsync(conflicting);
                return;
            }

            StartInstall(mod, options);
        }

        private void OnModUninstallClick(ModDefinition mod)
        {
            if (_actionInProgress)
            {
                return;
            }

            // InstallerCore.Uninstall calls mod.RemoveExtra unconditionally
            // for any mod that has one (Fish Map Script, Better Pangaea,
            // Lekmap), so the confirm dialog should mention it for whichever
            // mod this is, not just kek-mod specifically.
            string extraNote = mod.ExtraDisplayName != null ? " and the " + mod.ExtraDisplayName : "";
            DialogResult confirm = MessageBox.Show(
                this,
                "Remove all installed " + mod.DisplayName + " versions" + extraNote + " from your Civilization V install?",
                "Uninstall " + mod.DisplayName,
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);
            if (confirm != DialogResult.Yes)
            {
                return;
            }

            _txtLog.Clear();
            SetControlsEnabled(false);
            _progress.Value = 0;
            SetStatus("REMOVING " + mod.DisplayName.ToUpperInvariant() + " FILES...");

            _uninstallWorker.RunWorkerAsync(new List<ModDefinition> { mod });
        }

        private void OnEuiInstallClick(EuiVariant variant)
        {
            if (_actionInProgress)
            {
                return;
            }
            if (variant.Id == "eui_xits" && _installedModIds.Contains(ModRegistry.TournamentMod.Id)
                && !ConfirmTournamentModXitsConflict())
            {
                return;
            }
            _txtLog.Clear();
            SetControlsEnabled(false);
            _progress.Value = 0;
            SetStatus("INSTALLING " + variant.DisplayName.ToUpperInvariant() + "...");
            _euiWorker.RunWorkerAsync(variant);
        }

        private void OnEuiRemoveClick(EuiVariant variant)
        {
            if (_actionInProgress)
            {
                return;
            }
            _txtLog.Clear();
            SetControlsEnabled(false);
            _progress.Value = 0;
            SetStatus("REMOVING " + variant.DisplayName.ToUpperInvariant() + "...");
            _euiWorker.RunWorkerAsync(null);
        }

        private void StartInstall(ModDefinition mod, InstallOptions options)
        {
            _txtLog.Clear();
            SetControlsEnabled(false);
            _progress.Value = 0;
            SetStatus("INSTALLING " + mod.DisplayName.ToUpperInvariant() + "...");
            _worker.RunWorkerAsync(new object[] { mod, options });
        }

        private void Worker_DoWork(object sender, DoWorkEventArgs e)
        {
            var args = (object[])e.Argument;
            var mod = (ModDefinition)args[0];
            var options = (InstallOptions)args[1];
            var worker = (BackgroundWorker)sender;
            int lastPercent = 0;
            Action<string> log = msg => worker.ReportProgress(lastPercent, msg);
            Action<int> progress = pct => { lastPercent = pct; worker.ReportProgress(pct, null); };
            e.Result = InstallerCore.Run(mod, options, log, progress);
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
            // disk now, without needing a restart. Local-only -- installing
            // doesn't change what releases exist upstream, so no need to
            // re-fetch the VERSION dropdown's data.
            RefreshLocalState();
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

        private void BtnScanExtras_Click(object sender, EventArgs e)
        {
            Cursor = Cursors.WaitCursor;
            List<CheckResult> checks;
            List<ExtraFolderInfo> found;
            try
            {
                RefreshLocalState(); // local-only -- make sure checks run against current disk state
                checks = RunAllChecks();
                found = ExtraModScan.Scan();
            }
            finally
            {
                Cursor = Cursors.Default;
            }

            using (var dlg = new ExtraModsForm(checks, found))
            {
                dlg.ShowDialog(this);
            }
        }

        // Wipes every known GPU shader cache (NVIDIA/AMD/Intel + Windows'
        // D3DSCache) plus Civ5's cached gameplay DBs -- see
        // GraphicsCacheExtra.cs for why this fixes the black-map bug and
        // why each wipe is safe. Blocked while the game runs: the driver
        // holds cache files open and half of them would just be skipped.
        private void BtnClearGfx_Click(object sender, EventArgs e)
        {
            if (InstallerCore.IsCiv5Running())
            {
                MessageBox.Show(
                    this,
                    "Civilization V is currently running. Close the game first, then clear the caches.",
                    "Clear graphics caches",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            // No confirm prompt -- every target is a rebuild-on-demand
            // cache, so the worst case is one slower launch. The log window
            // shows the result; no summary popup needed after the fact.
            List<GraphicsCacheResult> results;
            Cursor = Cursors.WaitCursor;
            try
            {
                results = GraphicsCacheClear.ClearAll();
            }
            finally
            {
                Cursor = Cursors.Default;
            }

            _txtLog.AppendText("=== CLEAR GFX CACHE ===" + Environment.NewLine);
            foreach (GraphicsCacheResult r in results)
            {
                if (!r.Present)
                {
                    continue; // wrong vendor for this machine -- not worth a log line
                }
                _txtLog.AppendText(
                    r.Label + ": " + r.FilesDeleted + " deleted"
                        + (r.FilesSkipped > 0 ? ", " + r.FilesSkipped + " in use/skipped" : "")
                        + Environment.NewLine);
            }
            _txtLog.AppendText("Done. Caches rebuild automatically on next launch." + Environment.NewLine);
        }

        // While Civ5 runs, the DX9 button hides and the DX11 button widens
        // across both launch slots as the red FORCE CLOSE CIV button --
        // CursorTimer_Tick (already polling every 500ms for the status-bar
        // blink) keeps this synced, including if the game exits or crashes
        // on its own. The Text comparison doubles as the layout guard:
        // bounds only move on an actual state flip, not every tick.
        private const int LaunchDx11Width = 94;

        private void UpdateLaunchCivButtons()
        {
            bool running = InstallerCore.IsCiv5Running();
            string text = running ? "FORCE CLOSE CIV" : "LAUNCH DX11";
            if (_btnLaunchDx11.Text == text)
            {
                return;
            }
            _btnLaunchDx11.Text = text;
            _btnLaunchDx11.ForeColor = running ? ThemeRed : ThemeGreen;
            _btnLaunchDx9.Visible = !running;
            int left = running ? _btnLaunchDx9.Left : _btnLaunchDx9.Left + _btnLaunchDx9.Width + 4;
            int width = running ? _btnLaunchDx9.Width + 4 + LaunchDx11Width : LaunchDx11Width;
            _btnLaunchDx11.SetBounds(left, _btnLaunchDx11.Top, width, _btnLaunchDx11.Height);
        }

        private void BtnLaunchDx9_Click(object sender, EventArgs e)
        {
            LaunchCiv("CivilizationV.exe");
        }

        private void BtnLaunchDx11_Click(object sender, EventArgs e)
        {
            LaunchCiv("CivilizationV_DX11.exe");
        }

        // Launches the exe directly rather than via steam://rungameid/8930:
        // the URL launch silently uses whatever renderer the logged-in Steam
        // account last picked in Steam's chooser -- caught live 2026-07-22
        // handing DX9 to a machine whose DX9 path draws a black map -- and a
        // rungameid URL has no way to pin a launch option. Direct exe launch
        // is the only way to guarantee the renderer. Steam still needs to be
        // running for the game to boot; the game itself says so if it isn't.
        private void LaunchCiv(string exeName)
        {
            if (InstallerCore.IsCiv5Running())
            {
                DialogResult confirm = MessageBox.Show(
                    this,
                    "Civilization V is currently running.\n\nForce-closing it ends the process immediately -- any unsaved game progress will be lost. Continue?",
                    "Force close Civilization V",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning);
                if (confirm == DialogResult.Yes)
                {
                    InstallerCore.ForceCloseCiv5();
                    UpdateLaunchCivButtons();
                }
                return;
            }

            string gameFolder = InstallerCore.TryGetCiv5GameFolder();
            string exePath = gameFolder == null ? null : Path.Combine(gameFolder, exeName);
            if (exePath == null || !File.Exists(exePath))
            {
                MessageBox.Show(
                    this,
                    "Couldn't find " + exeName + " in the Civilization V install folder.",
                    "Launch Civilization V",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo(exePath) { WorkingDirectory = gameFolder, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    this,
                    "Couldn't launch " + exeName + ": " + ex.Message,
                    "Launch Civilization V",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        // Takes a list, not a single mod -- OnModInstallClick can hand this
        // multiple mods to remove at once when installing over one that's
        // currently installed.
        private void UninstallWorker_DoWork(object sender, DoWorkEventArgs e)
        {
            var mods = (List<ModDefinition>)e.Argument;
            var worker = (BackgroundWorker)sender;
            int lastPercent = 0;
            Action<string> log = msg => worker.ReportProgress(lastPercent, msg);
            Action<int> progress = pct => { lastPercent = pct; worker.ReportProgress(pct, null); };
            foreach (ModDefinition mod in mods)
            {
                InstallerCore.Uninstall(mod, log, progress);
            }
        }

        private void UninstallWorker_RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            SetControlsEnabled(true);

            if (e.Error != null)
            {
                SetStatus("FAILED: " + e.Error.Message);
                _txtLog.AppendText(Environment.NewLine + "ERROR: " + e.Error.Message + Environment.NewLine);
                _pendingInstallMod = null;
                _pendingInstallOptions = null;
                return;
            }

            SetStatus("UNINSTALLED.");

            // An install-triggered uninstall of a conflicting mod (see
            // OnModInstallClick) needs to proceed straight into installing
            // afterward, instead of just refreshing the status display.
            if (_pendingInstallOptions != null)
            {
                ModDefinition mod = _pendingInstallMod;
                InstallOptions options = _pendingInstallOptions;
                _pendingInstallMod = null;
                _pendingInstallOptions = null;
                StartInstall(mod, options);
                return;
            }

            RefreshLocalState(); // local-only, see Worker_RunWorkerCompleted
        }

        // Installs/removes the chosen EUI variant, then re-runs the active
        // mod's own ui_check.bat (a no-op if that mod isn't installed) so
        // its UI-file selection catches up with the new EUI state.
        private void EuiWorker_DoWork(object sender, DoWorkEventArgs e)
        {
            var variant = (EuiVariant)e.Argument; // null means "remove whatever's there"
            var worker = (BackgroundWorker)sender;
            Action<string> log = msg => worker.ReportProgress(0, msg);

            string dlcRoot = InstallerCore.TryGetDlcFolder();
            if (dlcRoot == null)
            {
                throw new InvalidOperationException("Couldn't locate the Civilization V DLC folder.");
            }

            bool installedSomething = true;
            if (variant == null)
            {
                EuiExtra.Remove(dlcRoot, log);
            }
            else
            {
                installedSomething = EuiExtra.Install(dlcRoot, variant, log);
            }

            // EUI is shared across Assets/DLC, not tied to one specific mod
            // -- re-run every mod's own ui_check.bat so whichever one(s) are
            // actually installed pick up the new UI files (each line below
            // is prefixed with that mod's name -- see RunUiCheck).
            // RerunUiCheck is already a no-op for a mod that isn't installed.
            foreach (ModDefinition mod in ModRegistry.All)
            {
                InstallerCore.RerunUiCheck(mod, log);
            }

            // Same "DONE: ..." format as ModCore.Run/Uninstall's completion
            // lines -- skipped only for the "isn't bundled" no-op above,
            // which already logged its own explanation.
            if (installedSomething)
            {
                log(variant == null ? "DONE: EUI removed." : "DONE: " + variant.DisplayName + " installed.");
            }
        }

        private void EuiWorker_RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            SetControlsEnabled(true);

            if (e.Error != null)
            {
                SetStatus("FAILED: " + e.Error.Message);
                _txtLog.AppendText(Environment.NewLine + "ERROR: " + e.Error.Message + Environment.NewLine);
                return;
            }

            SetStatus("DONE.");
            RefreshLocalState(); // local-only, see Worker_RunWorkerCompleted
        }

        // Locks (or restores) every per-box button while an
        // install/uninstall/EUI-change worker is running. Restoring defers
        // to UpdateModRowStyles/UpdateEuiRowStyles rather than a flat
        // "enabled = true", since which buttons make sense (e.g. UNINSTALL
        // only once something's installed) depends on current state.
        private void SetControlsEnabled(bool enabled)
        {
            _actionInProgress = !enabled;
            if (enabled)
            {
                UpdateModRowStyles();
                UpdateEuiRowStyles();
            }
            else
            {
                foreach (ModRowControls row in _modRows)
                {
                    row.InstallButton.Enabled = false;
                    row.UninstallButton.Enabled = false;
                    row.VersionButton.Enabled = false;
                }
                foreach (EuiRowControls row in _euiRows)
                {
                    row.InstallButton.Enabled = false;
                    row.RemoveButton.Enabled = false;
                }
            }
        }
    }

    // Blocking launch-time warning shown while Civ5 is running (see
    // MainForm.WarnIfCiv5RunningAtLaunch). Three ways out: RETRY re-polls
    // and closes this dialog to loop around; CANCEL closes the installer
    // entirely; FORCE CLOSE returns DialogResult.Abort as a sentinel so the
    // caller can run the same confirm-then-kill path as the main window's
    // launch/FORCE CLOSE CIV buttons, rather than duplicating that
    // confirmation inside this dialog.
    internal class Civ5RunningForm : Form
    {
        public Civ5RunningForm()
        {
            Text = "CIV V MOD INSTALLER";
            ClientSize = new Size(380, 145);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            BackColor = Color.Black;

            Panel pnl = MainForm.MakeRetroBox("CIVILIZATION V IS RUNNING", 12, 12, 356, 70, () => MainForm.ThemeRed);

            var lbl = new Label();
            lbl.SetBounds(12, 18, 332, 46);
            lbl.BackColor = Color.Black;
            lbl.ForeColor = MainForm.ThemeGreen;
            lbl.Font = new Font("Consolas", 9F);
            lbl.Text = "Please close the game before installing or\r\n"
                + "uninstalling mods, then click Retry.";
            pnl.Controls.Add(lbl);

            var btnRetry = new MainForm.RetroButton();
            btnRetry.Text = "RETRY";
            btnRetry.SetBounds(12, 94, 108, 30);
            btnRetry.Click += (s, e) => { DialogResult = DialogResult.Retry; Close(); };

            var btnForceClose = new MainForm.RetroButton();
            btnForceClose.Text = "FORCE CLOSE";
            btnForceClose.ForeColor = MainForm.ThemeRed;
            btnForceClose.SetBounds(134, 94, 122, 30);
            btnForceClose.Click += (s, e) => { DialogResult = DialogResult.Abort; Close(); };

            var btnCancel = new MainForm.RetroButton();
            btnCancel.Text = "CANCEL";
            btnCancel.SetBounds(270, 94, 98, 30);
            btnCancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };

            Controls.Add(pnl);
            Controls.Add(btnRetry);
            Controls.Add(btnForceClose);
            Controls.Add(btnCancel);
            AcceptButton = btnRetry;
            CancelButton = btnCancel;
        }
    }

    // Small modal dialog reached via a mod box's own VERSION button --
    // reuses MainForm's retro chrome (MakeRetroBox/RetroButton) so it reads
    // as part of the same theme rather than a bolted-on stock WinForms
    // dialog, the same way SettingsForm does. Lists that one mod's releases
    // (from the cache MainForm fetched once at startup -- no network here)
    // filtered by the current beta setting; picking one just records the
    // tag for the next time that mod's own INSTALL button is clicked.
    internal class VersionPickerForm : Form
    {
        private ComboBox _cmbVersion;
        private readonly List<string> _tags = new List<string>();

        public VersionPickerForm(ModDefinition mod, List<ModRelease> releases, bool showBeta, string currentTag)
        {
            Text = "CIV V MOD INSTALLER";
            ClientSize = new Size(320, 110);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            BackColor = Color.Black;

            Panel pnl = MainForm.MakeRetroBox(mod.DisplayName.ToUpperInvariant() + " VERSION", 12, 12, 296, 50);

            _cmbVersion = new ComboBox();
            _cmbVersion.SetBounds(12, 18, 272, 22);
            _cmbVersion.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbVersion.FlatStyle = FlatStyle.Flat;
            _cmbVersion.BackColor = Color.Black;
            _cmbVersion.ForeColor = MainForm.ThemeGreen;
            _cmbVersion.Font = new Font("Consolas", 9F, FontStyle.Bold);
            _cmbVersion.Items.Add((mod.Beta == BetaPolicy.OptIn && showBeta)
                ? "Latest (auto -- newest, incl. beta)"
                : "Latest (auto)");

            if (releases != null)
            {
                // Prerelease tags are stripped out entirely when beta builds
                // aren't enabled in Settings, so a hidden beta can't be
                // picked by tag even though it's technically published.
                IEnumerable<ModRelease> shown = showBeta ? releases : releases.Where(r => !r.Prerelease);
                foreach (ModRelease rel in shown)
                {
                    _tags.Add(rel.Tag);
                    string suffix = mod.Beta == BetaPolicy.OptIn ? (rel.Prerelease ? "  [beta]" : "  [stable]") : "";
                    if (!string.IsNullOrEmpty(rel.DisplayExtra))
                    {
                        suffix = "  - " + rel.DisplayExtra + suffix;
                    }
                    _cmbVersion.Items.Add(rel.Tag + suffix);
                }
            }
            int currentIdx = currentTag == null ? -1 : _tags.IndexOf(currentTag);
            _cmbVersion.SelectedIndex = currentIdx >= 0 ? currentIdx + 1 : 0;
            pnl.Controls.Add(_cmbVersion);

            var btnOk = new MainForm.RetroButton();
            btnOk.Text = "OK";
            btnOk.SetBounds(126, 72, 90, 26);
            btnOk.Click += (s, e) => { DialogResult = DialogResult.OK; Close(); };

            var btnCancel = new MainForm.RetroButton();
            btnCancel.Text = "CANCEL";
            btnCancel.SetBounds(220, 72, 88, 26);
            btnCancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };

            Controls.Add(pnl);
            Controls.Add(btnOk);
            Controls.Add(btnCancel);
            AcceptButton = btnOk;
        }

        // Null means "Latest (auto)".
        public string GetSelectedTag()
        {
            int i = _cmbVersion.SelectedIndex;
            return i > 0 ? _tags[i - 1] : null;
        }
    }

    // Small modal dialog reached only via MainForm's gear button -- generic
    // home for misc installer settings, currently just beta opt-in. "Enable
    // beta builds" is the sole way to surface beta tags in each mod's own
    // VERSION popup (see VersionPickerForm) and have Install consider them
    // (see OnModInstallClick).
    internal class SettingsForm : Form
    {
        private CheckBox _chkShowBeta;

        public SettingsForm()
        {
            Text = "CIV V MOD INSTALLER // SETTINGS";
            ClientSize = new Size(320, 120);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            BackColor = Color.Black;

            // Generic "SETTINGS" container -- this is where every misc
            // installer setting lives, not just beta, so it's titled for the
            // whole page rather than this one checkbox.
            Panel pnlSettings = MainForm.MakeRetroBox("SETTINGS", 12, 12, 296, 60);

            _chkShowBeta = new CheckBox();
            _chkShowBeta.Text = "Enable beta builds";
            _chkShowBeta.SetBounds(12, 22, 260, 20);
            _chkShowBeta.ForeColor = MainForm.ThemeGreen;
            _chkShowBeta.BackColor = Color.Black;
            _chkShowBeta.Font = new Font("Consolas", 8.5F);
            _chkShowBeta.Checked = SettingsManager.GetShowBeta();
            pnlSettings.Controls.Add(_chkShowBeta);

            var btnOk = new MainForm.RetroButton();
            btnOk.Text = "OK";
            btnOk.SetBounds(126, 82, 90, 26);
            btnOk.Click += BtnOk_Click;

            var btnCancel = new MainForm.RetroButton();
            btnCancel.Text = "CANCEL";
            btnCancel.SetBounds(220, 82, 88, 26);
            btnCancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };

            Controls.Add(pnlSettings);
            Controls.Add(btnOk);
            Controls.Add(btnCancel);

            AcceptButton = btnOk;
        }

        private void BtnOk_Click(object sender, EventArgs e)
        {
            SettingsManager.SetShowBeta(_chkShowBeta.Checked);
            DialogResult = DialogResult.OK;
            Close();
        }
    }

    // One line of MainForm.RunAllChecks' output -- a plain-language result
    // plus whether it's a pass, so ExtraModsForm can color and count them
    // without re-deriving anything.
    internal struct CheckResult
    {
        public readonly bool Ok;
        public readonly string Message;
        public CheckResult(bool ok, string message) { Ok = ok; Message = message; }
    }

    // Reached via MainForm's "VERIFY MODS" button. Two sections: every check
    // this installer knows how to run (see MainForm.RunAllChecks -- Civ5
    // running, conflicting mods, Tournament Mod/EUI XITS, missing bonus
    // maps, EUI version match, update available), then whatever
    // ExtraModScan.Scan() found -- unrecognized folders under Assets/DLC and
    // Assets/Maps -- one row each with its own ARCHIVE/DELETE buttons on the
    // right, same layout pattern as MainForm's mod/EUI boxes: each button
    // acts on that row immediately rather than staging a choice for a shared
    // Apply step. Not clicking anything on a row IS "keep" -- there's no
    // separate Keep button, matching how the rest of the app never has an
    // explicit "leave it alone" control either. DELETE gets its own
    // confirmation, since (unlike Archive) it can't be undone.
    internal class ExtraModsForm : Form
    {
        private readonly Panel _listPanel;
        private readonly Label _lblEmpty;

        public ExtraModsForm(List<CheckResult> checks, List<ExtraFolderInfo> items)
        {
            Text = "CIV V MOD INSTALLER // VERIFY MODS";
            ClientSize = new Size(520, 546);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            BackColor = Color.Black;

            bool allChecksOk = checks.All(c => c.Ok);
            Panel checksBox = MainForm.MakeRetroBox("CHECKS", 12, 10, 496, 150,
                () => allChecksOk ? MainForm.ThemeGreen : MainForm.ThemeRed);
            int checkY = 18;
            foreach (CheckResult c in checks)
            {
                var lbl = new Label();
                lbl.SetBounds(12, checkY, 472, 18);
                lbl.Text = (c.Ok ? "[OK]   " : "[!!]   ") + c.Message;
                lbl.Font = new Font("Consolas", 7.5F, FontStyle.Bold);
                lbl.ForeColor = c.Ok ? MainForm.ThemeGreen : MainForm.ThemeRed;
                lbl.BackColor = Color.Black;
                lbl.AutoEllipsis = true;
                checksBox.Controls.Add(lbl);
                checkY += 20;
            }

            var lblHint = new Label();
            lblHint.SetBounds(12, 170, 496, 60); // 4 lines at 7.5F Consolas -- ~15px/line
            lblHint.Text = "Folders in Assets/DLC and Assets/Maps this installer doesn't recognize:\r\n"
                + "Leave a row alone to KEEP it.\r\n"
                + "ARCHIVE -- zips the folder in place, then deletes the original.\r\n"
                + "DELETE -- permanently removes the folder. No undo.";
            lblHint.Font = new Font("Consolas", 7.5F);
            lblHint.ForeColor = MainForm.ThemeMagenta;
            lblHint.BackColor = Color.Black;

            _listPanel = new Panel();
            _listPanel.SetBounds(12, 236, 496, 250);
            _listPanel.AutoScroll = true;
            _listPanel.BackColor = Color.Black;
            _listPanel.BorderStyle = BorderStyle.FixedSingle;

            _lblEmpty = new Label();
            _lblEmpty.SetBounds(8, 8, 400, 20);
            _lblEmpty.Text = "Nothing left -- everything's been kept, archived, or deleted.";
            _lblEmpty.Font = new Font("Consolas", 8.5F);
            _lblEmpty.ForeColor = MainForm.ThemeDim;
            _lblEmpty.BackColor = Color.Black;
            _lblEmpty.Visible = items.Count == 0;
            _listPanel.Controls.Add(_lblEmpty);

            foreach (ExtraFolderInfo info in items)
            {
                _listPanel.Controls.Add(BuildRow(info));
            }
            RelayoutRows();

            var btnClose = new MainForm.RetroButton();
            btnClose.Text = "CLOSE";
            btnClose.SetBounds(420, 502, 88, 28);
            btnClose.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };

            Controls.Add(checksBox);
            Controls.Add(lblHint);
            Controls.Add(_listPanel);
            Controls.Add(btnClose);
        }

        private Panel BuildRow(ExtraFolderInfo info)
        {
            var row = new Panel();
            row.SetBounds(4, 0, 468, 26);
            row.BackColor = Color.Black;

            var lblName = new Label();
            lblName.SetBounds(0, 4, 200, 18);
            lblName.Text = "[" + (info.Location == ExtraFolderLocation.Dlc ? "DLC" : "Maps") + "] " + info.Name;
            lblName.Font = new Font("Consolas", 8.5F, FontStyle.Bold);
            lblName.ForeColor = MainForm.ThemeGreen;
            lblName.BackColor = Color.Black;
            lblName.AutoEllipsis = true;

            var lblSize = new Label();
            lblSize.SetBounds(204, 4, 70, 18);
            lblSize.Text = FormatSize(info.SizeBytes);
            lblSize.Font = new Font("Consolas", 8F);
            lblSize.ForeColor = MainForm.ThemeDim;
            lblSize.BackColor = Color.Black;

            var btnArchive = new MainForm.RetroButton();
            btnArchive.Text = "ARCHIVE";
            btnArchive.Font = new Font("Consolas", 7F, FontStyle.Bold);
            btnArchive.SetBounds(280, 1, 90, 24);
            btnArchive.Click += (s, e) => OnArchiveClick(info, row);

            var btnDelete = new MainForm.RetroButton();
            btnDelete.Text = "DELETE";
            btnDelete.Font = new Font("Consolas", 7F, FontStyle.Bold);
            btnDelete.SetBounds(376, 1, 90, 24);
            btnDelete.Click += (s, e) => OnDeleteClick(info, row);

            row.Controls.Add(lblName);
            row.Controls.Add(lblSize);
            row.Controls.Add(btnArchive);
            row.Controls.Add(btnDelete);

            return row;
        }

        private void OnArchiveClick(ExtraFolderInfo info, Panel row)
        {
            try
            {
                Cursor = Cursors.WaitCursor;
                ExtraModScan.Archive(info);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Couldn't archive " + info.Name + ": " + ex.Message, "Archive failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            finally
            {
                Cursor = Cursors.Default;
            }
            RemoveRow(row);
        }

        private void OnDeleteClick(ExtraFolderInfo info, Panel row)
        {
            DialogResult confirm = MessageBox.Show(
                this,
                "Permanently delete \"" + info.Name + "\"? This cannot be undone.",
                "Confirm delete",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);
            if (confirm != DialogResult.Yes)
            {
                return;
            }

            try
            {
                Cursor = Cursors.WaitCursor;
                ExtraModScan.Delete(info);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Couldn't delete " + info.Name + ": " + ex.Message, "Delete failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            finally
            {
                Cursor = Cursors.Default;
            }
            RemoveRow(row);
        }

        private void RemoveRow(Panel row)
        {
            _listPanel.Controls.Remove(row);
            _lblEmpty.Visible = _listPanel.Controls.Count == 0;
            RelayoutRows();
        }

        private void RelayoutRows()
        {
            int y = 4;
            foreach (Control c in _listPanel.Controls)
            {
                if (c == _lblEmpty)
                {
                    continue;
                }
                c.Top = y;
                y += c.Height + 4;
            }
        }

        private static string FormatSize(long bytes)
        {
            double mb = bytes / (1024.0 * 1024.0);
            return mb >= 1 ? mb.ToString("0.0") + " MB" : (bytes / 1024.0).ToString("0") + " KB";
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
