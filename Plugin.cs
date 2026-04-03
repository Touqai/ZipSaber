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
using BeatSaberMarkupLanguage.Attributes;
using BeatSaberMarkupLanguage.ViewControllers;
using BeatSaberMarkupLanguage.Util;
using BeatSaberMarkupLanguage.MenuButtons;

#nullable disable

namespace ZipSaber
{
    [Plugin(RuntimeOptions.SingleStartInit)]
    public class Plugin
    {
        #region Constants
        private const string UnityWindowClass    = "UnityWndClass";
        private const string BeatSaberWindowTitle = "Beat Saber";
        private const string CustomWipLevelsFolderName = "CustomWipLevels";
        private const string CustomLevelsFolderName    = "CustomLevels";
        private const string BeatSaberDataFolderName   = "Beat Saber_Data";
        #endregion

        #region Static Plugin Instance, Logger & Config
        internal static Plugin Instance { get; private set; }
        internal static PluginConfig Config { get; private set; }
        internal static IPA.Logging.Logger Log { get; private set; }
        #endregion

        #region Session Tracking  (WIP maps only – CustomLevels maps are never auto-deleted)
        private static List<string> _importedWipFoldersThisSession = new List<string>();
        private static readonly object _folderListLock = new object();
        #endregion

        #region WinAPI Imports and Constants
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

        #region Hook State Fields
        private static IntPtr          Hwnd             = IntPtr.Zero;
        private static WndProcDelegate wndProcDelegate;
        private static IntPtr          oldWndProc       = IntPtr.Zero;
        private static string          CustomWipLevelsPath;
        private static string          CustomLevelsPath;
        private static string          PluginsPath;
        private static bool            hooksAttempted   = false;
        private static bool            hooksActive      = false;
        #endregion

        #region BSIPA Plugin Lifecycle Methods
        [Init]
        public Plugin(Config conf, IPA.Logging.Logger logger)
        {
            Instance = this;
            Log      = logger;
            Config   = conf.Generated<PluginConfig>();
            Log.Info("Initializing ZipSaber...");
            var _ = SettingsViewController.instance;
            Log.Debug("SettingsViewController instance potentially initialized.");
            lock (_folderListLock) { _importedWipFoldersThisSession.Clear(); }
            CleanupPendingDeletes();
        }

        /// <summary>Delete any *.dll.del / *.manifest.del files left from previous session.</summary>
        private void CleanupPendingDeletes()
        {
            try
            {
                string pluginsDir = System.IO.Path.GetDirectoryName(
                    System.Reflection.Assembly.GetExecutingAssembly().Location);
                Log.Info($"[Cleanup] Scanning: {pluginsDir}");
                var markerFiles = System.IO.Directory.GetFiles(pluginsDir, "*.zs_del");
                if (markerFiles.Length > 0)
                    Log.Warn($"[Cleanup] {markerFiles.Length} marker(s) still present — post-exit cleanup may not have run yet.");
                // Also clean up any leftover batch file from previous session
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

            if (Config == null) { Log.Error("Config object is null after Init!"); }

            // Register BSML settings menu once the main menu is ready
            MainMenuAwaiter.MainMenuInitializing += RegisterBSMLSettings;

            // Schedule delayed window hook
            if (!hooksAttempted && !hooksActive)
            {
                Log.Info("Scheduling delayed hook setup.");
                hooksAttempted = true;
                Task.Run(async () => { await Task.Delay(3000); AttemptFindAndHookWindow(); });
            }
            else { Log.Debug($"Hook already attempted/active. Skipping."); }
        }

        [OnDisable]
        public void OnDisable()
        {
            Log.Info("OnDisable called.");
            CleanupWindowHook();
            hooksAttempted = false;

            MainMenuAwaiter.MainMenuInitializing -= RegisterBSMLSettings;

            // Auto-delete WIP maps if configured
            bool shouldDelete = Config?.DeleteOnClose ?? false;
            Log.Info($"Plugin disabling. DeleteOnClose={shouldDelete}");

            if (shouldDelete)
            {
                int deleteCount = 0;
                List<string> foldersToDelete;
                lock (_folderListLock)
                {
                    foldersToDelete = new List<string>(_importedWipFoldersThisSession);
                    _importedWipFoldersThisSession.Clear();
                }
                if (!foldersToDelete.Any()) { Log.Info("No WIP maps imported this session to delete."); }
                else
                {
                    Log.Info($"Deleting {foldersToDelete.Count} WIP folder(s)...");
                    foreach (string fp in foldersToDelete) { if (TryDeleteDirectory(fp)) deleteCount++; }
                    Log.Info($"Deleted {deleteCount} folder(s).");
                }
            }
            else
            {
                Log.Info("DeleteOnClose disabled. Keeping maps.");
                lock (_folderListLock) { _importedWipFoldersThisSession.Clear(); }
            }

            // Unregister BSML UI
            try
            {
                if (BSMLSettings.Instance != null)
                    BSMLSettings.Instance.RemoveSettingsMenu(SettingsViewController.instance);
                // MenuButton doesn't need unregistering — it auto-clears on scene change
                Log.Info("BSML UI unregistered.");
            }
            catch (Exception ex) { Log.Error($"Error removing BSML UI: {ex.Message}\n{ex}"); }

            // Delete any mods queued for deletion — write batch that runs after game exits
            ModManagerViewController.ExecutePendingDeletions();
            LaunchPostExitCleanup();
        }

        /// <summary>
        /// Writes and launches a batch file that waits for THIS process to exit,
        /// then deletes all *.zs_del marked files. Runs completely outside the game.
        /// </summary>
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
                // Wait until the Beat Saber process exits
                sb.AppendLine($":wait");
                sb.AppendLine($"tasklist /FI \"PID eq {pid}\" 2>nul | find \"{pid}\" >nul");
                sb.AppendLine($"if not errorlevel 1 (timeout /t 2 /nobreak >nul & goto wait)");
                // Now delete the files
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
                    FileName        = "cmd.exe",
                    Arguments       = $"/C \"{batPath}\"",
                    UseShellExecute = false,
                    CreateNoWindow  = true
                });

                Log.Info($"[Cleanup] Post-exit cleanup batch launched for {markerFiles.Length} file(s). Waiting for PID {pid} to exit.");
            }
            catch (Exception ex) { Log.Warn($"[Cleanup] LaunchPostExitCleanup failed: {ex.Message}"); }
        }
        #endregion

        #region BSML Settings Registration
        private void RegisterBSMLSettings()
        {
            MainMenuAwaiter.MainMenuInitializing -= RegisterBSMLSettings;
            try
            {
                // ZipSaber settings → gear icon
                BSMLSettings.Instance.AddSettingsMenu("ZipSaber", "ZipSaber.settings.bsml", SettingsViewController.instance);

                // Mod Manager → main menu MODS panel button (same as BeatLeader, Camera2 etc.)
                MenuButtons.Instance.RegisterButton(new MenuButton(
                    "ZipSaber",
                    "Manage & install mods",
                    ModManagerFlowCoordinator.Present
                ));

                Log.Info("BSML UI registered.");
            }
            catch (Exception ex) { Log.Error($"Error registering BSML UI: {ex.Message}\n{ex}"); }
        }
        #endregion

        #region Path Calculation
        private void CalculatePaths()
        {
            try
            {
                string gdp = Application.dataPath;
                if (string.IsNullOrEmpty(gdp)) { Log.Error("App dataPath null!"); return; }
                DirectoryInfo dataDir   = new DirectoryInfo(gdp);
                DirectoryInfo installDir = dataDir.Parent;
                if (installDir == null) { Log.Error("Parent dir null!"); return; }

                if (string.IsNullOrEmpty(CustomWipLevelsPath))
                {
                    CustomWipLevelsPath = Path.Combine(installDir.FullName, BeatSaberDataFolderName, CustomWipLevelsFolderName);
                    Log.Info($"WIP Path: {CustomWipLevelsPath}");
                    EnsureDirectoryExists(CustomWipLevelsPath, "WIPPath");
                }
                if (string.IsNullOrEmpty(CustomLevelsPath))
                {
                    CustomLevelsPath = Path.Combine(installDir.FullName, BeatSaberDataFolderName, CustomLevelsFolderName);
                    Log.Info($"Custom Path: {CustomLevelsPath}");
                    EnsureDirectoryExists(CustomLevelsPath, "CustomPath");
                }
                if (string.IsNullOrEmpty(PluginsPath))
                {
                    PluginsPath = Path.Combine(installDir.FullName, "Plugins");
                    Log.Info($"Plugins Path: {PluginsPath}");
                    EnsureDirectoryExists(PluginsPath, "PluginsPath");
                }
            }
            catch (Exception e) { Log.Error($"PathFail: {e.Message}\n{e}"); }
        }

        private bool EnsureDirectoryExists(string p, string ctx)
        {
            if (string.IsNullOrEmpty(p)) { Log.Warn($"{ctx}: null path"); return false; }
            if (!Directory.Exists(p)) { try { Directory.CreateDirectory(p); } catch (Exception ex) { Log.Error($"{ctx}: CreateFail: {ex.Message}"); return false; } }
            return true;
        }

        /// <summary>Exposed so ModRegistry and ModManagerViewController can get the path.</summary>
        internal static string GetPluginsPath() => PluginsPath;
        #endregion

        #region Window Hooking Logic
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
                Hwnd = wh;
                CalculatePaths();
                if (string.IsNullOrEmpty(CustomWipLevelsPath)) { Log.Error("Hook Abort: WIP path null."); Hwnd = IntPtr.Zero; return; }
                wndProcDelegate = StaticWndProc;
                IntPtr ptr = Marshal.GetFunctionPointerForDelegate(wndProcDelegate);
                oldWndProc = SetWindowLongPtr(Hwnd, GWLP_WNDPROC, ptr);
                int err = Marshal.GetLastWin32Error();
                if (oldWndProc == IntPtr.Zero && err != 0) { Log.Error($"SetFail: {err}"); CleanupWindowHook(); return; }
                DragAcceptFiles(Hwnd, true);
                Log.Info("Hook OK");
                hooksActive = true;
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

        #region Dropped File Handling
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

            if (!files.Any()) { Log.Info("Drop: No valid files"); return; }
            if (Instance == null) { Log.Error("Instance null processing drop."); return; }

            // Split dropped files by type
            var zips = files.Where(p => Path.GetExtension(p).Equals(".zip", StringComparison.OrdinalIgnoreCase)).ToList();
            var dlls = files.Where(p => Path.GetExtension(p).Equals(".dll", StringComparison.OrdinalIgnoreCase)).ToList();
            int skipped = files.Count - zips.Count - dlls.Count;
            if (skipped > 0) Log.Info($"Drop: Skipped {skipped} unrecognised file(s).");

            // ── Handle mod DLLs ──────────────────────────────────────────────────
            if (dlls.Any())
                Instance.HandleDroppedDlls(dlls);

            // ── Handle map ZIPs ──────────────────────────────────────────────────
            if (!zips.Any()) return;

            if (Config == null || !Config.ShowDestinationPrompt)
            {
                Log.Info("Destination prompt disabled – sending to CustomWipLevels.");
                Instance.ProcessDroppedFilesBatchToTarget(zips, wip: true);
                return;
            }

            Log.Info("Showing destination prompt...");
            DestinationModal.Instance.EnqueueBatch(zips);
        }

        /// <summary>
        /// Called by DestinationModal (or directly when prompt is off) with the user's choice.
        /// wip=true  → CustomWipLevels  (tracked for auto-delete)
        /// wip=false → CustomLevels     (never auto-deleted)
        /// </summary>
        internal void ProcessDroppedFilesBatchToTarget(List<string> paths, bool wip)
        {
            string targetBase = wip ? CustomWipLevelsPath : CustomLevelsPath;
            string targetName = wip ? "CustomWipLevels" : "CustomLevels";

            if (string.IsNullOrEmpty(targetBase)) { Log.Error($"Target path for {targetName} is null – aborting batch."); return; }

            bool anyOK = false; int ok = 0, fail = 0;
            Log.Info($"Batch ({targetName}): {paths.Count} file(s)");

            foreach (string p in paths)
            {
                try
                {
                    Log.Debug($"Batch ZIP: {Path.GetFileName(p)} → {targetName}");
                    if (ProcessMapZip(p, targetBase, wip)) { ok++; anyOK = true; }
                    else { fail++; }
                }
                catch (Exception ex) { Log.Error($"Batch Err {Path.GetFileName(p)}: {ex.Message}\n{ex}"); fail++; }
            }

            Log.Info($"Batch done. OK: {ok}, Fail: {fail}.");
            if (anyOK) { Log.Info("Requesting SongCore refresh..."); RequestSongRefresh(); }
        }
        #endregion

        #region Mod DLL Installation
        /// <summary>
        /// Validates each dropped DLL, copies valid BSIPA mods to the Plugins folder,
        /// then shows the restart prompt for all that were installed.
        /// </summary>
        internal void HandleDroppedDlls(List<string> dllPaths)
        {
            if (string.IsNullOrEmpty(PluginsPath))
            {
                Log.Error("[ModInstall] Plugins path unknown – cannot install mods.");
                return;
            }

            var installed = new List<string>();
            int invalid = 0;

            foreach (string dll in dllPaths)
            {
                string fileName = Path.GetFileName(dll);
                Log.Info($"[ModInstall] Inspecting: {fileName}");

                if (!ModValidator.IsBsipaPlugin(dll, out string modId, out string modVersion))
                {
                    Log.Warn($"[ModInstall] '{fileName}' has no embedded manifest.json – skipping.");
                    invalid++;
                    continue;
                }

                string dest = Path.Combine(PluginsPath, fileName);
                bool alreadyExists = File.Exists(dest);

                try
                {
                    File.Copy(dll, dest, overwrite: true);
                    string label = modVersion != "?" ? $"{modId} v{modVersion}" : modId;
                    installed.Add(label);
                    Log.Info($"[ModInstall] Installed '{label}' → {dest} (overwrite={alreadyExists})");
                }
                catch (Exception ex)
                {
                    Log.Error($"[ModInstall] Copy failed for '{fileName}': {ex.Message}\n{ex}");
                }
            }

            if (invalid > 0)
                Log.Warn($"[ModInstall] {invalid} file(s) rejected (not BSIPA plugins).");

            if (installed.Any())
                ModInstallModal.Instance.ShowForMods(installed);
        }
        #endregion

        #region Map Processing Logic
        /// <param name="targetBase">The full path of the destination folder (WIP or Custom).</param>
        /// <param name="trackForDelete">Only true for WIP maps – adds to session delete list.</param>
        internal bool ProcessMapZip(string zip, string targetBase, bool trackForDelete)
        {
            string mapName = Path.GetFileNameWithoutExtension(zip);
            string finalDir = null;
            bool extOK = false, valOK = false;
            Log.Debug($"MapProc '{mapName}' → {targetBase}");

            try
            {
                if (string.IsNullOrEmpty(targetBase) || !EnsureDirectoryExists(targetBase, $"MapProc {mapName}"))
                { Log.Error("Target path invalid"); return false; }

                string sanName = SanitizeFolderName(mapName);
                string curDir  = Path.Combine(targetBase, sanName);

                if (Directory.Exists(curDir))
                {
                    Log.Warn("Folder exists, finding unique name...");
                    int num = 1; string potName;
                    do { potName = $"{sanName}_{num++}"; curDir = Path.Combine(targetBase, potName); if (num > 100) { Log.Error("Unique name abort"); return false; } }
                    while (Directory.Exists(curDir));
                    Log.Info($"Using unique name: {potName}");
                }

                finalDir = curDir;
                Log.Debug($"Final target: {finalDir}");

                try
                {
                    Directory.CreateDirectory(finalDir);
                    ZipFile.ExtractToDirectory(zip, finalDir);
                    Log.Info("Extracted.");
                    extOK = true;
                }
                catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException)
                { Log.Error($"ExtractFail {ex.GetType().Name}: {ex.Message}"); }
                catch (Exception ex)
                { Log.Error($"ExtractErr {ex.GetType().Name}: {ex.Message}\n{ex}"); }

                if (extOK)
                {
                    valOK = IsValidMapFolder(finalDir);
                    if (!valOK) { Log.Warn("Validation failed."); }
                    else
                    {
                        Log.Info("Validation OK.");
                        if (trackForDelete)
                        {
                            lock (_folderListLock) { _importedWipFoldersThisSession.Add(finalDir); }
                            Log.Debug($"Tracking '{Path.GetFileName(finalDir)}' for auto-delete.");
                        }
                        else { Log.Debug($"'{Path.GetFileName(finalDir)}' in CustomLevels – not tracked for delete."); }
                    }
                }
            }
            catch (Exception ex) { Log.Error($"OuterErr: {ex.Message}\n{ex}"); }
            finally
            {
                if (finalDir != null && Directory.Exists(finalDir) && (!extOK || !valOK))
                {
                    Log.Warn("Extraction/validation failed – cleaning up partial folder.");
                    TryDeleteDirectory(finalDir);
                }
            }

            return extOK && valOK;
        }

        private bool IsValidMapFolder(string p)
        {
            if (!Directory.Exists(p)) return false;
            try
            {
                bool hasInfo  = Directory.EnumerateFiles(p, "info.dat", SearchOption.TopDirectoryOnly).Any(f => Path.GetFileName(f).Equals("info.dat", StringComparison.OrdinalIgnoreCase));
                bool hasAudio = Directory.EnumerateFiles(p, "*.*",      SearchOption.TopDirectoryOnly).Any(f => { var e = Path.GetExtension(f); return e.Equals(".egg", StringComparison.OrdinalIgnoreCase) || e.Equals(".ogg", StringComparison.OrdinalIgnoreCase) || e.Equals(".wav", StringComparison.OrdinalIgnoreCase); });
                bool hasDiff  = Directory.EnumerateFiles(p, "*.dat",    SearchOption.TopDirectoryOnly).Any(f => !Path.GetFileName(f).Equals("info.dat", StringComparison.OrdinalIgnoreCase));
                return hasInfo && hasAudio && hasDiff;
            }
            catch (Exception ex) { Log.Error($"ValidationErr: {ex.Message}\n{ex}"); return false; }
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
            try
            {
                if (Loader.Instance != null) { Loader.Instance.RefreshSongs(false); Log.Info("SongCore refresh requested."); }
                else { Log.Warn("SongCore.Loader.Instance is null – refresh skipped."); }
            }
            catch (Exception ex) { Log.Error($"Refresh ReqErr: {ex.Message}\n{ex}"); }
        }

        internal bool TryDeleteDirectory(string p)
        {
            if (string.IsNullOrEmpty(p)) return false;
            try { if (Directory.Exists(p)) { Directory.Delete(p, true); return true; } } catch (Exception ex) { Log.Error($"Cleanup Fail {Path.GetFileName(p)}: {ex.Message}\n{ex}"); }
            return false;
        }
        #endregion

    } // End Plugin Class


    // ── Settings View Controller ──────────────────────────────────────────────────
    [ViewDefinition("ZipSaber.settings.bsml")]
    [HotReload(RelativePathToLayout = @"settings.bsml")]
    internal class SettingsViewController : BSMLAutomaticViewController
    {
        private static SettingsViewController _instance;
        public static SettingsViewController instance
        {
            get
            {
                if (_instance == null)
                {
                    Plugin.Log?.Debug("[Settings] Creating SettingsViewController instance.");
                    _instance = new SettingsViewController();
                    _instance.InitializeValues();
                }
                return _instance;
            }
            private set => _instance = value;
        }

        public SettingsViewController() { }

        private bool _uiDeleteOnClose;
        private bool _uiShowPrompt;

        private void InitializeValues()
        {
            if (Plugin.Config != null)
            {
                _uiDeleteOnClose = Plugin.Config.DeleteOnClose;
                _uiShowPrompt    = Plugin.Config.ShowDestinationPrompt;
                Plugin.Log?.Info($"[Settings] Init: DeleteOnClose={_uiDeleteOnClose}, ShowPrompt={_uiShowPrompt}");
            }
            else { _uiDeleteOnClose = false; _uiShowPrompt = true; Plugin.Log?.Warn("[Settings] Config NULL during init."); }
        }

        [UIValue("delete-on-close")]
        public bool DeleteOnClose_UI
        {
            get => _uiDeleteOnClose;
            set
            {
                if (_uiDeleteOnClose == value) return;
                _uiDeleteOnClose = value;
                if (Plugin.Config != null) { Plugin.Config.DeleteOnClose = value; Plugin.Log?.Info($"[Settings] DeleteOnClose → {value}"); }
                NotifyPropertyChanged();
            }
        }

        [UIValue("show-destination-prompt")]
        public bool ShowDestinationPrompt_UI
        {
            get => _uiShowPrompt;
            set
            {
                if (_uiShowPrompt == value) return;
                _uiShowPrompt = value;
                if (Plugin.Config != null) { Plugin.Config.ShowDestinationPrompt = value; Plugin.Log?.Info($"[Settings] ShowDestinationPrompt → {value}"); }
                NotifyPropertyChanged();
            }
        }

        protected override void DidActivate(bool firstActivation, bool addedToHierarchy, bool screenSystemEnabling)
        {
            base.DidActivate(firstActivation, addedToHierarchy, screenSystemEnabling);
            if (Plugin.Config != null)
            {
                _uiDeleteOnClose = Plugin.Config.DeleteOnClose;
                _uiShowPrompt    = Plugin.Config.ShowDestinationPrompt;
            }
            NotifyPropertyChanged(nameof(DeleteOnClose_UI));
            NotifyPropertyChanged(nameof(ShowDestinationPrompt_UI));
        }
    }

} // End Namespace ZipSaber
