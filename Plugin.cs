<<<<<<< Updated upstream
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using IPA;
using IPA.Logging;
using IPA.Utilities.Async; // Needed for MainThreadScheduler
using UnityEngine;
using SongCore;
using System.Diagnostics;

namespace ZipSaber
{
    [Plugin(RuntimeOptions.SingleStartInit)]
    public class Plugin
    {
        internal static Plugin Instance { get; private set; }
        internal static IPA.Logging.Logger Log { get; private set; }

        // Hook state...
        private static IntPtr Hwnd = IntPtr.Zero;
        internal delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        private static WndProcDelegate wndProcDelegate;
        private static IntPtr oldWndProc = IntPtr.Zero;
        private static string CustomWipLevelsPath;
        private static bool hooksAttempted = false;
        private static bool hooksActive = false;

        // WinAPI...
        private const int GWLP_WNDPROC = -4; private const uint WM_DROPFILES = 0x233;
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)] private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)] private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern void DragAcceptFiles(IntPtr hWnd, bool fAccept);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern uint DragQueryFile(IntPtr hDrop, uint iFile, [Out] StringBuilder lpszFile, uint cch);
        [DllImport("shell32.dll")] private static extern void DragFinish(IntPtr hDrop);
        [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern IntPtr DefWindowProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)] private static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

        [Init]
        public Plugin(IPA.Logging.Logger logger)
        { Instance = this; Log = logger; Log.Info("ZipSaber initializing (BSIPA Init)."); }

        [OnEnable]
        public void OnEnable()
        {
            Log.Info("ZipSaber OnEnable called.");
            CalculatePaths(); // Calculate paths early
            if (!hooksAttempted && !hooksActive)
            { Log.Debug("Scheduling delayed hook setup via Task.Delay."); hooksAttempted = true; Task.Run(async () => { await Task.Delay(3000); AttemptFindAndHookWindow(); }); }
            else { Log.Warn($"Hook setup already attempted/active. Skipping OnEnable setup."); }
        }

        [OnDisable]
        public void OnDisable()
        { Log.Info("ZipSaber OnDisable called."); CleanupWindowHook(); hooksAttempted = false; hooksActive = false; }

        private void CalculatePaths()
        {
            if (!string.IsNullOrEmpty(CustomWipLevelsPath)) return;
            try
            {
                string gameDataPath = Application.dataPath; if(string.IsNullOrEmpty(gameDataPath)) { Log.Error("Application.dataPath is null or empty!"); return; }
                DirectoryInfo gameDataDir = new DirectoryInfo(gameDataPath); DirectoryInfo gameDir = gameDataDir.Parent; if(gameDir == null) { Log.Error("Could not get parent directory of Application.dataPath!"); return; }
                CustomWipLevelsPath = Path.Combine(gameDir.FullName, "Beat Saber_Data", "CustomWipLevels");
                Log.Info($"Target CustomWipLevels path: {CustomWipLevelsPath}");
                if (!Directory.Exists(CustomWipLevelsPath)) { Log.Warn($"CustomWipLevels folder not found at target path. Creating it..."); Directory.CreateDirectory(CustomWipLevelsPath); Log.Info("Created CustomWipLevels folder."); }
            } catch (Exception e) { Log.Error($"Failed to calculate paths: {e.Message}"); Log.Debug(e); }
        }

        private void AttemptFindAndHookWindow()
        {
            Log.Info("Attempting to find Beat Saber window...");
            IntPtr foundHwnd = FindWindow("UnityWndClass", null); if (foundHwnd == IntPtr.Zero) foundHwnd = FindWindow(null, "Beat Saber"); if (foundHwnd == IntPtr.Zero) { try { foundHwnd = Process.GetCurrentProcess().MainWindowHandle; } catch { /* Ignore */ } }
            if (foundHwnd != IntPtr.Zero) { Log.Info($"Found window handle {foundHwnd}. Proceeding with hook setup."); SetupWindowHook(foundHwnd); }
            else { Log.Error("Failed to find Beat Saber window handle. Drag/drop inactive."); hooksAttempted = false; }
        }

        private void SetupWindowHook(IntPtr windowHandle)
        {
             if (hooksActive || oldWndProc != IntPtr.Zero || Hwnd != IntPtr.Zero) { Log.Warn("SetupWindowHook called but hooks seem already active. Skipping."); return; }
            try {
                 Hwnd = windowHandle; Log.Info($"Setting up hooks for Window Handle: {Hwnd}");
                 CalculatePaths(); // Ensure paths are calculated
                 wndProcDelegate = new WndProcDelegate(StaticWndProc); oldWndProc = SetWindowLongPtr(Hwnd, GWLP_WNDPROC, Marshal.GetFunctionPointerForDelegate(wndProcDelegate)); int err = Marshal.GetLastWin32Error(); if (oldWndProc == IntPtr.Zero) { Log.Error($"Failed hook. Error: {err}"); CleanupWindowHook(); return; } Log.Info($"Set hook. Original: {oldWndProc}");
                 DragAcceptFiles(Hwnd, true); err = Marshal.GetLastWin32Error(); if (err != 0) { Log.Error($"DragAcceptFiles failed. Error: {err}"); CleanupWindowHook(); return; } Log.Info("DragAcceptFiles enabled successfully.");
                 Log.Info("ZipSaber hooks setup complete."); hooksActive = true;
            } catch (Exception ex) { Log.Error($"Critical error during hook setup: {ex.Message}"); Log.Debug(ex); CleanupWindowHook(); }
        }

        private void CleanupWindowHook()
        {
            IntPtr currentHwnd = Hwnd; IntPtr currentOldWndProc = oldWndProc;
            if (currentHwnd != IntPtr.Zero) { try { DragAcceptFiles(currentHwnd, false); } catch {} if (currentOldWndProc != IntPtr.Zero) { try { SetWindowLongPtr(currentHwnd, GWLP_WNDPROC, currentOldWndProc); } catch {} } }
            wndProcDelegate = null; oldWndProc = IntPtr.Zero; Hwnd = IntPtr.Zero; hooksActive = false;
            Log.Info("ZipSaber cleanup finished.");
        }

        // --- Validation, Extraction & Helper Logic ---

        /// <summary> Checks if a folder contains the basic required files for a Beat Saber map. </summary>
        private bool IsValidMapFolder(string folderPath)
        {
            if (!Directory.Exists(folderPath)) return false;
            bool hasInfo = Directory.EnumerateFiles(folderPath, "info.dat", SearchOption.TopDirectoryOnly).Any(f => Path.GetFileName(f).Equals("info.dat", StringComparison.OrdinalIgnoreCase));
            bool hasAudio = Directory.EnumerateFiles(folderPath).Any(f => f.EndsWith(".egg", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".wav", StringComparison.OrdinalIgnoreCase));
            bool hasDifficulty = Directory.EnumerateFiles(folderPath, "*.dat", SearchOption.TopDirectoryOnly).Any(f => !Path.GetFileName(f).Equals("info.dat", StringComparison.OrdinalIgnoreCase));
            Log.Debug($"Validation Check for {folderPath}: Info={hasInfo}, Audio={hasAudio}, Difficulty={hasDifficulty}");
            return hasInfo && hasAudio && hasDifficulty;
        }

        /// <summary> Sanitizes a string to be safe for use as a folder name. </summary>
        private string SanitizeFolderName(string name) {
             string sanitized = string.Join("_", name.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries)).TrimEnd('.');
             return string.IsNullOrWhiteSpace(sanitized) ? "ImportedMap_" + Guid.NewGuid().ToString("N").Substring(0, 8) : sanitized;
        }

        /// <summary> Validates, extracts, and moves a map zip file. </summary>
        internal bool ProcessMapZip(string zipFilePath)
        {
            string finalExtractionDir = null;
            bool validationPassed = false;
            bool extractionSucceeded = false;
            string mapNameForLog = Path.GetFileNameWithoutExtension(zipFilePath);

            try
            {
                CalculatePaths();
                if (string.IsNullOrEmpty(CustomWipLevelsPath)) { Log.Error($"[{mapNameForLog}] Cannot process: CustomWipLevelsPath is not set."); return false; }
                if (!Directory.Exists(CustomWipLevelsPath)) { Log.Warn($"CustomWipLevels folder ({CustomWipLevelsPath}) not found. Creating."); try { Directory.CreateDirectory(CustomWipLevelsPath); } catch (Exception ce) { Log.Error($"Failed to create CustomWipLevels: {ce.Message}"); return false; } }

                string sanitizedMapName = SanitizeFolderName(mapNameForLog);
                finalExtractionDir = Path.Combine(CustomWipLevelsPath, sanitizedMapName);
                Log.Info($"[{mapNameForLog}] Preparing final destination: {finalExtractionDir}");

                if (Directory.Exists(finalExtractionDir))
                {
                    Log.Warn($"[{mapNameForLog}] Final destination '{finalExtractionDir}' already exists. Skipping extraction.");
                    // Optional: Overwrite logic could go here (delete existing folder)
                    return false; // Skip
                }

                // --- Extract Directly to Final Destination ---
                Log.Debug($"[{mapNameForLog}] Attempting to extract directly to {finalExtractionDir}");
                // Create the target directory first BEFORE extracting into it
                 Directory.CreateDirectory(finalExtractionDir);
                 Log.Debug($"[{mapNameForLog}] Created final directory.");

                ZipFile.ExtractToDirectory(zipFilePath, finalExtractionDir);
                Log.Info($"[{mapNameForLog}] Successfully extracted zip to final location: {finalExtractionDir}");
                extractionSucceeded = true;

                // --- Validate AFTER Extraction ---
                Log.Debug($"[{mapNameForLog}] Validating extracted folder contents at {finalExtractionDir}...");
                validationPassed = IsValidMapFolder(finalExtractionDir);

                if (!validationPassed)
                {
                    Log.Warn($"[{mapNameForLog}] Validation failed AFTER extraction. Deleting extracted folder.");
                    // If validation fails, clean up the folder we just created/extracted into
                    TryDeleteDirectory(finalExtractionDir);
                    return false; // Indicate failure due to invalid content
                }

                Log.Info($"[{mapNameForLog}] Validation passed for extracted folder.");

            }
            catch (IOException ioEx) when ((ioEx.HResult & 0xFFFF) == 32 || (ioEx.HResult & 0xFFFF) == 33) { Log.Error($"IOError extracting '{mapNameForLog}': File likely in use/locked. Details: {ioEx.Message}"); extractionSucceeded = false; }
            catch (IOException ioEx) { Log.Error($"IOError extracting '{mapNameForLog}': {ioEx.Message}"); Log.Debug(ioEx); if (finalExtractionDir != null) TryDeleteDirectory(finalExtractionDir); extractionSucceeded = false; }
            catch (InvalidDataException invEx) { Log.Error($"InvalidDataError extracting '{mapNameForLog}': Zip file corrupted/invalid. Details: {invEx.Message}"); if (finalExtractionDir != null) TryDeleteDirectory(finalExtractionDir); extractionSucceeded = false; }
            catch (UnauthorizedAccessException authEx) { Log.Error($"AuthError extracting/creating dir '{mapNameForLog}': Permission denied for '{finalExtractionDir}'. Details: {authEx.Message}"); if (finalExtractionDir != null) TryDeleteDirectory(finalExtractionDir); extractionSucceeded = false; }
            catch (Exception ex) { Log.Error($"Unexpected error processing zip '{mapNameForLog}': {ex.GetType().Name} - {ex.Message}"); Log.Debug(ex); if (finalExtractionDir != null) TryDeleteDirectory(finalExtractionDir); extractionSucceeded = false; }
            // No temp folder cleanup needed in this approach

            // Return true only if extraction succeeded AND validation passed
            return extractionSucceeded && validationPassed;
        }



        internal void RequestSongRefresh() { try { if (Loader.Instance != null) { Log.Info("Requesting SongCore song refresh on main thread."); UnityMainThreadTaskScheduler.Factory.StartNew(() => { try { Log.Debug("Executing Loader.Instance.RefreshSongs(false) on main thread."); Loader.Instance.RefreshSongs(false); Log.Info("SongCore refresh requested successfully."); } catch (Exception ex) { Log.Error($"Error calling RefreshSongs via SongCore: {ex.Message}"); Log.Debug(ex); } }); } else { Log.Warn("SongCore not loaded. Cannot auto-refresh songs."); } } catch (TypeLoadException tlEx) { Log.Error($"TypeLoadException accessing MainThreadScheduler. BSIPA.Utilities missing or corrupt? {tlEx.Message}"); } catch (Exception ex) { Log.Error($"Unexpected error in RequestSongRefresh: {ex.Message}"); Log.Debug(ex); } }
        internal void TryDeleteDirectory(string path) { try { if (Directory.Exists(path)) { Directory.Delete(path, true); Log.Debug($"Cleaned up directory: {path}"); } } catch (Exception cleanEx) { Log.Warn($"Failed to cleanup directory '{path}': {cleanEx.Message}"); } }


        // --- Static Window Procedure and File Handler ---
        internal static IntPtr StaticWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == WM_DROPFILES) { Log?.Info($"WndProc: WM_DROPFILES message received! wParam (hDrop): {wParam}"); IntPtr hDrop = wParam; Task.Run(() => StaticHandleDroppedFiles(hDrop)); return IntPtr.Zero; }
            IntPtr currentOldWndProc = oldWndProc; if (currentOldWndProc != IntPtr.Zero) { try { return CallWindowProc(currentOldWndProc, hWnd, msg, wParam, lParam); } catch (Exception ex) { Log?.Error($"Exception calling original WndProc (msg: {msg}): {ex.Message}. Falling back to DefWindowProc."); Log?.Debug(ex); return DefWindowProc(hWnd, msg, wParam, lParam); } }
            else { Log?.Warn($"WndProc called (msg: {msg}) but oldWndProc is Zero. Using DefWindowProc."); return DefWindowProc(hWnd, msg, wParam, lParam); }
        }

        private static void StaticHandleDroppedFiles(IntPtr hDrop)
        {
            List<string> filesToProcess = new List<string>();
            try { uint fileCount = DragQueryFile(hDrop, 0xFFFFFFFF, null, 0); Log?.Info($"Number of files dropped: {fileCount}"); for (uint i = 0; i < fileCount; i++) { uint pathLength = DragQueryFile(hDrop, i, null, 0); if (pathLength == 0) continue; StringBuilder sb = new StringBuilder((int)pathLength + 1); if (DragQueryFile(hDrop, i, sb, (uint)sb.Capacity) > 0) { string filePath = sb.ToString(); Log?.Debug($"Adding dropped file to queue: {filePath}"); filesToProcess.Add(filePath); } } }
            catch (Exception ex) { Log?.Error($"Error querying dropped files: {ex.Message}"); Log?.Debug(ex); }
            finally { DragFinish(hDrop); Log?.Debug("Drag operation finished and memory released."); }
            if (filesToProcess.Any()) { Log?.Info($"Queueing processing for {filesToProcess.Count} files."); Task.Run(() => ProcessDroppedFilesBatch(filesToProcess)); }
        }

        // Modified to call ProcessMapZip
        private static void ProcessDroppedFilesBatch(List<string> filePaths)
        {
             bool anyProcessedSuccessfully = false;
             Log?.Debug($"Processing {filePaths.Count} files in background task.");
             foreach (string filePath in filePaths) { try { if (Path.GetExtension(filePath).Equals(".zip", StringComparison.OrdinalIgnoreCase)) { Log?.Info($"Processing ZIP file: {filePath}"); if (Instance != null) { bool processed = Instance.ProcessMapZip(filePath); if (processed) anyProcessedSuccessfully = true; } else { Log?.Error("Plugin instance is null, cannot process map zip."); } } else { Log?.Warn($"Skipping non-zip file: {filePath}"); } } catch (Exception ex) { Log?.Error($"Error processing file {filePath}: {ex.Message}"); Log?.Debug(ex); } }
             if (anyProcessedSuccessfully && Instance != null) { Log?.Info("Finished processing dropped files batch. Requesting song refresh."); Instance.RequestSongRefresh(); }
             else if (anyProcessedSuccessfully) { Log?.Error("Files processed but Plugin instance is null. Cannot request refresh."); }
             else { Log?.Debug("No files successfully processed, skipping song refresh request."); }
        }

    } // End Plugin class
} // End namespace
=======
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
>>>>>>> Stashed changes
