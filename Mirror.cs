// Mirror.cs -- fallback for players who can't reach GitHub, GitLab or Google
// Drive (ISP throttling, outages, GitHub's 60/hour API rate limit).
// mirror.ww3.cx keeps a copy of the latest version of every mod the
// installer offers, plus the ticker, refreshed every few minutes (the sync
// job lives in the private build repo):
//
//   <base>ticker.txt
//   <base><owner>/<repo>/latest.json      GitHub releases (Repos): /releases/latest, cut down
//   <base>mods/<id>/latest.json           Lekmod, SnowVe, Vox Populi, same shape
//   <base>.../<tag>/<file>
//   <base>api/<contents API path>/listing.json
//   <base>api/<...>/files/<git blob sha>/<name>
//                                         GitHub folder listings (ContentsPaths):
//                                         Better Pangaea, Lekmap, Weevee Map
//
// latest.json uses GitHub's release field names, so it parses straight into
// GhRelease; listing.json is the contents API's own answer. URLs in both are
// relative to the file. Only the latest version is there, so a VERSION pick
// of anything older still needs the real source.
//
// Everything still goes to the real source first. Try() falls back on any
// error, and a network-level one (timeout, refused, reset, DNS) also parks
// that service for DownFor, so the 30-second status loop goes straight to
// the mirror instead of waiting out a timeout every round.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.ExceptionServices;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace KekModInstaller
{
    internal static class Mirror
    {
        // Tried in order. Each must serve the layout above.
        private static readonly string[] Bases = { "https://mirror.ww3.cx/v1/" };

        private static readonly HashSet<string> Repos = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "OBLASTWAR/civ5-mod-installer",
            "OBLASTWAR/kek-mod",
            "OBLASTWAR/pangea-stratbal",
            "OBLASTWAR/enhanced-user-interface",
            "OBLASTWAR/enhanced-user-interface-xits",
            "OBLASTWAR/civ5-lobby-patch",
            "catscatsforever/Civ5-Patch", // Tournament Mod
        };

        // api.github.com paths (no trailing slash) of the mirrored folder listings.
        private static readonly HashSet<string> ContentsPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "repos/ImmoSS/Civ5-Patch/contents/Maps",          // Better Pangaea
            "repos/EnormousApplePie/Lekmod/contents/Lekmap",  // Lekmap
            "repositories/1354734621/contents",               // Weevee Map
        };

        // Services, parked separately: in Russia api.github.com often works
        // while raw.githubusercontent.com or the release download CDN doesn't.
        public const string Api = "api";          // api.github.com + github.com's atom feeds
        public const string Raw = "raw";          // raw.githubusercontent.com
        public const string Downloads = "downloads"; // release assets (github.com -> CDN redirect)
        public const string Drive = "drive";      // Google Drive (Lekmod, Vox Populi)
        public const string GitLab = "gitlab";    // gitlab.com (SnowVe)

        private const string ApiPrefix = "https://api.github.com/";

        private static readonly TimeSpan DownFor = TimeSpan.FromMinutes(10);
        private static readonly Dictionary<string, DateTime> DownUntil = new Dictionary<string, DateTime>();

        // latest.json answers, briefly, so a status round asking for the
        // same repo three times costs one request.
        private static readonly TimeSpan JsonMaxAge = TimeSpan.FromSeconds(60);
        private static readonly Dictionary<string, KeyValuePair<DateTime, GhRelease>> JsonCache =
            new Dictionary<string, KeyValuePair<DateTime, GhRelease>>();

        private static readonly object Lock = new object();

        // Header timeout for every request; downloads also fail after
        // StallTimeout without a byte (a throttled connection that stalls
        // partway would otherwise hang until HttpClient's 100s default, or
        // forever once headers are in).
        public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);
        private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(30);

        private static readonly Regex GitHubAssetUrl = new Regex(
            @"^https://github\.com/([^/]+)/([^/]+)/releases/download/([^/]+)/([^/?#]+)$", RegexOptions.IgnoreCase);

        public static string UserAgent
        {
            get { return "KekModInstaller/" + InstallerCore.GetInstallerVersion(); }
        }

        public static string Host
        {
            get { return new Uri(Bases[0]).Host; }
        }

        public static bool Covers(string owner, string repo)
        {
            return Repos.Contains(owner + "/" + repo);
        }

        public static bool IsDown(string service)
        {
            lock (Lock)
            {
                DateTime until;
                return DownUntil.TryGetValue(service, out until) && DateTime.UtcNow < until;
            }
        }

        public static void MarkDown(string service)
        {
            lock (Lock)
            {
                DownUntil[service] = DateTime.UtcNow + DownFor;
            }
        }

        private static void MarkUp(string service)
        {
            lock (Lock)
            {
                DownUntil.Remove(service);
            }
        }

        // Couldn't talk to the server at all, as opposed to an HTTP answer
        // (404, rate limit) -- only these park a service.
        public static bool IsNetworkFailure(Exception ex)
        {
            if (ex is AggregateException && ex.InnerException != null)
            {
                ex = ex.InnerException;
            }
            return ex is TaskCanceledException || ex is TimeoutException || ex is IOException || ex is WebException
                || (ex is HttpRequestException && ex.InnerException != null);
        }

        // fromGitHub, or fromMirror if GitHub fails. Rethrows GitHub's error
        // if the mirror fails too -- its message is the one players and the
        // existing error handling expect.
        public static T Try<T>(string service, Func<T> fromGitHub, Func<T> fromMirror)
        {
            Exception gitHubError = null;
            if (!IsDown(service))
            {
                try
                {
                    return fromGitHub();
                }
                catch (Exception ex)
                {
                    gitHubError = ex;
                    if (IsNetworkFailure(ex))
                    {
                        MarkDown(service);
                    }
                }
            }
            try
            {
                return fromMirror();
            }
            catch (Exception)
            {
                if (gitHubError == null)
                {
                    // GitHub was parked and the mirror's out too: ask GitHub
                    // anyway rather than fail without trying.
                    T result = fromGitHub();
                    MarkUp(service);
                    return result;
                }
                ExceptionDispatchInfo.Capture(gitHubError).Throw();
                throw; // unreachable
            }
        }

        // The mirror's copy of owner/repo's latest release, asset URLs made
        // absolute.
        public static GhRelease FetchLatest(string owner, string repo)
        {
            if (!Covers(owner, repo))
            {
                throw new InvalidOperationException(owner + "/" + repo + " isn't mirrored.");
            }
            return FetchManifest(owner + "/" + repo);
        }

        // The mirror's latest version of a non-GitHub mod: "lekmod",
        // "snowve" or "voxpopuli".
        public static GhRelease FetchMod(string id)
        {
            return FetchManifest("mods/" + id);
        }

        // Downloads the mirror's copy of mod id's version tag to a temp file
        // (ext: ".zip"). Fails if the mirror's latest is a different version.
        public static string DownloadMod(string id, string tag, Action<string> log)
        {
            GhRelease release = FetchMod(id);
            if (!string.Equals(release.TagName, tag, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("The mirror only has " + id + " " + release.TagName + ", not " + tag + ".");
            }
            if (release.Assets == null || release.Assets.Count == 0)
            {
                throw new InvalidOperationException("The mirror's " + id + " " + tag + " has no files.");
            }
            string tempPath = Path.Combine(Path.GetTempPath(), "modinstall_" + Guid.NewGuid().ToString("N") + ".zip");
            try
            {
                Download(release.Assets[0], tempPath, null, log);
            }
            catch (Exception)
            {
                try { File.Delete(tempPath); } catch (Exception) { }
                throw;
            }
            return tempPath;
        }

        private static GhRelease FetchManifest(string dir)
        {
            return FromEachBase(b =>
            {
                string url = b + dir + "/latest.json";
                lock (Lock)
                {
                    KeyValuePair<DateTime, GhRelease> hit;
                    if (JsonCache.TryGetValue(url, out hit) && DateTime.UtcNow - hit.Key < JsonMaxAge)
                    {
                        return hit.Value;
                    }
                }
                GhRelease release;
                var serializer = new DataContractJsonSerializer(typeof(GhRelease));
                using (var stream = new MemoryStream(GetBytes(url)))
                {
                    release = (GhRelease)serializer.ReadObject(stream);
                }
                if (release == null || string.IsNullOrEmpty(release.TagName))
                {
                    throw new InvalidDataException("Unreadable " + url);
                }
                foreach (GhAsset a in release.Assets ?? new List<GhAsset>())
                {
                    a.BrowserDownloadUrl = new Uri(new Uri(url), a.BrowserDownloadUrl).AbsoluteUri;
                }
                lock (Lock)
                {
                    JsonCache[url] = new KeyValuePair<DateTime, GhRelease>(DateTime.UtcNow, release);
                }
                return release;
            });
        }

        // A file at the top of the mirror, e.g. "ticker.txt".
        public static string FetchText(string path)
        {
            return FromEachBase(b => Encoding.UTF8.GetString(GetBytes(b + path)));
        }

        // apiUrl's path below api.github.com if it's a mirrored folder
        // listing, else null.
        private static string ContentsPath(string apiUrl)
        {
            if (apiUrl == null || !apiUrl.StartsWith(ApiPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            string path = apiUrl.Substring(ApiPrefix.Length).TrimEnd('/');
            return ContentsPaths.Contains(path) ? path : null;
        }

        public static bool CoversContents(string apiUrl)
        {
            return ContentsPath(apiUrl) != null;
        }

        // The mirror's copy of a GitHub contents API folder listing,
        // download URLs made absolute.
        public static List<GhContentsEntry> FetchContents(string apiUrl)
        {
            string path = ContentsPath(apiUrl);
            if (path == null)
            {
                throw new InvalidOperationException(apiUrl + " isn't mirrored.");
            }
            return FromEachBase(b =>
            {
                string url = b + "api/" + path + "/listing.json";
                List<GhContentsEntry> entries;
                var serializer = new DataContractJsonSerializer(typeof(List<GhContentsEntry>));
                using (var stream = new MemoryStream(GetBytes(url)))
                {
                    entries = (List<GhContentsEntry>)serializer.ReadObject(stream);
                }
                if (entries == null)
                {
                    throw new InvalidDataException("Unreadable " + url);
                }
                foreach (GhContentsEntry e in entries)
                {
                    e.DownloadUrl = new Uri(new Uri(url), e.DownloadUrl).AbsoluteUri;
                }
                return entries;
            });
        }

        // One file of a folder listing from apiUrl: from GitHub, then the
        // mirror (by the file's git blob sha), checking that sha either way.
        public static byte[] GetContentsFile(string apiUrl, GhContentsEntry entry)
        {
            string path = ContentsPath(apiUrl);
            bool fromGitHub = entry.DownloadUrl != null
                && entry.DownloadUrl.StartsWith("https://raw.githubusercontent.com/", StringComparison.OrdinalIgnoreCase);
            if (!fromGitHub || path == null || string.IsNullOrEmpty(entry.Sha))
            {
                byte[] direct = GetBytes(entry.DownloadUrl);
                VerifyBlobSha(entry, direct);
                return direct;
            }
            return Try(Raw,
                () =>
                {
                    byte[] data = GetBytes(entry.DownloadUrl);
                    VerifyBlobSha(entry, data);
                    return data;
                },
                () => FromEachBase(b =>
                {
                    byte[] data = GetBytes(b + "api/" + path + "/files/" + entry.Sha + "/" + Uri.EscapeDataString(entry.Name));
                    VerifyBlobSha(entry, data);
                    return data;
                }));
        }

        // Git's blob hash: sha1("blob <length>\0" + content).
        private static void VerifyBlobSha(GhContentsEntry entry, byte[] data)
        {
            if (string.IsNullOrEmpty(entry.Sha))
            {
                return;
            }
            byte[] header = Encoding.ASCII.GetBytes("blob " + data.Length + "\0");
            string got;
            using (var sha = SHA1.Create())
            {
                sha.TransformBlock(header, 0, header.Length, null, 0);
                sha.TransformFinalBlock(data, 0, data.Length);
                got = string.Concat(sha.Hash.Select(x => x.ToString("x2")));
            }
            if (!string.Equals(got, entry.Sha, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(entry.Name + " failed its checksum (download corrupted).");
            }
        }

        private static T FromEachBase<T>(Func<string, T> fetch)
        {
            Exception last = null;
            foreach (string b in Bases)
            {
                try
                {
                    return fetch(b);
                }
                catch (Exception ex)
                {
                    last = ex;
                }
            }
            ExceptionDispatchInfo.Capture(last).Throw();
            throw last; // unreachable
        }

        // Where the mirror keeps a GitHub-hosted asset; null if it doesn't.
        private static string MirrorUrlFor(string gitHubUrl, string b)
        {
            Match m = GitHubAssetUrl.Match(gitHubUrl ?? "");
            if (!m.Success || !Covers(m.Groups[1].Value, m.Groups[2].Value))
            {
                return null;
            }
            return b + m.Groups[1].Value + "/" + m.Groups[2].Value + "/" + m.Groups[3].Value + "/" + m.Groups[4].Value;
        }

        // Downloads asset to path: from GitHub, then the mirror (or the other
        // way round while GitHub downloads are parked), checking the sha256
        // GitHub publishes for it. log (may be null) hears about a fallback.
        public static void Download(GhAsset asset, string path, Action<int> progress, Action<string> log)
        {
            string url = asset.BrowserDownloadUrl;
            bool fromGitHub = GitHubAssetUrl.IsMatch(url ?? "");
            var sources = new List<string>();
            if (fromGitHub)
            {
                sources.Add(url);
                foreach (string b in Bases)
                {
                    string m = MirrorUrlFor(url, b);
                    if (m != null)
                    {
                        sources.Add(m);
                    }
                }
                if (sources.Count > 1 && IsDown(Downloads))
                {
                    sources.RemoveAt(0);
                    sources.Add(url);
                }
            }
            else
            {
                sources.Add(url); // already a mirror (or other) URL
            }

            Exception last = null;
            for (int i = 0; i < sources.Count; i++)
            {
                string source = sources[i];
                bool isGitHub = source == url && fromGitHub;
                if (!isGitHub && log != null)
                {
                    log(last != null
                        ? "GitHub download failed (" + InstallerCore.DescribeException(last) + ") -- trying " + new Uri(source).Host + "..."
                        : "Downloading from " + new Uri(source).Host + "...");
                }
                try
                {
                    DownloadTo(source, path, progress);
                    VerifyDigest(asset, path);
                    if (isGitHub)
                    {
                        MarkUp(Downloads);
                    }
                    return;
                }
                catch (Exception ex)
                {
                    if (isGitHub && IsNetworkFailure(ex))
                    {
                        MarkDown(Downloads);
                    }
                    last = ex;
                }
            }
            ExceptionDispatchInfo.Capture(last).Throw();
        }

        private static void VerifyDigest(GhAsset asset, string path)
        {
            const string prefix = "sha256:";
            if (asset.Digest == null || !asset.Digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return; // GitHub only started publishing digests in 2025
            }
            string want = asset.Digest.Substring(prefix.Length).ToLowerInvariant();
            string got;
            using (var sha = SHA256.Create())
            using (FileStream f = File.OpenRead(path))
            {
                got = string.Concat(sha.ComputeHash(f).Select(x => x.ToString("x2")));
            }
            if (got != want)
            {
                throw new InvalidDataException(asset.Name + " failed its checksum (download corrupted).");
            }
        }

        public static byte[] GetBytes(string url)
        {
            using (var client = new HttpClient())
            {
                client.Timeout = RequestTimeout;
                client.DefaultRequestHeaders.Add("User-Agent", UserAgent);
                using (HttpResponseMessage response = client.GetAsync(url).GetAwaiter().GetResult())
                {
                    ThrowIfFailed(response, url);
                    return response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                }
            }
        }

        // Streams url into path; progress (may be null) gets 0-100. Fails on
        // a stall or a short body, not just a dropped connection.
        public static void DownloadTo(string url, string path, Action<int> progress)
        {
            DownloadTo(url, path, progress, RequestTimeout);
        }

        // headersTimeout: how long the server may take to start answering
        // (GitLab builds its archives on request).
        public static void DownloadTo(string url, string path, Action<int> progress, TimeSpan headersTimeout)
        {
            using (var client = new HttpClient())
            {
                client.Timeout = headersTimeout;
                client.DefaultRequestHeaders.Add("User-Agent", UserAgent);
                using (HttpResponseMessage response = client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult())
                {
                    ThrowIfFailed(response, url);
                    CopyTo(client, response, path, progress);
                }
            }
        }

        // Writes response's body to path, failing after StallTimeout without
        // a byte or on a body shorter than its Content-Length. Deletes the
        // partial file on failure.
        public static void CopyTo(HttpClient client, HttpResponseMessage response, string path, Action<int> progress)
        {
            string host = response.RequestMessage != null ? response.RequestMessage.RequestUri.Host : "the server";
            long total = response.Content.Headers.ContentLength ?? 0;
            try
            {
                using (Stream input = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult())
                using (FileStream output = File.Create(path))
                {
                    var buffer = new byte[81920];
                    long done = 0;
                    while (true)
                    {
                        Task<int> read = input.ReadAsync(buffer, 0, buffer.Length);
                        if (Task.WaitAny(new Task[] { read }, StallTimeout) < 0)
                        {
                            client.CancelPendingRequests();
                            throw new TimeoutException("Download from " + host + " stalled (no data for "
                                + (int)StallTimeout.TotalSeconds + "s).");
                        }
                        int n = read.GetAwaiter().GetResult();
                        if (n == 0)
                        {
                            break;
                        }
                        output.Write(buffer, 0, n);
                        done += n;
                        if (progress != null && total > 0)
                        {
                            progress((int)(done * 100 / total));
                        }
                    }
                    if (total > 0 && done != total)
                    {
                        throw new IOException("Download from " + host + " was cut off ("
                            + done + " of " + total + " bytes).");
                    }
                }
            }
            catch (Exception)
            {
                try { File.Delete(path); } catch (Exception) { }
                throw;
            }
        }

        // An HTTP error answer -- deliberately not HttpRequestException, so
        // IsNetworkFailure doesn't park GitHub over a 404.
        private static void ThrowIfFailed(HttpResponseMessage response, string url)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(new Uri(url).Host + " returned " + (int)response.StatusCode + " "
                    + response.ReasonPhrase + " for " + new Uri(url).AbsolutePath + ".");
            }
        }
    }
}
