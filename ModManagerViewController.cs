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
        [UIValue("confirm-visible")]    public bool   ConfirmVisible   { get; private set; } = false;
        [UIValue("list-visible")]       public bool   ListVisible      { get; private set; } = true;
        [UIValue("dependency-warning")] public string DependencyWarning { get; private set; } = "";
        [UIValue("dep-warn-visible")]   public bool   DepWarnVisible   { get; private set; } = false;
        [UIValue("auto-cancel-label")]  public string AutoCancelLabel  { get; private set; } = "";
        [UIValue("pending-label")]      public string PendingLabel     { get; private set; } = "";
        [UIValue("pending-visible")]    public bool   PendingVisible   { get; private set; } = false;

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
            LoadAndRefresh();
        }

        protected override void DidActivate(bool firstActivation, bool addedToHierarchy, bool screenSystemEnabling)
        {
            base.DidActivate(firstActivation, addedToHierarchy, screenSystemEnabling);
            if (!firstActivation) LoadAndRefresh();
        }

        // string-setting on-change — single source of truth for search
        [UIAction("search-changed")]
        private void OnSearchChanged(string val)
        {
            _searchText = val ?? "";
            BuildRows();
            StartCoroutine(ScrollToTopNextFrame());
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
            var modsToShow = string.IsNullOrWhiteSpace(_searchText)
                ? _allMods
                : _allMods.Where(m =>
                    m.DisplayLabel.IndexOf(_searchText, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    m.Id.IndexOf(_searchText, StringComparison.OrdinalIgnoreCase) >= 0).ToList();

            foreach (var mod in modsToShow)
                BuildRow(container, mod, pendingIds.Contains(mod.Id));

            Plugin.Log?.Info($"[ModManager] Built {modsToShow.Count} rows.");
        }

        // Wait one frame so the layout has rebuilt, then scroll to top
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

        private void BuildRow(Transform parent, ModInfo mod, bool isPending)
        {
            var rowGo = new GameObject($"Row_{mod.Id}");
            rowGo.transform.SetParent(parent, false);
            var rowRect = rowGo.GetComponent<RectTransform>() ?? rowGo.AddComponent<RectTransform>();
            rowRect.sizeDelta = new Vector2(0, 10);

            var bgImg = rowGo.AddComponent<Image>();
            bgImg.sprite        = GetNoGlowSprite();
            bgImg.material      = GetNoGlowMaterial();
            bgImg.type          = Image.Type.Sliced;
            bgImg.color         = isPending ? new Color(0.35f, 0.05f, 0.05f, 0.85f) : new Color(0.1f, 0.1f, 0.15f, 0.75f);
            bgImg.raycastTarget = true;

            var layout = rowGo.AddComponent<HorizontalLayoutGroup>();
            layout.spacing = 4;
            typeof(HorizontalLayoutGroup).GetProperty("childAlignment")?.SetValue(layout, 3);
            layout.childForceExpandWidth = false; layout.childForceExpandHeight = false;
            layout.padding = new RectOffset(8, 6, 1, 1);
            var fitter = rowGo.AddComponent<ContentSizeFitter>();
            fitter.verticalFit   = ContentSizeFitter.FitMode.PreferredSize;
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;

            Color hoverCol  = isPending ? new Color(0.5f, 0.08f, 0.08f, 0.95f) : new Color(0.18f, 0.18f, 0.25f, 0.9f);
            Color normalCol = bgImg.color;
            var trigger = rowGo.AddComponent<UnityEngine.EventSystems.EventTrigger>();
            AddHover(trigger, () => bgImg.color = hoverCol, () => bgImg.color = normalCol);

            string nameColor = isPending ? "#FF9999" : "#DDDDDD";
            string suffix    = isPending ? " <color=#666666>[pending delete]</color>" : "";
            var labelGo = new GameObject("Label");
            labelGo.transform.SetParent(rowGo.transform, false);
            var lLE = labelGo.AddComponent<LayoutElement>();
            lLE.flexibleWidth = 1; lLE.preferredHeight = 8;
            var lTMP = labelGo.AddComponent<TextMeshProUGUI>();
            lTMP.text = $"<color={nameColor}>{mod.DisplayLabel}</color>  <color=#777777>v{mod.Version}</color>{suffix}";
            lTMP.fontSize = 3.5f; lTMP.enableWordWrapping = false; lTMP.richText = true;
            lTMP.fontSharedMaterial = GetNoGlowTMPMaterial(lTMP);

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

            var btnImg = btnGo.AddComponent<Image>();
            btnImg.sprite = GetNoGlowSprite(); btnImg.material = GetNoGlowMaterial();
            btnImg.type = Image.Type.Sliced; btnImg.color = color;

            var trigger = btnGo.AddComponent<UnityEngine.EventSystems.EventTrigger>();
            Color hov = new Color(Mathf.Min(color.r * 1.3f, 1f), Mathf.Min(color.g * 1.3f, 1f), Mathf.Min(color.b * 1.3f, 1f), 1f);
            AddHover(trigger, () => btnImg.color = hov, () => btnImg.color = color);

            var lblGo = new GameObject("Lbl");
            lblGo.transform.SetParent(btnGo.transform, false);
            var lr = lblGo.AddComponent<RectTransform>();
            lr.anchorMin = Vector2.zero; lr.anchorMax = Vector2.one;
            lr.offsetMin = Vector2.zero; lr.offsetMax = Vector2.zero;
            var lTMP = lblGo.AddComponent<TextMeshProUGUI>();
            lTMP.text = label; lTMP.fontSize = 3f;
            lTMP.alignment = TextAlignmentOptions.Center;
            lTMP.color = Color.white; lTMP.raycastTarget = false;
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
            ConfirmVisible = false; ListVisible = true; AutoCancelLabel = "";
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