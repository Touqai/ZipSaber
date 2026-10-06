using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BeatSaberMarkupLanguage;
using BeatSaberMarkupLanguage.Attributes;
using BeatSaberMarkupLanguage.FloatingScreen;
using TMPro;
using UnityEngine;

namespace ZipSaber
{
    internal class DestinationModal : MonoBehaviour
    {
        // ── Singleton ────────────────────────────────────────────────────────────
        private static DestinationModal _instance;
        internal static DestinationModal Instance
        {
            get
            {
                if (_instance == null)
                {
                    var go = new GameObject("ZipSaber_DestinationModal");
                    DontDestroyOnLoad(go);
                    _instance = go.AddComponent<DestinationModal>();
                }
                return _instance;
            }
        }

        // ── State ────────────────────────────────────────────────────────────────
        private readonly Queue<PendingBatch> _queue = new Queue<PendingBatch>();
        private PendingBatch _current;
        private bool _showing    = false;
        private FloatingScreen _screen    = null;
        private bool _bsmlParsed = false;
        private Coroutine _autoCancelCo   = null;

        // Direct TMP references — set after BSML parse, updated directly (bypasses BSML binding)
        private TextMeshProUGUI _mapNameTMP    = null;
        private TextMeshProUGUI _autoCancelTMP = null;

        private struct PendingBatch
        {
            public List<string> ZipPaths;
            public string DisplayLabel;
        }

        // ── BSML initial-value properties (read once at parse time) ───────────────
        [UIValue("map-name-label")]
        public string MapNameLabel { get; private set; } = "";

        [UIValue("auto-cancel-label")]
        public string AutoCancelLabel { get; private set; } = "Auto closes in 20s…";

        // Required by BSML even if we don't use it for live updates
        public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;

        // ── Button actions ───────────────────────────────────────────────────────
        [UIAction("destination-wip")]
        private void OnChooseWip()
        {
            Plugin.Log?.Info("[Modal] User chose CustomWipLevels.");
            StopAutoCancel();
            var batch = _current; _current = default;
            HideScreen();
            Plugin.Instance?.ProcessDroppedFilesBatchToTarget(batch.ZipPaths, wip: true);
            StartCoroutine(DelayedAdvance());
        }

        [UIAction("destination-custom")]
        private void OnChooseCustom()
        {
            Plugin.Log?.Info("[Modal] User chose CustomLevels.");
            StopAutoCancel();
            var batch = _current; _current = default;
            HideScreen();
            Plugin.Instance?.ProcessDroppedFilesBatchToTarget(batch.ZipPaths, wip: false);
            StartCoroutine(DelayedAdvance());
        }

        [UIAction("destination-cancel")]
        private void OnCancel()
        {
            Plugin.Log?.Info("[Modal] User dismissed — map not imported.");
            StopAutoCancel();
            _current = default;
            HideScreen();
            StartCoroutine(DelayedAdvance());
        }

        // ── Public API ───────────────────────────────────────────────────────────
        internal void EnqueueBatch(List<string> zipPaths)
        {
            var names = zipPaths.Select(p => Path.GetFileNameWithoutExtension(p)).ToList();
            string label = names.Count == 1
                ? names[0]
                : names.Count <= 3
                    ? string.Join("\n", names)
                    : string.Join("\n", names.Take(3)) + $"\nand {names.Count - 3} more…";

            MainThreadDispatcher.Enqueue(() =>
            {
                _queue.Enqueue(new PendingBatch { ZipPaths = zipPaths, DisplayLabel = label });
                if (!_showing) ShowNext();
            });
        }

        // ── Internal helpers ─────────────────────────────────────────────────────
        private void ShowNext()
        {
            if (_queue.Count == 0) { _showing = false; return; }
            _current = _queue.Dequeue();
            _showing = true;

            try
            {
                EnsureScreen();
                _screen.gameObject.SetActive(true);

                // Update TMP components directly — reliable regardless of BSML binding
                if (_mapNameTMP    != null) _mapNameTMP.text    = _current.DisplayLabel;
                if (_autoCancelTMP != null) _autoCancelTMP.text = "Auto closes in 20s…";

                StopAutoCancel();
                _autoCancelCo = StartCoroutine(AutoCancelCountdown());
                Plugin.Log?.Info("[Modal] Floating screen shown.");
            }
            catch (Exception ex)
            {
                Plugin.Log?.Error($"[Modal] Show failed: {ex.Message}\n{ex}");
                _current = default; _showing = false;
                StartCoroutine(DelayedAdvance());
            }
        }

        private IEnumerator AutoCancelCountdown()
        {
            int secs = 20;
            while (secs > 0)
            {
                if (_autoCancelTMP != null)
                    _autoCancelTMP.text = $"Auto closes in {secs}s…";
                Plugin.Log?.Debug($"[Modal] Countdown: {secs}s");
                yield return new WaitForSeconds(1f);
                secs--;
            }
            Plugin.Log?.Info("[Modal] Auto-cancel fired — dismissing without action.");
            if (_autoCancelTMP != null) _autoCancelTMP.text = "";
            _current = default;
            HideScreen();
            StartCoroutine(DelayedAdvance());
        }

        private void StopAutoCancel()
        {
            if (_autoCancelCo != null) { StopCoroutine(_autoCancelCo); _autoCancelCo = null; }
            if (_autoCancelTMP != null) _autoCancelTMP.text = "";
        }

        // ── BSML post-parse: grab TMP references ──────────────────────────────────
        [UIAction("#post-parse")]
        private void OnPostParse()
        {
            // Walk all TMP components on the screen and identify by their initial text
            // (set from UIValue at parse time)
            if (_screen == null) return;
            foreach (var tmp in _screen.GetComponentsInChildren<TextMeshProUGUI>(true))
            {
                if (tmp.text == "" || tmp.text == _current.DisplayLabel || tmp.text == MapNameLabel)
                {
                    // Try to identify by GameObject name set in BSML
                    if (tmp.gameObject.name.Contains("map-name") ||
                        tmp.transform.parent?.gameObject.name.Contains("map-name") == true)
                        _mapNameTMP = tmp;
                    else if (tmp.gameObject.name.Contains("auto-cancel") ||
                             tmp.transform.parent?.gameObject.name.Contains("auto-cancel") == true)
                        _autoCancelTMP = tmp;
                }
                else if (tmp.text == "Auto closes in 20s…")
                    _autoCancelTMP = tmp;
            }
            Plugin.Log?.Debug($"[Modal] Post-parse: mapTMP={((_mapNameTMP != null) ? "OK" : "null")}, cancelTMP={((_autoCancelTMP != null) ? "OK" : "null")}");
        }

        private void EnsureScreen()
        {
            if (_screen != null && _bsmlParsed) return;

            _screen = FloatingScreen.CreateFloatingScreen(
                new Vector2(100, 65),
                false,
                new Vector3(0f, 1.5f, 2.4f),
                Quaternion.Euler(0f, 0f, 0f));

            _screen.gameObject.name = "ZipSaber_PromptScreen";
            DontDestroyOnLoad(_screen.gameObject);

            foreach (var canvas in _screen.GetComponentsInChildren<Canvas>(true))
            {
                canvas.overrideSorting = true;
                canvas.sortingOrder    = 32767;
            }
            foreach (var canvas in _screen.GetComponentsInChildren<Canvas>(true))
                if (canvas.GetComponent<UnityEngine.EventSystems.BaseRaycaster>() == null)
                    canvas.gameObject.AddComponent<UnityEngine.UI.GraphicRaycaster>();

            var blockerGo = new GameObject("ClickBlocker");
            blockerGo.transform.SetParent(_screen.transform, false);
            var br = blockerGo.AddComponent<RectTransform>();
            br.anchorMin = Vector2.zero; br.anchorMax = Vector2.one;
            br.offsetMin = new Vector2(-500, -500); br.offsetMax = new Vector2(500, 500);
            var bi = blockerGo.AddComponent<UnityEngine.UI.Image>();
            bi.color = Color.clear; bi.raycastTarget = true;
            blockerGo.transform.SetAsFirstSibling();

            // Set initial values before parse so BSML reads them correctly
            MapNameLabel    = _current.DisplayLabel;
            AutoCancelLabel = "Auto closes in 20s…";

            string bsml = Utilities.GetResourceContent(
                Assembly.GetExecutingAssembly(), "ZipSaber.destination-modal.bsml");
            BSMLParser.Instance.Parse(bsml, _screen.gameObject, this);
            _bsmlParsed = true;

            // After parse: find TMP refs by their current text content
            FindTMPRefs();

            Plugin.Log?.Debug("[Modal] FloatingScreen + BSML created.");
        }

        private void FindTMPRefs()
        {
            if (_screen == null) return;
            _mapNameTMP = null; _autoCancelTMP = null;
            foreach (var tmp in _screen.GetComponentsInChildren<TextMeshProUGUI>(true))
            {
                if (tmp.text == "Auto closes in 20s…")
                    _autoCancelTMP = tmp;
                else if (tmp.text == MapNameLabel && !string.IsNullOrEmpty(MapNameLabel))
                    _mapNameTMP = tmp;
            }
            Plugin.Log?.Debug($"[Modal] TMP refs: mapTMP={((_mapNameTMP != null) ? "OK" : "null")}, cancelTMP={((_autoCancelTMP != null) ? "OK" : "null")}");
        }

        private void HideScreen()
        {
            if (_screen != null) _screen.gameObject.SetActive(false);
            _showing = false;
        }

        private IEnumerator DelayedAdvance()
        {
            yield return new WaitForSeconds(0.35f);
            if (_queue.Count > 0) ShowNext();
        }
    }
}