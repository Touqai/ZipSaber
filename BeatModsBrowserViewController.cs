using System;
using System.Collections;
using System.Collections.Generic;
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
        private List<BeatModsEntry> _allMods        = new List<BeatModsEntry>();
        private List<BeatModsEntry> _filtered       = new List<BeatModsEntry>();
        private BeatModsEntry       _pendingInstall = null;
        private string              _searchText     = "";
        private Coroutine           _autoCancelCo   = null;
        private int                 _autoCancelSec  = 20;

        // Populated after fetch; used by ExecutePendingInstalls on game close
        internal static readonly Dictionary<string, string> DownloadUrlsByName
            = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // ── BSML bindings ─────────────────────────────────────────────────────────
        [UIValue("status-label")]
        public string StatusLabel { get; private set; } = "Loading…";

        [UIValue("search-text")]
        public string SearchText { get => _searchText; set { _searchText = value; _scrollToTop = true; ApplyFilter(); } }

        [UIObject("confirm-host")] private GameObject _confirmHost = null;
        [UIObject("back-slot")]    private GameObject _backSlot = null;
        private InlinePrompt _card;
        private InlinePrompt Card => _card ?? (_confirmHost != null ? (_card = InlinePrompt.Build(_confirmHost)) : null);

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

        // ── Sorting ───────────────────────────────────────────────────────────────
        internal const string SortNameAsc      = "Name (A-Z)";
        internal const string SortNameDesc     = "Name (Z-A)";
        internal const string SortNotInstalled = "Not installed first";
        internal const string SortInstalled    = "Installed first";
        internal const string SortCategory     = "Category";
        internal const string SortUpdated      = "Recently updated";

        [UIValue("sort-options")]
        private List<object> SortOptions => new List<object>
            { SortNameAsc, SortNameDesc, SortNotInstalled, SortInstalled, SortCategory, SortUpdated };

        [UIValue("sort-mode")]
        public string SortMode
        {
            get
            {
                string v = Plugin.Config?.BrowseSortMode;
                return SortOptions.Contains(v) ? v : SortNameAsc;
            }
            set { if (Plugin.Config != null) Plugin.Config.BrowseSortMode = value; }
        }

        [UIAction("sort-changed")]
        private void OnSortChanged(object val)
        {
            if (Plugin.Config != null) Plugin.Config.BrowseSortMode = val as string ?? SortNameAsc;
            _scrollToTop = true;
            BuildRows();
        }

        private bool _scrollToTop = true;

        // ── Lifecycle ─────────────────────────────────────────────────────────────
        [UIAction("#post-parse")]
        private void OnPostParse()
        {
            Plugin.Log?.Info("[BeatMods] #post-parse.");
            ViewportClickGuard.Attach(_containerGo);
            StartCoroutine(FetchMods());
        }

        [UIComponent("zs-tag")] private TextMeshProUGUI _zsTag = null;

        [UIAction("go-back")]
        private void OnGoBack() => ModManagerFlowCoordinator.GoBack();

        protected override void DidActivate(bool firstActivation, bool addedToHierarchy, bool screenSystemEnabling)
        {
            if (firstActivation) FlatView.Prepare(this);   // before BSML builds the view
            base.DidActivate(firstActivation, addedToHierarchy, screenSystemEnabling);
            if (firstActivation)
            {
                FlatView.Finish(this);
                UiKit.SlotButton(_backSlot, "BackBtn", UiKit.Neutral, OnGoBack, "<", 4f);
            }
            if (_zsTag != null) _zsTag.color = Theme.Accent;
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
            ModManagerViewController.CommitInstall(_pendingInstall.Name);
            Plugin.Log?.Info($"[BeatMods] Queued install on close: {_pendingInstall.Name}");
            _pendingInstall = null;
            StopAutoCancel(); HideConfirm();
            UpdateFooter();
            ApplyFilter();
        }

        [UIAction("install-cancel")]
        private void OnInstallCancel() { _pendingInstall = null; StopAutoCancel(); HideConfirm(); }

        private IEnumerator InstallAndRestart(BeatModsEntry mod)
        {
            StatusLabel = $"Downloading {mod.Name}…";
            NotifyPropertyChanged(nameof(StatusLabel));
            yield return StartCoroutine(DoInstall(mod));
            Plugin.Instance?.LaunchPostExitCleanupPublic();
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

            string gameVer = IPA.Utilities.UnityGame.GameVersion.ToString();
            int underscore = gameVer.IndexOf('_');
            if (underscore > 0) gameVer = gameVer.Substring(0, underscore);

            string url  = $"{BeatModsApi}/mod?status=approved&gameVersion={gameVer}&sort=name&sortDirection=1";
            string json = null;
            yield return FetchUrl(url, r => json = r);

            if (string.IsNullOrEmpty(json))
            {
                StatusLabel = "Failed to load. Check connection.";
                NotifyPropertyChanged(nameof(StatusLabel));
                yield break;
            }

            var parsed = ParseBeatModsJson(json);
            var deduped = parsed
                .GroupBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderByDescending(m => SafeVersion(m.Version)).First())
                .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            _allMods = deduped;
            StatusLabel = $"{_allMods.Count} mods";
            NotifyPropertyChanged(nameof(StatusLabel));

            // Cache URL map so pending installs can download on close
            DownloadUrlsByName.Clear();
            foreach (var m in _allMods)
                DownloadUrlsByName[m.Name] = m.DownloadUrl;

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

            var installedMods = ModRegistry.GetAllMods(Plugin.GetPluginsPath());
            // BeatMods uses display names; BSIPA manifests use 'id'. Check both to handle mismatches.
            var installedIds = new HashSet<string>(
                installedMods.SelectMany(m => new[] { m.Id, m.Name }),
                StringComparer.OrdinalIgnoreCase);
            var pendingInstalls = new HashSet<string>(
                ModManagerViewController.PendingInstalls, StringComparer.OrdinalIgnoreCase);

            var rows = _filtered.Select(m => new BRow
            {
                Mod = m,
                Installed = installedIds.Contains(m.Name),
                Queued = pendingInstalls.Contains(m.Name),
            }).ToList();

            var cmp = StringComparer.OrdinalIgnoreCase;
            string mode = SortMode;

            switch (mode)
            {
                case SortNameDesc:
                    rows = rows.OrderByDescending(r => r.Mod.Name, cmp).ToList(); break;
                case SortUpdated:
                    rows = rows.OrderByDescending(r => r.Mod.Updated).ThenBy(r => r.Mod.Name, cmp).ToList(); break;
                case SortNotInstalled:
                case SortInstalled:
                {
                    bool installedFirst = mode == SortInstalled;
                    var avail = rows.Where(r => !r.Installed && !r.Queued).OrderBy(r => r.Mod.Name, cmp).ToList();
                    var queued = rows.Where(r => r.Queued && !r.Installed).OrderBy(r => r.Mod.Name, cmp).ToList();
                    var inst = rows.Where(r => r.Installed).OrderBy(r => r.Mod.Name, cmp).ToList();
                    var order = installedFirst
                        ? new[] { ("INSTALLED", inst), ("QUEUED", queued), ("NOT INSTALLED", avail) }
                        : new[] { ("NOT INSTALLED", avail), ("QUEUED", queued), ("INSTALLED", inst) };
                    BuildGrouped(container, order.ToList());
                    goto Done;
                }
                case SortCategory:
                {
                    var byCat = rows.GroupBy(r => r.Mod.Category ?? "Other", cmp)
                        .OrderBy(g => g.Key.Equals("Other", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                        .ThenBy(g => g.Key, cmp)
                        .Select(g => (g.Key.ToUpperInvariant(), g.OrderBy(r => r.Mod.Name, cmp).ToList()))
                        .ToList();
                    BuildGrouped(container, byCat);
                    goto Done;
                }
                default:
                    rows = rows.OrderBy(r => r.Mod.Name, cmp).ToList(); break;
            }

            foreach (var r in rows) BuildRow(container, r.Mod, r.Installed, r.Queued);

        Done:
            if (rows.Count == 0) BuildGroupHeader(container, "NO MODS MATCH YOUR SEARCH", -1);
            Plugin.Log?.Debug($"[BeatMods] Rendered {rows.Count} rows ({mode}).");
            ScrollFix.Refresh(this, _containerGo, _scrollToTop);
            _scrollToTop = false;
        }

        private class BRow
        {
            internal BeatModsEntry Mod;
            internal bool Installed, Queued;
        }

        private void BuildGrouped(Transform container, List<(string title, List<BRow> items)> groups)
        {
            foreach (var (title, items) in groups)
            {
                if (items.Count == 0) continue;
                BuildGroupHeader(container, title, items.Count);
                foreach (var r in items) BuildRow(container, r.Mod, r.Installed, r.Queued);
            }
        }

        private void BuildGroupHeader(Transform parent, string title, int count)
        {
            var tmp = UiKit.Text(parent, "Header_" + title, count >= 0 ? $"{title}  <color=#666677>{count}</color>" : title,
                                 2.8f, Theme.AccentShade(0.9f, 1f), TextAlignmentOptions.BottomLeft);
            tmp.fontStyle = FontStyles.Bold; tmp.characterSpacing = 4;
            tmp.margin = new Vector4(2, 0, 0, 0.5f);
            var le = UiKit.Size(tmp.gameObject, -1, 5);
            le.flexibleWidth = 1;
        }

        private void BuildRow(Transform parent, BeatModsEntry mod, bool installed, bool queued)
        {
            var rowGo = new GameObject($"BRow_{mod.Name}");
            rowGo.transform.SetParent(parent, false);
            rowGo.AddComponent<RectTransform>();

            var bgImg = rowGo.AddComponent<Image>();
            bgImg.sprite = GetNoGlowSprite(); bgImg.material = GetNoGlowMaterial(); bgImg.type = Image.Type.Sliced;
            Color normalCol = new Color(0.10f, 0.10f, 0.14f, 0.70f);
            Color hoverCol  = new Color(0.17f, 0.17f, 0.23f, 0.88f);
            bgImg.color = normalCol; bgImg.raycastTarget = true;

            var layout = rowGo.AddComponent<HorizontalLayoutGroup>();
            layout.spacing = 2;
            layout.childAlignment = TextAnchor.MiddleLeft;
            layout.childForceExpandWidth = false; layout.childForceExpandHeight = false;
            layout.childControlWidth = true; layout.childControlHeight = true;
            layout.padding = new RectOffset(0, 2, 1, 1);
            var rowLE = rowGo.AddComponent<LayoutElement>();
            rowLE.preferredHeight = 7.5f; rowLE.minHeight = 7.5f;

            // Hover: highlight + show what the mod does in the description panel
            var trigger = rowGo.AddComponent<UnityEngine.EventSystems.EventTrigger>();
            AddHover(trigger,
                () => { bgImg.color = hoverCol; ShowDescription(mod, installed, queued); },
                () => { bgImg.color = normalCol; });

            // Accent bar: green installed, blue queued, grey available
            Color accent = installed ? new Color(0.25f, 0.80f, 0.45f, 1f)
                         : queued    ? new Color(0.35f, 0.55f, 1.00f, 1f)
                         :             new Color(0.40f, 0.40f, 0.48f, 1f);
            var barGo = new GameObject("Accent");
            barGo.transform.SetParent(rowGo.transform, false);
            var barLE = barGo.AddComponent<LayoutElement>();
            barLE.preferredWidth = 0.8f; barLE.minWidth = 0.8f; barLE.preferredHeight = 5.5f;
            var barImg = barGo.AddComponent<Image>();
            barImg.sprite = GetNoGlowSprite(); barImg.material = GetNoGlowMaterial(); barImg.type = Image.Type.Sliced;
            barImg.color = accent; barImg.raycastTarget = false;

            string tag = installed ? "  <color=#55AA77><size=80%>INSTALLED</size></color>"
                       : queued    ? "  <color=#6688DD><size=80%>QUEUED</size></color>" : "";
            var labelGo = new GameObject("Label");
            labelGo.transform.SetParent(rowGo.transform, false);
            var lLE = labelGo.AddComponent<LayoutElement>();
            lLE.flexibleWidth = 1; lLE.preferredHeight = 5.5f; lLE.minWidth = 10;
            var lTMP = labelGo.AddComponent<TextMeshProUGUI>();
            lTMP.text = $"<color=#E6E6EA>{Esc(mod.Name)}</color>  <color=#5E5E68><size=75%>v{Esc(mod.Version)}</size></color>{tag}";
            lTMP.fontSize = 3.3f; lTMP.enableWordWrapping = false; lTMP.overflowMode = TextOverflowModes.Ellipsis;
            lTMP.alignment = TextAlignmentOptions.Left; lTMP.richText = true; lTMP.raycastTarget = false;
            lTMP.margin = new Vector4(1.5f, 0, 0, 0);
            lTMP.fontSharedMaterial = GetNoGlowTMPMaterial(lTMP);

            if (!installed && !queued)
                BuildButton(rowGo.transform, "INSTALL", new Color(0.10f, 0.30f, 0.60f, 1f), () => ShowInstallConfirm(mod), 16);
            else
            {
                var sp = new GameObject("Spacer");
                sp.transform.SetParent(rowGo.transform, false);
                var spLE = sp.AddComponent<LayoutElement>();
                spLE.preferredWidth = 16; spLE.minWidth = 16; spLE.preferredHeight = 5.5f;
            }
        }

        // ── Description panel ─────────────────────────────────────────────────────
        private const string DescPlaceholder = "<color=#666677>Hover over a mod to see what it does.</color>";
        private static string _descText = DescPlaceholder;

        [UIValue("desc-text")]
        public string DescText => _descText;

        private void ShowDescription(BeatModsEntry mod, bool installed, bool queued)
        {
            string desc = string.IsNullOrWhiteSpace(mod.Description) ? "No description provided." : mod.Description.Trim();
            desc = desc.Replace("\\r", "").Replace("\\n", " ").Replace("\r", "").Replace("\n", " ");
            if (desc.Length > 260) desc = desc.Substring(0, 257).TrimEnd() + "…";
            string status = installed ? "  <color=#55AA77><size=80%>INSTALLED</size></color>"
                          : queued    ? "  <color=#6688DD><size=80%>QUEUED</size></color>" : "";
            var meta = new List<string> { mod.Category };
            if (!string.IsNullOrEmpty(mod.Author)) meta.Add("by " + mod.Author);
            if (mod.Updated > DateTime.MinValue) meta.Add("updated " + mod.Updated.ToString("d MMM yyyy", System.Globalization.CultureInfo.InvariantCulture));
            string metaLine = $"  <color=#6E6E80><size=80%>{Esc(string.Join("  ·  ", meta))}</size></color>";
            _descText = $"<b>{Esc(mod.Name)}</b> <color=#777788><size=80%>v{Esc(mod.Version)}</size></color>{status}{metaLine}\n<color=#C8C8D2>{Esc(desc)}</color>";
            NotifyPropertyChanged(nameof(DescText));
        }

        // Keep mod-supplied text from injecting TMP rich-text tags
        private static string Esc(string s) => "<noparse>" + (s ?? "").Replace("</noparse>", "") + "</noparse>";

        private void ShowInstallConfirm(BeatModsEntry mod)
        {
            _pendingInstall = mod;
            ConfirmTitle    = $"Install  {mod.Name}  v{mod.Version}?";
            var meta = new List<string>();
            if (!string.IsNullOrEmpty(mod.Category)) meta.Add(mod.Category);
            if (!string.IsNullOrEmpty(mod.Author)) meta.Add("by " + mod.Author);
            string desc = string.IsNullOrWhiteSpace(mod.Description) ? "" : mod.Description.Replace("\r", "").Replace("\n", " ").Replace("\n", " ").Trim();
            if (desc.Length > 150) desc = desc.Substring(0, 147).TrimEnd() + "...";
            Card?.Set("INSTALL FROM BEATMODS", $"{mod.Name}  v{mod.Version}",
                $"<color=#8A8A99><size=90%>{PromptUi.Esc(string.Join("  ·  ", meta))}</size></color>\n<color=#C8C8D2>{PromptUi.Esc(desc)}</color>",
                "<color=#F2C94C>Only install mods you trust</color><color=#9A9AA6> - mods run code on your PC. New mods load after a restart.</color>", null,
                InlinePrompt.Btn.Primary("RESTART & INSTALL", OnInstallRestart, 1.3f),
                InlinePrompt.Btn.Neutral("INSTALL ON CLOSE", OnInstallClose, 1.2f),
                InlinePrompt.Btn.Neutral("CANCEL", OnInstallCancel, 0.7f));
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
            const float total = 20f;
            float start = Time.unscaledTime;
            while (true)
            {
                float left = total - (Time.unscaledTime - start);
                if (left <= 0f) break;
                _autoCancelSec = Mathf.CeilToInt(left);
                _card?.SetTimer(left / total, $"Cancels in {_autoCancelSec}s if you don't pick");
                yield return null;
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
            le.preferredWidth = width; le.minWidth = width; le.preferredHeight = 5.5f;

            var bgImg = btnGo.AddComponent<Image>();
            bgImg.sprite   = GetNoGlowSprite();
            bgImg.material = GetNoGlowMaterial();
            bgImg.type     = Image.Type.Sliced;
            bgImg.color    = color;

            var trigger = btnGo.AddComponent<UnityEngine.EventSystems.EventTrigger>();
            Color hov = new Color(Mathf.Min(color.r * 1.35f + 0.04f, 1f), Mathf.Min(color.g * 1.35f + 0.04f, 1f), Mathf.Min(color.b * 1.35f + 0.04f, 1f), 1f);
            AddHover(trigger, () => bgImg.color = hov, () => bgImg.color = color);

            var lblGo = new GameObject("Lbl");
            lblGo.transform.SetParent(btnGo.transform, false);
            var lr = lblGo.AddComponent<RectTransform>();
            lr.anchorMin = Vector2.zero; lr.anchorMax = Vector2.one;
            lr.offsetMin = Vector2.zero; lr.offsetMax = Vector2.zero;
            var lTMP = lblGo.AddComponent<TextMeshProUGUI>();
            lTMP.text      = label;
            lTMP.fontSize  = 2.6f;
            lTMP.fontStyle = FontStyles.Bold;
            lTMP.characterSpacing = 2;
            lTMP.alignment = TextAlignmentOptions.Center;
            lTMP.color     = Color.white;
            lTMP.raycastTarget = false;
            lTMP.fontSharedMaterial = GetNoGlowTMPMaterial(lTMP);

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

        // ── No-glow sprite & material cache ──────────────────────────────────────
        private static Sprite   _noGlowSprite   = null;
        private static Material _noGlowMaterial = null;

        private static Sprite GetNoGlowSprite()
        {
            if (_noGlowSprite != null) return _noGlowSprite;
            foreach (var s in Resources.FindObjectsOfTypeAll<Sprite>())
                if (s.name == "RoundRect10" || s.name == "RoundRectSmall" || s.name == "Background")
                { _noGlowSprite = s; break; }
            return _noGlowSprite;
        }

        private static Material GetNoGlowMaterial()
        {
            if (_noGlowMaterial != null) return _noGlowMaterial;
            foreach (var m in Resources.FindObjectsOfTypeAll<Material>())
                if (m.name == "UINoGlow")
                { _noGlowMaterial = m; break; }
            return _noGlowMaterial;
        }

        private static Material GetNoGlowTMPMaterial(TextMeshProUGUI tmp)
        {
            if (tmp.font == null) return null;
            var mat = new Material(tmp.fontSharedMaterial);
            if (mat.HasProperty("_GlowColor"))  mat.SetColor("_GlowColor",  Color.clear);
            if (mat.HasProperty("_GlowPower"))  mat.SetFloat("_GlowPower",  0f);
            if (mat.HasProperty("_GlowOffset")) mat.SetFloat("_GlowOffset", 0f);
            if (mat.HasProperty("_GlowOuter"))  mat.SetFloat("_GlowOuter",  0f);
            return mat;
        }

        // ── BeatMods JSON → entries ────────────────────────────────────────────────
        private static List<BeatModsEntry> ParseBeatModsJson(string json)
        {
            var result = new List<BeatModsEntry>();
            try
            {
                if (!(MiniJson.Parse(json) is List<object> mods)) return result;
                foreach (var o in mods)
                {
                    if (!(o is Dictionary<string, object> m)) continue;
                    string name = m.GetString("name");
                    if (string.IsNullOrEmpty(name)) continue;

                    string dlUrl = null;
                    var downloads = m.GetList("downloads");
                    if (downloads != null)
                        foreach (var d in downloads)
                        {
                            var dd = d as Dictionary<string, object>;
                            string type = dd.GetString("type");
                            string u = dd.GetString("url");
                            if (string.IsNullOrEmpty(u)) continue;
                            // prefer universal/steam builds over oculus
                            if (dlUrl == null || type == "universal" || type == "steam") dlUrl = u;
                        }
                    if (string.IsNullOrEmpty(dlUrl)) continue;
                    if (!dlUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)) dlUrl = "https://beatmods.com" + dlUrl;

                    DateTime.TryParse(m.GetString("updatedDate") ?? m.GetString("uploadDate"),
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                        out DateTime updated);

                    result.Add(new BeatModsEntry
                    {
                        Name        = name,
                        Version     = m.GetString("version") ?? "?",
                        Description = m.GetString("description") ?? "",
                        DownloadUrl = dlUrl,
                        Category    = string.IsNullOrWhiteSpace(m.GetString("category")) ? "Other" : m.GetString("category").Trim(),
                        Author      = m.GetObj("author").GetString("username") ?? "",
                        Updated     = updated,
                        Required    = m.GetBool("required"),
                    });
                }
            }
            catch (Exception ex) { Plugin.Log?.Error($"[BeatMods] JSON parse: {ex.Message}"); }
            return result;
        }

        private static Version SafeVersion(string v)
        {
            if (string.IsNullOrEmpty(v)) return new Version(0, 0);
            string core = v.Split('-', '+')[0];
            return Version.TryParse(core, out var ver) ? ver : new Version(0, 0);
        }

        internal class BeatModsEntry
        {
            public string Name        { get; set; }
            public string Version     { get; set; }
            public string Description { get; set; }
            public string DownloadUrl { get; set; }
            public string Category    { get; set; } = "Other";
            public string Author      { get; set; } = "";
            public DateTime Updated   { get; set; }
            public bool Required      { get; set; }
        }
    }
}