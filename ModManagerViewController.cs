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
        // ── Pending deletions / installs ──────────────────────────────────────────
        internal static readonly List<ModInfo> PendingDeletions = new List<ModInfo>();
        internal static readonly List<string>  PendingInstalls  = new List<string>(); // display names
        private static readonly object _pendingLock = new object();

        private List<ModInfo> _allMods       = new List<ModInfo>();
        private ModInfo       _selectedMod   = null;
        private Coroutine     _autoCancelCo  = null;
        private int           _autoCancelSec = 20;

        // ── BSML bound properties ─────────────────────────────────────────────────
        [UIValue("mod-count-label")]
        public string ModCountLabel { get; private set; } = "";

        [UIValue("confirm-title")]
        public string ConfirmTitle { get; private set; } = "";

        [UIValue("confirm-visible")]
        public bool ConfirmVisible { get; private set; } = false;

        [UIValue("list-visible")]
        public bool ListVisible { get; private set; } = true;

        [UIValue("dependency-warning")]
        public string DependencyWarning { get; private set; } = "";

        [UIValue("dep-warn-visible")]
        public bool DepWarnVisible { get; private set; } = false;

        [UIValue("auto-cancel-label")]
        public string AutoCancelLabel { get; private set; } = "";

        [UIValue("pending-label")]
        public string PendingLabel { get; private set; } = "";

        [UIValue("pending-visible")]
        public bool PendingVisible { get; private set; } = false;

        [UIValue("search-text")]
        public string SearchText { get; set; } = "";

        // ── BSML refs ─────────────────────────────────────────────────────────────
        [UIObject("mod-list-container")]
        private GameObject _containerGo = null;

        [UIObject("confirm-panel")]
        private GameObject _confirmPanelGo = null;


        // ── Lifecycle ─────────────────────────────────────────────────────────────
        [UIAction("#post-parse")]
        private void OnPostParse()
        {
            Plugin.Log?.Info($"[ModManager] #post-parse. Container={(_containerGo != null ? "OK" : "NULL")}");
            LoadAndRefresh();
        }

        protected override void DidActivate(bool firstActivation, bool addedToHierarchy, bool screenSystemEnabling)
        {
            base.DidActivate(firstActivation, addedToHierarchy, screenSystemEnabling);
            Plugin.Log?.Info($"[ModManager] DidActivate first={firstActivation}");
            if (!firstActivation) LoadAndRefresh();
        }

        [UIAction("search-changed")]
        private void OnSearchChanged(string val) { SearchText = val ?? ""; BuildRows(); }

        [UIAction("open-browse")]
        private void OnOpenBrowse() => ModManagerFlowCoordinator.PresentBrowser();

        // ── Confirm actions ───────────────────────────────────────────────────────
        [UIAction("confirm-restart")]
        private void OnConfirmRestart()
        {
            Plugin.Log?.Info($"[ModManager] confirm-restart. selected={_selectedMod?.Id ?? "null"}");
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
            Plugin.Log?.Info($"[ModManager] confirm-close. selected={_selectedMod?.Id ?? "null"}");
            if (_selectedMod == null) return;
            CommitDeletion(_selectedMod); _selectedMod = null;
            StopAutoCancel(); HideConfirm();
            UpdateFooter(); BuildRows();
        }

        [UIAction("confirm-cancel")]
        private void OnConfirmCancel()
        {
            Plugin.Log?.Info("[ModManager] confirm-cancel.");
            _selectedMod = null;
            StopAutoCancel(); HideConfirm();
        }

        internal void OnDeleteClicked(ModInfo mod)  { _selectedMod = mod; ShowConfirm(mod); }
        internal void OnUndoClicked(ModInfo mod)
        {
            lock (_pendingLock) PendingDeletions.RemoveAll(m => m.Id.Equals(mod.Id, StringComparison.OrdinalIgnoreCase));
            UpdateFooter(); BuildRows();
        }

        // ── Core ──────────────────────────────────────────────────────────────────
        private void LoadAndRefresh()
        {
            ModRegistry.Invalidate();
            _allMods = ModRegistry.GetAllMods(Plugin.GetPluginsPath());
            ModCountLabel = $"{_allMods.Count} mod{(_allMods.Count == 1 ? "" : "s")} installed";
            NotifyPropertyChanged(nameof(ModCountLabel));
            UpdateFooter();
            BuildRows();
        }

        private void BuildRows()
        {
            if (_containerGo == null) return;
            var container = _containerGo.transform;
            foreach (Transform child in container) Destroy(child.gameObject);

            var pendingIds = new HashSet<string>(PendingDeletions.Select(m => m.Id), StringComparer.OrdinalIgnoreCase);
            var modsToShow = string.IsNullOrWhiteSpace(SearchText)
                ? _allMods
                : _allMods.Where(m =>
                    m.DisplayLabel.IndexOf(SearchText, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    m.Id.IndexOf(SearchText, StringComparison.OrdinalIgnoreCase) >= 0).ToList();

            foreach (var mod in modsToShow)
                BuildRow(container, mod, pendingIds.Contains(mod.Id));

            Plugin.Log?.Info($"[ModManager] Built {modsToShow.Count} rows.");
        }


        private void BuildRow(Transform parent, ModInfo mod, bool isPending)
        {
            // ── Row container ──────────────────────────────────────────────────────
            var rowGo = new GameObject($"Row_{mod.Id}");
            rowGo.transform.SetParent(parent, false);

            var rowRect = rowGo.GetComponent<RectTransform>() ?? rowGo.AddComponent<RectTransform>();
            rowRect.sizeDelta = new Vector2(0, 10);

            // Rounded background via BSML's round-rect sprite
            var bgImg = rowGo.AddComponent<Image>();
            bgImg.sprite = GetRoundRectSprite();
            bgImg.type   = Image.Type.Sliced;
            bgImg.material = null;
            bgImg.color  = isPending
                ? new Color(0.35f, 0.05f, 0.05f, 0.85f)
                : new Color(0.1f, 0.1f, 0.15f, 0.75f);
            bgImg.raycastTarget = true;

            var layout = rowGo.AddComponent<HorizontalLayoutGroup>();
            layout.spacing = 4;
            typeof(HorizontalLayoutGroup).GetProperty("childAlignment")?.SetValue(layout, 3);
            layout.childForceExpandWidth  = false;
            layout.childForceExpandHeight = false;
            layout.padding = new RectOffset(8, 6, 1, 1);

            var fitter = rowGo.AddComponent<ContentSizeFitter>();
            fitter.verticalFit   = ContentSizeFitter.FitMode.PreferredSize;
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;

            // Hover highlight
            Color hoverCol  = isPending ? new Color(0.5f, 0.08f, 0.08f, 0.95f) : new Color(0.18f, 0.18f, 0.25f, 0.9f);
            Color normalCol = bgImg.color;
            var trigger = rowGo.AddComponent<UnityEngine.EventSystems.EventTrigger>();
            AddHover(trigger, () => bgImg.color = hoverCol, () => bgImg.color = normalCol);

            // ── Label ──────────────────────────────────────────────────────────────
            string nameColor = isPending ? "#FF9999" : "#DDDDDD";
            string suffix    = isPending ? " <color=#666666>[pending delete]</color>" : "";
            var labelGo = new GameObject("Label");
            labelGo.transform.SetParent(rowGo.transform, false);
            var lLE = labelGo.AddComponent<LayoutElement>();
            lLE.flexibleWidth = 1; lLE.preferredHeight = 8;
            var lTMP = labelGo.AddComponent<TextMeshProUGUI>();
            lTMP.text  = $"<color={nameColor}>{mod.DisplayLabel}</color>  <color=#777777>v{mod.Version}</color>{suffix}";
            lTMP.fontSize = 3.5f; lTMP.enableWordWrapping = false; lTMP.richText = true;
            var noGlowFont = GetNoGlowFont();
            if (noGlowFont != null) lTMP.font = noGlowFont;

            // ── Button ─────────────────────────────────────────────────────────────
            string btnLabel = isPending ? "Undo" : "Delete";
            Color  btnColor = isPending ? new Color(0.3f, 0.3f, 0.4f, 1f) : new Color(0.6f, 0.08f, 0.08f, 1f);
            Action btnAct   = isPending ? (Action)(() => OnUndoClicked(mod)) : () => OnDeleteClicked(mod);
            BuildButton(rowGo.transform, btnLabel, btnColor, btnAct, 22);
        }

        private static void BuildButton(Transform parent, string label, Color color, Action onClick, float width)
        {
            var btnGo = new GameObject("Btn_" + label);
            btnGo.transform.SetParent(parent, false);
            var btnLE = btnGo.AddComponent<LayoutElement>();
            btnLE.preferredWidth = width; btnLE.preferredHeight = 8;

            // Rounded rect background
            var btnImg = btnGo.AddComponent<Image>();
            btnImg.sprite = GetRoundRectSprite();
            btnImg.type   = Image.Type.Sliced;
            btnImg.material = null;
            btnImg.color  = color;

            // Hover brighten
            var trigger = btnGo.AddComponent<UnityEngine.EventSystems.EventTrigger>();
            Color hov = new Color(color.r * 1.3f, color.g * 1.3f, color.b * 1.3f, 1f);
            AddHover(trigger, () => btnImg.color = hov, () => btnImg.color = color);

            // Label
            var lblGo = new GameObject("Lbl");
            lblGo.transform.SetParent(btnGo.transform, false);
            var lr = lblGo.AddComponent<RectTransform>();
            lr.anchorMin = Vector2.zero; lr.anchorMax = Vector2.one;
            lr.offsetMin = Vector2.zero; lr.offsetMax = Vector2.zero;
            var lTMP = lblGo.AddComponent<TextMeshProUGUI>();
            lTMP.text = label; lTMP.fontSize = 3f;
            lTMP.alignment = TextAlignmentOptions.Center;
            lTMP.color = Color.white; lTMP.raycastTarget = false;
            var noGlowFont = GetNoGlowFont();
            if (noGlowFont != null) lTMP.font = noGlowFont;

            // Transparent hit area on top
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
            e.callback.AddListener(_ => onEnter());
            trigger.triggers.Add(e);
            var x = new UnityEngine.EventSystems.EventTrigger.Entry { eventID = UnityEngine.EventSystems.EventTriggerType.PointerExit };
            x.callback.AddListener(_ => onExit());
            trigger.triggers.Add(x);
        }

        // Use Beat Saber's own round-rect sprite (from BSML's resource cache)
        private static Sprite _roundRectSprite = null;
        private static Sprite GetRoundRectSprite()
        {
            if (_roundRectSprite != null) return _roundRectSprite;
            // Find a suitable sprite from loaded resources
            foreach (var s in Resources.FindObjectsOfTypeAll<Sprite>())
                if (s.name == "RoundRect10" || s.name == "RoundRectSmall" || s.name == "Background")
                { _roundRectSprite = s; break; }
            return _roundRectSprite; // null is fine — Image will just use a square
        }

        /// <summary>
        /// Returns a Beat Saber TMP_FontAsset whose material does NOT have a bloom/glow
        /// shader, preventing manually-created TextMeshProUGUI labels from blooming.
        /// The result is cached after the first successful lookup.
        /// </summary>
        private static TMP_FontAsset _noGlowFont = null;
        private static TMP_FontAsset GetNoGlowFont()
        {
            if (_noGlowFont != null) return _noGlowFont;

            // First preference: any font whose name explicitly says "No Glow"
            foreach (var f in Resources.FindObjectsOfTypeAll<TMP_FontAsset>())
            {
                if (f.name.IndexOf("No Glow", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    f.name.IndexOf("NoGlow",  StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    _noGlowFont = f;
                    Plugin.Log?.Debug($"[ModManager] No-glow font found: {f.name}");
                    return _noGlowFont;
                }
            }

            // Second preference: Teko-Medium (Beat Saber's primary UI font)
            foreach (var f in Resources.FindObjectsOfTypeAll<TMP_FontAsset>())
            {
                if (f.name.StartsWith("Teko-Medium", StringComparison.OrdinalIgnoreCase))
                {
                    _noGlowFont = f;
                    Plugin.Log?.Debug($"[ModManager] Fallback no-glow font: {f.name}");
                    return _noGlowFont;
                }
            }

            Plugin.Log?.Warn("[ModManager] No suitable no-glow font found; TMP labels may bloom.");
            return null;
        }

        // ── Confirm panel ─────────────────────────────────────────────────────────
        private void ShowConfirm(ModInfo mod)
        {
            _selectedMod  = mod;
            ConfirmTitle  = $"Delete  {mod.DisplayLabel}  v{mod.Version}?";
            var req = mod.RequiredBy.Where(id =>
                !PendingDeletions.Any(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase))).ToList();
            DependencyWarning = req.Any()
                ? $"⚠ Required by: {(req.Count <= 3 ? string.Join(", ", req) : string.Join(", ", req.Take(3)) + $"\nand {req.Count - 3} more")}"
                : "";
            DepWarnVisible  = req.Any();
            ConfirmVisible  = true;
            ListVisible     = false;
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
            Plugin.Log?.Info("[ModManager] Prompt auto-cancelled.");
            _selectedMod = null; HideConfirm();
        }

        private void StopAutoCancel()
        {
            if (_autoCancelCo != null) { StopCoroutine(_autoCancelCo); _autoCancelCo = null; }
        }

        // ── Footer ────────────────────────────────────────────────────────────────
        internal static void UpdateFooterStatic()
        {
            // Called from BeatModsBrowserVC when an install is queued
        }

        private void UpdateFooter()
        {
            int del = PendingDeletions.Count;
            int ins = PendingInstalls.Count;
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

        // ── Helpers ───────────────────────────────────────────────────────────────
        private void CommitDeletion(ModInfo mod)
        {
            lock (_pendingLock)
                if (!PendingDeletions.Any(m => m.Id.Equals(mod.Id, StringComparison.OrdinalIgnoreCase)))
                    PendingDeletions.Add(mod);
            Plugin.Log?.Info($"[ModManager] Queued for deletion: {mod.Id}");
        }

        internal static void ExecutePendingDeletions()
        {
            List<ModInfo> toDelete;
            lock (_pendingLock) { toDelete = new List<ModInfo>(PendingDeletions); PendingDeletions.Clear(); }
            if (!toDelete.Any()) return;
            Plugin.Log?.Info($"[ModManager] Deleting {toDelete.Count} mod(s)...");
            foreach (var mod in toDelete) { TryMarkDelete(mod.DllPath, "DLL"); TryMarkDelete(mod.ManifestPath, "manifest"); }
            Plugin.Log?.Info("[ModManager] Deletion complete.");
        }

        private static void TryMarkDelete(string path, string label)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
            try { File.WriteAllText(path + ".zs_del", "pending"); Plugin.Log?.Info($"[ModManager] Marked for deletion ({label}): {Path.GetFileName(path)}"); }
            catch (Exception ex) { Plugin.Log?.Error($"[ModManager] Failed to mark {label} '{Path.GetFileName(path)}': {ex.Message}"); }
        }

        private static void RestartGame()
        {
            try
            {
                string exe  = Process.GetCurrentProcess().MainModule?.FileName;
                string args = string.Join(" ", Environment.GetCommandLineArgs().Skip(1).Select(a => a.Contains(' ') ? $"\"{a}\"" : a));
                Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe) });
                Thread.Sleep(500);
                Application.Quit();
            }
            catch (Exception ex) { Plugin.Log?.Error($"[ModManager] Restart failed: {ex.Message}"); }
        }
    }
}
