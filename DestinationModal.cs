using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using BeatSaberMarkupLanguage;
using BeatSaberMarkupLanguage.Attributes;
using BeatSaberMarkupLanguage.FloatingScreen;
using UnityEngine;

namespace ZipSaber
{
    /// <summary>
    /// Shows a floating BSML screen asking the user to choose a destination for dropped maps.
    /// Uses FloatingScreen which works independently of the HMUI screen hierarchy.
    /// </summary>
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
        private bool _showing = false;
        private FloatingScreen _screen = null;
        private bool _bsmlParsed = false;

        private struct PendingBatch
        {
            public List<string> ZipPaths;
            public string DisplayLabel;
        }

        // ── BSML bound property ──────────────────────────────────────────────────
        private string _mapNameLabel = "";

        [UIValue("map-name-label")]
        public string MapNameLabel
        {
            get => _mapNameLabel;
            set
            {
                _mapNameLabel = value;
                PropertyChanged?.Invoke(this,
                    new System.ComponentModel.PropertyChangedEventArgs(nameof(MapNameLabel)));
            }
        }
        public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;

        // ── Button actions ───────────────────────────────────────────────────────
        [UIAction("destination-wip")]
        private void OnChooseWip()
        {
            Plugin.Log?.Info("[Modal] User chose CustomWipLevels.");
            var batch = _current;
            _current = default;
            HideScreen();
            Plugin.Instance?.ProcessDroppedFilesBatchToTarget(batch.ZipPaths, wip: true);
            StartCoroutine(DelayedAdvance());
        }

        [UIAction("destination-custom")]
        private void OnChooseCustom()
        {
            Plugin.Log?.Info("[Modal] User chose CustomLevels.");
            var batch = _current;
            _current = default;
            HideScreen();
            Plugin.Instance?.ProcessDroppedFilesBatchToTarget(batch.ZipPaths, wip: false);
            StartCoroutine(DelayedAdvance());
        }

        // ── Public API ───────────────────────────────────────────────────────────
        internal void EnqueueBatch(List<string> zipPaths)
        {
            string label = zipPaths.Count == 1
                ? System.IO.Path.GetFileNameWithoutExtension(zipPaths[0])
                : $"{zipPaths.Count} maps";

            MainThreadDispatcher.Enqueue(() =>
            {
                _queue.Enqueue(new PendingBatch { ZipPaths = zipPaths, DisplayLabel = label });
                if (!_showing)
                    ShowNext();
            });
        }

        // ── Internal helpers ─────────────────────────────────────────────────────
        private void ShowNext()
        {
            if (_queue.Count == 0) { _showing = false; return; }

            _current = _queue.Dequeue();
            _showing = true;
            MapNameLabel = _current.DisplayLabel;

            try
            {
                EnsureScreen();
                // Update label if already parsed
                NotifyPropertyChanged(nameof(MapNameLabel));
                _screen.gameObject.SetActive(true);
                Plugin.Log?.Info("[Modal] Floating screen shown.");
            }
            catch (Exception ex)
            {
                Plugin.Log?.Error($"[Modal] Show failed: {ex.Message}\n{ex}");
                FallbackToWip();
            }
        }

        private void EnsureScreen()
        {
            if (_screen != null && _bsmlParsed) return;

            _screen = FloatingScreen.CreateFloatingScreen(
                new Vector2(100, 55),
                false,
                new Vector3(0f, 1.5f, 2.4f),
                Quaternion.Euler(0f, 0f, 0f));

            _screen.gameObject.name = "ZipSaber_PromptScreen";
            DontDestroyOnLoad(_screen.gameObject);

            // Force this canvas to render on top of everything — fixes click-through
            // and playlist icons appearing above the prompt
            var canvas = _screen.GetComponent<Canvas>() ?? _screen.GetComponentInChildren<Canvas>();
            if (canvas != null)
            {
                canvas.overrideSorting = true;
                canvas.sortingOrder    = 32767; // maximum sorting order
            }
            // Also add a GraphicRaycaster so clicks are consumed by this screen
            if (_screen.GetComponent<UnityEngine.EventSystems.BaseRaycaster>() == null)
                _screen.gameObject.AddComponent<UnityEngine.UI.GraphicRaycaster>();

            string bsml = Utilities.GetResourceContent(
                Assembly.GetExecutingAssembly(), "ZipSaber.destination-modal.bsml");
            BSMLParser.Instance.Parse(bsml, _screen.gameObject, this);
            _bsmlParsed = true;
            Plugin.Log?.Debug("[Modal] FloatingScreen + BSML created.");
        }

        private void HideScreen()
        {
            if (_screen != null)
                _screen.gameObject.SetActive(false);
            _showing = false;
        }

        private void FallbackToWip()
        {
            _showing = false;
            var batch = _current;
            _current = default;
            Plugin.Log?.Warn("[Modal] Falling back to CustomWipLevels.");
            Plugin.Instance?.ProcessDroppedFilesBatchToTarget(batch.ZipPaths, wip: true);
            StartCoroutine(DelayedAdvance());
        }

        private void NotifyPropertyChanged(string name)
        {
            PropertyChanged?.Invoke(this,
                new System.ComponentModel.PropertyChangedEventArgs(name));
        }

        private IEnumerator DelayedAdvance()
        {
            yield return new WaitForSeconds(0.35f);
            if (_queue.Count > 0) ShowNext();
        }
    }
}
