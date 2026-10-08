// GitLabModSource.cs -- IModSource backed by GitLab Releases, for mods that
// publish on gitlab.com instead of GitHub (SnowVe today). GitLab releases
// there carry no uploaded assets at all -- just the tag's auto-generated
// source archive (/-/archive/<tag>/<project>-<tag>.zip), whose single
// top-level folder is "<project>-<tag>" (confirmed against snowve 8.1.0:
// "snowve-8.1.0/"). That archive is the whole repo, game files plus the
// DLL's C++ source, ~500MB for snowve -- so unlike GitHubModSource's
// DownloadAsset this streams straight to disk with a long timeout rather
// than buffering it in memory under HttpClient's default 100s.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace KekModInstaller
{
    [DataContract]
    internal class GlRelease
    {
        [DataMember(Name = "tag_name")]
        public string TagName { get; set; }

        // ISO 8601, e.g. "2026-10-03T05:20:31.676Z".
        [DataMember(Name = "released_at")]
        public string ReleasedAt { get; set; }
    }

    internal class GitLabModSource : IModSource
    {
        private readonly string _owner;
        private readonly string _repo;

        public GitLabModSource(string owner, string repo)
        {
            _owner = owner;
            _repo = repo;
        }

        public List<ModRelease> ListReleases()
        {
            return FetchReleases().Select(ToModRelease).ToList();
        }

        public ModRelease ResolveRelease(InstallOptions options)
        {
            List<GlRelease> releases = FetchReleases();
            if (!string.IsNullOrEmpty(options.TagName))
            {
                GlRelease byTag = releases.FirstOrDefault(
                    r => string.Equals(r.TagName, options.TagName, StringComparison.OrdinalIgnoreCase));
                if (byTag == null)
                {
                    throw new InvalidOperationException(
                        "Release " + options.TagName + " no longer exists on GitLab. Pick another version.");
                }
                return ToModRelease(byTag);
            }
            return ToModRelease(releases[0]); // newest -- no beta channel to filter on
        }

        public string DownloadRelease(ModRelease release, InstallOptions options, Action<string> log)
        {
            string tag = release.Tag;
            string url = "https://gitlab.com/" + _owner + "/" + _repo + "/-/archive/"
                + Uri.EscapeDataString(tag) + "/" + _repo + "-" + Uri.EscapeDataString(tag) + ".zip";
            log("Archive: " + _repo + "-" + tag + ".zip (large, this can take a few minutes)");

            string tempPath = Path.Combine(Path.GetTempPath(), "modinstall_" + Guid.NewGuid().ToString("N") + ".zip");
            using (var client = new HttpClient())
            {
                client.DefaultRequestHeaders.Add("User-Agent", "KekModInstaller");
                client.Timeout = TimeSpan.FromMinutes(30);
                using (HttpResponseMessage response = client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult())
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        throw new InvalidOperationException(
                            "GitLab returned " + (int)response.StatusCode + " " + response.ReasonPhrase
                            + " downloading " + _repo + " " + tag + ".");
                    }
                    using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write))
                    {
                        response.Content.CopyToAsync(fs).GetAwaiter().GetResult();
                    }
                }
            }
            return tempPath;
        }

        private static ModRelease ToModRelease(GlRelease r)
        {
            var mr = new ModRelease();
            mr.Tag = r.TagName;
            mr.Prerelease = false;
            mr.DisplayExtra = (r.ReleasedAt != null && r.ReleasedAt.Length >= 10) ? r.ReleasedAt.Substring(0, 10) : null;
            mr.Native = r;
            return mr;
        }

        // GitLab returns releases newest-first (ordered by released_at).
        private List<GlRelease> FetchReleases()
        {
            string url = "https://gitlab.com/api/v4/projects/"
                + Uri.EscapeDataString(_owner + "/" + _repo) + "/releases?per_page=50";
            using (var client = new HttpClient())
            {
                client.DefaultRequestHeaders.Add("User-Agent", "KekModInstaller");

                HttpResponseMessage response = client.GetAsync(url).GetAwaiter().GetResult();
                if (!response.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException(
                        "GitLab returned " + (int)response.StatusCode + " " + response.ReasonPhrase
                        + " for " + _owner + "/" + _repo + ".");
                }

                byte[] body = response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                var serializer = new DataContractJsonSerializer(typeof(List<GlRelease>));
                using (var stream = new MemoryStream(body))
                {
                    var releases = (List<GlRelease>)serializer.ReadObject(stream);
                    if (releases == null || releases.Count == 0)
                    {
                        throw new InvalidOperationException(
                            "No releases found for " + _owner + "/" + _repo + ".");
                    }
                    return releases;
                }
            }
        }
    }
}
