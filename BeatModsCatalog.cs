using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using Entry = ZipSaber.BeatModsBrowserViewController.BeatModsEntry;

namespace ZipSaber
{
    /// <summary>
    /// BeatMods catalog for this game version, shared by the Mod Manager (update checks) and the
    /// browser. Fetched once per session in the background.
    /// </summary>
    internal static class BeatModsCatalog
    {
        private static List<Entry> _mods = new List<Entry>();
        private static bool _loading;
        internal static bool Loaded { get; private set; }
        internal static IReadOnlyList<Entry> Mods => _mods;

        /// <summary>Raised on the main thread when the catalog arrives or an update finishes.</summary>
        internal static event Action Changed;

        /// <summary>Mods updated this session (by id/name): new files are on disk, restart to load them.</summary>
        internal static readonly HashSet<string> UpdatedThisSession = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        internal static readonly HashSet<string> Updating = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private static readonly List<Action<string>> _waiters = new List<Action<string>>();
        internal static bool FromCache { get; private set; }

        private static string CachePath => Path.Combine(ModFileOps.GameDir, "UserData", "ZipSaber", "beatmods-cache.json");

        internal static void EnsureLoaded() => Load(null);

        /// <summary>
        /// One shared fetch (Mod Manager + Browse used to fetch separately). Retries, and falls back
        /// to the last good copy on disk if BeatMods is unreachable. done(error) runs on the main thread;
        /// error is null on success (live or cached).
        /// </summary>
        internal static void Load(Action<string> done, bool force = false)
        {
            if (Loaded && !force) { done?.Invoke(null); return; }
            if (done != null) _waiters.Add(done);
            if (_loading) return;
            _loading = true;

            string gameVer = IPA.Utilities.UnityGame.GameVersion.ToString();
            int us = gameVer.IndexOf('_'); if (us > 0) gameVer = gameVer.Substring(0, us);
            string url = $"{BeatModsBrowserViewController.BeatModsApi}/mod?status=approved&gameVersion={gameVer}&sort=name&sortDirection=1";

            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                string json = null, error = null; bool cached = false;
                try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
                for (int attempt = 1; attempt <= 3 && json == null; attempt++)
                {
                    try
                    {
                        using (var wc = new WebClient())
                        {
                            wc.Headers[HttpRequestHeader.UserAgent] = "ZipSaber/2.13 (Beat Saber mod manager)";
                            wc.Headers[HttpRequestHeader.Accept] = "application/json";
                            json = wc.DownloadString(url);
                        }
                        if (string.IsNullOrWhiteSpace(json) || json.TrimStart()[0] != '[') { error = "BeatMods sent an unexpected reply"; json = null; }
                    }
                    catch (WebException wex)
                    {
                        var code = (wex.Response as HttpWebResponse)?.StatusCode;
                        error = code != null ? $"BeatMods replied {(int)code} {code}" : wex.Status + ": " + wex.Message;
                        Plugin.Log?.Warn($"[BeatMods] Fetch attempt {attempt} failed: {error}");
                    }
                    catch (Exception ex) { error = ex.Message; Plugin.Log?.Warn($"[BeatMods] Fetch attempt {attempt} failed: {ex.Message}"); }
                    if (json == null && attempt < 3) System.Threading.Thread.Sleep(1500 * attempt);
                }

                if (json != null) { try { File.WriteAllText(CachePath, json); } catch { } }
                else if (File.Exists(CachePath))
                {
                    try { json = File.ReadAllText(CachePath); cached = true; Plugin.Log?.Warn("[BeatMods] Using the saved copy of the mod list."); } catch { }
                }

                List<Entry> list = null;
                if (json != null)
                {
                    try
                    {
                        list = BeatModsBrowserViewController.ParseBeatModsJson(json)
                            .GroupBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
                            .Select(g => g.OrderByDescending(m => Ver(m.Version)).First())
                            .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
                            .ToList();
                    }
                    catch (Exception ex) { error = "Couldn't read the mod list: " + ex.Message; list = null; }
                }

                MainThreadDispatcher.Enqueue(() =>
                {
                    _loading = false;
                    if (list != null) { FromCache = cached; Set(list); error = null; }
                    else Plugin.Log?.Error($"[BeatMods] Mod list unavailable: {error}");
                    var w = _waiters.ToList(); _waiters.Clear();
                    foreach (var cb in w) try { cb(error ?? null); } catch (Exception ex) { Plugin.Log?.Error($"[BeatMods] {ex}"); }
                });
            });
        }

        /// <summary>Browser already fetched the list: share it.</summary>
        internal static void Set(List<Entry> mods)
        {
            _mods = mods ?? new List<Entry>();
            Loaded = true;
            int n = 0;
            foreach (var m in ModRegistry.GetAllMods(Plugin.GetPluginsPath())) if (FindUpdate(m) != null) n++;
            Plugin.Log?.Info($"[Updates] Catalog: {_mods.Count} mods, {n} update(s) available.");
            Changed?.Invoke();
        }

        internal static Entry Find(ModInfo m)
        {
            if (m == null) return null;
            string a = Norm(m.Name), b = Norm(m.Id);
            return _mods.FirstOrDefault(e => { string n = Norm(e.Name); return n == a || n == b; });
        }

        internal static Entry Find(string name)
        {
            string n = Norm(name);
            return _mods.FirstOrDefault(e => Norm(e.Name) == n);
        }

        /// <summary>The newer BeatMods entry for an installed mod, or null.</summary>
        internal static Entry FindUpdate(ModInfo m)
        {
            if (m == null || UpdatedThisSession.Contains(m.Id)) return null;
            var e = Find(m);
            if (e == null || string.IsNullOrEmpty(e.DownloadUrl)) return null;
            return Compare(e.Version, m.Version) > 0 ? e : null;
        }

        internal static List<(ModInfo Mod, Entry Update)> AllUpdates()
            => ModRegistry.GetAllMods(Plugin.GetPluginsPath())
                .Select(m => (m, FindUpdate(m))).Where(t => t.Item2 != null).ToList();

        // ── Updating ─────────────────────────────────────────────────────────────
        /// <summary>
        /// Download and install the new version. The running game has the old DLL loaded, so it's
        /// moved out of Plugins first (Windows allows renaming a loaded DLL); if even that fails the
        /// file goes to IPA/Pending, which BSIPA copies into place at the next launch.
        /// done(success, message) runs on the main thread.
        /// </summary>
        internal static void Update(ModInfo installed, Entry entry, Action<bool, string> done)
        {
            string key = installed.Id;
            if (!Updating.Add(key)) return;
            Changed?.Invoke();

            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                bool ok = false; string msg;
                try
                {
                    var wc = new WebClient(); wc.Headers[HttpRequestHeader.UserAgent] = "ZipSaber/2.0";
                    byte[] data = wc.DownloadData(entry.DownloadUrl);
                    int files = InstallZip(data, installed, out int pending);
                    ok = files > 0;
                    msg = !ok ? $"Update for {entry.Name} had no files to install."
                        : pending > 0 ? $"Updated {entry.Name} to v{entry.Version} - finishes installing on the next launch."
                        : $"Updated {entry.Name} to v{entry.Version}. Restart to load it.";
                    Plugin.Log?.Info($"[Updates] {installed.DisplayLabel} {installed.Version} -> {entry.Version}: {files} file(s), {pending} pending.");
                }
                catch (Exception ex)
                {
                    msg = $"Couldn't update {entry.Name}: {ex.Message}";
                    Plugin.Log?.Error($"[Updates] {entry.Name}: {ex}");
                }

                MainThreadDispatcher.Enqueue(() =>
                {
                    Updating.Remove(key);
                    if (ok) { UpdatedThisSession.Add(key); ModRegistry.Invalidate(); }
                    Changed?.Invoke();
                    done?.Invoke(ok, msg);
                });
            });
        }

        private static int InstallZip(byte[] data, ModInfo installed, out int pending)
        {
            pending = 0;
            int count = 0;
            string game = ModFileOps.GameDir;
            string pendingRoot = Path.Combine(game, "IPA", "Pending");
            var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            using (var ms = new MemoryStream(data))
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Read))
            {
                foreach (var e in zip.Entries)
                {
                    if (string.IsNullOrEmpty(e.Name)) continue;   // folder
                    string rel = e.FullName.Replace('\\', '/').TrimStart('/');
                    // BeatMods zips mirror the game folder (Plugins/, Libs/...). Bare DLLs go to Plugins.
                    if (!rel.Contains("/"))
                    {
                        if (!rel.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;
                        rel = "Plugins/" + rel;
                    }
                    if (rel.Contains("..")) continue;
                    string dest = Path.Combine(game, rel.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(dest));

                    if (File.Exists(dest) && !ModFileOps.Remove(dest, "old version"))
                    {
                        // still locked: stage it for BSIPA to copy in on the next launch
                        string staged = Path.Combine(pendingRoot, rel.Replace('/', Path.DirectorySeparatorChar));
                        Directory.CreateDirectory(Path.GetDirectoryName(staged));
                        using (var fs = File.Create(staged)) using (var es = e.Open()) es.CopyTo(fs);
                        // don't let the after-exit cleanup delete the file we're replacing
                        try { File.Delete(dest + ".zs_del"); } catch { }
                        pending++; count++;
                        continue;
                    }
                    using (var fs = File.Create(dest)) using (var es = e.Open()) es.CopyTo(fs);
                    written.Add(Path.GetFileName(dest));
                    count++;
                }
            }

            // The new build ships a differently named DLL: remove the old one so both don't load
            if (count > 0 && !string.IsNullOrEmpty(installed.DllPath) && File.Exists(installed.DllPath)
                && !written.Contains(Path.GetFileName(installed.DllPath)) && pending == 0)
                ModFileOps.Remove(installed.DllPath, "old DLL");

            return count;
        }

        // ── Version helpers ──────────────────────────────────────────────────────
        internal static int Compare(string a, string b) => Ver(a).CompareTo(Ver(b));

        private static Version Ver(string v)
        {
            if (string.IsNullOrEmpty(v)) return new Version(0, 0, 0, 0);
            string core = v.TrimStart('v', 'V').Split('-', '+', ' ')[0];
            var parts = core.Split('.').Select(p => int.TryParse(p, out int n) ? n : 0).Concat(new[] { 0, 0, 0, 0 }).Take(4).ToArray();
            return new Version(parts[0], parts[1], parts[2], parts[3]);
        }

        private static string Norm(string s) => (s ?? "").Replace(" ", "").Replace("-", "").Replace("_", "").ToLowerInvariant();
    }
}
