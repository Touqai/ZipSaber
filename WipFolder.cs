using System;
using System.IO;
using SongCore;
using SongCore.Data;

namespace ZipSaber
{
    /// <summary>
    /// Where dropped WIP maps go. By default Beat Saber_Data/CustomWipLevels; optionally a custom
    /// folder, which is registered with SongCore as its own WIP folder so the maps show up in-game.
    /// </summary>
    internal static class WipFolder
    {
        private static object _registered;      // SongCore SeparateSongFolder for the custom path
        private static string _registeredPath;

        internal static string DefaultPath { get; set; }

        internal static bool UsingCustom => !string.IsNullOrWhiteSpace(Plugin.Config?.CustomWipPath);

        /// <summary>Folder dropped WIP maps should be extracted into.</summary>
        internal static string Current
        {
            get
            {
                if (UsingCustom && TryNormalize(Plugin.Config.CustomWipPath, out string p, out _)) return p;
                return DefaultPath;
            }
        }

        /// <summary>Validate a user-entered path. Creates it if it doesn't exist.</summary>
        internal static bool TryNormalize(string input, out string full, out string error)
        {
            full = null; error = null;
            try
            {
                if (string.IsNullOrWhiteSpace(input)) { error = "No folder entered."; return false; }
                string trimmed = input.Trim().Trim('"');
                if (!Path.IsPathRooted(trimmed)) { error = "Use a full path, e.g. D:\\Maps\\WIP"; return false; }
                full = Path.GetFullPath(trimmed);
                if (!Directory.Exists(full)) Directory.CreateDirectory(full);
                // Write test
                string probe = Path.Combine(full, ".zipsaber_probe");
                File.WriteAllText(probe, "ok"); File.Delete(probe);
                return true;
            }
            catch (Exception ex) { error = ex.Message; full = null; return false; }
        }

        /// <summary>Set (or clear with null/empty) the custom folder. Returns an error message or null.</summary>
        internal static string Set(string input)
        {
            if (Plugin.Config == null) return "Config not loaded.";
            if (string.IsNullOrWhiteSpace(input))
            {
                Plugin.Config.CustomWipPath = "";
                SyncSongCore();
                return null;
            }
            if (!TryNormalize(input, out string full, out string err)) return err;
            if (DefaultPath != null && string.Equals(full.TrimEnd('\\', '/'), DefaultPath.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
            {
                Plugin.Config.CustomWipPath = "";   // same as default — no need for a custom entry
                SyncSongCore();
                return null;
            }
            Plugin.Config.CustomWipPath = full;
            SyncSongCore();
            return null;
        }

        /// <summary>Register/unregister the custom folder with SongCore and refresh songs if it changed.</summary>
        internal static void SyncSongCore()
        {
            try
            {
                string want = UsingCustom && TryNormalize(Plugin.Config.CustomWipPath, out string p, out _) ? p : null;
                if (string.Equals(want, _registeredPath, StringComparison.OrdinalIgnoreCase)) return;

                if (_registered != null)
                {
                    Loader.SeparateSongFolders.Remove((SeparateSongFolder)_registered);
                    _registered = null; _registeredPath = null;
                }
                if (want != null)
                {
                    _registered = Collections.AddSeparateSongFolder("ZipSaber WIP", want, FolderLevelPack.CustomWIPLevels, null, wip: true);
                    _registeredPath = want;
                    Plugin.Log?.Info($"[WipFolder] Registered custom WIP folder with SongCore: {want}");
                }
                Plugin.Instance?.RequestSongRefresh();
            }
            catch (Exception ex) { Plugin.Log?.Warn($"[WipFolder] SongCore registration failed: {ex.Message}"); }
        }
    }
}
