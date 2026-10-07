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
        private Coroutine _autoCancelCo   = null;

        // Code-built UI (flat, matches the Mod Manager)
        private TextMeshProUGUI _mapNameTMP    = null;
        private TextMeshProUGUI _autoCancelTMP = null;
        private TextMeshProUGUI _wipSubTMP     = null;
        private UnityEngine.UI.Image _timerFill = null;
        private UnityEngine.UI.Image _accentBar, _tagBorder;
        private TextMeshProUGUI _tagTMP;
        private float _countdownStart;
        private const float CountdownSeconds = 20f;

        private struct PendingBatch
        {
            public List<string> ZipPaths;
            public string DisplayLabel;
        }

        // ── Button actions ───────────────────────────────────────────────────────
        private void OnChooseWip()
        {
            Plugin.Log?.Info("[Modal] User chose CustomWipLevels.");
            StopAutoCancel();
            var batch = _current; _current = default;
            HideScreen();
            Plugin.Instance?.ProcessDroppedFilesBatchToTarget(batch.ZipPaths, wip: true);
            StartCoroutine(DelayedAdvance());
        }

        private void OnChooseCustom()
        {
            Plugin.Log?.Info("[Modal] User chose CustomLevels.");
            StopAutoCancel();
            var batch = _current; _current = default;
            HideScreen();
            Plugin.Instance?.ProcessDroppedFilesBatchToTarget(batch.ZipPaths, wip: false);
            StartCoroutine(DelayedAdvance());
        }

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

                RefreshContent();

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
            _countdownStart = Time.unscaledTime;
            while (true)
            {
                float left = CountdownSeconds - (Time.unscaledTime - _countdownStart);
                if (left <= 0f) break;
                if (_autoCancelTMP != null) _autoCancelTMP.text = $"Closes in {Mathf.CeilToInt(left)}s if you don't pick";
                if (_timerFill != null) _timerFill.rectTransform.anchorMax = new Vector2(Mathf.Clamp01(left / CountdownSeconds), 1f);
                yield return null;
            }
            Plugin.Log?.Info("[Modal] Auto-cancel fired — dismissing without action.");
            _current = default;
            HideScreen();
            StartCoroutine(DelayedAdvance());
        }

        private void StopAutoCancel()
        {
            if (_autoCancelCo != null) { StopCoroutine(_autoCancelCo); _autoCancelCo = null; }
        }

        // ── UI ────────────────────────────────────────────────────────────────────
        private void RefreshContent()
        {
            int count = _current.ZipPaths?.Count ?? 0;
            if (_mapNameTMP != null)
                _mapNameTMP.text = (count > 1 ? $"<color=#9A9AA6>{count} maps</color>\n" : "") + Esc(_current.DisplayLabel);

            if (_wipSubTMP != null)
            {
                string folder = WipFolder.UsingCustom ? Shorten(WipFolder.Current) : "CustomWipLevels";
                bool del = Plugin.Config?.DeleteOnClose ?? false;
                _wipSubTMP.text = $"{Esc(folder)}\n" + (del
                    ? "<color=#F2C94C>Deleted when the game closes</color>"
                    : "<color=#7E7E8C>Kept after closing</color>");
            }

            var a = Theme.Accent;
            if (_accentBar != null) _accentBar.color = a;
            if (_tagTMP != null) _tagTMP.color = a;
            if (_tagBorder != null) _tagBorder.color = new Color(a.r, a.g, a.b, 0.35f);
            if (_timerFill != null) _timerFill.color = a;
            if (_autoCancelTMP != null) _autoCancelTMP.text = $"Closes in {Mathf.CeilToInt(CountdownSeconds)}s if you don't pick";
        }

        private static string Esc(string s) => "<noparse>" + (s ?? "").Replace("</noparse>", "") + "</noparse>";

        private static string Shorten(string path)
        {
            if (string.IsNullOrEmpty(path) || path.Length <= 42) return path;
            return "..." + path.Substring(path.Length - 39);
        }

        private void EnsureScreen()
        {
            if (_screen != null) return;

            const float W = 100f, H = 62f;
            _screen = FloatingScreen.CreateFloatingScreen(new Vector2(W, H), false,
                new Vector3(0f, 1.5f, 2.4f), Quaternion.identity, 0f, false);   // flat
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

            var root = _screen.transform;

            // Swallow clicks around the prompt so you don't hit the menu behind it
            var blocker = UiKit.Img(root, "ClickBlocker", UiKit.RoundSprite, Color.clear);
            blocker.raycastTarget = true;
            blocker.rectTransform.anchorMin = Vector2.zero; blocker.rectTransform.anchorMax = Vector2.one;
            blocker.rectTransform.offsetMin = new Vector2(-500, -500); blocker.rectTransform.offsetMax = new Vector2(500, 500);

            // Panel
            _tagBorder = UiKit.Img(root, "Border", UiKit.RoundSprite, Color.white, sliced: true);
            UiKit.Stretch(_tagBorder.rectTransform, -0.4f);
            var panel = UiKit.Img(root, "Panel", UiKit.RoundSprite, new Color(0.06f, 0.06f, 0.09f, 0.98f), sliced: true);
            UiKit.Stretch(panel.rectTransform);
            panel.raycastTarget = true;
            var p = panel.transform;

            // Header: accent bar, ZIPSABER tag, title, close button
            _accentBar = UiKit.Img(p, "AccentBar", UiKit.RoundSprite, Theme.Accent, sliced: true);
            Place(_accentBar.rectTransform, 3f, H - 4f, 0.9f, 9f);

            _tagTMP = UiKit.Text(p, "Tag", "ZIPSABER", 2.4f, Theme.Accent, TextAlignmentOptions.BottomLeft);
            _tagTMP.fontStyle = FontStyles.Bold;
            Place(_tagTMP.rectTransform, 6f, H - 4f, 60f, 3.5f);

            var title = UiKit.Text(p, "Title", "Where should this map go?", 4.6f, Color.white, TextAlignmentOptions.TopLeft);
            title.fontStyle = FontStyles.Bold;
            Place(title.rectTransform, 6f, H - 7.5f, 76f, 6f);

            var close = UiKit.Button(p, "Close", new Color(0.16f, 0.16f, 0.21f, 0.95f), 8, 8, OnCancel, "X", 3.6f);
            Place(close.rectTransform, W - 12f, H - 4f, 8f, 8f);

            // Map name(s)
            _mapNameTMP = UiKit.Text(p, "MapName", "", 3f, new Color(0.85f, 0.85f, 0.9f), TextAlignmentOptions.TopLeft);
            _mapNameTMP.enableWordWrapping = true;
            _mapNameTMP.fontStyle = FontStyles.Italic;
            _mapNameTMP.overflowMode = TextOverflowModes.Ellipsis;
            Place(_mapNameTMP.rectTransform, 6f, H - 16f, W - 12f, 10f);

            // Choice cards
            const float cardW = 42f, cardH = 20f, cardTop = 32f;
            var wip = Card(p, "WipCard", 6f, cardTop, cardW, cardH, "WIP LEVELS", out _wipSubTMP, OnChooseWip);
            Card(p, "CustomCard", W - 6f - cardW, cardTop, cardW, cardH, "CUSTOM LEVELS", out var customSub, OnChooseCustom);
            customSub.text = "CustomLevels\n<color=#7E7E8C>Kept permanently</color>";

            // Countdown text + bar
            _autoCancelTMP = UiKit.Text(p, "Countdown", "", 2.5f, new Color(0.55f, 0.55f, 0.62f), TextAlignmentOptions.BottomLeft);
            Place(_autoCancelTMP.rectTransform, 6f, 9f, 70f, 4f);

            var track = UiKit.Img(p, "TimerTrack", UiKit.RoundSprite, new Color(1f, 1f, 1f, 0.08f), sliced: true);
            Place(track.rectTransform, 6f, 4.5f, W - 12f, 1.2f);
            _timerFill = UiKit.Img(track.transform, "Fill", UiKit.RoundSprite, Theme.Accent, sliced: true);
            var fr = _timerFill.rectTransform;
            fr.anchorMin = Vector2.zero; fr.anchorMax = Vector2.one; fr.offsetMin = fr.offsetMax = Vector2.zero;

            Plugin.Log?.Debug("[Modal] Prompt screen built.");
        }

        /// <summary>A big clickable choice: bold title + two-line subtitle.</summary>
        private static UnityEngine.UI.Image Card(Transform parent, string name, float x, float top, float w, float h,
                                                 string title, out TextMeshProUGUI sub, Action onClick)
        {
            var bg = UiKit.Button(parent, name, new Color(0.13f, 0.13f, 0.18f, 1f), w, h, onClick);
            Place(bg.rectTransform, x, top, w, h);

            var t = UiKit.Text(bg.transform, "Title", title, 3.6f, Color.white, TextAlignmentOptions.TopLeft);
            t.fontStyle = FontStyles.Bold; t.characterSpacing = 3;
            var tr = t.rectTransform;
            tr.anchorMin = new Vector2(0, 1); tr.anchorMax = new Vector2(1, 1);
            tr.offsetMin = new Vector2(3f, -8f); tr.offsetMax = new Vector2(-3f, -2.5f);

            sub = UiKit.Text(bg.transform, "Sub", "", 2.5f, new Color(0.72f, 0.72f, 0.78f), TextAlignmentOptions.TopLeft);
            sub.enableWordWrapping = true;
            var sr = sub.rectTransform;
            sr.anchorMin = Vector2.zero; sr.anchorMax = Vector2.one;
            sr.offsetMin = new Vector2(3f, 2f); sr.offsetMax = new Vector2(-3f, -9f);
            return bg;
        }

        /// <summary>Position by top-left corner in panel units (origin bottom-left).</summary>
        private static void Place(RectTransform rt, float x, float top, float w, float h)
        {
            rt.anchorMin = rt.anchorMax = Vector2.zero;
            rt.pivot = new Vector2(0f, 1f);
            rt.sizeDelta = new Vector2(w, h);
            rt.anchoredPosition = new Vector2(x, top);
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