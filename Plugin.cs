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