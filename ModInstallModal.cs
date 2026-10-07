using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using UnityEngine;

namespace ZipSaber
{
    /// <summary>
    /// Prompt for mod DLLs dropped onto the game window:
    ///   1. "Install this mod?"  — nothing is copied until you confirm
    ///   2. "Mod installed"      — restart now or later
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

        internal class Candidate
        {
            internal string SourcePath, FileName, Id, Version, InstalledVersion;
            internal string Label => Version != "?" ? $"{Id} v{Version}" : Id;
        }

        private enum Stage { Hidden, Confirm, Installed }

        // ── State ────────────────────────────────────────────────────────────────
        private PromptUi _ui;
        private Stage _stage = Stage.Hidden;
        private Coroutine _countdownCo;
        private const float CountdownSeconds = 20f;

        private readonly List<Candidate> _toConfirm = new List<Candidate>();
        private readonly List<string> _rejected = new List<string>();
        private readonly List<string> _installed = new List<string>();

        // ── Public API ───────────────────────────────────────────────────────────
        /// <summary>Ask before installing. Safe to call from any thread.</summary>
        internal void Confirm(List<Candidate> candidates, List<string> rejectedFiles)
        {
            MainThreadDispatcher.Enqueue(() =>
            {
                foreach (var c in candidates)
                    if (!_toConfirm.Any(x => x.FileName.Equals(c.FileName, StringComparison.OrdinalIgnoreCase))) _toConfirm.Add(c);
                foreach (var r in rejectedFiles) if (!_rejected.Contains(r)) _rejected.Add(r);

                if (_toConfirm.Count == 0)
                {
                    if (_rejected.Count > 0)
                        Toast.Show($"<color=#FF6B6B>Not a Beat Saber mod:</color> {PromptUi.Esc(string.Join(", ", _rejected))}");
                    _rejected.Clear();
                    return;
                }
                // If the restart prompt is up, the new drop takes over; installed list is kept for the summary
                ShowStage(Stage.Confirm);
            });
        }

        // ── Buttons ──────────────────────────────────────────────────────────────
        private void OnPrimary()
        {
            if (_stage == Stage.Confirm) InstallConfirmed();
            else if (_stage == Stage.Installed) { Plugin.Log?.Info("[ModInstall] Restart Now."); Close(); RestartGame(); }
        }

        private void OnSecondary()
        {
            if (_stage == Stage.Confirm) CancelConfirm("Cancelled");
            else Close();
        }

        private void OnClose()
        {
            if (_stage == Stage.Confirm) CancelConfirm("Cancelled");
            else Close();
        }

        private void InstallConfirmed()
        {
            string plugins = Plugin.GetPluginsPath();
            var failed = new List<string>();
            foreach (var c in _toConfirm)
            {
                try
                {
                    File.Copy(c.SourcePath, Path.Combine(plugins, c.FileName), overwrite: true);
                    _installed.Add(c.Label);
                    Plugin.Log?.Info($"[ModInstall] Installed '{c.Label}'");
                }
                catch (Exception ex)
                {
                    failed.Add(c.FileName);
                    Plugin.Log?.Error($"[ModInstall] Copy failed '{c.FileName}': {ex.Message}");
                }
            }
            _toConfirm.Clear();
            _rejected.Clear();
            ModRegistry.Invalidate();

            if (failed.Count > 0)
                Toast.Show($"<color=#FF6B6B>Couldn't install:</color> {PromptUi.Esc(string.Join(", ", failed))}. Is the game folder writable?");

            if (_installed.Count > 0) ShowStage(Stage.Installed);
            else Close();
        }

        private void CancelConfirm(string why)
        {
            Plugin.Log?.Info($"[ModInstall] {why} - nothing installed ({_toConfirm.Count} mod(s)).");
            _toConfirm.Clear();
            _rejected.Clear();
            if (_installed.Count > 0) ShowStage(Stage.Installed);   // still owe the restart prompt
            else Close();
        }

        // ── Stages ───────────────────────────────────────────────────────────────
        private void ShowStage(Stage stage)
        {
            EnsureUi();
            _stage = stage;
            _ui.ApplyTheme();

            if (stage == Stage.Confirm)
            {
                bool many = _toConfirm.Count > 1;
                _ui.Title.text = many ? $"Install {_toConfirm.Count} mods?" : "Install this mod?";
                _ui.Body.text = ListText(_toConfirm.Select(DescribeCandidate).ToList())
                    + (_rejected.Count > 0 ? $"\n<color=#FF6B6B><size=85%>Skipped (not a Beat Saber mod): {PromptUi.Esc(string.Join(", ", _rejected))}</size></color>" : "");
                _ui.Note.text = "<color=#F2C94C>Only install mods you trust</color> - mods run code on your PC. Copied to Plugins; loads after a restart.";
                _ui.PrimaryLabel.text = many ? "INSTALL ALL" : "INSTALL";
                _ui.SecondaryLabel.text = "CANCEL";
            }
            else
            {
                _ui.Title.text = _installed.Count > 1 ? $"{_installed.Count} mods installed" : "Mod installed";
                _ui.Body.text = ListText(_installed.Select(n => $"<b>{PromptUi.Esc(n)}</b>").ToList());
                _ui.Note.text = "<color=#F2C94C>Restart required</color> - the mod won't load until Beat Saber restarts.";
                _ui.PrimaryLabel.text = "RESTART NOW";
                _ui.SecondaryLabel.text = "LATER";
            }

            _ui.Show(true);
            if (_countdownCo != null) StopCoroutine(_countdownCo);
            _countdownCo = StartCoroutine(CountdownRoutine(stage));
            Plugin.Log?.Info($"[ModInstall] Prompt: {stage}.");
        }

        private static string DescribeCandidate(Candidate c)
        {
            string s = $"<b>{PromptUi.Esc(c.Id)}</b> <color=#8A8A99>v{PromptUi.Esc(c.Version)}</color>";
            if (c.InstalledVersion != null)
                s += c.InstalledVersion == c.Version
                    ? "  <color=#9A9AA6><size=85%>reinstall (same version)</size></color>"
                    : $"  <color=#5FA8E8><size=85%>update from v{PromptUi.Esc(c.InstalledVersion)}</size></color>";
            else s += "  <color=#7ED957><size=85%>new</size></color>";
            return s;
        }

        private static string ListText(List<string> lines)
        {
            if (lines.Count <= 3) return string.Join("\n", lines);
            return string.Join("\n", lines.Take(3)) + $"\n<color=#8A8A99>and {lines.Count - 3} more...</color>";
        }

        private IEnumerator CountdownRoutine(Stage stage)
        {
            float start = Time.unscaledTime;
            while (true)
            {
                float left = CountdownSeconds - (Time.unscaledTime - start);
                if (left <= 0f) break;
                _ui.Countdown.text = stage == Stage.Confirm
                    ? $"Cancels in {Mathf.CeilToInt(left)}s if you don't pick"
                    : $"Closes in {Mathf.CeilToInt(left)}s";
                _ui.SetTimer(left / CountdownSeconds);
                yield return null;
            }
            _countdownCo = null;
            if (stage == Stage.Confirm) CancelConfirm("Timed out");   // never installs on its own
            else Close();
        }

        private void Close()
        {
            if (_countdownCo != null) { StopCoroutine(_countdownCo); _countdownCo = null; }
            _ui?.Show(false);
            _stage = Stage.Hidden;
            _installed.Clear();
        }

        private void EnsureUi()
        {
            if (_ui != null && _ui.Screen != null) return;
            _ui = PromptUi.Build("ZipSaber_ModInstallScreen", 100f, 62f, OnPrimary, OnSecondary, OnClose);
            _ui.Show(false);
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
                Application.Quit();
            }
            catch (Exception ex) { Plugin.Log?.Error($"[ModInstall] Restart failed: {ex.Message}"); }
        }
    }
}
