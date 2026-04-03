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
using UnityEngine;

namespace ZipSaber
{
    /// <summary>
    /// Shows a floating screen telling the user that one or more mods were installed
    /// and offering to restart Beat Saber immediately to load them.
    /// </summary>
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

        // Accumulate mods installed during the session so the "Later" path still
        // lets subsequent drops add to the pending list without re-prompting.
        private readonly List<string> _pendingModNames = new List<string>();

        // ── BSML property ────────────────────────────────────────────────────────
        private string _modListLabel = "";

        [UIValue("mod-list-label")]
        public string ModListLabel
        {
            get => _modListLabel;
            set
            {
                _modListLabel = value;
                PropertyChanged?.Invoke(this,
                    new System.ComponentModel.PropertyChangedEventArgs(nameof(ModListLabel)));
            }
        }
        public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;

        // ── Button actions ───────────────────────────────────────────────────────
        [UIAction("restart-now")]
        private void OnRestartNow()
        {
            Plugin.Log?.Info("[ModInstall] User chose Restart Now.");
            HideScreen();
            RestartGame();
        }

        [UIAction("restart-later")]
        private void OnRestartLater()
        {
            Plugin.Log?.Info("[ModInstall] User chose Later.");
            HideScreen();
            // Leave _pendingModNames intact so if more mods are dropped,
            // the next prompt includes them all.
        }

        // ── Public API ───────────────────────────────────────────────────────────
        /// <summary>
        /// Show the prompt for one or more newly-installed mods.
        /// Safe to call from any thread.
        /// </summary>
        internal void ShowForMods(List<string> modNames)
        {
            MainThreadDispatcher.Enqueue(() =>
            {
                foreach (var n in modNames)
                    if (!_pendingModNames.Contains(n)) _pendingModNames.Add(n);

                BuildLabel();
                EnsureScreen();
                _screen.gameObject.SetActive(true);
                Plugin.Log?.Info("[ModInstall] Prompt shown.");
            });
        }

        // ── Internal helpers ─────────────────────────────────────────────────────
        private void BuildLabel()
        {
            if (_pendingModNames.Count == 1)
                ModListLabel = _pendingModNames[0];
            else
                ModListLabel = string.Join("\n", _pendingModNames.Select(n => $"• {n}"));
        }

        private void EnsureScreen()
        {
            if (_screen != null && _bsmlParsed) return;

            _screen = FloatingScreen.CreateFloatingScreen(
                new Vector2(100, 60),
                false,
                new Vector3(0f, 1.5f, 2.4f),
                Quaternion.Euler(0f, 0f, 0f));

            _screen.gameObject.name = "ZipSaber_ModInstallScreen";
            DontDestroyOnLoad(_screen.gameObject);
            _screen.gameObject.SetActive(false);

            var canvas = _screen.GetComponent<Canvas>() ?? _screen.GetComponentInChildren<Canvas>();
            if (canvas != null) { canvas.overrideSorting = true; canvas.sortingOrder = 32767; }
            if (_screen.GetComponent<UnityEngine.EventSystems.BaseRaycaster>() == null)
                _screen.gameObject.AddComponent<UnityEngine.UI.GraphicRaycaster>();

            string bsml = Utilities.GetResourceContent(
                Assembly.GetExecutingAssembly(), "ZipSaber.mod-install-modal.bsml");
            BSMLParser.Instance.Parse(bsml, _screen.gameObject, this);
            _bsmlParsed = true;
            Plugin.Log?.Debug("[ModInstall] FloatingScreen + BSML created.");
        }

        private void HideScreen()
        {
            if (_screen != null)
                _screen.gameObject.SetActive(false);
        }

        /// <summary>
        /// Relaunch Beat Saber. Grabs the current exe path, starts a new process,
        /// then quits this one.
        /// </summary>
        private static void RestartGame()
        {
            try
            {
                string exe = Process.GetCurrentProcess().MainModule?.FileName;
                if (string.IsNullOrEmpty(exe))
                {
                    Plugin.Log?.Error("[ModInstall] Could not get exe path for restart.");
                    return;
                }

                Plugin.Log?.Info($"[ModInstall] Restarting: {exe}");

                // Get the original launch arguments so VR mode etc. are preserved
                string args = GetLaunchArgs();

                var psi = new ProcessStartInfo(exe, args)
                {
                    UseShellExecute  = true,
                    WorkingDirectory = Path.GetDirectoryName(exe)
                };
                Process.Start(psi);

                // Give the new process a moment to start, then exit this one
                System.Threading.Thread.Sleep(500);
                UnityEngine.Application.Quit();
            }
            catch (Exception ex)
            {
                Plugin.Log?.Error($"[ModInstall] Restart failed: {ex.Message}\n{ex}");
            }
        }

        /// <summary>
        /// Attempt to reconstruct launch arguments from the current process.
        /// Falls back to empty string if not available.
        /// </summary>
        private static string GetLaunchArgs()
        {
            try
            {
                // Environment.GetCommandLineArgs()[0] is the exe, rest are args
                var allArgs = Environment.GetCommandLineArgs();
                if (allArgs.Length <= 1) return string.Empty;

                // Rejoin all args after the exe, quoting ones with spaces
                var parts = allArgs.Skip(1).Select(a => a.Contains(' ') ? $"\"{a}\"" : a);
                return string.Join(" ", parts);
            }
            catch { return string.Empty; }
        }
    }
}
