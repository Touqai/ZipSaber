using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ZipSaber
{
    /// <summary>
    /// Inspects DLL files by scanning raw PE bytes for an embedded manifest.json,
    /// without loading the assembly (which would fail due to Beat Saber dependencies).
    /// </summary>
    internal static class ModValidator
    {
        /// <summary>
        /// Returns true if the DLL is a BSIPA plugin (has embedded manifest.json).
        /// Populates a ModInfo with id, name, version, dependsOn.
        /// </summary>
        internal static bool TryReadModInfo(string dllPath, out ModInfo info)
        {
            info = null;
            if (!File.Exists(dllPath)) return false;

            try
            {
                byte[] bytes = File.ReadAllBytes(dllPath);
                if (!ContainsString(bytes, "manifest.json")) return false;

                string json = TryExtractManifestJson(bytes);

                info = new ModInfo
                {
                    DllPath  = dllPath,
                    Id       = ExtractJsonString(json, "id")      ?? Path.GetFileNameWithoutExtension(dllPath),
                    Name     = ExtractJsonString(json, "name")    ?? null,
                    Version  = ExtractJsonString(json, "version") ?? "?",
                    DependsOn = ExtractDependsOn(json)
                };
                // If name wasn't set, use id
                if (string.IsNullOrEmpty(info.Name)) info.Name = info.Id;

                return true;
            }
            catch (Exception ex)
            {
                Plugin.Log?.Warn($"[ModValidator] Failed to inspect '{Path.GetFileName(dllPath)}': {ex.Message}");
                return false;
            }
        }

        /// <summary>Legacy helper used by the drop-install path.</summary>
        internal static bool IsBsipaPlugin(string dllPath, out string modId, out string modVersion)
        {
            if (TryReadModInfo(dllPath, out ModInfo info))
            {
                modId      = info.Id;
                modVersion = info.Version;
                return true;
            }
            modId      = Path.GetFileNameWithoutExtension(dllPath);
            modVersion = "?";
            return false;
        }

        // ── Helpers ───────────────────────────────────────────────────────────────

        private static bool ContainsString(byte[] haystack, string needle)
        {
            if (IndexOf(haystack, Encoding.UTF8.GetBytes(needle)) >= 0)    return true;
            if (IndexOf(haystack, Encoding.Unicode.GetBytes(needle)) >= 0) return true;
            return false;
        }

        private static string TryExtractManifestJson(byte[] bytes)
        {
            byte[] idKey = Encoding.UTF8.GetBytes("\"id\"");
            int idPos = IndexOf(bytes, idKey);
            if (idPos < 0) return null;

            int bracePos = -1;
            for (int i = idPos; i >= 0 && i > idPos - 512; i--)
                if (bytes[i] == (byte)'{') { bracePos = i; break; }
            if (bracePos < 0) return null;

            int depth = 0, end = -1;
            for (int i = bracePos; i < bytes.Length && i < bracePos + 8192; i++)
            {
                if      (bytes[i] == (byte)'{') depth++;
                else if (bytes[i] == (byte)'}') { depth--; if (depth == 0) { end = i; break; } }
            }
            if (end < 0) return null;

            return Encoding.UTF8.GetString(bytes, bracePos, end - bracePos + 1);
        }

        /// <summary>
        /// Extracts the keys of the "dependsOn" object from the manifest JSON.
        /// Format is: "dependsOn": { "BSIPA": "^4.3.0", "SongCore": "^3.9.0" }
        /// </summary>
        private static List<string> ExtractDependsOn(string json)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(json)) return result;

            int depIdx = json.IndexOf("\"dependsOn\"", StringComparison.OrdinalIgnoreCase);
            if (depIdx < 0) return result;

            int openBrace = json.IndexOf('{', depIdx);
            if (openBrace < 0) return result;

            int closeBrace = json.IndexOf('}', openBrace);
            if (closeBrace < 0) return result;

            string depBlock = json.Substring(openBrace + 1, closeBrace - openBrace - 1);

            // Parse key names from: "KeyName": "version"
            int pos = 0;
            while (pos < depBlock.Length)
            {
                int q1 = depBlock.IndexOf('"', pos);
                if (q1 < 0) break;
                int q2 = depBlock.IndexOf('"', q1 + 1);
                if (q2 < 0) break;

                string key = depBlock.Substring(q1 + 1, q2 - q1 - 1);
                if (!string.IsNullOrEmpty(key)) result.Add(key);

                // Skip past the value string
                int colon = depBlock.IndexOf(':', q2);
                if (colon < 0) break;
                int vq1 = depBlock.IndexOf('"', colon);
                if (vq1 < 0) break;
                int vq2 = depBlock.IndexOf('"', vq1 + 1);
                if (vq2 < 0) break;
                pos = vq2 + 1;
            }

            return result;
        }

        private static string ExtractJsonString(string json, string key)
        {
            if (string.IsNullOrEmpty(json)) return null;
            string search = $"\"{key}\"";
            int ki = json.IndexOf(search, StringComparison.OrdinalIgnoreCase);
            if (ki < 0) return null;
            int colon = json.IndexOf(':', ki + search.Length);
            if (colon < 0) return null;
            int q1 = json.IndexOf('"', colon + 1);
            if (q1 < 0) return null;
            int q2 = json.IndexOf('"', q1 + 1);
            if (q2 < 0) return null;
            return json.Substring(q1 + 1, q2 - q1 - 1);
        }

        private static int IndexOf(byte[] haystack, byte[] needle)
        {
            if (needle.Length == 0) return 0;
            int limit = haystack.Length - needle.Length;
            for (int i = 0; i <= limit; i++)
            {
                bool match = true;
                for (int j = 0; j < needle.Length; j++)
                    if (haystack[i + j] != needle[j]) { match = false; break; }
                if (match) return i;
            }
            return -1;
        }
    }
}
