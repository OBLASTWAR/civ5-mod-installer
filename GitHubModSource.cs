// GitHubModSource.cs -- IModSource backed by GitHub Releases. Parameterized
// by repo owner/name and an asset-picking rule, so this one class can cover
// every mod that's plain GitHub Releases even though the picking rules could
// in principle differ (every mod today publishes exactly one asset per
// release, via PickSoleAsset).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.RegularExpressions;

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

    internal class GitHubModSource : IModSource
    {
        private readonly string _owner;
        private readonly string _repo;
        private readonly Func<GhRelease, InstallOptions, GhAsset> _assetPicker;

        // False for mods whose tags/releases carry no meaningful prerelease
        // concept: ResolveRelease then always takes the newest release
        // outright rather than filtering on WantBeta/Prerelease.
        private readonly bool _supportsBetaChannel;

        public GitHubModSource(string owner, string repo, Func<GhRelease, InstallOptions, GhAsset> assetPicker, bool supportsBetaChannel)
        {
            _owner = owner;
            _repo = repo;
            _assetPicker = assetPicker;
            _supportsBetaChannel = supportsBetaChannel;
        }

        public List<ModRelease> ListReleases()
        {
            List<GhRelease> releases = FetchReleases(_owner, _repo);
            if (!_supportsBetaChannel)
            {
                // Keep this list's "newest first" contract honest for mods
                // whose releases[0] we can't trust (see ResolveRelease) --
                // this cached list is also what IsUpdateAvailable and the
                // VERSION picker's "Latest (auto)" entry use, so if it stays
                // in raw list order those get the same wrong-latest bug even
                // though installing itself (ResolveRelease) is now fixed.
                MoveLatestToFront(releases);
            }
            return releases.Select(ToModRelease).ToList();
        }

        // Best-effort: a failed extra lookup just leaves the list in its
        // original (occasionally wrong) order rather than failing the whole
        // release list fetch.
        private void MoveLatestToFront(List<GhRelease> releases)
        {
            GhRelease latest;
            try
            {
                latest = FetchLatestRelease(_owner, _repo);
            }
            catch (Exception)
            {
                return;
            }

            int idx = releases.FindIndex(
                r => string.Equals(r.TagName, latest.TagName, StringComparison.OrdinalIgnoreCase));
            if (idx > 0)
            {
                releases.RemoveAt(idx);
                releases.Insert(0, latest);
            }
        }

        public ModRelease ResolveRelease(InstallOptions options)
        {
            if (!string.IsNullOrEmpty(options.TagName))
            {
                List<GhRelease> releases = FetchReleases(_owner, _repo);
                GhRelease byTag = releases.FirstOrDefault(
                    r => string.Equals(r.TagName, options.TagName, StringComparison.OrdinalIgnoreCase));
                if (byTag == null)
                {
                    throw new InvalidOperationException(
                        "Release " + options.TagName + " no longer exists on GitHub. Pick another version.");
                }
                return ToModRelease(byTag);
            }

            if (!_supportsBetaChannel)
            {
                // Don't assume releases[0] from the list matches GitHub's own
                // "Latest" designation -- some mods (catscats' tournament map
                // pack) don't publish releases in order, so the newest entry
                // by list order can differ from whichever release GitHub
                // itself marks Latest. Ask GitHub directly instead.
                return ToModRelease(FetchLatestRelease(_owner, _repo));
            }

            // GitHub returns releases newest-first. WantBeta takes the newest
            // release of any kind; otherwise the newest one that isn't a
            // prerelease.
            List<GhRelease> allReleases = FetchReleases(_owner, _repo);
            GhRelease chosen = options.WantBeta ? allReleases[0] : allReleases.FirstOrDefault(r => !r.Prerelease);
            if (chosen == null)
            {
                throw new InvalidOperationException(
                    "No stable (non-prerelease) release found. Check \"Include beta versions\" to install the latest beta instead.");
            }
            return ToModRelease(chosen);
        }

        public string DownloadRelease(ModRelease release, InstallOptions options, Action<string> log)
        {
            var ghRelease = (GhRelease)release.Native;
            GhAsset asset = _assetPicker(ghRelease, options);
            log("Asset: " + asset.Name);
            return DownloadAsset(asset);
        }

        private static ModRelease ToModRelease(GhRelease r)
        {
            var mr = new ModRelease();
            mr.Tag = r.TagName;
            mr.Prerelease = r.Prerelease;
            mr.Native = r;
            return mr;
        }

        // Always exactly one asset per release -- the dev zip (package.sh
        // still builds it for local LAN testing) is never published, so
        // there's nothing to pick between.
        public static GhAsset PickSoleAsset(GhRelease release, InstallOptions options)
        {
            if (release.Assets == null || release.Assets.Count == 0)
            {
                throw new InvalidOperationException("Release " + release.TagName + " has no assets.");
            }
            return release.Assets[0];
        }

        public static List<GhRelease> FetchReleases(string owner, string repo)
        {
            string url = "https://api.github.com/repos/" + owner + "/" + repo + "/releases?per_page=50";
            byte[] body = GitHubApi.Get(url, response => new InvalidOperationException(DescribeError(response, owner, repo)));
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

        public static GhRelease FetchLatestRelease(string owner, string repo)
        {
            string url = "https://api.github.com/repos/" + owner + "/" + repo + "/releases/latest";
            byte[] body = GitHubApi.Get(url, response =>
            {
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    return new InvalidOperationException(
                        "No \"Latest\" release found for " + owner + "/" + repo
                        + ". Every release may be marked as a prerelease or draft.");
                }
                return new InvalidOperationException(DescribeError(response, owner, repo));
            });
            var serializer = new DataContractJsonSerializer(typeof(GhRelease));
            using (var stream = new MemoryStream(body))
            {
                return (GhRelease)serializer.ReadObject(stream);
            }
        }

        // Turns a non-success GitHub API response into a clear, actionable
        // message. Most commonly hit via the unauthenticated rate limit (60
        // requests/hour per IP, shared across every mod's status check) --
        // .NET's default HttpClient exception for this ("Response status
        // code does not indicate success") gives no hint what actually
        // happened or when it'll work again, which is what this replaces.
        private static string DescribeError(HttpResponseMessage response, string owner, string repo)
        {
            if (response.StatusCode == HttpStatusCode.Forbidden || response.StatusCode == (HttpStatusCode)429)
            {
                IEnumerable<string> remaining;
                bool outOfRequests = response.Headers.TryGetValues("X-RateLimit-Remaining", out remaining)
                    && remaining.FirstOrDefault() == "0";
                if (outOfRequests)
                {
                    string resetNote = "";
                    IEnumerable<string> resetValues;
                    long resetEpoch;
                    if (response.Headers.TryGetValues("X-RateLimit-Reset", out resetValues)
                        && long.TryParse(resetValues.FirstOrDefault(), out resetEpoch))
                    {
                        DateTime resetTime = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                            .AddSeconds(resetEpoch).ToLocalTime();
                        resetNote = " Try again after " + resetTime.ToShortTimeString() + ".";
                    }
                    return "GitHub API rate limit reached (60 requests/hour without signing in)."
                        + resetNote;
                }
            }
            return "GitHub returned " + (int)response.StatusCode + " " + response.ReasonPhrase
                + " for " + owner + "/" + repo + ".";
        }

        public static string DownloadAsset(GhAsset asset)
        {
            string tempPath = Path.Combine(Path.GetTempPath(), "modinstall_" + Guid.NewGuid().ToString("N") + ".zip");
            using (var client = new HttpClient())
            {
                client.DefaultRequestHeaders.Add("User-Agent", "KekModInstaller");
                byte[] data = client.GetByteArrayAsync(asset.BrowserDownloadUrl).GetAwaiter().GetResult();
                File.WriteAllBytes(tempPath, data);
            }
            return tempPath;
        }
    }

    // Every GitHub REST API GET goes through here, gated by the repo's public
    // Atom feed. Anonymous API calls are capped at 60/hour per IP -- 304s
    // included -- while github.com's releases.atom / commits.atom pages are
    // free, so each URL's last API answer is cached with a fingerprint of the
    // matching feed, and the API is only asked again when the feed changes.
    // That's what lets MainForm re-run the full status check every 30
    // seconds. Feeds: .../releases* -> releases.atom (includes pre-releases),
    // .../contents/* -> commits.atom (default branch); or pass one explicitly.
    internal static class GitHubApi
    {
        // Re-ask the API this long after the last real answer even if the
        // feed looks unchanged (a missed or unreadable feed can't hide an
        // update forever)...
        private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(30);
        // ...and keep asking for this long after the feed changes: Jenkins
        // creates a release before uploading its assets, and the feed
        // doesn't change again when they land.
        private static readonly TimeSpan SettleTime = TimeSpan.FromMinutes(5);

        private static readonly Regex ReposUrl = new Regex(@"^https://api\.github\.com/repos/([^/]+)/([^/]+)/(releases|contents)\b");
        private static readonly Regex FeedEntry = new Regex(@"<entry>.*?<id>(.*?)</id>.*?<updated>(.*?)</updated>", RegexOptions.Singleline);

        private class Entry
        {
            public byte[] Body;
            public string Feed;          // fingerprint when Body was fetched
            public DateTime FetchedUtc;
            public DateTime FeedChangedUtc;
        }

        private static readonly Dictionary<string, Entry> Cache = new Dictionary<string, Entry>();
        private static readonly object CacheLock = new object();

        // fail turns a non-success response into the exception to throw;
        // null throws a plain HttpRequestException with the status code.
        public static byte[] Get(string url, Func<HttpResponseMessage, Exception> fail)
        {
            return Get(url, fail, null);
        }

        public static byte[] Get(string url, Func<HttpResponseMessage, Exception> fail, string feedUrl)
        {
            if (feedUrl == null)
            {
                Match m = ReposUrl.Match(url);
                if (m.Success)
                {
                    feedUrl = "https://github.com/" + m.Groups[1].Value + "/" + m.Groups[2].Value
                        + (m.Groups[3].Value == "releases" ? "/releases.atom" : "/commits.atom");
                }
            }

            Entry cached;
            lock (CacheLock)
            {
                Cache.TryGetValue(url, out cached);
            }

            DateTime now = DateTime.UtcNow;
            string feed = feedUrl == null ? null : TryFetchFeedFingerprint(feedUrl);
            DateTime feedChangedUtc = DateTime.MinValue;
            if (cached != null)
            {
                feedChangedUtc = cached.FeedChangedUtc;
                if (feed != null && feed != cached.Feed)
                {
                    feedChangedUtc = now;
                }
                bool fresh = now - cached.FetchedUtc < MaxAge;
                bool settled = now - feedChangedUtc >= SettleTime;
                // Unreadable feed: trust the cache until it's MaxAge old.
                if (fresh && settled && (feed == null || feed == cached.Feed))
                {
                    return cached.Body;
                }
            }

            byte[] body = FetchApi(url, fail);
            lock (CacheLock)
            {
                Cache[url] = new Entry
                {
                    Body = body,
                    Feed = feed ?? (cached != null ? cached.Feed : null),
                    FetchedUtc = now,
                    FeedChangedUtc = feedChangedUtc,
                };
            }
            return body;
        }

        // null if the feed couldn't be read or had no entries.
        private static string TryFetchFeedFingerprint(string feedUrl)
        {
            try
            {
                using (var client = new HttpClient())
                {
                    client.DefaultRequestHeaders.Add("User-Agent", "KekModInstaller");
                    string xml = client.GetStringAsync(feedUrl).GetAwaiter().GetResult();
                    var sb = new StringBuilder();
                    foreach (Match m in FeedEntry.Matches(xml))
                    {
                        sb.Append(m.Groups[1].Value).Append('|').Append(m.Groups[2].Value).Append('\n');
                    }
                    return sb.Length == 0 ? null : sb.ToString();
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static byte[] FetchApi(string url, Func<HttpResponseMessage, Exception> fail)
        {
            using (var client = new HttpClient())
            {
                client.DefaultRequestHeaders.Add("User-Agent", "KekModInstaller");
                client.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
                using (HttpResponseMessage response = client.GetAsync(url).GetAwaiter().GetResult())
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        if (fail != null)
                        {
                            throw fail(response);
                        }
                        throw new HttpRequestException("GitHub returned " + (int)response.StatusCode + " "
                            + response.ReasonPhrase + " for " + url + ".");
                    }
                    return response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                }
            }
        }
    }
}
