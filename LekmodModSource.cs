// LekmodModSource.cs -- IModSource backed by Lekmod's own distribution setup:
// a JSON manifest of {tag -> {file_id, release_date, size, changelog}} hosted
// on Lekmod's own GitHub repo (LekmodInstaller/github_setup/versions.json),
// with the actual mod zips hosted on Google Drive by file ID. Mirrors, in
// spirit, what GitHubModSource does for kek-mod/Tournament Mod, but Google
// Drive has none of GitHub's Releases API/asset model -- just a raw file per
// version and a virus-scan interstitial for anything over ~25MB, which has to
// be worked around by hand. Confirmed against Lekmod's own installer
// (LekmodInstaller/google_drive_api.py, updater.py in the cloned lekmod repo).
//
// DataContractJsonSerializer (used elsewhere in this codebase for GitHub's
// JSON) can't deserialize a JSON object with dynamic string keys into a
// Dictionary -- it expects either a fixed DataContract shape or a JSON array
// -- so this hand-parses the one flat "{ tag: { field: value, ... }, ... }"
// shape the manifest actually uses, rather than pulling in a JSON library
// just for this.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace KekModInstaller
{
    internal class LekmodModSource : IModSource
    {
        private const string ManifestUrl =
            "https://raw.githubusercontent.com/EnormousApplePie/Lekmod/main/LekmodInstaller/github_setup/versions.json";

        public List<ModRelease> ListReleases()
        {
            return FetchManifest().Select(ToModRelease).ToList();
        }

        public ModRelease ResolveRelease(InstallOptions options)
        {
            List<Entry> entries = FetchManifest();
            if (entries.Count == 0)
            {
                throw new InvalidOperationException("No Lekmod versions found in the online manifest.");
            }

            if (!string.IsNullOrEmpty(options.TagName))
            {
                Entry byTag = entries.FirstOrDefault(
                    en => string.Equals(en.Tag, options.TagName, StringComparison.OrdinalIgnoreCase));
                if (byTag == null)
                {
                    throw new InvalidOperationException(
                        "Lekmod " + options.TagName + " no longer exists in the online manifest. Pick another version.");
                }
                return ToModRelease(byTag);
            }

            return ToModRelease(entries[0]); // newest -- Lekmod has no beta channel to filter on
        }

        public string DownloadRelease(ModRelease release, InstallOptions options, Action<string> log)
        {
            var entry = (Entry)release.Native;
            if (string.IsNullOrEmpty(entry.FileId))
            {
                throw new InvalidOperationException("Lekmod " + entry.Tag + " has no Google Drive file ID in the manifest.");
            }
            log("Google Drive file: " + entry.FileId + (string.IsNullOrEmpty(entry.Size) ? "" : " (" + entry.Size + ")"));
            return DownloadFromGoogleDrive(entry.FileId, log);
        }

        private class Entry
        {
            public string Tag;
            public string FileId;
            public string ReleaseDate;
            public string Size;
            public string Changelog;
        }

        private static ModRelease ToModRelease(Entry e)
        {
            var mr = new ModRelease();
            mr.Tag = e.Tag;
            mr.Prerelease = false; // Lekmod's manifest has no beta channel
            string extra = e.ReleaseDate;
            if (!string.IsNullOrEmpty(e.Size))
            {
                extra = string.IsNullOrEmpty(extra) ? e.Size : extra + ", " + e.Size;
            }
            mr.DisplayExtra = extra;
            mr.Native = e;
            return mr;
        }

        // Fetches and parses the manifest, newest tag first.
        private static List<Entry> FetchManifest()
        {
            string json;
            using (var client = new HttpClient())
            {
                client.DefaultRequestHeaders.Add("User-Agent", "KekModInstaller");
                // Cache-busting query param -- same trick Lekmod's own installer
                // uses (installer_updater.py), since raw.githubusercontent.com's
                // CDN can otherwise serve a stale copy for several minutes after
                // a new version is published.
                long unixSeconds = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
                string url = ManifestUrl + "?t=" + unixSeconds;
                json = client.GetStringAsync(url).GetAwaiter().GetResult();
            }

            var entries = new List<Entry>();
            foreach (KeyValuePair<string, Dictionary<string, string>> kv in ParseFlatJsonObjectOfObjects(json))
            {
                var e = new Entry();
                e.Tag = kv.Key;
                kv.Value.TryGetValue("file_id", out e.FileId);
                kv.Value.TryGetValue("release_date", out e.ReleaseDate);
                kv.Value.TryGetValue("size", out e.Size);
                kv.Value.TryGetValue("changelog", out e.Changelog);
                if (!string.IsNullOrEmpty(e.FileId))
                {
                    entries.Add(e);
                }
            }
            entries.Sort((a, b) => CompareTags(b.Tag, a.Tag)); // newest first
            return entries;
        }

        private static Dictionary<string, Dictionary<string, string>> ParseFlatJsonObjectOfObjects(string json)
        {
            var result = new Dictionary<string, Dictionary<string, string>>();
            var outerPattern = new Regex("\"([^\"]+)\"\\s*:\\s*\\{([^{}]*)\\}", RegexOptions.Singleline);
            var fieldPattern = new Regex("\"([^\"]+)\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
            foreach (Match m in outerPattern.Matches(json))
            {
                var fields = new Dictionary<string, string>();
                foreach (Match fm in fieldPattern.Matches(m.Groups[2].Value))
                {
                    fields[fm.Groups[1].Value] = Regex.Unescape(fm.Groups[2].Value);
                }
                result[m.Groups[1].Value] = fields;
            }
            return result;
        }

        // "v34.15" -> [34,15]; falls back to a zero/empty parse (sorts
        // lowest) for anything that doesn't fit -- same idiom as
        // TournamentMapExtra's map-version comparison, in case a hotfix ever
        // carries a trailing letter (e.g. "34.15a").
        private static int CompareTags(string a, string b)
        {
            int[] av, bv;
            string asuf, bsuf;
            ParseTag(a, out av, out asuf);
            ParseTag(b, out bv, out bsuf);
            int len = Math.Max(av.Length, bv.Length);
            for (int i = 0; i < len; i++)
            {
                int x = i < av.Length ? av[i] : 0;
                int y = i < bv.Length ? bv[i] : 0;
                if (x != y)
                {
                    return x.CompareTo(y);
                }
            }
            return string.CompareOrdinal(asuf ?? "", bsuf ?? "");
        }

        private static void ParseTag(string raw, out int[] version, out string suffix)
        {
            string s = raw;
            if (!string.IsNullOrEmpty(s) && (s[0] == 'v' || s[0] == 'V'))
            {
                s = s.Substring(1);
            }
            Match m = Regex.Match(s ?? "", @"^(\d+(?:\.\d+)*)([a-zA-Z]*)$");
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

        // Downloads fileId from Google Drive, working around the "can't scan
        // this file for viruses" interstitial Google serves for anything
        // over ~25MB (Lekmod's release zips are ~106MB, so this always
        // triggers) -- same fallback sequence as Lekmod's own installer
        // (google_drive_api.py): try the plain uc?export=download link,
        // follow the confirm-token cookie if Google set one, then fall back
        // to the drive.usercontent.google.com host with confirm=t if it
        // still comes back as an HTML interstitial instead of the file.
        private static string DownloadFromGoogleDrive(string fileId, Action<string> log)
        {
            var handler = new HttpClientHandler();
            handler.CookieContainer = new CookieContainer();
            handler.AllowAutoRedirect = true;

            using (var client = new HttpClient(handler))
            {
                client.DefaultRequestHeaders.Add("User-Agent", "KekModInstaller");
                client.Timeout = TimeSpan.FromMinutes(10);

                string url = "https://drive.google.com/uc?export=download&id=" + fileId;
                HttpResponseMessage response = client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();

                if (IsHtml(response))
                {
                    string token = FindConfirmToken(handler.CookieContainer);
                    if (token != null)
                    {
                        response.Dispose();
                        url = "https://drive.google.com/uc?export=download&id=" + fileId + "&confirm=" + token;
                        response = client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
                    }
                }

                if (IsHtml(response))
                {
                    response.Dispose();
                    log("Large file detected, bypassing Google Drive's virus-scan interstitial...");
                    url = "https://drive.usercontent.google.com/download?id=" + fileId + "&export=download&confirm=t";
                    response = client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
                }

                if (IsHtml(response))
                {
                    throw new InvalidOperationException(
                        "Google Drive returned a web page instead of the file. It may no longer be shared publicly, "
                        + "or its download quota has been exceeded for today.");
                }

                using (response)
                {
                    string tempPath = Path.Combine(Path.GetTempPath(), "modinstall_" + Guid.NewGuid().ToString("N") + ".zip");
                    using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write))
                    {
                        response.Content.CopyToAsync(fs).GetAwaiter().GetResult();
                    }
                    return tempPath;
                }
            }
        }

        private static bool IsHtml(HttpResponseMessage response)
        {
            return response.Content.Headers.ContentType != null
                && string.Equals(response.Content.Headers.ContentType.MediaType, "text/html", StringComparison.OrdinalIgnoreCase);
        }

        private static string FindConfirmToken(CookieContainer cookies)
        {
            foreach (Cookie c in cookies.GetCookies(new Uri("https://drive.google.com")))
            {
                if (c.Name.IndexOf("download_warning", StringComparison.OrdinalIgnoreCase) >= 0
                    || c.Name.IndexOf("confirm", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return c.Value;
                }
            }
            return null;
        }
    }
}
