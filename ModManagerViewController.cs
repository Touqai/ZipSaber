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
        // ── Pending deletions ─────────────────────────────────────────────────────
        internal static readonly List<ModInfo> PendingDeletions = new List<ModInfo>();
        private static readonly object _pendingLock = new object();

        private List<ModInfo> _allMods = new List<ModInfo>();
        private ModInfo _selectedMod   = null;
        private Coroutine _autoCancelCoroutine = null;
        private int _autoCancelSecondsLeft = 20;

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

        // ── BSML component reference ──────────────────────────────────────────────
        [UIObject("mod-list-container")]
        private GameObject _containerGo = null;

        [UIObject("confirm-panel")]
        private GameObject _confirmPanelGo = null;

        // ── Lifecycle ─────────────────────────────────────────────────────────────
        [UIAction("#post-parse")]
        private void OnPostParse()
        {
            Plugin.Log?.Info($"[ModManager] #post-parse. Container={(_containerGo != null ? "OK" : "NULL")}");

            // Flatten the confirm panel's canvas so it doesn't curve
            if (_confirmPanelGo != null)
            {
                foreach (var curved in _confirmPanelGo.GetComponentsInChildren<HMUI.CurvedCanvasSettings>(true))
                {
                    try { curved.SetRadius(0f); }
                    catch { /* some versions don't have SetRadius */ }
                }
            }

            LoadAndRefresh();
        }

        protected override void DidActivate(bool firstActivation, bool addedToHierarchy, bool screenSystemEnabling)
        {
            base.DidActivate(firstActivation, addedToHierarchy, screenSystemEnabling);
            Plugin.Log?.Info($"[ModManager] DidActivate first={firstActivation}");
            if (!firstActivation)
                LoadAndRefresh();
        }

        // ── Search ────────────────────────────────────────────────────────────────
        [UIValue("search-text")]
        public string SearchText { get; set; } = "";

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

        // ── Row button callbacks ──────────────────────────────────────────────────
        internal void OnDeleteClicked(ModInfo mod)
        {
            _selectedMod = mod;
            ShowConfirm(mod);
        }

        internal void OnUndoClicked(ModInfo mod)
        {
            lock (_pendingLock)
                PendingDeletions.RemoveAll(m => m.Id.Equals(mod.Id, StringComparison.OrdinalIgnoreCase));
            UpdateFooter(); BuildRows();
        }

        // ── Core ──────────────────────────────────────────────────────────────────
        private void LoadAndRefresh()
        {
            ModRegistry.Invalidate();
            _allMods = ModRegistry.GetAllMods(Plugin.GetPluginsPath());
            Plugin.Log?.Info($"[ModManager] Loaded {_allMods.Count} mods.");
            ModCountLabel = $"{_allMods.Count} mod{(_allMods.Count == 1 ? "" : "s")} installed";
            NotifyPropertyChanged(nameof(ModCountLabel));
            UpdateFooter();
            BuildRows();
        }

        private void BuildRows()
        {
            if (_containerGo == null) { Plugin.Log?.Warn("[ModManager] Container null, skipping BuildRows."); return; }
            var container = _containerGo.transform;

            foreach (Transform child in container)
                Destroy(child.gameObject);

            var pendingIds = new HashSet<string>(
                PendingDeletions.Select(m => m.Id), StringComparer.OrdinalIgnoreCase);

            // Filter by search text
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
            var rowGo = new GameObject($"Row_{mod.Id}");
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

            // Hover highlight — transparent image behind the row content
            var hoverImg = rowGo.AddComponent<Image>();
            hoverImg.color = isPending ? new Color(0.4f, 0.05f, 0.05f, 0.15f) : Color.clear;
            var hoverTrigger = rowGo.AddComponent<UnityEngine.EventSystems.EventTrigger>();
            var enterEntry = new UnityEngine.EventSystems.EventTrigger.Entry
                { eventID = UnityEngine.EventSystems.EventTriggerType.PointerEnter };
            Color hoverColor = isPending ? new Color(0.5f, 0.08f, 0.08f, 0.35f) : new Color(1f, 1f, 1f, 0.07f);
            Color normalColor = isPending ? new Color(0.4f, 0.05f, 0.05f, 0.15f) : Color.clear;
            enterEntry.callback.AddListener(_ => hoverImg.color = hoverColor);
            hoverTrigger.triggers.Add(enterEntry);
            var exitEntry = new UnityEngine.EventSystems.EventTrigger.Entry
                { eventID = UnityEngine.EventSystems.EventTriggerType.PointerExit };
            exitEntry.callback.AddListener(_ => hoverImg.color = normalColor);
            hoverTrigger.triggers.Add(exitEntry);
            string nameColor = isPending ? "#FF8888" : "#DDDDDD";
            string suffix    = isPending ? " <color=#666666>[pending delete]</color>" : "";
            var labelGo = new GameObject("Label");
            labelGo.transform.SetParent(rowGo.transform, false);
            var labelLE = labelGo.AddComponent<LayoutElement>();
            labelLE.flexibleWidth = 1; labelLE.preferredHeight = 8;
            var tmp = labelGo.AddComponent<TextMeshProUGUI>();
            tmp.text  = $"<color={nameColor}>{mod.DisplayLabel}</color>  <color=#777777>v{mod.Version}</color>{suffix}";
            tmp.fontSize = 3.5f; tmp.enableWordWrapping = false; tmp.richText = true;

            // Button — text label only, transparent background
            string btnText = isPending ? "Undo" : "Delete";
            Action btnAction = isPending ? (Action)(() => OnUndoClicked(mod)) : () => OnDeleteClicked(mod);

            var btnGo = new GameObject("Btn");
            btnGo.transform.SetParent(rowGo.transform, false);
            var btnLE = btnGo.AddComponent<LayoutElement>();
            btnLE.preferredWidth = 20; btnLE.preferredHeight = 8;

            var hitImg = btnGo.AddComponent<Image>();
            hitImg.color = Color.clear;

            var btnLabelGo = new GameObject("BtnLabel");
            btnLabelGo.transform.SetParent(btnGo.transform, false);
            var lr = btnLabelGo.AddComponent<RectTransform>();
            lr.anchorMin = Vector2.zero; lr.anchorMax = Vector2.one;
            lr.offsetMin = Vector2.zero; lr.offsetMax = Vector2.zero;
            var btnTMP = btnLabelGo.AddComponent<TextMeshProUGUI>();
            btnTMP.text      = $"<color={( isPending ? "#AAAACC" : "#CC4444")}>[{btnText}]</color>";
            btnTMP.fontSize  = 3f;
            btnTMP.alignment = TextAlignmentOptions.Center;
            btnTMP.richText  = true;
            btnTMP.raycastTarget = false;

            var btn = btnGo.AddComponent<Button>();
            btn.targetGraphic = hitImg;
            btn.onClick.AddListener(() => btnAction());
        }


        // ── Confirm panel ─────────────────────────────────────────────────────────
        private void ShowConfirm(ModInfo mod)
        {
            ConfirmTitle = $"Delete  {mod.DisplayLabel}  v{mod.Version}?";

            var req = mod.RequiredBy
                .Where(id => !PendingDeletions.Any(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            if (req.Any())
            {
                string depText = req.Count <= 3
                    ? string.Join(", ", req)
                    : string.Join(", ", req.Take(3)) + $"\nand {req.Count - 3} more";
                DependencyWarning = $"⚠ Required by: {depText}";
                DepWarnVisible    = true;
            }
            else
            {
                DependencyWarning = "";
                DepWarnVisible    = false;
            }

            ConfirmVisible = true;
            ListVisible    = false;
            NotifyPropertyChanged(nameof(ConfirmTitle));
            NotifyPropertyChanged(nameof(DependencyWarning));
            NotifyPropertyChanged(nameof(DepWarnVisible));
            NotifyPropertyChanged(nameof(ConfirmVisible));
            NotifyPropertyChanged(nameof(ListVisible));

            StopAutoCancel();
            _autoCancelCoroutine = StartCoroutine(AutoCancelCountdown());
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
            _autoCancelSecondsLeft = 20;
            while (_autoCancelSecondsLeft > 0)
            {
                AutoCancelLabel = $"Auto-cancelling in {_autoCancelSecondsLeft}s…";
                NotifyPropertyChanged(nameof(AutoCancelLabel));
                yield return new WaitForSeconds(1f);
                _autoCancelSecondsLeft--;
            }
            Plugin.Log?.Info("[ModManager] Prompt auto-cancelled.");
            _selectedMod = null;
            HideConfirm();
        }

        private void StopAutoCancel()
        {
            if (_autoCancelCoroutine != null)
            {
                StopCoroutine(_autoCancelCoroutine);
                _autoCancelCoroutine = null;
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────────
        private void CommitDeletion(ModInfo mod)
        {
            lock (_pendingLock)
                if (!PendingDeletions.Any(m => m.Id.Equals(mod.Id, StringComparison.OrdinalIgnoreCase)))
                    PendingDeletions.Add(mod);
            Plugin.Log?.Info($"[ModManager] Queued for deletion: {mod.Id}");
        }

        private void UpdateFooter()
        {
            int c = PendingDeletions.Count;
            PendingLabel   = c > 0 ? $"Pending deletion: {c} mod{(c == 1 ? "" : "s")}" : "";
            PendingVisible = c > 0;
            NotifyPropertyChanged(nameof(PendingLabel));
            NotifyPropertyChanged(nameof(PendingVisible));
        }

        // ── Static helpers (called from Plugin.OnDisable and FlowCoordinator) ──────
        internal static void ExecutePendingDeletions()
        {
            List<ModInfo> toDelete;
            lock (_pendingLock) { toDelete = new List<ModInfo>(PendingDeletions); PendingDeletions.Clear(); }
            if (!toDelete.Any()) return;
            Plugin.Log?.Info($"[ModManager] Deleting {toDelete.Count} mod(s)...");
            foreach (var mod in toDelete)
            {
                TryDeleteFile(mod.DllPath,      "DLL");
                TryDeleteFile(mod.ManifestPath, "manifest");
            }
            Plugin.Log?.Info("[ModManager] Deletion complete.");
        }

        private static void TryDeleteFile(string path, string label)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
            try
            {
                // Write a marker file — actual deletion happens on next startup via CleanupPendingDeletes
                File.WriteAllText(path + ".zs_del", "pending");
                Plugin.Log?.Info($"[ModManager] Marked for deletion ({label}): {Path.GetFileName(path)}");
            }
            catch (Exception ex)
            {
                Plugin.Log?.Error($"[ModManager] Failed to mark {label} '{Path.GetFileName(path)}': {ex.Message}");
            }
        }

        private static void RestartGame()
        {
            try
            {
                string exe  = Process.GetCurrentProcess().MainModule?.FileName;
                string args = string.Join(" ", Environment.GetCommandLineArgs().Skip(1)
                    .Select(a => a.Contains(' ') ? $"\"{a}\"" : a));
                Plugin.Log?.Info($"[ModManager] Restarting: {exe}");
                Process.Start(new ProcessStartInfo(exe, args)
                    { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe) });
                Thread.Sleep(500);
                Application.Quit();
            }
            catch (Exception ex) { Plugin.Log?.Error($"[ModManager] Restart failed: {ex.Message}"); }
        }
    }
}
