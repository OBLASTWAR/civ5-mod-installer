// GraphicsCacheExtra.cs -- backs MainForm's CLEAR GFX CACHE button.
//
// Corrupted on-disk GPU shader caches are a recurring cause of Civ5's
// "black map / smearing icons" bug: the game's own logs stay completely
// clean while the driver silently feeds it broken precompiled shaders.
// Reinstalling the mod (or the game), and rebooting, never fixes it
// because these caches are ordinary per-user files the driver keeps
// between sessions -- deleting them forces a clean shader rebuild on the
// next launch. Diagnosed live on a real machine 2026-07-22: same exe,
// same args, black map before the wipe and a working map after.
//
// Every target here is a cache by contract -- drivers and the game
// rebuild whatever is missing -- so wiping is always safe. Files that
// are locked or access-denied (e.g. the ProgramData NV_Cache when not
// elevated) are simply skipped and counted; a partial clear still helps
// and never errors out.

using System;
using System.Collections.Generic;
using System.IO;

namespace KekModInstaller
{
    internal sealed class GraphicsCacheResult
    {
        public string Label;
        public string Path;
        public bool Present;
        public int FilesDeleted;
        public int FilesSkipped;
    }

    internal static class GraphicsCacheClear
    {
        // One entry per known shader-cache location across GPU vendors.
        // Missing directories (wrong vendor for this machine, older driver
        // layout) are reported as absent and skipped -- listing all vendors
        // unconditionally is what makes the same button work on NVIDIA,
        // AMD and Intel machines alike.
        private sealed class Target
        {
            public string Label;
            public string Path;
            public bool Civ5DbFilesOnly;
        }

        private static List<Target> BuildTargets()
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

            var targets = new List<Target>();

            // Windows' vendor-neutral D3D shader cache -- present on every
            // machine regardless of GPU brand.
            targets.Add(new Target { Label = "Windows D3D cache", Path = Path.Combine(localAppData, "D3DSCache") });

            // NVIDIA. NV_Cache is the driver's second-level store: entries
            // wiped from DXCache get restored from here on the next run, so
            // clearing DXCache alone is not enough (observed live).
            targets.Add(new Target { Label = "NVIDIA DX cache", Path = Path.Combine(localAppData, "NVIDIA", "DXCache") });
            targets.Add(new Target { Label = "NVIDIA GL cache", Path = Path.Combine(localAppData, "NVIDIA", "GLCache") });
            targets.Add(new Target { Label = "NVIDIA NV_Cache (user)", Path = Path.Combine(localAppData, "NVIDIA Corporation", "NV_Cache") });
            targets.Add(new Target { Label = "NVIDIA NV_Cache (system)", Path = Path.Combine(programData, "NVIDIA Corporation", "NV_Cache") });

            // AMD -- cache folder names have shifted across driver
            // generations, so list every known one.
            targets.Add(new Target { Label = "AMD DX cache", Path = Path.Combine(localAppData, "AMD", "DxCache") });
            targets.Add(new Target { Label = "AMD DXC cache", Path = Path.Combine(localAppData, "AMD", "DxcCache") });
            targets.Add(new Target { Label = "AMD GL cache", Path = Path.Combine(localAppData, "AMD", "GLCache") });
            targets.Add(new Target { Label = "AMD Vulkan cache", Path = Path.Combine(localAppData, "AMD", "VkCache") });
            targets.Add(new Target { Label = "AMD OGL cache", Path = Path.Combine(localAppData, "AMD", "OglCache") });

            // Intel.
            targets.Add(new Target { Label = "Intel shader cache", Path = Path.Combine(localAppData, "Intel", "ShaderCache") });

            // Civ5's cached gameplay databases. Only the top-level *.db
            // files: subfolders under cache/ (downloads, images) belong to
            // other tooling and are not the game's to rebuild.
            targets.Add(new Target
            {
                Label = "Civ5 game cache",
                Path = Path.Combine(documents, "My Games", "Sid Meier's Civilization 5", "cache"),
                Civ5DbFilesOnly = true,
            });

            return targets;
        }

        public static List<GraphicsCacheResult> ClearAll()
        {
            var results = new List<GraphicsCacheResult>();
            foreach (Target target in BuildTargets())
            {
                var result = new GraphicsCacheResult { Label = target.Label, Path = target.Path };
                results.Add(result);

                if (!Directory.Exists(target.Path))
                {
                    continue;
                }
                result.Present = true;

                string[] files;
                try
                {
                    files = target.Civ5DbFilesOnly
                        ? Directory.GetFiles(target.Path, "*.db", SearchOption.TopDirectoryOnly)
                        : Directory.GetFiles(target.Path, "*", SearchOption.AllDirectories);
                }
                catch (Exception)
                {
                    // Can't even enumerate (ACLs) -- report as present but
                    // untouched rather than failing the whole pass.
                    continue;
                }

                foreach (string file in files)
                {
                    try
                    {
                        File.SetAttributes(file, FileAttributes.Normal);
                        File.Delete(file);
                        result.FilesDeleted++;
                    }
                    catch (Exception)
                    {
                        result.FilesSkipped++;
                    }
                }

                if (!target.Civ5DbFilesOnly)
                {
                    RemoveEmptySubdirectories(target.Path);
                }
            }
            return results;
        }

        // Cosmetic only -- an empty leftover subfolder is harmless, so any
        // failure (locked, ACL) is ignored. Never removes the root itself:
        // some drivers get confused when their cache directory disappears
        // entirely, an empty one is always safe.
        private static void RemoveEmptySubdirectories(string root)
        {
            string[] subdirs;
            try
            {
                subdirs = Directory.GetDirectories(root, "*", SearchOption.AllDirectories);
            }
            catch (Exception)
            {
                return;
            }

            // Deepest first so parents empty out as their children go.
            Array.Sort(subdirs, (a, b) => b.Length.CompareTo(a.Length));
            foreach (string dir in subdirs)
            {
                try
                {
                    Directory.Delete(dir, false);
                }
                catch (Exception)
                {
                    // Not empty or not ours to remove -- fine.
                }
            }
        }
    }
}
