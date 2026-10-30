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

        // Built from the mirror's latest.json rather than GitLab's answer.
        public bool FromMirror;
    }

    internal class GitLabModSource : IModSource
    {
        private readonly string _owner;
        private readonly string _repo;
        // The mod's id on the mirror (Mirror.FetchMod), or null if it isn't
        // mirrored.
        private readonly string _mirrorId;

        public GitLabModSource(string owner, string repo, string mirrorId)
        {
            _owner = owner;
            _repo = repo;
            _mirrorId = mirrorId;
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

        // From GitLab, else the mirror's copy if it's the same version. A
        // release that came from the mirror (FetchReleases) goes straight there.
        public string DownloadRelease(ModRelease release, InstallOptions options, Action<string> log)
        {
            string tag = release.Tag;
            if (release.Native == null)
            {
                return Mirror.DownloadMod(_mirrorId, tag, log);
            }
            if (_mirrorId == null)
            {
                return DownloadFromGitLab(tag, log);
            }
            return Mirror.Try(Mirror.GitLab, () => DownloadFromGitLab(tag, log), () =>
            {
                log("GitLab failed -- trying " + Mirror.Host + "...");
                return Mirror.DownloadMod(_mirrorId, tag, log);
            });
        }

        private string DownloadFromGitLab(string tag, Action<string> log)
        {
            string url = "https://gitlab.com/" + _owner + "/" + _repo + "/-/archive/"
                + Uri.EscapeDataString(tag) + "/" + _repo + "-" + Uri.EscapeDataString(tag) + ".zip";
            log("Archive: " + _repo + "-" + tag + ".zip (large, this can take a few minutes)");

            string tempPath = Path.Combine(Path.GetTempPath(), "modinstall_" + Guid.NewGuid().ToString("N") + ".zip");
            // GitLab builds the archive on request, so it may take a while
            // to start answering; after that, Mirror's stall timeout applies.
            Mirror.DownloadTo(url, tempPath, null, TimeSpan.FromMinutes(5));
            return tempPath;
        }

        private static ModRelease ToModRelease(GlRelease r)
        {
            var mr = new ModRelease();
            mr.Tag = r.TagName;
            mr.Prerelease = false;
            mr.DisplayExtra = (r.ReleasedAt != null && r.ReleasedAt.Length >= 10) ? r.ReleasedAt.Substring(0, 10) : null;
            mr.Native = r.FromMirror ? null : r;
            return mr;
        }

        // GitLab returns releases newest-first (ordered by released_at). When
        // GitLab can't answer, the mirror's latest alone, marked by a null
        // ModRelease.Native (see ToModRelease callers).
        private List<GlRelease> FetchReleases()
        {
            if (_mirrorId == null)
            {
                return FetchReleasesFromGitLab();
            }
            return Mirror.Try(Mirror.GitLab, FetchReleasesFromGitLab, () =>
            {
                GhRelease m = Mirror.FetchMod(_mirrorId);
                return new List<GlRelease> { new GlRelease { TagName = m.TagName, ReleasedAt = m.ReleasedAt, FromMirror = true } };
            });
        }

        private List<GlRelease> FetchReleasesFromGitLab()
        {
            string url = "https://gitlab.com/api/v4/projects/"
                + Uri.EscapeDataString(_owner + "/" + _repo) + "/releases?per_page=50";
            using (var client = new HttpClient())
            {
                client.Timeout = Mirror.RequestTimeout;
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
