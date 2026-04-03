using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using IPA;
using IPA.Config;
using IPA.Config.Stores;
using IPA.Logging;
using SongCore;
using UnityEngine;
using BeatSaberMarkupLanguage.Settings;
using BeatSaberMarkupLanguage.Util;
using BeatSaberMarkupLanguage.MenuButtons;

#nullable disable

namespace ZipSaber
{
    [Plugin(RuntimeOptions.SingleStartInit)]
    public class Plugin
    {
        #region Constants
        private const string UnityWindowClass         = "UnityWndClass";
        private const string BeatSaberWindowTitle     = "Beat Saber";
        private const string CustomWipLevelsFolderName = "CustomWipLevels";
        private const string CustomLevelsFolderName    = "CustomLevels";
        private const string BeatSaberDataFolderName   = "Beat Saber_Data";
        #endregion

        internal static Plugin Instance { get; private set; }
        internal static PluginConfig Config { get; private set; }
        internal static IPA.Logging.Logger Log { get; private set; }

        private static List<string> _importedWipFoldersThisSession = new List<string>();
        private static readonly object _folderListLock = new object();

        #region WinAPI
        private const int  GWLP_WNDPROC  = -4;
        private const uint WM_DROPFILES  = 0x233;
        internal delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll",  SetLastError = true, CharSet = CharSet.Auto)] private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
        [DllImport("user32.dll",  SetLastError = true, CharSet = CharSet.Auto)] private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern void DragAcceptFiles(IntPtr hWnd, bool fAccept);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern uint DragQueryFile(IntPtr hDrop, uint iFile, [Out] StringBuilder lpszFile, uint cch);
        [DllImport("shell32.dll")] private static extern void DragFinish(IntPtr hDrop);
        [DllImport("user32.dll",  CharSet = CharSet.Auto)] private static extern IntPtr DefWindowProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll",  SetLastError = true, CharSet = CharSet.Auto)] private static extern IntPtr FindWindow(string lpClassName, string lpWindowName);
        #endregion

        private static IntPtr          Hwnd           = IntPtr.Zero;
        private static WndProcDelegate wndProcDelegate;
        private static IntPtr          oldWndProc     = IntPtr.Zero;
        private static string          CustomWipLevelsPath;
        private static string          CustomLevelsPath;
        private static string          PluginsPath;
        private static string          InstallDirPath;   // cached so WIP path can be recalculated without re-deriving from dataPath
        private static bool            hooksAttempted = false;
        private static bool            hooksActive    = false;

        [Init]
        public Plugin(IPA.Config.Config conf, IPA.Logging.Logger logger)
        {
            Instance = this;
            Log      = logger;
            Config   = conf.Generated<PluginConfig>();
            Log.Info("Initializing ZipSaber...");
            lock (_folderListLock) { _importedWipFoldersThisSession.Clear(); }
            CleanupPendingDeletes();
        }

        private void CleanupPendingDeletes()
        {
            try
            {
                string pluginsDir = System.IO.Path.GetDirectoryName(
                    System.Reflection.Assembly.GetExecutingAssembly().Location);
                Log.Info($"[Cleanup] Scanning: {pluginsDir}");
                var markerFiles = System.IO.Directory.GetFiles(pluginsDir, "*.zs_del");
                if (markerFiles.Length > 0)
                    Log.Warn($"[Cleanup] {markerFiles.Length} marker(s) still present.");
                string batPath = System.IO.Path.Combine(pluginsDir, "zs_cleanup.bat");
                if (System.IO.File.Exists(batPath))
                    try { System.IO.File.Delete(batPath); } catch { }
            }
            catch (Exception ex) { Log.Warn($"[Cleanup] Scan failed: {ex.Message}"); }
        }

        [OnEnable]
        public void OnEnable()
        {
            Log.Info("OnEnable called.");
            CalculatePaths();

            // Guard against duplicate subscription across OnEnable calls
            MainMenuAwaiter.MainMenuInitializing -= RegisterBSMLSettings;
            MainMenuAwaiter.MainMenuInitializing += RegisterBSMLSettings;

            if (!hooksAttempted && !hooksActive)
            {
                Log.Info("Scheduling delayed hook setup.");
                hooksAttempted = true;
                Task.Run(async () => { await Task.Delay(3000); AttemptFindAndHookWindow(); });
            }
        }

        [OnDisable]
        public void OnDisable()
        {
            Log.Info("OnDisable called.");
            CleanupWindowHook();
            hooksAttempted = false;
            MainMenuAwaiter.MainMenuInitializing -= RegisterBSMLSettings;

            bool shouldDelete = Config?.DeleteOnClose ?? false;
            if (shouldDelete)
            {
                List<string> foldersToDelete;
                lock (_folderListLock)
                {
                    foldersToDelete = new List<string>(_importedWipFoldersThisSession);
                    _importedWipFoldersThisSession.Clear();
                }
                int deleteCount = 0;
                foreach (string fp in foldersToDelete) { if (TryDeleteDirectory(fp)) deleteCount++; }
                Log.Info($"Deleted {deleteCount} WIP folder(s).");
            }
            else { lock (_folderListLock) { _importedWipFoldersThisSession.Clear(); } }

            ModManagerViewController.ExecutePendingDeletions();
            ModManagerViewController.ExecutePendingInstalls(BeatModsBrowserViewController.DownloadUrlsByName);
            LaunchPostExitCleanup();
        }

        public void LaunchPostExitCleanupPublic() => LaunchPostExitCleanup();

        private void LaunchPostExitCleanup()
        {
            try
            {
                string pluginsDir = System.IO.Path.GetDirectoryName(
                    System.Reflection.Assembly.GetExecutingAssembly().Location);
                var markerFiles = System.IO.Directory.GetFiles(pluginsDir, "*.zs_del");
                if (markerFiles.Length == 0) return;

                int pid = System.Diagnostics.Process.GetCurrentProcess().Id;
                string batPath = System.IO.Path.Combine(pluginsDir, "zs_cleanup.bat");
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("@echo off");
                sb.AppendLine(":wait");
                sb.AppendLine($"tasklist /FI \"PID eq {pid}\" 2>nul | find \"{pid}\" >nul");
                sb.AppendLine($"if not errorlevel 1 (timeout /t 2 /nobreak >nul & goto wait)");
                foreach (string marker in markerFiles)
                {
                    string target = marker.Substring(0, marker.Length - ".zs_del".Length);
                    sb.AppendLine($"del /f /q \"{target}\"");
                    sb.AppendLine($"del /f /q \"{marker}\"");
                }
                sb.AppendLine($"del /f /q \"{batPath}\"");
                System.IO.File.WriteAllText(batPath, sb.ToString());
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "cmd.exe", Arguments = $"/C \"{batPath}\"",
                    UseShellExecute = false, CreateNoWindow = true
                });
                Log.Info($"[Cleanup] Post-exit cleanup launched for {markerFiles.Length} file(s).");
            }
            catch (Exception ex) { Log.Warn($"[Cleanup] LaunchPostExitCleanup failed: {ex.Message}"); }
        }

        // Fires on EVERY MainMenuInitializing (initial load + every scene reload after settings Ok).
        // Do NOT unsubscribe inside here — must keep firing on every reload.
        private void RegisterBSMLSettings()
        {
            try
            {
                // Use the persistent singleton — do NOT reset/recreate it.
                // The VC reads fresh values from Plugin.Config in DidActivate.
                BSMLSettings.Instance?.AddSettingsMenu(
                    "ZipSaber", "ZipSaber.settings.bsml", SettingsViewController.instance);

                // MenuButtons clears on every scene change so always re-register
                MenuButtons.Instance?.RegisterButton(new MenuButton(
                    "ZipSaber", "Manage & install mods", ModManagerFlowCoordinator.Present));

                Log.Info("BSML UI registered.");
            }
            catch (Exception ex) { Log.Error($"Error registering BSML UI: {ex.Message}\n{ex}"); }
        }

        #region Path Calculation
        private void CalculatePaths()
        {
            try
            {
                string gdp = Application.dataPath;
                if (string.IsNullOrEmpty(gdp)) { Log.Error("App dataPath null!"); return; }
                DirectoryInfo dataDir    = new DirectoryInfo(gdp);
                DirectoryInfo installDir = dataDir.Parent;
                if (installDir == null) { Log.Error("Parent dir null!"); return; }

                InstallDirPath = installDir.FullName;

                // WIP path: always (re)calculated so a config change takes effect immediately
                RecalculateWipPath();

                if (string.IsNullOrEmpty(CustomLevelsPath))
                {
                    CustomLevelsPath = Path.Combine(InstallDirPath, BeatSaberDataFolderName, CustomLevelsFolderName);
                    Log.Info($"Custom Path: {CustomLevelsPath}");
                    EnsureDirectoryExists(CustomLevelsPath, "CustomPath");
                }
                if (string.IsNullOrEmpty(PluginsPath))
                {
                    PluginsPath = Path.Combine(InstallDirPath, "Plugins");
                    Log.Info($"Plugins Path: {PluginsPath}");
                    EnsureDirectoryExists(PluginsPath, "PluginsPath");
                }
            }
            catch (Exception e) { Log.Error($"PathFail: {e.Message}\n{e}"); }
        }

        /// <summary>
        /// Recalculates CustomWipLevelsPath from the current config value.
        /// Call this whenever CustomWipFolderName changes in settings.
        /// </summary>
        internal static void RecalculateWipPath()
        {
            if (string.IsNullOrEmpty(InstallDirPath)) return;
            try
            {
                string folderName = GetWipFolderName();
                string newPath = Path.Combine(InstallDirPath, BeatSaberDataFolderName, folderName);
                if (newPath == CustomWipLevelsPath) return;
                CustomWipLevelsPath = newPath;
                Log.Info($"WIP Path: {CustomWipLevelsPath}");
                Instance?.EnsureDirectoryExists(CustomWipLevelsPath, "WIPPath");
            }
            catch (Exception e) { Log.Error($"WipPathFail: {e.Message}"); }
        }

        /// <summary>
        /// The folder name actually in use — either the user's custom value or the default.
        /// </summary>
        internal static string GetWipFolderName()
        {
            string custom = Config?.CustomWipFolderName?.Trim() ?? "";
            // Reject anything that looks like an absolute path or contains separators
            if (!string.IsNullOrEmpty(custom) &&
                custom.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
                !Path.IsPathRooted(custom))
                return custom;
            return CustomWipLevelsFolderName;
        }

        private bool EnsureDirectoryExists(string p, string ctx)
        {
            if (string.IsNullOrEmpty(p)) { Log.Warn($"{ctx}: null path"); return false; }
            if (!Directory.Exists(p)) { try { Directory.CreateDirectory(p); } catch (Exception ex) { Log.Error($"{ctx}: CreateFail: {ex.Message}"); return false; } }
            return true;
        }

        internal static string GetPluginsPath() => PluginsPath;
        #endregion

        #region Window Hook
        private void AttemptFindAndHookWindow()
        {
            Log.Debug("[Hooking] Finding window...");
            IntPtr fHwnd = FindWindow(UnityWindowClass, null);
            if (fHwnd == IntPtr.Zero) fHwnd = FindWindow(null, BeatSaberWindowTitle);
            if (fHwnd == IntPtr.Zero) { try { fHwnd = Process.GetCurrentProcess().MainWindowHandle; } catch { Log.Warn("[Hooking] Failed get process handle."); } }
            if (fHwnd != IntPtr.Zero) { Log.Info($"[Hooking] Found {fHwnd}. Setting hook."); SetupWindowHook(fHwnd); }
            else { Log.Error("[Hooking] Failed find window."); hooksAttempted = false; }
        }

        private void SetupWindowHook(IntPtr wh)
        {
            if (hooksActive || Hwnd != IntPtr.Zero) { Log.Warn("Hook already active."); return; }
            try
            {
                Hwnd = wh; CalculatePaths();
                if (string.IsNullOrEmpty(CustomWipLevelsPath)) { Log.Error("Hook Abort: WIP path null."); Hwnd = IntPtr.Zero; return; }
                wndProcDelegate = StaticWndProc;
                IntPtr ptr = Marshal.GetFunctionPointerForDelegate(wndProcDelegate);
                oldWndProc = SetWindowLongPtr(Hwnd, GWLP_WNDPROC, ptr);
                int err = Marshal.GetLastWin32Error();
                if (oldWndProc == IntPtr.Zero && err != 0) { Log.Error($"SetFail: {err}"); CleanupWindowHook(); return; }
                DragAcceptFiles(Hwnd, true);
                Log.Info("Hook OK"); hooksActive = true;
            }
            catch (Exception ex) { Log.Error($"Hook Setup Err: {ex.Message}\n{ex}"); CleanupWindowHook(); }
        }

        private void CleanupWindowHook()
        {
            Log.Debug("Hook Cleanup...");
            IntPtr cHwnd = Hwnd, cOld = oldWndProc;
            if (cHwnd != IntPtr.Zero)
            {
                try { DragAcceptFiles(cHwnd, false); } catch { }
                if (cOld != IntPtr.Zero) { try { SetWindowLongPtr(cHwnd, GWLP_WNDPROC, cOld); } catch (Exception ex) { Log.Warn($"Failed restore hook: {ex.Message}"); } }
            }
            wndProcDelegate = null; oldWndProc = IntPtr.Zero; Hwnd = IntPtr.Zero; hooksActive = false;
            Log.Info("Hook Cleanup OK");
        }

        internal static IntPtr StaticWndProc(IntPtr h, uint m, IntPtr w, IntPtr l)
        {
            if (m == WM_DROPFILES) { Log.Debug("WM_DROPFILES received"); Task.Run(() => StaticHandleDroppedFiles(w)); return IntPtr.Zero; }
            IntPtr currentOld = oldWndProc;
            if (currentOld != IntPtr.Zero) { try { return CallWindowProc(currentOld, h, m, w, l); } catch (Exception ex) { Log.Error($"CallOrigErr: {ex.Message}"); return DefWindowProc(h, m, w, l); } }
            return DefWindowProc(h, m, w, l);
        }
        #endregion

        #region Drop Handling
        private static void StaticHandleDroppedFiles(IntPtr hDrop)
        {
            List<string> files = new List<string>();
            try
            {
                uint count = DragQueryFile(hDrop, 0xFFFFFFFF, null, 0);
                Log.Info($"Drop: {count} items");
                for (uint i = 0; i < count; i++)
                {
                    uint len = DragQueryFile(hDrop, i, null, 0);
                    if (len > 0) { var sb = new StringBuilder((int)len + 1); if (DragQueryFile(hDrop, i, sb, (uint)sb.Capacity) > 0) files.Add(sb.ToString()); }
                }
            }
            catch (Exception ex) { Log.Error($"DropQueryErr: {ex.Message}\n{ex}"); }
            finally { try { DragFinish(hDrop); } catch { } }

            if (!files.Any()) return;
            if (Instance == null) { Log.Error("Instance null."); return; }

            var zips    = files.Where(p => Path.GetExtension(p).Equals(".zip", StringComparison.OrdinalIgnoreCase)).ToList();
            var dlls    = files.Where(p => Path.GetExtension(p).Equals(".dll", StringComparison.OrdinalIgnoreCase)).ToList();
            int skipped = files.Count - zips.Count - dlls.Count;
            if (skipped > 0) Log.Info($"Drop: Skipped {skipped} unrecognised file(s).");

            if (dlls.Any()) Instance.HandleDroppedDlls(dlls);
            if (!zips.Any()) return;
            if (Config == null || !Config.ShowDestinationPrompt)
            { Instance.ProcessDroppedFilesBatchToTarget(zips, wip: true); return; }
            DestinationModal.Instance.EnqueueBatch(zips);
        }

        internal void ProcessDroppedFilesBatchToTarget(List<string> paths, bool wip)
        {
            string targetBase = wip ? CustomWipLevelsPath : CustomLevelsPath;
            string targetName = wip ? "CustomWipLevels" : "CustomLevels";
            if (string.IsNullOrEmpty(targetBase)) { Log.Error($"Target path null for {targetName}."); return; }
            bool anyOK = false; int ok = 0, fail = 0;
            foreach (string p in paths)
            {
                try { if (ProcessMapZip(p, targetBase, wip)) { ok++; anyOK = true; } else fail++; }
                catch (Exception ex) { Log.Error($"Batch Err {Path.GetFileName(p)}: {ex.Message}"); fail++; }
            }
            Log.Info($"Batch done. OK:{ok} Fail:{fail}");
            if (anyOK) RequestSongRefresh();
        }
        #endregion

        #region Mod DLL Installation
        internal void HandleDroppedDlls(List<string> dllPaths)
        {
            if (string.IsNullOrEmpty(PluginsPath)) { Log.Error("[ModInstall] Plugins path unknown."); return; }
            var installed = new List<string>(); int invalid = 0;
            foreach (string dll in dllPaths)
            {
                string fileName = Path.GetFileName(dll);
                if (!ModValidator.IsBsipaPlugin(dll, out string modId, out string modVersion))
                { Log.Warn($"[ModInstall] '{fileName}' rejected."); invalid++; continue; }
                try
                {
                    File.Copy(dll, Path.Combine(PluginsPath, fileName), overwrite: true);
                    string label = modVersion != "?" ? $"{modId} v{modVersion}" : modId;
                    installed.Add(label); Log.Info($"[ModInstall] Installed '{label}'");
                }
                catch (Exception ex) { Log.Error($"[ModInstall] Copy failed '{fileName}': {ex.Message}"); }
            }
            if (invalid > 0) Log.Warn($"[ModInstall] {invalid} rejected.");
            if (installed.Any()) ModInstallModal.Instance.ShowForMods(installed);
        }
        #endregion

        #region Map Processing
        internal bool ProcessMapZip(string zip, string targetBase, bool trackForDelete)
        {
            string mapName = Path.GetFileNameWithoutExtension(zip);
            string finalDir = null; bool extOK = false, valOK = false;
            try
            {
                if (!EnsureDirectoryExists(targetBase, "MapProc")) return false;
                string sanName = SanitizeFolderName(mapName);
                string curDir  = Path.Combine(targetBase, sanName);
                if (Directory.Exists(curDir))
                {
                    int num = 1; string potName;
                    do { potName = $"{sanName}_{num++}"; curDir = Path.Combine(targetBase, potName); if (num > 100) return false; }
                    while (Directory.Exists(curDir));
                }
                finalDir = curDir;
                try { Directory.CreateDirectory(finalDir); ZipFile.ExtractToDirectory(zip, finalDir); extOK = true; }
                catch (Exception ex) { Log.Error($"ExtractFail: {ex.Message}"); }
                if (extOK)
                {
                    valOK = IsValidMapFolder(finalDir);
                    if (valOK && trackForDelete)
                        lock (_folderListLock) { _importedWipFoldersThisSession.Add(finalDir); }
                }
            }
            catch (Exception ex) { Log.Error($"OuterErr: {ex.Message}"); }
            finally { if (finalDir != null && Directory.Exists(finalDir) && (!extOK || !valOK)) TryDeleteDirectory(finalDir); }
            return extOK && valOK;
        }

        private bool IsValidMapFolder(string p)
        {
            if (!Directory.Exists(p)) return false;
            try
            {
                bool hasInfo  = Directory.EnumerateFiles(p, "info.dat", SearchOption.TopDirectoryOnly).Any(f => Path.GetFileName(f).Equals("info.dat", StringComparison.OrdinalIgnoreCase));
                bool hasAudio = Directory.EnumerateFiles(p, "*.*", SearchOption.TopDirectoryOnly).Any(f => { var e = Path.GetExtension(f); return e.Equals(".egg", StringComparison.OrdinalIgnoreCase) || e.Equals(".ogg", StringComparison.OrdinalIgnoreCase) || e.Equals(".wav", StringComparison.OrdinalIgnoreCase); });
                bool hasDiff  = Directory.EnumerateFiles(p, "*.dat", SearchOption.TopDirectoryOnly).Any(f => !Path.GetFileName(f).Equals("info.dat", StringComparison.OrdinalIgnoreCase));
                return hasInfo && hasAudio && hasDiff;
            }
            catch (Exception ex) { Log.Error($"ValidationErr: {ex.Message}"); return false; }
        }

        private string SanitizeFolderName(string n)
        {
            char[] inv = Path.GetInvalidFileNameChars();
            string san = string.Join("_", n.Split(inv, StringSplitOptions.RemoveEmptyEntries)).TrimEnd('.', ' ');
            return string.IsNullOrWhiteSpace(san) ? "ImportedMap_" + Guid.NewGuid().ToString("N").Substring(0, 8) : san;
        }
        #endregion

        #region Utilities
        internal void RequestSongRefresh()
        {
            try { if (Loader.Instance != null) { Loader.Instance.RefreshSongs(false); Log.Info("SongCore refresh requested."); } }
            catch (Exception ex) { Log.Error($"Refresh ReqErr: {ex.Message}"); }
        }

        internal bool TryDeleteDirectory(string p)
        {
            if (string.IsNullOrEmpty(p)) return false;
            try { if (Directory.Exists(p)) { Directory.Delete(p, true); return true; } } catch (Exception ex) { Log.Error($"Cleanup Fail: {ex.Message}"); }
            return false;
        }
        #endregion
    }
}