using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using BeatSaberMarkupLanguage;
using BeatSaberMarkupLanguage.Attributes;
using BeatSaberMarkupLanguage.ViewControllers;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ZipSaber
{
    /// <summary>
    /// Browses BeatMods for the current game version and lets the user
    /// download + install a mod directly into the Plugins folder.
    /// </summary>
    [ViewDefinition("ZipSaber.beat-mods-browser.bsml")]
    internal class BeatModsBrowserViewController : BSMLAutomaticViewController
    {
        // Game version — used to query BeatMods
        private const string BeatModsApiBase = "https://beatmods.com/api/v1";

        private static BeatModsBrowserViewController _instance;
        internal static BeatModsBrowserViewController Instance
        {
            get { if (_instance == null) _instance = BeatSaberUI.CreateViewController<BeatModsBrowserViewController>(); return _instance; }
        }

        // ── State ─────────────────────────────────────────────────────────────────
        private List<BeatModsEntry> _allMods  = new List<BeatModsEntry>();
        private List<BeatModsEntry> _filtered = new List<BeatModsEntry>();
        private string _searchText = "";
        private bool _loading = false;

        // ── BSML bindings ─────────────────────────────────────────────────────────
        [UIValue("status-label")]
        public string StatusLabel { get; private set; } = "Loading mods from BeatMods…";

        [UIValue("search-text")]
        public string SearchText
        {
            get => _searchText;
            set { _searchText = value; ApplyFilter(); }
        }

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
        }

        [UIAction("search-changed")]
        private void OnSearchChanged(string val) { _searchText = val; ApplyFilter(); }

        // ── BeatMods API ──────────────────────────────────────────────────────────
        private IEnumerator FetchMods()
        {
            _loading = true;
            StatusLabel = "Fetching mod list from BeatMods…";
            NotifyPropertyChanged(nameof(StatusLabel));

            string gameVersion = IPA.Utilities.UnityGame.GameVersion.ToString();
            string url = $"{BeatModsApiBase}/mod?status=approved&gameVersion={gameVersion}&sort=name&sortDirection=1";
            Plugin.Log?.Info($"[BeatMods] Fetching: {url}");

            string json = null;
            yield return FetchUrl(url, result => json = result);

            if (string.IsNullOrEmpty(json))
            {
                StatusLabel = "Failed to load mods. Check your internet connection.";
                NotifyPropertyChanged(nameof(StatusLabel));
                yield break;
            }

            _allMods = ParseBeatModsJson(json);
            Plugin.Log?.Info($"[BeatMods] Parsed {_allMods.Count} mods.");
            StatusLabel = $"{_allMods.Count} mods available";
            NotifyPropertyChanged(nameof(StatusLabel));
            _loading = false;
            ApplyFilter();
        }

        private IEnumerator FetchUrl(string url, Action<string> callback)
        {
            // Use WebClient on a background thread via coroutine
            string result = null;
            bool done = false;
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    var wc = new WebClient();
                    wc.Headers[HttpRequestHeader.UserAgent] = "ZipSaber/1.5.1";
                    result = wc.DownloadString(url);
                }
                catch (Exception ex) { Plugin.Log?.Error($"[BeatMods] Fetch error: {ex.Message}"); }
                finally { done = true; }
            });
            yield return new WaitUntil(() => done);
            callback(result);
        }

        private void ApplyFilter()
        {
            if (_containerGo == null) return;

            _filtered = string.IsNullOrWhiteSpace(_searchText)
                ? new List<BeatModsEntry>(_allMods)
                : _allMods.Where(m =>
                    m.Name.IndexOf(_searchText, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    m.Description.IndexOf(_searchText, StringComparison.OrdinalIgnoreCase) >= 0)
                  .ToList();

            BuildRows();
        }

        private void BuildRows()
        {
            if (_containerGo == null) return;
            var container = _containerGo.transform;
            foreach (Transform child in container) Destroy(child.gameObject);

            // Check which mods are already installed
            var installedIds = new HashSet<string>(
                ModRegistry.GetAllMods(Plugin.GetPluginsPath()).Select(m => m.Id),
                StringComparer.OrdinalIgnoreCase);

            foreach (var mod in _filtered)
                BuildRow(container, mod, installedIds.Contains(mod.Name));

            Plugin.Log?.Info($"[BeatMods] Rendered {_filtered.Count} rows.");
        }

        private void BuildRow(Transform parent, BeatModsEntry mod, bool installed)
        {
            var rowGo = new GameObject($"BRow_{mod.Name}");
            rowGo.transform.SetParent(parent, false);

            var rowRect = rowGo.GetComponent<RectTransform>() ?? rowGo.AddComponent<RectTransform>();
            rowRect.sizeDelta = new Vector2(0, 10);

            var layout = rowGo.AddComponent<HorizontalLayoutGroup>();
            layout.spacing = 4;
            typeof(HorizontalLayoutGroup).GetProperty("childAlignment")?.SetValue(layout, 3);
            layout.childForceExpandWidth  = false;
            layout.childForceExpandHeight = false;
            layout.padding = new RectOffset(6, 4, 1, 1);

            var fitter = rowGo.AddComponent<ContentSizeFitter>();
            fitter.verticalFit   = ContentSizeFitter.FitMode.PreferredSize;
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;

            // Hover highlight
            var hoverImg = rowGo.AddComponent<Image>();
            hoverImg.color = Color.clear;
            AddHoverHighlight(rowGo, hoverImg);

            // Label
            string nameColor   = installed ? "#88CC88" : "#DDDDDD";
            string installTag  = installed ? " <color=#666666>[installed]</color>" : "";
            var labelGo = new GameObject("Label");
            labelGo.transform.SetParent(rowGo.transform, false);
            var lLE = labelGo.AddComponent<LayoutElement>();
            lLE.flexibleWidth = 1; lLE.preferredHeight = 8;
            var lTMP = labelGo.AddComponent<TextMeshProUGUI>();
            lTMP.text    = $"<color={nameColor}>{mod.Name}</color>  <color=#777777>v{mod.Version}</color>{installTag}";
            lTMP.fontSize = 3.5f; lTMP.enableWordWrapping = false; lTMP.richText = true;

            // Install / Already installed button
            if (!installed)
            {
                var btnGo = new GameObject("InstallBtn");
                btnGo.transform.SetParent(rowGo.transform, false);
                var btnLE = btnGo.AddComponent<LayoutElement>();
                btnLE.preferredWidth = 22; btnLE.preferredHeight = 8;
                var hitImg = btnGo.AddComponent<Image>();
                hitImg.color = Color.clear;

                var btnLabelGo = new GameObject("BtnLabel");
                btnLabelGo.transform.SetParent(btnGo.transform, false);
                var blr = btnLabelGo.AddComponent<RectTransform>();
                blr.anchorMin = Vector2.zero; blr.anchorMax = Vector2.one;
                blr.offsetMin = Vector2.zero; blr.offsetMax = Vector2.zero;
                var bTMP = btnLabelGo.AddComponent<TextMeshProUGUI>();
                bTMP.text = "<color=#44CC44>[Install]</color>";
                bTMP.fontSize = 3f; bTMP.alignment = TextAlignmentOptions.Center;
                bTMP.richText = true; bTMP.raycastTarget = false;

                var btn = btnGo.AddComponent<Button>();
                btn.targetGraphic = hitImg;
                btn.onClick.AddListener(() => StartCoroutine(InstallMod(mod)));
            }
        }

        private static void AddHoverHighlight(GameObject go, Image img)
        {
            var trigger = go.AddComponent<UnityEngine.EventSystems.EventTrigger>();
            var enter = new UnityEngine.EventSystems.EventTrigger.Entry
                { eventID = UnityEngine.EventSystems.EventTriggerType.PointerEnter };
            enter.callback.AddListener(_ => img.color = new Color(1f, 1f, 1f, 0.07f));
            trigger.triggers.Add(enter);
            var exit = new UnityEngine.EventSystems.EventTrigger.Entry
                { eventID = UnityEngine.EventSystems.EventTriggerType.PointerExit };
            exit.callback.AddListener(_ => img.color = Color.clear);
            trigger.triggers.Add(exit);
        }

        private IEnumerator InstallMod(BeatModsEntry mod)
        {
            StatusLabel = $"Downloading {mod.Name} v{mod.Version}…";
            NotifyPropertyChanged(nameof(StatusLabel));
            Plugin.Log?.Info($"[BeatMods] Installing: {mod.Name} from {mod.DownloadUrl}");

            byte[] data = null;
            bool done = false;
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    var wc = new WebClient();
                    wc.Headers[HttpRequestHeader.UserAgent] = "ZipSaber/1.5.1";
                    data = wc.DownloadData(mod.DownloadUrl);
                }
                catch (Exception ex) { Plugin.Log?.Error($"[BeatMods] Download error: {ex.Message}"); }
                finally { done = true; }
            });
            yield return new WaitUntil(() => done);

            if (data == null)
            {
                StatusLabel = $"Failed to download {mod.Name}.";
                NotifyPropertyChanged(nameof(StatusLabel));
                yield break;
            }

            // Extract DLLs from the zip into Plugins folder
            try
            {
                string pluginsPath = Plugin.GetPluginsPath();
                using (var ms = new MemoryStream(data))
                using (var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Read))
                {
                    int extracted = 0;
                    foreach (var entry in zip.Entries)
                    {
                        // Only extract DLLs that go in Plugins
                        if (entry.FullName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) &&
                            (entry.FullName.StartsWith("Plugins/", StringComparison.OrdinalIgnoreCase) ||
                             entry.FullName.StartsWith("Plugins\\", StringComparison.OrdinalIgnoreCase) ||
                             !entry.FullName.Contains("/")))
                        {
                            string dest = Path.Combine(pluginsPath, Path.GetFileName(entry.FullName));
                            using (var fs = File.Create(dest))
                            using (var es = entry.Open())
                                es.CopyTo(fs);
                            extracted++;
                            Plugin.Log?.Info($"[BeatMods] Extracted: {Path.GetFileName(entry.FullName)}");
                        }
                    }
                    StatusLabel = extracted > 0
                        ? $"Installed {mod.Name}! Restart to activate."
                        : $"Installed {mod.Name} (no Plugins DLLs found in zip).";
                }
            }
            catch (Exception ex)
            {
                StatusLabel = $"Install failed: {ex.Message}";
                Plugin.Log?.Error($"[BeatMods] Install error: {ex.Message}");
            }

            NotifyPropertyChanged(nameof(StatusLabel));
            ModRegistry.Invalidate();
            ApplyFilter();
        }

        // ── Minimal JSON parser for BeatMods API ──────────────────────────────────
        private static List<BeatModsEntry> ParseBeatModsJson(string json)
        {
            var result = new List<BeatModsEntry>();
            try
            {
                // Very lightweight parser — finds each mod object's key fields
                // BeatMods returns: [{"name":"...","version":"...","description":"...","downloads":[{"type":"universal","url":"..."}]}]
                int i = 0;
                while (i < json.Length)
                {
                    int nameIdx = json.IndexOf("\"name\"", i, StringComparison.Ordinal);
                    if (nameIdx < 0) break;

                    string name    = ExtractString(json, nameIdx);
                    int verIdx     = json.IndexOf("\"version\"",    nameIdx, StringComparison.Ordinal);
                    int descIdx    = json.IndexOf("\"description\"", nameIdx, StringComparison.Ordinal);
                    int dlIdx      = json.IndexOf("\"downloads\"",   nameIdx, StringComparison.Ordinal);

                    string version = verIdx  >= 0 ? ExtractString(json, verIdx)  : "?";
                    string desc    = descIdx >= 0 ? ExtractString(json, descIdx) : "";

                    string downloadUrl = "";
                    if (dlIdx >= 0)
                    {
                        int urlIdx = json.IndexOf("\"url\"", dlIdx, StringComparison.Ordinal);
                        if (urlIdx >= 0)
                            downloadUrl = "https://beatmods.com" + ExtractString(json, urlIdx);
                    }

                    if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(downloadUrl))
                        result.Add(new BeatModsEntry { Name = name, Version = version, Description = desc, DownloadUrl = downloadUrl });

                    i = nameIdx + 6;
                }
            }
            catch (Exception ex) { Plugin.Log?.Error($"[BeatMods] JSON parse error: {ex.Message}"); }
            return result;
        }

        private static string ExtractString(string json, int keyIdx)
        {
            int colon = json.IndexOf(':', keyIdx);
            if (colon < 0) return "";
            int q1 = json.IndexOf('"', colon + 1);
            if (q1 < 0) return "";
            int q2 = json.IndexOf('"', q1 + 1);
            if (q2 < 0) return "";
            return json.Substring(q1 + 1, q2 - q1 - 1);
        }

        private class BeatModsEntry
        {
            public string Name        { get; set; }
            public string Version     { get; set; }
            public string Description { get; set; }
            public string DownloadUrl { get; set; }
        }
    }
}
