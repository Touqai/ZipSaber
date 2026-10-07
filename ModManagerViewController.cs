using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using BeatSaberMarkupLanguage;
using BeatSaberMarkupLanguage.Attributes;
using BeatSaberMarkupLanguage.ViewControllers;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ZipSaber
{
    [ViewDefinition("ZipSaber.mod-manager.bsml")]
    internal class ModManagerViewController : BSMLAutomaticViewController
    {
        internal static readonly List<ModInfo> PendingDeletions = new List<ModInfo>();
        internal static readonly List<string>  PendingInstalls  = new List<string>();
        private static readonly object _pendingLock = new object();

        private List<ModInfo> _allMods       = new List<ModInfo>();
        private ModInfo       _selectedMod   = null;
        private Coroutine     _autoCancelCo  = null;
        private int           _autoCancelSec = 20;

        [UIValue("mod-count-label")]    public string ModCountLabel    { get; private set; } = "";
        [UIValue("confirm-title")]      public string ConfirmTitle     { get; private set; } = "";
        [UIObject("confirm-host")]      private GameObject _confirmHost = null;
        private InlinePrompt _card;
        private InlinePrompt Card => _card ?? (_confirmHost != null ? (_card = InlinePrompt.Build(_confirmHost)) : null);
        [UIValue("confirm-visible")]    public bool   ConfirmVisible   { get; private set; } = false;
        [UIValue("list-visible")]       public bool   ListVisible      { get; private set; } = true;
        [UIValue("dependency-warning")] public string DependencyWarning { get; private set; } = "";
        [UIValue("dep-warn-visible")]   public bool   DepWarnVisible   { get; private set; } = false;
        [UIValue("auto-cancel-label")]  public string AutoCancelLabel  { get; private set; } = "";
        [UIValue("pending-label")]      public string PendingLabel     { get; private set; } = "";
        [UIValue("pending-visible")]    public bool   PendingVisible   { get; private set; } = false;

        // ── Enable/disable toggle ─────────────────────────────────────────────────
        [UIValue("toggle-confirm-visible")] public bool   ToggleConfirmVisible { get; private set; } = false;
        [UIValue("toggle-confirm-title")]   public string ToggleConfirmTitle   { get; private set; } = "";
        [UIValue("toggle-confirm-body")]    public string ToggleConfirmBody    { get; private set; } = "";
        [UIValue("toggle-status")]          public string ToggleStatus         => _toggleStatus;

        // static so the last result survives the VC being re-shown
        private static string _toggleStatus = "";
        private ModInfo _toggleMod = null;

        [UIObject("toggle-banner")] private GameObject _bannerGo    = null;
        [UIComponent("zs-tag")]     private TextMeshProUGUI _zsTag = null;
        [UIObject("header-icons")]  private GameObject _headerIcons = null;
        [UIObject("reload-btn")]    private GameObject _reloadBtnGo = null;
        [UIObject("back-slot")]     private GameObject _backSlot    = null;
        [UIObject("browse-slot")]   private GameObject _browseSlot  = null;
        private UnityEngine.UI.Image _browseBtn, _reloadBtn;

        // Property setter does NOT call BuildRows — only OnSearchChanged does, preventing double-fire
        private string _searchText = "";
        [UIValue("search-text")]
        public string SearchText
        {
            get => _searchText;
            set => _searchText = value ?? "";
        }

        [UIObject("mod-list-container")]
        private GameObject _containerGo = null;

        // ── Lifecycle ─────────────────────────────────────────────────────────────
        [UIAction("#post-parse")]
        private void OnPostParse()
        {
            Plugin.Log?.Info($"[ModManager] #post-parse. Container={(_containerGo != null ? "OK" : "NULL")}");
            ViewportClickGuard.Attach(_containerGo);
            BuildHeaderIcons();
            LoadAndRefresh();
            UpdateBanner();
        }

        [UIAction("go-back")]
        private void OnGoBack() => ModManagerFlowCoordinator.GoBack();

        protected override void DidDeactivate(bool removedFromHierarchy, bool screenSystemDisabling)
        {
            Theme.Changed -= OnThemeChanged;
            base.DidDeactivate(removedFromHierarchy, screenSystemDisabling);
        }

        private void OnThemeChanged()
        {
            if (_zsTag != null) _zsTag.color = Theme.Accent;
            if (_browseBtn != null) UiKit.Recolor(_browseBtn, Theme.Accent);
            if (_reloadBtn != null) UiKit.Recolor(_reloadBtn, Theme.Accent);
            BuildRows();
        }

        // Slot machine + settings cog, top right of the header
        private void BuildHeaderIcons()
        {
            if (_headerIcons == null) return;
            UiKit.ClearChildren(_headerIcons.transform);
            var dark = new Color(0.16f, 0.16f, 0.21f, 0.95f);
            UiKit.Button(_headerIcons.transform, "GambleBtn", dark, 7, 7, ModManagerFlowCoordinator.PresentGamble, icon: IconFactory.Slot, iconInset: 0.9f);
            UiKit.Button(_headerIcons.transform, "SettingsBtn", dark, 7, 7, ModManagerFlowCoordinator.PresentSettings, icon: IconFactory.Cog, iconInset: 0.9f);
        }

        protected override void DidActivate(bool firstActivation, bool addedToHierarchy, bool screenSystemEnabling)
        {
            if (firstActivation) FlatView.Prepare(this);   // before BSML builds the view
            base.DidActivate(firstActivation, addedToHierarchy, screenSystemEnabling);
            if (firstActivation)
            {
                FlatView.Finish(this);
                UiKit.SlotButton(_backSlot, "BackBtn", UiKit.Neutral, OnGoBack, "<", 4f);
                _browseBtn = UiKit.SlotButton(_browseSlot, "BrowseBtn", Theme.Accent, OnOpenBrowse, "BROWSE MODS", 2.8f);
                _reloadBtn = UiKit.SlotButton(_reloadBtnGo, "ReloadBtn", Theme.Accent, OnReloadMenu, "RELOAD MENU", 2.6f);
            }
            if (_browseBtn != null) UiKit.Recolor(_browseBtn, Theme.Accent);
            if (_reloadBtn != null) UiKit.Recolor(_reloadBtn, Theme.Accent);
            Theme.Changed -= OnThemeChanged;
            Theme.Changed += OnThemeChanged;
            if (_zsTag != null) _zsTag.color = Theme.Accent;
            if (!firstActivation) LoadAndRefresh();
            UpdateBanner();
        }


        // ── Enable / disable ──────────────────────────────────────────────────────
        internal void OnToggleClicked(ModInfo mod, ModRuntimeState state)
        {
            if (ModToggleService.Busy) return;
            if (state == ModRuntimeState.Enabled)
            {
                var dependents = ModToggleService.GetEnabledDependents(mod);
                if (dependents.Any()) { ShowToggleConfirm(mod, dependents); return; }
                RunToggle(mod, disable: true);
            }
            else if (state == ModRuntimeState.Disabled)
            {
                RunToggle(mod, disable: false);
            }
        }

        [UIAction("toggle-confirm-yes")]
        private void OnToggleConfirmYes()
        {
            var mod = _toggleMod;
            StopAutoCancel(); HideConfirm();
            if (mod != null) RunToggle(mod, disable: true);
        }

        [UIAction("toggle-confirm-cancel")]
        private void OnToggleConfirmCancel() { StopAutoCancel(); HideConfirm(); }

        [UIAction("reload-menu")]
        private void OnReloadMenu()
        {
            if (ModToggleService.Busy) return;
            _toggleStatus = "";
            if (!ModToggleService.ReloadMenu())
            {
                SetToggleStatus("Menu reload failed — see log. Changes are still saved for next launch.");
            }
        }

        private void ShowToggleConfirm(ModInfo mod, List<string> dependents)
        {
            _toggleMod = mod;
            ToggleConfirmTitle = $"Disable  {mod.DisplayLabel}?";
            string list = dependents.Count <= 6
                ? string.Join(", ", dependents)
                : string.Join(", ", dependents.Take(6)) + $" and {dependents.Count - 6} more";
            ToggleConfirmBody = $"These mods depend on it and will be disabled too:\n{list}";
            Card?.Set("DISABLE MOD", $"{mod.DisplayLabel}  v{mod.Version}",
                $"<color=#C8C8D2>{dependents.Count} mod{(dependents.Count == 1 ? "" : "s")} depend on it and will be turned off too:</color>\n<b>{PromptUi.Esc(list)}</b>",
                "<color=#9A9AA6>You can turn them all back on from the list later.</color>", null,
                InlinePrompt.Btn.Primary("DISABLE ALL", OnToggleConfirmYes, 1.2f),
                InlinePrompt.Btn.Neutral("CANCEL", OnToggleConfirmCancel, 0.8f));
            ConfirmVisible = true; ListVisible = false;
            NotifyPropertyChanged(nameof(ConfirmVisible));
            NotifyPropertyChanged(nameof(ListVisible));
            StopAutoCancel();
            _autoCancelCo = StartCoroutine(AutoCancelCountdown());
        }

        private async void RunToggle(ModInfo mod, bool disable)
        {
            SetToggleStatus(disable ? $"Disabling {mod.DisplayLabel}…" : $"Enabling {mod.DisplayLabel}…");
            ToggleResult result;
            try
            {
                result = disable ? await ModToggleService.DisableAsync(mod)
                                 : await ModToggleService.EnableAsync(mod);
            }
            catch (Exception ex)
            {
                result = new ToggleResult { Message = "Toggle failed: " + ex.Message };
                Plugin.Log?.Error($"[ModManager] Toggle error: {ex}");
            }

            if (this == null) return; // VC destroyed (menu reloaded) while awaiting
            string msg = result.Message;
            if (result.Success && ModToggleService.ChangedSinceReload.Count > 0)
                msg += "  Reload Menu to fully apply.";
            SetToggleStatus(msg);
            BuildRows();
        }

        private void SetToggleStatus(string msg)
        {
            _toggleStatus = msg ?? "";
            NotifyPropertyChanged(nameof(ToggleStatus));
            UpdateBanner();
        }

        // BSML 'active' bindings are unreliable on some layout elements, so drive these directly.
        private void UpdateBanner()
        {
            bool reload = ModToggleService.ChangedSinceReload.Count > 0;
            if (string.IsNullOrEmpty(_toggleStatus) && reload)
                _toggleStatus = $"{ModToggleService.ChangedSinceReload.Count} mod(s) toggled. Reload Menu to fully apply.";
            NotifyPropertyChanged(nameof(ToggleStatus));
            if (_bannerGo    != null) _bannerGo.SetActive(!string.IsNullOrEmpty(_toggleStatus) || reload);
            if (_reloadBtnGo != null) _reloadBtnGo.SetActive(reload);
        }

        // string-setting on-change — single source of truth for search
        [UIAction("search-changed")]
        private void OnSearchChanged(string val)
        {
            _searchText = val ?? "";
            _scrollToTop = true;
            BuildRows();
        }

        [UIAction("open-browse")]
        private void OnOpenBrowse() => ModManagerFlowCoordinator.PresentBrowser();

        // ── Confirm ───────────────────────────────────────────────────────────────
        [UIAction("confirm-restart")]
        private void OnConfirmRestart()
        {
            if (_selectedMod == null) return;
            CommitDeletion(_selectedMod); _selectedMod = null;
            StopAutoCancel(); HideConfirm();
            ExecutePendingDeletions();
            Plugin.Instance?.LaunchPostExitCleanupPublic();
            RestartGame();
        }

        [UIAction("confirm-close")]
        private void OnConfirmClose()
        {
            if (_selectedMod == null) return;
            CommitDeletion(_selectedMod); _selectedMod = null;
            StopAutoCancel(); HideConfirm();
            UpdateFooter(); BuildRows();
        }

        [UIAction("confirm-cancel")]
        private void OnConfirmCancel() { _selectedMod = null; StopAutoCancel(); HideConfirm(); }

        internal void OnDeleteClicked(ModInfo mod) { _selectedMod = mod; ShowConfirm(mod); }
        internal void OnUndoClicked(ModInfo mod)
        {
            lock (_pendingLock) PendingDeletions.RemoveAll(m => m.Id.Equals(mod.Id, StringComparison.OrdinalIgnoreCase));
            UpdateFooter(); BuildRows();
        }

        // ── Sorting ───────────────────────────────────────────────────────────────
        internal const string SortNameAsc   = "Name (A-Z)";
        internal const string SortNameDesc  = "Name (Z-A)";
        internal const string SortOnFirst   = "Enabled first";
        internal const string SortOffFirst  = "Disabled first";
        internal const string SortCoreFirst = "Core & libraries first";
        internal const string SortMostUsed  = "Most depended on";
        internal const string SortRecent    = "Recently installed";

        [UIValue("sort-options")]
        private List<object> SortOptions => new List<object>
            { SortNameAsc, SortNameDesc, SortOnFirst, SortOffFirst, SortCoreFirst, SortMostUsed, SortRecent };

        [UIValue("sort-mode")]
        public string SortMode
        {
            get
            {
                string v = Plugin.Config?.ModSortMode;
                return SortOptions.Contains(v) ? v : SortNameAsc;
            }
            set { if (Plugin.Config != null) Plugin.Config.ModSortMode = value; }
        }

        [UIAction("sort-changed")]
        private void OnSortChanged(object val)
        {
            if (Plugin.Config != null) Plugin.Config.ModSortMode = val as string ?? SortNameAsc;
            _scrollToTop = true;
            BuildRows();
        }

        private enum Group { On, Off, Core, Problem }

        private static Group GroupOf(ModRuntimeState s)
        {
            switch (s)
            {
                case ModRuntimeState.Enabled:   return Group.On;
                case ModRuntimeState.Disabled:  return Group.Off;
                case ModRuntimeState.Protected:
                case ModRuntimeState.Library:   return Group.Core;
                default:                        return Group.Problem;
            }
        }

        private static string GroupTitle(Group g)
        {
            switch (g)
            {
                case Group.On:   return "ENABLED";
                case Group.Off:  return "DISABLED";
                case Group.Core: return "CORE & LIBRARIES";
                default:         return "NEEDS ATTENTION";
            }
        }

        private static DateTime FileTime(ModInfo m)
        {
            try { return File.GetLastWriteTimeUtc(m.DllPath); } catch { return DateTime.MinValue; }
        }

        // ── Core ──────────────────────────────────────────────────────────────────
        private void LoadAndRefresh()
        {
            ModRegistry.Invalidate();
            _allMods = ModRegistry.GetAllMods(Plugin.GetPluginsPath());
            UpdateCountLabel();
            UpdateFooter();
            BuildRows();
        }

        private void UpdateCountLabel()
        {
            int on = 0, off = 0;
            foreach (var m in _allMods)
            {
                var st = ModToggleService.GetState(m);
                if (st == ModRuntimeState.Enabled || st == ModRuntimeState.Protected || st == ModRuntimeState.Library) on++;
                else if (st == ModRuntimeState.Disabled) off++;
            }
            ModCountLabel = $"{_allMods.Count} mods  <color=#55CC77>{on} on</color>  <color=#888888>{off} off</color>";
            NotifyPropertyChanged(nameof(ModCountLabel));
        }

        private void BuildRows()
        {
            if (_containerGo == null) return;
            var container = _containerGo.transform;
            foreach (Transform child in container) Destroy(child.gameObject);

            var pendingIds = new HashSet<string>(PendingDeletions.Select(m => m.Id), StringComparer.OrdinalIgnoreCase);
            var rows = _allMods
                .Where(m => string.IsNullOrWhiteSpace(_searchText) ||
                            m.DisplayLabel.IndexOf(_searchText, StringComparison.OrdinalIgnoreCase) >= 0 ||
                            m.Id.IndexOf(_searchText, StringComparison.OrdinalIgnoreCase) >= 0)
                .Select(m => new { Mod = m, State = ModToggleService.GetState(m) })
                .ToList();

            Func<ModInfo, string> name = m => m.DisplayLabel;
            var cmp = StringComparer.OrdinalIgnoreCase;
            string mode = SortMode;
            bool grouped = false;
            Group[] groupOrder = null;

            switch (mode)
            {
                case SortNameDesc:
                    rows = rows.OrderByDescending(r => name(r.Mod), cmp).ToList(); break;
                case SortOnFirst:
                    grouped = true; groupOrder = new[] { Group.On, Group.Core, Group.Off, Group.Problem }; break;
                case SortOffFirst:
                    grouped = true; groupOrder = new[] { Group.Off, Group.Problem, Group.On, Group.Core }; break;
                case SortCoreFirst:
                    grouped = true; groupOrder = new[] { Group.Core, Group.On, Group.Off, Group.Problem }; break;
                case SortMostUsed:
                    rows = rows.OrderByDescending(r => r.Mod.RequiredBy.Count).ThenBy(r => name(r.Mod), cmp).ToList(); break;
                case SortRecent:
                    rows = rows.OrderByDescending(r => FileTime(r.Mod)).ThenBy(r => name(r.Mod), cmp).ToList(); break;
                default:
                    rows = rows.OrderBy(r => name(r.Mod), cmp).ToList(); break;
            }

            if (grouped)
            {
                foreach (var g in groupOrder)
                {
                    var inGroup = rows.Where(r => GroupOf(r.State) == g).OrderBy(r => name(r.Mod), cmp).ToList();
                    if (inGroup.Count == 0) continue;
                    BuildGroupHeader(container, GroupTitle(g), inGroup.Count);
                    foreach (var r in inGroup) BuildRow(container, r.Mod, pendingIds.Contains(r.Mod.Id), r.State);
                }
            }
            else
            {
                foreach (var r in rows) BuildRow(container, r.Mod, pendingIds.Contains(r.Mod.Id), r.State);
            }

            if (rows.Count == 0) BuildGroupHeader(container, "NO MODS MATCH YOUR SEARCH", -1);

            UpdateCountLabel();
            Plugin.Log?.Debug($"[ModManager] Built {rows.Count} rows ({mode}).");
            ScrollFix.Refresh(this, _containerGo, _scrollToTop);
            _scrollToTop = false;
        }

        // Wait one frame so the layout has rebuilt, then scroll to top
        private bool _scrollToTop;

        private IEnumerator ScrollToTopNextFrame()
        {
            yield return null; // wait one frame
            if (_containerGo == null) yield break;

            // Walk up the hierarchy from the container to find the ScrollRect
            Transform t = _containerGo.transform.parent;
            while (t != null)
            {
                var sr = t.GetComponent<ScrollRect>();
                if (sr != null) { sr.verticalNormalizedPosition = 1f; yield break; }
                t = t.parent;
            }
        }

        // ── Row building ──────────────────────────────────────────────────────────
        private static readonly Color AccentOn      = new Color(0.25f, 0.80f, 0.45f, 1f);
        private static readonly Color AccentOff     = new Color(0.40f, 0.40f, 0.45f, 1f);
        private static readonly Color AccentCore    = new Color(0.30f, 0.60f, 0.95f, 1f);
        private static readonly Color AccentProblem = new Color(0.95f, 0.65f, 0.20f, 1f);
        private static readonly Color AccentPending = new Color(0.95f, 0.30f, 0.30f, 1f);

        private void BuildGroupHeader(Transform parent, string title, int count)
        {
            var go = new GameObject("Header_" + title);
            go.transform.SetParent(parent, false);
            var le = go.AddComponent<LayoutElement>();
            le.preferredHeight = 5; le.flexibleWidth = 1;
            var tmp = go.AddComponent<TextMeshProUGUI>();
            tmp.text = count >= 0 ? $"{title}  <color=#666677>{count}</color>" : title;
            tmp.fontSize = 2.8f; tmp.fontStyle = FontStyles.Bold; tmp.characterSpacing = 4;
            tmp.color = Theme.AccentShade(0.9f, 1f);
            tmp.alignment = TextAlignmentOptions.BottomLeft;
            tmp.margin = new Vector4(2, 0, 0, 0.5f);
            tmp.richText = true; tmp.raycastTarget = false;
            tmp.fontSharedMaterial = GetNoGlowTMPMaterial(tmp);
        }

        private void BuildRow(Transform parent, ModInfo mod, bool isPending, ModRuntimeState state)
        {
            bool isOff = state == ModRuntimeState.Disabled || state == ModRuntimeState.Ignored;

            var rowGo = new GameObject($"Row_{mod.Id}");
            rowGo.transform.SetParent(parent, false);
            rowGo.AddComponent<RectTransform>();

            var bgImg = rowGo.AddComponent<Image>();
            bgImg.sprite = GetNoGlowSprite(); bgImg.material = GetNoGlowMaterial(); bgImg.type = Image.Type.Sliced;
            Color normalCol = isPending ? new Color(0.25f, 0.06f, 0.06f, 0.80f)
                            : isOff     ? new Color(0.06f, 0.06f, 0.08f, 0.55f)
                            :             new Color(0.10f, 0.10f, 0.14f, 0.70f);
            Color hoverCol  = new Color(normalCol.r + 0.06f, normalCol.g + 0.06f, normalCol.b + 0.08f, Mathf.Min(normalCol.a + 0.15f, 1f));
            bgImg.color = normalCol; bgImg.raycastTarget = true;

            var layout = rowGo.AddComponent<HorizontalLayoutGroup>();
            layout.spacing = 2;
            layout.childAlignment = TextAnchor.MiddleLeft;
            layout.childForceExpandWidth = false; layout.childForceExpandHeight = false;
            layout.childControlWidth = true; layout.childControlHeight = true;
            layout.padding = new RectOffset(0, 2, 1, 1);
            var rowLE = rowGo.AddComponent<LayoutElement>();
            rowLE.preferredHeight = 7.5f; rowLE.minHeight = 7.5f;

            var trigger = rowGo.AddComponent<UnityEngine.EventSystems.EventTrigger>();
            AddHover(trigger, () => bgImg.color = hoverCol, () => bgImg.color = normalCol);

            // Status accent bar
            Color accent = isPending ? AccentPending
                         : state == ModRuntimeState.Enabled  ? AccentOn
                         : state == ModRuntimeState.Disabled ? AccentOff
                         : (state == ModRuntimeState.Protected || state == ModRuntimeState.Library) ? AccentCore
                         : AccentProblem;
            var barGo = new GameObject("Accent");
            barGo.transform.SetParent(rowGo.transform, false);
            var barLE = barGo.AddComponent<LayoutElement>();
            barLE.preferredWidth = 0.8f; barLE.minWidth = 0.8f; barLE.preferredHeight = 5.5f;
            var barImg = barGo.AddComponent<Image>();
            barImg.sprite = GetNoGlowSprite(); barImg.material = GetNoGlowMaterial(); barImg.type = Image.Type.Sliced;
            barImg.color = isOff ? new Color(accent.r, accent.g, accent.b, 0.5f) : accent;
            barImg.raycastTarget = false;

            // Name + version
            string nameColor = isPending ? "#FF9999" : isOff ? "#7A7A80" : "#E6E6EA";
            string tag = isPending ? "  <color=#CC5555><size=80%>PENDING DELETE</size></color>"
                       : state == ModRuntimeState.Ignored   ? "  <color=#CC8833><size=80%>FAILED TO LOAD</size></color>"
                       : state == ModRuntimeState.NotLoaded ? "  <color=#6688AA><size=80%>RESTART TO LOAD</size></color>"
                       : "";
            var labelGo = new GameObject("Label");
            labelGo.transform.SetParent(rowGo.transform, false);
            var lLE = labelGo.AddComponent<LayoutElement>();
            lLE.flexibleWidth = 1; lLE.preferredHeight = 5.5f; lLE.minWidth = 10;
            var lTMP = labelGo.AddComponent<TextMeshProUGUI>();
            lTMP.text = $"<color={nameColor}>{mod.DisplayLabel}</color>  <color=#5E5E68><size=75%>v{mod.Version}</size></color>{tag}";
            lTMP.fontSize = 3.3f; lTMP.enableWordWrapping = false; lTMP.overflowMode = TextOverflowModes.Ellipsis;
            lTMP.alignment = TextAlignmentOptions.Left; lTMP.richText = true; lTMP.raycastTarget = false;
            lTMP.margin = new Vector4(1.5f, 0, 0, 0);
            lTMP.fontSharedMaterial = GetNoGlowTMPMaterial(lTMP);

            // "used by N" hint
            if (mod.RequiredBy.Count > 0)
            {
                var useGo = new GameObject("UsedBy");
                useGo.transform.SetParent(rowGo.transform, false);
                var uLE = useGo.AddComponent<LayoutElement>();
                uLE.preferredWidth = 15; uLE.preferredHeight = 5.5f;
                var uTMP = useGo.AddComponent<TextMeshProUGUI>();
                uTMP.text = $"used by {mod.RequiredBy.Count}";
                uTMP.fontSize = 2.4f; uTMP.color = new Color(0.45f, 0.45f, 0.52f, 1f);
                uTMP.alignment = TextAlignmentOptions.Right; uTMP.enableWordWrapping = false; uTMP.raycastTarget = false;
                uTMP.fontSharedMaterial = GetNoGlowTMPMaterial(uTMP);
            }

            // Toggle pill
            string tLabel; Color tColor; Action tAct = null;
            switch (state)
            {
                case ModRuntimeState.Enabled:   tLabel = "ON";   tColor = new Color(0.12f, 0.50f, 0.25f, 1f); tAct = () => OnToggleClicked(mod, state); break;
                case ModRuntimeState.Disabled:  tLabel = "OFF";  tColor = new Color(0.24f, 0.24f, 0.29f, 1f); tAct = () => OnToggleClicked(mod, state); break;
                case ModRuntimeState.Protected: tLabel = "CORE"; tColor = new Color(0.13f, 0.20f, 0.32f, 1f); break;
                case ModRuntimeState.Library:   tLabel = "LIB";  tColor = new Color(0.13f, 0.20f, 0.32f, 1f); break;
                case ModRuntimeState.Ignored:   tLabel = "ERR";  tColor = new Color(0.38f, 0.25f, 0.06f, 1f); break;
                default:                        tLabel = "NEW";  tColor = new Color(0.14f, 0.20f, 0.30f, 1f); break;
            }
            BuildButton(rowGo.transform, tLabel, tColor, tAct, 11);

            // Delete / Undo (not offered for core mods ZipSaber depends on)
            if (state == ModRuntimeState.Protected)
            {
                BuildSpacer(rowGo.transform, 14);
            }
            else
            {
                string btnLabel = isPending ? "UNDO" : "DELETE";
                Color  btnColor = isPending ? new Color(0.28f, 0.28f, 0.36f, 1f) : new Color(0.42f, 0.10f, 0.10f, 1f);
                Action btnAct   = isPending ? (Action)(() => OnUndoClicked(mod)) : () => OnDeleteClicked(mod);
                BuildButton(rowGo.transform, btnLabel, btnColor, btnAct, 14);
            }
        }

        private static void BuildSpacer(Transform parent, float width)
        {
            var go = new GameObject("Spacer");
            go.transform.SetParent(parent, false);
            var le = go.AddComponent<LayoutElement>();
            le.preferredWidth = width; le.minWidth = width; le.preferredHeight = 5.5f;
        }

        private static void BuildButton(Transform parent, string label, Color color, Action onClick, float width)
        {
            var btnGo = new GameObject("Btn_" + label);
            btnGo.transform.SetParent(parent, false);
            var btnLE = btnGo.AddComponent<LayoutElement>();
            btnLE.preferredWidth = width; btnLE.minWidth = width; btnLE.preferredHeight = 5.5f;

            var btnImg = btnGo.AddComponent<Image>();
            btnImg.sprite = GetNoGlowSprite(); btnImg.material = GetNoGlowMaterial();
            btnImg.type = Image.Type.Sliced; btnImg.color = color;

            if (onClick != null)
            {
                var trigger = btnGo.AddComponent<UnityEngine.EventSystems.EventTrigger>();
                Color hov = new Color(Mathf.Min(color.r * 1.35f + 0.04f, 1f), Mathf.Min(color.g * 1.35f + 0.04f, 1f), Mathf.Min(color.b * 1.35f + 0.04f, 1f), 1f);
                AddHover(trigger, () => btnImg.color = hov, () => btnImg.color = color);
            }

            var lblGo = new GameObject("Lbl");
            lblGo.transform.SetParent(btnGo.transform, false);
            var lr = lblGo.AddComponent<RectTransform>();
            lr.anchorMin = Vector2.zero; lr.anchorMax = Vector2.one;
            lr.offsetMin = Vector2.zero; lr.offsetMax = Vector2.zero;
            var lTMP = lblGo.AddComponent<TextMeshProUGUI>();
            lTMP.text = label; lTMP.fontSize = 2.6f; lTMP.fontStyle = FontStyles.Bold; lTMP.characterSpacing = 2;
            lTMP.alignment = TextAlignmentOptions.Center;
            lTMP.color = onClick != null ? Color.white : new Color(0.62f, 0.70f, 0.82f, 1f); lTMP.raycastTarget = false;
            lTMP.fontSharedMaterial = GetNoGlowTMPMaterial(lTMP);

            if (onClick == null) return;

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

        private static void AddHover(UnityEngine.EventSystems.EventTrigger trigger, Action onEnter, Action onExit)
        {
            var e = new UnityEngine.EventSystems.EventTrigger.Entry { eventID = UnityEngine.EventSystems.EventTriggerType.PointerEnter };
            e.callback.AddListener(_ => onEnter()); trigger.triggers.Add(e);
            var x = new UnityEngine.EventSystems.EventTrigger.Entry { eventID = UnityEngine.EventSystems.EventTriggerType.PointerExit };
            x.callback.AddListener(_ => onExit()); trigger.triggers.Add(x);
        }

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
                if (m.name == "UINoGlow") { _noGlowMaterial = m; break; }
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

        // ── Confirm panel ─────────────────────────────────────────────────────────
        private void ShowConfirm(ModInfo mod)
        {
            _selectedMod = mod;
            ConfirmTitle = $"Delete  {mod.DisplayLabel}  v{mod.Version}?";
            var req = mod.RequiredBy.Where(id =>
                !PendingDeletions.Any(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase))).ToList();
            DependencyWarning = req.Any()
                ? $"Required by: {(req.Count <= 3 ? string.Join(", ", req) : string.Join(", ", req.Take(3)) + $" and {req.Count - 3} more")}"
                : "";
            DepWarnVisible = req.Any(); ConfirmVisible = true; ListVisible = false;
            string body = req.Any()
                ? $"<color=#FF8A8A><b>Required by:</b> {PromptUi.Esc(req.Count <= 4 ? string.Join(", ", req) : string.Join(", ", req.Take(4)) + $" and {req.Count - 4} more")}</color>\nThose mods may stop working."
                : "<color=#C8C8D2>Removes the mod's DLL from your Plugins folder.</color>";
            Card?.Set("DELETE MOD", $"{mod.DisplayLabel}  v{mod.Version}", body,
                "Deleting mods may cause game-breaking issues.", new Color(1f, 0.37f, 0.42f),
                InlinePrompt.Btn.Danger("RESTART & DELETE", OnConfirmRestart, 1.3f),
                InlinePrompt.Btn.Neutral("DELETE ON CLOSE", OnConfirmClose, 1.2f),
                InlinePrompt.Btn.Neutral("CANCEL", OnConfirmCancel, 0.7f));
            NotifyPropertyChanged(nameof(ConfirmTitle));
            NotifyPropertyChanged(nameof(DependencyWarning));
            NotifyPropertyChanged(nameof(DepWarnVisible));
            NotifyPropertyChanged(nameof(ConfirmVisible));
            NotifyPropertyChanged(nameof(ListVisible));
            StopAutoCancel();
            _autoCancelCo = StartCoroutine(AutoCancelCountdown());
        }

        private void HideConfirm()
        {
            ConfirmVisible = false; ToggleConfirmVisible = false; ListVisible = true; AutoCancelLabel = "";
            _toggleMod = null;
            NotifyPropertyChanged(nameof(ConfirmVisible));
            NotifyPropertyChanged(nameof(ToggleConfirmVisible));
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
            _selectedMod = null; HideConfirm();
        }

        private void StopAutoCancel()
        {
            if (_autoCancelCo != null) { StopCoroutine(_autoCancelCo); _autoCancelCo = null; }
        }

        internal static void UpdateFooterStatic() { }

        private void UpdateFooter()
        {
            int del = PendingDeletions.Count, ins = PendingInstalls.Count;
            if (del == 0 && ins == 0) { PendingLabel = ""; PendingVisible = false; }
            else
            {
                var parts = new List<string>();
                if (del > 0) parts.Add($"🗑 {del} pending delete{(del == 1 ? "" : "s")}");
                if (ins > 0) parts.Add($"⬇ {ins} pending install{(ins == 1 ? "" : "s")}");
                PendingLabel = string.Join("   ", parts); PendingVisible = true;
            }
            NotifyPropertyChanged(nameof(PendingLabel));
            NotifyPropertyChanged(nameof(PendingVisible));
        }

        private void CommitDeletion(ModInfo mod)
        {
            lock (_pendingLock)
                if (!PendingDeletions.Any(m => m.Id.Equals(mod.Id, StringComparison.OrdinalIgnoreCase)))
                    PendingDeletions.Add(mod);
        }

        internal static void CommitInstall(string modName)
        {
            lock (_pendingLock)
                if (!PendingInstalls.Contains(modName, StringComparer.OrdinalIgnoreCase))
                    PendingInstalls.Add(modName);
        }

        /// <summary>
        /// Downloads and extracts all queued pending installs synchronously.
        /// Called from Plugin.OnDisable before the process exits.
        /// </summary>
        internal static void ExecutePendingInstalls(
            System.Collections.Generic.Dictionary<string, string> downloadUrlsByName)
        {
            List<string> toInstall;
            lock (_pendingLock) { toInstall = new List<string>(PendingInstalls); PendingInstalls.Clear(); }
            if (!toInstall.Any()) return;

            string pluginsPath = Plugin.GetPluginsPath();
            if (string.IsNullOrEmpty(pluginsPath)) { Plugin.Log?.Error("[ModManager] Plugins path null — cannot execute pending installs."); return; }

            foreach (string modName in toInstall)
            {
                if (!downloadUrlsByName.TryGetValue(modName, out string url) || string.IsNullOrEmpty(url))
                { Plugin.Log?.Warn($"[ModManager] No download URL for '{modName}', skipping."); continue; }

                try
                {
                    Plugin.Log?.Info($"[ModManager] Installing (on-close): {modName}");
                    byte[] data;
                    using (var wc = new System.Net.WebClient())
                    {
                        wc.Headers[System.Net.HttpRequestHeader.UserAgent] = "ZipSaber/2.0.0";
                        data = wc.DownloadData(url);
                    }
                    using (var ms = new System.IO.MemoryStream(data))
                    using (var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Read))
                    {
                        foreach (var entry in zip.Entries)
                        {
                            if (entry.FullName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) &&
                                (entry.FullName.StartsWith("Plugins/",  StringComparison.OrdinalIgnoreCase) ||
                                 entry.FullName.StartsWith("Plugins\\", StringComparison.OrdinalIgnoreCase) ||
                                 !entry.FullName.Contains("/")))
                            {
                                string dest = System.IO.Path.Combine(pluginsPath, System.IO.Path.GetFileName(entry.FullName));
                                using (var fs = System.IO.File.Create(dest))
                                using (var es = entry.Open()) es.CopyTo(fs);
                            }
                        }
                    }
                    Plugin.Log?.Info($"[ModManager] Installed (on-close): {modName}");
                }
                catch (Exception ex) { Plugin.Log?.Error($"[ModManager] Install failed for '{modName}': {ex.Message}"); }
            }
        }

        internal static void ExecutePendingDeletions()
        {
            List<ModInfo> toDelete;
            lock (_pendingLock) { toDelete = new List<ModInfo>(PendingDeletions); PendingDeletions.Clear(); }
            if (!toDelete.Any()) return;
            foreach (var mod in toDelete) { TryMarkDelete(mod.DllPath, "DLL"); TryMarkDelete(mod.ManifestPath, "manifest"); }
        }

        private static void TryMarkDelete(string path, string label)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
            try { File.WriteAllText(path + ".zs_del", "pending"); }
            catch (Exception ex) { Plugin.Log?.Error($"[ModManager] Mark failed ({label}): {ex.Message}"); }
        }

        private static void RestartGame()
        {
            try
            {
                string exe  = Process.GetCurrentProcess().MainModule?.FileName;
                string args = string.Join(" ", Environment.GetCommandLineArgs().Skip(1).Select(a => a.Contains(' ') ? $"\"{a}\"" : a));
                Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe) });
                Thread.Sleep(500); Application.Quit();
            }
            catch (Exception ex) { Plugin.Log?.Error($"[ModManager] Restart failed: {ex.Message}"); }
        }
    }
}