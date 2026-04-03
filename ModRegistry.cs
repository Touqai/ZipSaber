using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ZipSaber
{
    /// <summary>
    /// Scans the Plugins folder, builds the full list of valid BSIPA mods,
    /// and computes the reverse dependency map (who depends on what).
    /// </summary>
    internal static class ModRegistry
    {
        private static List<ModInfo> _cache = null;

        /// <summary>
        /// Returns all valid BSIPA mods in the Plugins folder, excluding ZipSaber itself.
        /// Result is cached; call Invalidate() to force a rescan.
        /// </summary>
        internal static List<ModInfo> GetAllMods(string pluginsPath)
        {
            if (_cache != null) return _cache;
            return Refresh(pluginsPath);
        }

        internal static void Invalidate() => _cache = null;

        internal static List<ModInfo> Refresh(string pluginsPath)
        {
            _cache = null;
            var mods = new List<ModInfo>();

            if (string.IsNullOrEmpty(pluginsPath) || !Directory.Exists(pluginsPath))
            {
                Plugin.Log?.Error("[ModRegistry] Plugins path invalid.");
                return mods;
            }

            // ── Step 1: scan all DLLs ─────────────────────────────────────────────
            foreach (string dll in Directory.GetFiles(pluginsPath, "*.dll"))
            {
                string fileName = Path.GetFileNameWithoutExtension(dll);

                // Skip ZipSaber itself
                if (fileName.Equals("ZipSaber", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (ModValidator.TryReadModInfo(dll, out ModInfo info))
                {
                    // Look for companion .manifest sidecar file
                    string manifestSidecar = Path.Combine(pluginsPath, fileName + ".manifest");
                    if (File.Exists(manifestSidecar))
                        info.ManifestPath = manifestSidecar;

                    mods.Add(info);
                }
            }

            // ── Step 2: build reverse dependency map ──────────────────────────────
            // Map from mod ID (lowercase) → ModInfo for fast lookup
            var byId = mods.ToDictionary(
                m => m.Id.ToLowerInvariant(),
                m => m,
                StringComparer.OrdinalIgnoreCase);

            foreach (var mod in mods)
            {
                foreach (string dep in mod.DependsOn)
                {
                    if (byId.TryGetValue(dep.ToLowerInvariant(), out ModInfo depMod))
                        depMod.RequiredBy.Add(mod.Id);
                }
            }

            // Sort alphabetically by display name
            mods.Sort((a, b) => string.Compare(a.DisplayLabel, b.DisplayLabel, StringComparison.OrdinalIgnoreCase));

            _cache = mods;
            Plugin.Log?.Info($"[ModRegistry] Found {mods.Count} mods.");
            return mods;
        }
    }
}
