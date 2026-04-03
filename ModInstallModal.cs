using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
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
    internal class ModInstallModal : MonoBehaviour
    {
        // ── Singleton ────────────────────────────────────────────────────────────
        private static ModInstallModal _instance;
        internal static ModInstallModal Instance
        {
            get
            {
                if (_instance == null)
                {
                    var go = new GameObject("ZipSaber_ModInstallModal");
                    DontDestroyOnLoad(go);
                    _instance = go.AddComponent<ModInstallModal>();
                }
                return _instance;
            }
        }

        // ── State ────────────────────────────────────────────────────────────────
        private FloatingScreen _screen  = null;
        private bool _bsmlParsed        = false;
        private Coroutine _autoCancelCo = null;
        private readonly List<string> _pendingModNames = new List<string>();

        // Direct TMP references updated each second — bypasses BSML live-binding
        private TextMeshProUGUI _modListTMP    = null;
        private TextMeshProUGUI _autoCancelTMP = null;

        // ── BSML initial-value properties (read once at parse) ────────────────────
        [UIValue("mod-list-label")]
        public string ModListLabel { get; private set; } = "";

        [UIValue("auto-cancel-label")]
        public string AutoCancelLabel { get; private set; } = "Auto closes in 20s…";

        public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;

        // ── Button actions ───────────────────────────────────────────────────────
        [UIAction("restart-now")]
        private void OnRestartNow()
        {
            Plugin.Log?.Info("[ModInstall] User chose Restart Now.");
            StopAutoCancel();
            HideScreen();
            RestartGame();
        }

        [UIAction("restart-later")]
        private void OnRestartLater()
        {
            Plugin.Log?.Info("[ModInstall] User chose Later.");
            StopAutoCancel();
            HideScreen();
        }

        // ── Public API ───────────────────────────────────────────────────────────
        internal void ShowForMods(List<string> modNames)
        {
            MainThreadDispatcher.Enqueue(() =>
            {
                foreach (var n in modNames)
                    if (!_pendingModNames.Contains(n)) _pendingModNames.Add(n);

                string label = BuildLabel();
                ModListLabel  = label;
                AutoCancelLabel = "Auto closes in 20s…";

                EnsureScreen();
                _screen.gameObject.SetActive(true);

                // Update TMP components directly
                if (_modListTMP    != null) _modListTMP.text    = label;
                if (_autoCancelTMP != null) _autoCancelTMP.text = "Auto closes in 20s…";

                StopAutoCancel();
                _autoCancelCo = StartCoroutine(AutoCancelCountdown());
                Plugin.Log?.Info("[ModInstall] Prompt shown.");
            });
        }

        // ── Timer ─────────────────────────────────────────────────────────────────
        private IEnumerator AutoCancelCountdown()
        {
            int secs = 20;
            while (secs > 0)
            {
                if (_autoCancelTMP != null)
                    _autoCancelTMP.text = $"Auto closes in {secs}s…";
                Plugin.Log?.Debug($"[ModInstall] Countdown: {secs}s");
                yield return new WaitForSeconds(1f);
                secs--;
            }
            Plugin.Log?.Info("[ModInstall] Auto-cancel fired.");
            if (_autoCancelTMP != null) _autoCancelTMP.text = "";
            HideScreen();
        }

        private void StopAutoCancel()
        {
            if (_autoCancelCo != null) { StopCoroutine(_autoCancelCo); _autoCancelCo = null; }
            if (_autoCancelTMP != null) _autoCancelTMP.text = "";
        }

        // ── Helpers ───────────────────────────────────────────────────────────────
        private string BuildLabel()
        {
            if (_pendingModNames.Count == 1) return _pendingModNames[0];
            if (_pendingModNames.Count <= 3) return string.Join("\n", _pendingModNames.Select(n => $"• {n}"));
            return string.Join("\n", _pendingModNames.Take(3).Select(n => $"• {n}"))
                   + $"\nand {_pendingModNames.Count - 3} more…";
        }

        private void FindTMPRefs()
        {
            if (_screen == null) return;
            _modListTMP = null; _autoCancelTMP = null;
            foreach (var tmp in _screen.GetComponentsInChildren<TextMeshProUGUI>(true))
            {
                if (tmp.text == "Auto closes in 20s…")
                    _autoCancelTMP = tmp;
                else if (tmp.text == ModListLabel && !string.IsNullOrEmpty(ModListLabel))
                    _modListTMP = tmp;
            }
            Plugin.Log?.Debug($"[ModInstall] TMP refs: modListTMP={((_modListTMP != null) ? "OK" : "null")}, cancelTMP={((_autoCancelTMP != null) ? "OK" : "null")}");
        }

        private void EnsureScreen()
        {
            if (_screen != null && _bsmlParsed) return;

            _screen = FloatingScreen.CreateFloatingScreen(
                new Vector2(100, 65),
                false,
                new Vector3(0f, 1.5f, 2.4f),
                Quaternion.Euler(0f, 0f, 0f));

            _screen.gameObject.name = "ZipSaber_ModInstallScreen";
            DontDestroyOnLoad(_screen.gameObject);
            _screen.gameObject.SetActive(false);

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

            string bsml = Utilities.GetResourceContent(
                Assembly.GetExecutingAssembly(), "ZipSaber.mod-install-modal.bsml");
            BSMLParser.instance.Parse(bsml, _screen.gameObject, this);
            _bsmlParsed = true;

            FindTMPRefs();
            Plugin.Log?.Debug("[ModInstall] FloatingScreen + BSML created.");
        }

        private void HideScreen()
        {
            if (_screen != null) _screen.gameObject.SetActive(false);
        }

        private static void RestartGame()
        {
            try
            {
                string exe  = Process.GetCurrentProcess().MainModule?.FileName;
                string args = string.Join(" ", Environment.GetCommandLineArgs().Skip(1)
                                  .Select(a => a.Contains(' ') ? $"\"{a}\"" : a));
                Process.Start(new ProcessStartInfo(exe, args)
                    { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe) });
                System.Threading.Thread.Sleep(500);
                UnityEngine.Application.Quit();
            }
            catch (Exception ex) { Plugin.Log?.Error($"[ModInstall] Restart failed: {ex.Message}"); }
        }
    }
}