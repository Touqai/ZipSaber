using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using BeatSaberMarkupLanguage;
using BeatSaberMarkupLanguage.Attributes;
using BeatSaberMarkupLanguage.ViewControllers;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ZipSaber
{
    [ViewDefinition("ZipSaber.beat-mods-browser.bsml")]
    internal class BeatModsBrowserViewController : BSMLAutomaticViewController
    {
        private const string BeatModsApi = "https://beatmods.com/api/v1";

        private static BeatModsBrowserViewController _instance;
        internal static BeatModsBrowserViewController Instance
        {
            get { if (_instance == null) _instance = BeatSaberUI.CreateViewController<BeatModsBrowserViewController>(); return _instance; }
        }

        // ── State ─────────────────────────────────────────────────────────────────
        private List<BeatModsEntry> _allMods   = new List<BeatModsEntry>();
        private List<BeatModsEntry> _filtered  = new List<BeatModsEntry>();
        private BeatModsEntry       _pendingInstall = null;
        private string              _searchText    = "";
        private Coroutine           _autoCancelCo  = null;
        private int                 _autoCancelSec = 20;

        // ── BSML bindings ─────────────────────────────────────────────────────────
        [UIValue("status-label")]
        public string StatusLabel { get; private set; } = "Loading…";

        [UIValue("search-text")]
        public string SearchText { get => _searchText; set { _searchText = value; ApplyFilter(); } }

        [UIValue("confirm-visible")]
        public bool ConfirmVisible { get; private set; } = false;

        [UIValue("list-visible")]
        public bool ListVisible { get; private set; } = true;

        [UIValue("confirm-title")]
        public string ConfirmTitle { get; private set; } = "";

        [UIValue("auto-cancel-label")]
        public string AutoCancelLabel { get; private set; } = "";

        [UIValue("pending-label")]
        public string PendingLabel { get; private set; } = "";

        [UIValue("pending-visible")]
        public bool PendingVisible { get; private set; } = false;

        [UIObject("mod-list-container")]
        private GameObject _containerGo = null;


        // ── Lifecycle ─────────────────────────────────────────────────────────────
        [UIAction("#post-parse")]
        private void OnPostParse()
        {
            Plugin.Log?.Info("[BeatMods] #post-parse.");
            StartCoroutine(FetchMods());
        }

        protected override void DidActivate(bool firstActivation, bool addedToHierarchy, bool screenSystemEnabling)
        {
            base.DidActivate(firstActivation, addedToHierarchy, screenSystemEnabling);
            if (!firstActivation && _allMods.Count == 0)
                StartCoroutine(FetchMods());
            UpdateFooter();
        }

        [UIAction("search-changed")]
        private void OnSearchChanged(string val) { _searchText = val ?? ""; ApplyFilter(); }

        // ── Install confirm ───────────────────────────────────────────────────────
        [UIAction("install-restart")]
        private void OnInstallRestart()
        {
            if (_pendingInstall == null) return;
            StopAutoCancel(); HideConfirm();
            StartCoroutine(InstallAndRestart(_pendingInstall));
            _pendingInstall = null;
        }

        [UIAction("install-close")]
        private void OnInstallClose()
        {
            if (_pendingInstall == null) return;
            lock (ModManagerViewController.PendingDeletions) // reuse lock object idiom
                ModManagerViewController.PendingInstalls.Add(_pendingInstall.Name);
            Plugin.Log?.Info($"[BeatMods] Queued install on close: {_pendingInstall.Name}");
            _pendingInstall = null;
            StopAutoCancel(); HideConfirm();
            UpdateFooter();
        }

        [UIAction("install-cancel")]
        private void OnInstallCancel() { _pendingInstall = null; StopAutoCancel(); HideConfirm(); }

        private IEnumerator InstallAndRestart(BeatModsEntry mod)
        {
            StatusLabel = $"Downloading {mod.Name}…";
            NotifyPropertyChanged(nameof(StatusLabel));
            yield return StartCoroutine(DoInstall(mod));
            Plugin.Instance?.LaunchPostExitCleanupPublic(); // flush any pending deletes too
            // Restart
            try
            {
                string exe  = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
                string args = string.Join(" ", Environment.GetCommandLineArgs().Skip(1).Select(a => a.Contains(' ') ? $"\"{a}\"" : a));
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, args) { UseShellExecute = true });
                System.Threading.Thread.Sleep(500);
                Application.Quit();
            }
            catch (Exception ex) { Plugin.Log?.Error($"[BeatMods] Restart failed: {ex.Message}"); }
        }

        // ── BeatMods API ──────────────────────────────────────────────────────────
        private IEnumerator FetchMods()
        {
            StatusLabel = "Fetching from BeatMods…";
            NotifyPropertyChanged(nameof(StatusLabel));

            // Detect game version — strip build suffix (e.g. "1.40.8_7379" → "1.40.8")
            string gameVer = IPA.Utilities.UnityGame.GameVersion.ToString();
            int underscore = gameVer.IndexOf('_');
            if (underscore > 0) gameVer = gameVer.Substring(0, underscore);
            Plugin.Log?.Info($"[BeatMods] Game version: {gameVer}");

            string url  = $"{BeatModsApi}/mod?status=approved&gameVersion={gameVer}&sort=name&sortDirection=1";
            string json = null;
            yield return FetchUrl(url, r => json = r);

            if (string.IsNullOrEmpty(json))
            {
                StatusLabel = "Failed to load. Check connection.";
                NotifyPropertyChanged(nameof(StatusLabel));
                yield break;
            }

            // Parse and deduplicate by name (keep latest version)
            var parsed = ParseBeatModsJson(json);
            var deduped = parsed
                .GroupBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderByDescending(m => m.Version).First())
                .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            _allMods = deduped;
            Plugin.Log?.Info($"[BeatMods] {parsed.Count} entries → {_allMods.Count} unique mods.");
            StatusLabel = $"{_allMods.Count} mods";
            NotifyPropertyChanged(nameof(StatusLabel));
            ApplyFilter();
        }

        private IEnumerator FetchUrl(string url, Action<string> cb)
        {
            string result = null; bool done = false;
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try { var wc = new WebClient(); wc.Headers[HttpRequestHeader.UserAgent] = "ZipSaber/2.0.0"; result = wc.DownloadString(url); }
                catch (Exception ex) { Plugin.Log?.Error($"[BeatMods] Fetch: {ex.Message}"); }
                finally { done = true; }
            });
            yield return new WaitUntil(() => done);
            cb(result);
        }

        private void ApplyFilter()
        {
            _filtered = string.IsNullOrWhiteSpace(_searchText)
                ? new List<BeatModsEntry>(_allMods)
                : _allMods.Where(m =>
                    m.Name.IndexOf(_searchText, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    m.Description.IndexOf(_searchText, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            BuildRows();
        }


        private void BuildRows()
        {
            if (_containerGo == null) return;
            var container = _containerGo.transform;
            foreach (Transform child in container) Destroy(child.gameObject);

            var installedIds = new HashSet<string>(
                ModRegistry.GetAllMods(Plugin.GetPluginsPath()).Select(m => m.Id),
                StringComparer.OrdinalIgnoreCase);
            var pendingInstalls = new HashSet<string>(
                ModManagerViewController.PendingInstalls, StringComparer.OrdinalIgnoreCase);

            foreach (var mod in _filtered)
            {
                bool installed = installedIds.Contains(mod.Name);
                bool queued    = pendingInstalls.Contains(mod.Name);
                BuildRow(container, mod, installed, queued);
            }
            Plugin.Log?.Info($"[BeatMods] Rendered {_filtered.Count} rows.");
        }

        private void BuildRow(Transform parent, BeatModsEntry mod, bool installed, bool queued)
        {
            var rowGo = new GameObject($"BRow_{mod.Name}");
            rowGo.transform.SetParent(parent, false);

            var rowRect = rowGo.GetComponent<RectTransform>() ?? rowGo.AddComponent<RectTransform>();
            rowRect.sizeDelta = new Vector2(0, 10);

            // Rounded background
            var bgImg = rowGo.AddComponent<Image>();
            bgImg.sprite = GetRoundRectSprite();
            bgImg.type   = Image.Type.Sliced;
            bgImg.material = null;
            bgImg.color  = installed ? new Color(0.05f, 0.25f, 0.05f, 0.75f)
                         : queued    ? new Color(0.05f, 0.12f, 0.3f, 0.75f)
                         :             new Color(0.1f,  0.1f,  0.15f, 0.75f);
            bgImg.raycastTarget = true;

            var layout = rowGo.AddComponent<HorizontalLayoutGroup>();
            layout.spacing = 4;
            typeof(HorizontalLayoutGroup).GetProperty("childAlignment")?.SetValue(layout, 3);
            layout.childForceExpandWidth  = false;
            layout.childForceExpandHeight = false;
            layout.padding = new RectOffset(8, 6, 1, 1);
            rowGo.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            // Hover
            Color hoverCol  = installed ? new Color(0.07f, 0.35f, 0.07f, 0.9f)
                            : queued    ? new Color(0.07f, 0.18f, 0.42f, 0.9f)
                            :             new Color(0.18f, 0.18f, 0.25f, 0.9f);
            Color normalCol = bgImg.color;
            var trigger = rowGo.AddComponent<UnityEngine.EventSystems.EventTrigger>();
            AddHover(trigger, () => bgImg.color = hoverCol, () => bgImg.color = normalCol);

            // Label
            string nameColor = installed ? "#88CC88" : queued ? "#8888FF" : "#DDDDDD";
            string tag       = installed ? " <color=#555555>[installed]</color>"
                             : queued    ? " <color=#555555>[pending install]</color>" : "";
            var labelGo = new GameObject("Label");
            labelGo.transform.SetParent(rowGo.transform, false);
            var lLE = labelGo.AddComponent<LayoutElement>();
            lLE.flexibleWidth = 1; lLE.preferredHeight = 8;
            var lTMP = labelGo.AddComponent<TextMeshProUGUI>();
            lTMP.text    = $"<color={nameColor}>{mod.Name}</color>  <color=#777777>v{mod.Version}</color>{tag}";
            lTMP.fontSize = 3.5f; lTMP.enableWordWrapping = false; lTMP.richText = true;

            // Install button (only if not already installed or queued)
            if (!installed && !queued)
            {
                Color btnColor = new Color(0.08f, 0.25f, 0.6f, 1f); // blue
                BuildButton(rowGo.transform, "Install", btnColor, () => ShowInstallConfirm(mod), 22);
            }
        }

        private void ShowInstallConfirm(BeatModsEntry mod)
        {
            _pendingInstall = mod;
            ConfirmTitle    = $"Install  {mod.Name}  v{mod.Version}?";
            ConfirmVisible  = true;
            ListVisible     = false;
            NotifyPropertyChanged(nameof(ConfirmTitle));
            NotifyPropertyChanged(nameof(ConfirmVisible));
            NotifyPropertyChanged(nameof(ListVisible));
            StopAutoCancel();
            _autoCancelCo = StartCoroutine(AutoCancelCountdown());
        }

        private void HideConfirm()
        {
            ConfirmVisible  = false;
            ListVisible     = true;
            AutoCancelLabel = "";
            NotifyPropertyChanged(nameof(ConfirmVisible));
            NotifyPropertyChanged(nameof(ListVisible));
            NotifyPropertyChanged(nameof(AutoCancelLabel));
        }

        private IEnumerator AutoCancelCountdown()
        {
            _autoCancelSec = 20;
            while (_autoCancelSec > 0)
            {
                AutoCancelLabel = $"Auto-cancelling in {_autoCancelSec}s…";
                NotifyPropertyChanged(nameof(AutoCancelLabel));
                yield return new WaitForSeconds(1f);
                _autoCancelSec--;
            }
            _pendingInstall = null; HideConfirm();
        }

        private void StopAutoCancel()
        {
            if (_autoCancelCo != null) { StopCoroutine(_autoCancelCo); _autoCancelCo = null; }
        }

        private void UpdateFooter()
        {
            int del = ModManagerViewController.PendingDeletions.Count;
            int ins = ModManagerViewController.PendingInstalls.Count;
            if (del == 0 && ins == 0) { PendingLabel = ""; PendingVisible = false; }
            else
            {
                var parts = new List<string>();
                if (del > 0) parts.Add($"🗑 {del} pending delete{(del == 1 ? "" : "s")}");
                if (ins > 0) parts.Add($"⬇ {ins} pending install{(ins == 1 ? "" : "s")}");
                PendingLabel   = string.Join("   ", parts);
                PendingVisible = true;
            }
            NotifyPropertyChanged(nameof(PendingLabel));
            NotifyPropertyChanged(nameof(PendingVisible));
        }

        private IEnumerator DoInstall(BeatModsEntry mod)
        {
            StatusLabel = $"Downloading {mod.Name}…";
            NotifyPropertyChanged(nameof(StatusLabel));
            byte[] data = null; bool done = false;
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try { var wc = new WebClient(); wc.Headers[HttpRequestHeader.UserAgent] = "ZipSaber/2.0.0"; data = wc.DownloadData(mod.DownloadUrl); }
                catch (Exception ex) { Plugin.Log?.Error($"[BeatMods] Download: {ex.Message}"); }
                finally { done = true; }
            });
            yield return new WaitUntil(() => done);

            if (data == null) { StatusLabel = $"Download failed for {mod.Name}."; NotifyPropertyChanged(nameof(StatusLabel)); yield break; }

            try
            {
                string pluginsPath = Plugin.GetPluginsPath();
                using (var ms = new System.IO.MemoryStream(data))
                using (var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Read))
                {
                    int extracted = 0;
                    foreach (var entry in zip.Entries)
                    {
                        if (entry.FullName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) &&
                            (entry.FullName.StartsWith("Plugins/", StringComparison.OrdinalIgnoreCase) ||
                             entry.FullName.StartsWith("Plugins\\", StringComparison.OrdinalIgnoreCase) ||
                             !entry.FullName.Contains("/")))
                        {
                            string dest = System.IO.Path.Combine(pluginsPath, System.IO.Path.GetFileName(entry.FullName));
                            using (var fs = System.IO.File.Create(dest))
                            using (var es = entry.Open()) es.CopyTo(fs);
                            extracted++;
                            Plugin.Log?.Info($"[BeatMods] Extracted: {System.IO.Path.GetFileName(entry.FullName)}");
                        }
                    }
                    StatusLabel = extracted > 0 ? $"Installed {mod.Name}! Restart to activate." : $"Installed {mod.Name}.";
                }
            }
            catch (Exception ex) { StatusLabel = $"Install error: {ex.Message}"; Plugin.Log?.Error($"[BeatMods] Install: {ex.Message}"); }

            NotifyPropertyChanged(nameof(StatusLabel));
            ModRegistry.Invalidate();
            ApplyFilter();
        }

        // ── Shared UI helpers ─────────────────────────────────────────────────────
        private static void BuildButton(Transform parent, string label, Color color, Action onClick, float width)
        {
            var btnGo = new GameObject("Btn_" + label);
            btnGo.transform.SetParent(parent, false);
            var le = btnGo.AddComponent<LayoutElement>();
            le.preferredWidth = width; le.preferredHeight = 8;

            var bgImg = btnGo.AddComponent<Image>();
            bgImg.sprite = GetRoundRectSprite();
            bgImg.type   = Image.Type.Sliced;
            bgImg.material = null;
            bgImg.color  = color;

            var trigger = btnGo.AddComponent<UnityEngine.EventSystems.EventTrigger>();
            Color hov = new Color(color.r * 1.3f, color.g * 1.3f, color.b * 1.3f, 1f);
            AddHover(trigger, () => bgImg.color = hov, () => bgImg.color = color);

            var lblGo = new GameObject("Lbl");
            lblGo.transform.SetParent(btnGo.transform, false);
            var lr = lblGo.AddComponent<RectTransform>();
            lr.anchorMin = Vector2.zero; lr.anchorMax = Vector2.one;
            lr.offsetMin = Vector2.zero; lr.offsetMax = Vector2.zero;
            var lTMP = lblGo.AddComponent<TextMeshProUGUI>();
            lTMP.text = label; lTMP.fontSize = 3f;
            lTMP.alignment = TextAlignmentOptions.Center;
            lTMP.color = Color.white; lTMP.raycastTarget = false;

            var hitGo = new GameObject("Hit");
            hitGo.transform.SetParent(btnGo.transform, false);
            var hr = hitGo.AddComponent<RectTransform>();
            hr.anchorMin = Vector2.zero; hr.anchorMax = Vector2.one;
            hr.offsetMin = Vector2.zero; hr.offsetMax = Vector2.zero;
            var hitImg = hitGo.AddComponent<Image>();
            hitImg.color = Color.clear;
            var btn = hitGo.AddComponent<Button>();
            btn.targetGraphic = hitImg;
            btn.onClick.AddListener(() => onClick());
        }

        private static void AddHover(UnityEngine.EventSystems.EventTrigger t, Action onEnter, Action onExit)
        {
            var e = new UnityEngine.EventSystems.EventTrigger.Entry { eventID = UnityEngine.EventSystems.EventTriggerType.PointerEnter };
            e.callback.AddListener(_ => onEnter()); t.triggers.Add(e);
            var x = new UnityEngine.EventSystems.EventTrigger.Entry { eventID = UnityEngine.EventSystems.EventTriggerType.PointerExit };
            x.callback.AddListener(_ => onExit()); t.triggers.Add(x);
        }

        private static Sprite _roundRectSprite;
        private static Sprite GetRoundRectSprite()
        {
            if (_roundRectSprite != null) return _roundRectSprite;
            foreach (var s in Resources.FindObjectsOfTypeAll<Sprite>())
                if (s.name == "RoundRect10" || s.name == "RoundRectSmall" || s.name == "Background")
                { _roundRectSprite = s; break; }
            return _roundRectSprite;
        }

        // ── Minimal JSON parser ───────────────────────────────────────────────────
        private static List<BeatModsEntry> ParseBeatModsJson(string json)
        {
            var result = new List<BeatModsEntry>();
            try
            {
                int i = 0;
                while (i < json.Length)
                {
                    int nameIdx = json.IndexOf("\"name\"", i, StringComparison.Ordinal);
                    if (nameIdx < 0) break;
                    string name    = ExtractStr(json, nameIdx);
                    int verIdx     = json.IndexOf("\"version\"",     nameIdx, StringComparison.Ordinal);
                    int descIdx    = json.IndexOf("\"description\"", nameIdx, StringComparison.Ordinal);
                    int dlIdx      = json.IndexOf("\"downloads\"",   nameIdx, StringComparison.Ordinal);
                    string version = verIdx  >= 0 ? ExtractStr(json, verIdx)  : "?";
                    string desc    = descIdx >= 0 ? ExtractStr(json, descIdx) : "";
                    string dlUrl   = "";
                    if (dlIdx >= 0)
                    {
                        int urlIdx = json.IndexOf("\"url\"", dlIdx, StringComparison.Ordinal);
                        if (urlIdx >= 0) dlUrl = "https://beatmods.com" + ExtractStr(json, urlIdx);
                    }
                    if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(dlUrl))
                        result.Add(new BeatModsEntry { Name = name, Version = version, Description = desc, DownloadUrl = dlUrl });
                    i = nameIdx + 6;
                }
            }
            catch (Exception ex) { Plugin.Log?.Error($"[BeatMods] JSON parse: {ex.Message}"); }
            return result;
        }

        private static string ExtractStr(string json, int keyIdx)
        {
            int colon = json.IndexOf(':', keyIdx); if (colon < 0) return "";
            int q1    = json.IndexOf('"', colon + 1); if (q1 < 0) return "";
            int q2    = json.IndexOf('"', q1 + 1);    if (q2 < 0) return "";
            return json.Substring(q1 + 1, q2 - q1 - 1);
        }

        internal class BeatModsEntry
        {
            public string Name { get; set; }
            public string Version { get; set; }
            public string Description { get; set; }
            public string DownloadUrl { get; set; }
        }
    }
}
