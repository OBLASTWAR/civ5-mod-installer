// GitHubModSource.cs -- IModSource backed by GitHub Releases. Parameterized
// by repo owner/name and an asset-picking rule, so this one class can cover
// every mod that's plain GitHub Releases even though the picking rules
// differ (kek-mod: regex prod/dev match; a future mod might always have
// exactly one asset per release).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
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
            return FetchReleases(_owner, _repo).Select(ToModRelease).ToList();
        }

        public ModRelease ResolveRelease(InstallOptions options)
        {
            List<GhRelease> releases = FetchReleases(_owner, _repo);

            if (!string.IsNullOrEmpty(options.TagName))
            {
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
                return ToModRelease(releases[0]); // newest overall -- no beta channel to filter on
            }

            // GitHub returns releases newest-first. WantBeta takes the newest
            // release of any kind; otherwise the newest one that isn't a
            // prerelease.
            GhRelease chosen = options.WantBeta ? releases[0] : releases.FirstOrDefault(r => !r.Prerelease);
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

        // gh CLI sanitizes spaces in uploaded asset filenames (observed:
        // "kekmod prod 1.5.zip" -> "kekmod.prod.1.5.zip" on GitHub), so match
        // "prod" as a whole word regardless of the separator (space, dot,
        // dash, underscore) around it.
        public static GhAsset PickProdAsset(GhRelease release, InstallOptions options)
        {
            var pattern = new Regex("\\bprod\\b", RegexOptions.IgnoreCase);
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
                    "Release " + release.TagName + " has no asset matching \"prod\". Assets found: " + found);
            }
            return asset;
        }

        // Always exactly one asset per release (Tournament Mod), no
        // prod/dev concept to pick between.
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
            using (var client = new HttpClient())
            {
                client.DefaultRequestHeaders.Add("User-Agent", "KekModInstaller");
                client.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");

                HttpResponseMessage response = client.GetAsync(url).GetAwaiter().GetResult();
                if (!response.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException(DescribeError(response, owner, repo));
                }

                byte[] body = response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
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
}
