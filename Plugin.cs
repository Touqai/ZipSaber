using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using IPA;
// using IPA.Logging; // Removed reference
// using IPA.Utilities.Async; // Removed reference
using SongCore;
using UnityEngine;

#nullable disable // Disable nullable context for the whole file for net472 compatibility

namespace ZipSaber
{
    /// <summary>
    /// Main BSIPA Plugin class for ZipSaber. Handles window hooking, drag-and-drop to CustomWipLevels, and duplicate folder renaming.
    /// *** WARNING: Logging and BSIPA Main Thread Scheduling are disabled due to missing references. ***
    /// </summary>
    [Plugin(RuntimeOptions.SingleStartInit)]
    public class Plugin
    {
        #region Constants
        private const string UnityWindowClass = "UnityWndClass";
        private const string BeatSaberWindowTitle = "Beat Saber";
        private const string CustomWipLevelsFolderName = "CustomWipLevels";
        private const string BeatSaberDataFolderName = "Beat Saber_Data";
        #endregion

        #region Static Plugin Instance
        internal static Plugin Instance { get; private set; }
        #endregion

        #region WinAPI Imports and Constants
        private const int GWLP_WNDPROC = -4;
        private const uint WM_DROPFILES = 0x233;
        internal delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)] private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)] private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        // Removed SetLastError from DragAcceptFiles as it seemed to cause issues in the environment
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern void DragAcceptFiles(IntPtr hWnd, bool fAccept);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern uint DragQueryFile(IntPtr hDrop, uint iFile, [Out] StringBuilder lpszFile, uint cch);
        [DllImport("shell32.dll")] private static extern void DragFinish(IntPtr hDrop);
        [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern IntPtr DefWindowProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

        // Added nullable annotations '?' to fix CS8625 warnings
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern IntPtr FindWindow(string lpClassName, string lpWindowName);
        #endregion

        #region Hook State Fields
        private static IntPtr Hwnd = IntPtr.Zero;
        private static WndProcDelegate wndProcDelegate;
        private static IntPtr oldWndProc = IntPtr.Zero;
        private static string CustomWipLevelsPath;
        private static bool hooksAttempted = false;
        private static bool hooksActive = false;
        #endregion

        #region BSIPA Plugin Lifecycle Methods
        [Init]
        public Plugin(/*IPA.Logging.Logger logger*/) // Logger parameter removed
        {
            Instance = this;
            Console.WriteLine("[ZipSaber] Initializing (Duplicate Handling Enabled, Logging Disabled).");
        }

        [OnEnable]
        public void OnEnable()
        {
            Console.WriteLine("[ZipSaber] OnEnable called.");
            CalculatePaths();

            if (!hooksAttempted && !hooksActive)
            {
                Console.WriteLine("[ZipSaber] Scheduling delayed hook setup.");
                hooksAttempted = true;
                Task.Run(async () => { await Task.Delay(3000); AttemptFindAndHookWindow(); });
            }
            else { Console.WriteLine($"[ZipSaber] Hook setup already attempted/active. Skipping."); }
        }

        [OnDisable]
        public void OnDisable()
        {
            Console.WriteLine("[ZipSaber] OnDisable called.");
            CleanupWindowHook();
            hooksAttempted = false;
        }
        #endregion

        #region Path Calculation
        private void CalculatePaths()
        {
            if (!string.IsNullOrEmpty(CustomWipLevelsPath)) return;
            try
            {
                string gameDataPath = Application.dataPath;
                if (string.IsNullOrEmpty(gameDataPath)) { Console.WriteLine("[ZipSaber][Pathing] Application.dataPath null!"); CustomWipLevelsPath = null; return; }
                DirectoryInfo gameDataDir = new DirectoryInfo(gameDataPath);
                DirectoryInfo gameDirInfo = gameDataDir.Parent;
                if (gameDirInfo == null) { Console.WriteLine("[ZipSaber][Pathing] Parent directory null!"); CustomWipLevelsPath = null; return; }
                string gameDir = gameDirInfo.FullName;

                CustomWipLevelsPath = Path.Combine(gameDir, BeatSaberDataFolderName, CustomWipLevelsFolderName);
                Console.WriteLine($"[ZipSaber][Pathing] Target CustomWipLevels: {CustomWipLevelsPath}");
                EnsureDirectoryExists(CustomWipLevelsPath, "[ZipSaber][Pathing]");
            }
            catch (Exception e) { Console.WriteLine($"[ZipSaber][Pathing] Failed: {e.Message}"); CustomWipLevelsPath = null; }
        }

        private bool EnsureDirectoryExists(string path, string logContext)
        {
             // Added null check here as well for robustness, fixing CS8604 implicitly
             if (string.IsNullOrEmpty(path))
             {
                Console.WriteLine($"{logContext} Received null or empty path.");
                return false;
             }
             if (!Directory.Exists(path)) { Console.WriteLine($"{logContext} Dir not found. Creating: {path}"); try { Directory.CreateDirectory(path); Console.WriteLine($"{logContext} Created: {Path.GetFileName(path)}"); return true; } catch (Exception ex) { Console.WriteLine($"{logContext} Failed create '{Path.GetFileName(path)}': {ex.Message}"); return false; } }
             return true;
        }
        #endregion

        #region Window Hooking Logic
        private void AttemptFindAndHookWindow()
        {
             Console.WriteLine("[ZipSaber][Hooking] Finding window...");
            IntPtr foundHwnd = FindWindow(UnityWindowClass, null); // Call uses null okay now
            if (foundHwnd == IntPtr.Zero) foundHwnd = FindWindow(null, BeatSaberWindowTitle); // Call uses null okay now
            if (foundHwnd == IntPtr.Zero) { try { foundHwnd = Process.GetCurrentProcess().MainWindowHandle; } catch { /* Ignore */ } }

            if (foundHwnd != IntPtr.Zero) { Console.WriteLine($"[ZipSaber][Hooking] Found {foundHwnd}. Setting hook."); SetupWindowHook(foundHwnd); }
            else { Console.WriteLine("[ZipSaber][Hooking] Failed find window handle."); }
        }

        private void SetupWindowHook(IntPtr windowHandle)
        {
            if (hooksActive || Hwnd != IntPtr.Zero) { Console.WriteLine("[ZipSaber][Hooking] Already active/hooked."); return; }
            try
            {
                Hwnd = windowHandle; Console.WriteLine($"[ZipSaber][Hooking] Setting hook for {Hwnd}"); CalculatePaths();
                if (string.IsNullOrEmpty(CustomWipLevelsPath)) { Console.WriteLine("[ZipSaber][Hooking] Abort: Path not set."); Hwnd = IntPtr.Zero; return; }
                wndProcDelegate = new WndProcDelegate(StaticWndProc); IntPtr wndProcPtr = Marshal.GetFunctionPointerForDelegate(wndProcDelegate); oldWndProc = SetWindowLongPtr(Hwnd, GWLP_WNDPROC, wndProcPtr); int err = Marshal.GetLastWin32Error();
                if (oldWndProc == IntPtr.Zero && err != 0) { Console.WriteLine($"[ZipSaber][Hooking] SetWinLongPtr failed: {err}"); CleanupWindowHook(); return; }

                // Call DragAcceptFiles without checking error code now
                DragAcceptFiles(Hwnd, true);
                Console.WriteLine("[ZipSaber][Hooking] Setup successful (DragAcceptFiles called)."); hooksActive = true;

            } catch (Exception ex) { Console.WriteLine($"[ZipSaber][Hooking] Critical setup error: {ex.Message}"); CleanupWindowHook(); }
        }

        private void CleanupWindowHook()
        {
             Console.WriteLine("[ZipSaber][Hooking] Cleaning up..."); IntPtr currentHwnd = Hwnd; IntPtr currentOldWndProc = oldWndProc;
            if (currentHwnd != IntPtr.Zero) { try { DragAcceptFiles(currentHwnd, false); } catch { } if (currentOldWndProc != IntPtr.Zero) { try { SetWindowLongPtr(currentHwnd, GWLP_WNDPROC, currentOldWndProc); } catch { } } }
            wndProcDelegate = null; oldWndProc = IntPtr.Zero; Hwnd = IntPtr.Zero; hooksActive = false; Console.WriteLine("[ZipSaber][Hooking] Cleanup finished.");
        }

        internal static IntPtr StaticWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == WM_DROPFILES) { Console.WriteLine($"[ZipSaber][WndProc] WM_DROPFILES received. Processing async."); Task.Run(() => StaticHandleDroppedFiles(wParam)); return IntPtr.Zero; }
            IntPtr currentOldWndProc = oldWndProc; if (currentOldWndProc != IntPtr.Zero) { try { return CallWindowProc(currentOldWndProc, hWnd, msg, wParam, lParam); } catch (Exception ex) { Console.WriteLine($"[ZipSaber][WndProc] Error calling original: {ex.Message}. Using Def."); return DefWindowProc(hWnd, msg, wParam, lParam); } }
             Console.WriteLine($"[ZipSaber][WndProc] Original Zero. Using Def."); return DefWindowProc(hWnd, msg, wParam, lParam);
        }
        #endregion

        #region Dropped File Handling
        private static void StaticHandleDroppedFiles(IntPtr hDrop)
        {
            List<string> filesToProcess = new List<string>();
            try
            {
                uint fileCount = DragQueryFile(hDrop, 0xFFFFFFFF, null, 0); Console.WriteLine($"[ZipSaber][Drop] {fileCount} files dropped.");
                for (uint i = 0; i < fileCount; i++) { uint len = DragQueryFile(hDrop, i, null, 0); if (len > 0) { StringBuilder sb = new StringBuilder((int)len + 1); if (DragQueryFile(hDrop, i, sb, (uint)sb.Capacity) > 0) filesToProcess.Add(sb.ToString()); } }
            } catch (Exception ex) { Console.WriteLine($"[ZipSaber][Drop] Error query: {ex.Message}"); }
            finally { try { DragFinish(hDrop); } catch { /* Ignore DragFinish errors */ } }

            if (filesToProcess.Any()) { ProcessDroppedFilesBatch(filesToProcess); }
            else { Console.WriteLine("[ZipSaber][Drop] No files retrieved."); }
        }

        private static void ProcessDroppedFilesBatch(List<string> filePaths)
        {
             bool anyProcessedSuccessfully = false; int successCount = 0; int failCount = 0; int skipCount = 0;
             Console.WriteLine($"[ZipSaber][Proc] Starting batch for {filePaths.Count} files.");
             foreach (string filePath in filePaths) { try { if (Path.GetExtension(filePath).Equals(".zip", StringComparison.OrdinalIgnoreCase)) { Console.WriteLine($"[ZipSaber][Proc] ZIP: {Path.GetFileName(filePath)}"); if (Plugin.Instance?.ProcessMapZip(filePath) == true) { successCount++; anyProcessedSuccessfully = true; } else { failCount++; } } else { skipCount++; Console.WriteLine($"[ZipSaber][Proc] Skipping non-zip: {Path.GetFileName(filePath)}"); } } catch (Exception ex) { Console.WriteLine($"[ZipSaber][Proc] Batch error for '{Path.GetFileName(filePath)}': {ex.Message}"); failCount++; } }
             Console.WriteLine($"[ZipSaber][Proc] Batch finished. Succeeded={successCount}, Failed/Skipped-Zip={failCount}, Skipped-NonZip={skipCount}.");
             if (anyProcessedSuccessfully) { Plugin.Instance?.RequestSongRefresh(); }
        }
        #endregion

        #region Map Processing Logic (with Duplicate Handling)
        internal bool ProcessMapZip(string zipFilePath)
        {
            string mapNameForLog = Path.GetFileNameWithoutExtension(zipFilePath);
            string targetBasePath = CustomWipLevelsPath;
            string finalExtractionDir = null;
            bool extractionOk = false;
            bool validationOk = false;

             Console.WriteLine($"[ZipSaber][MapProc - {mapNameForLog}] Start");
            try
            {
                // Fix for CS8604: Explicitly check targetBasePath before calling EnsureDirectoryExists
                if (string.IsNullOrEmpty(targetBasePath))
                {
                    Console.WriteLine($"[ZipSaber][MapProc - {mapNameForLog}] Target base path is null or empty.");
                    return false;
                }
                if (!EnsureDirectoryExists(targetBasePath, $"[ZipSaber][MapProc - {mapNameForLog}]"))
                {
                    Console.WriteLine($"[ZipSaber][MapProc - {mapNameForLog}] EnsureDirectoryExists failed for target path.");
                    return false;
                }

                // --- Duplicate Check and Renaming Logic ---
                string sanitizedBaseName = SanitizeFolderName(mapNameForLog);
                string currentExtractionDir = Path.Combine(targetBasePath, sanitizedBaseName);

                if (Directory.Exists(currentExtractionDir))
                {
                     Console.WriteLine($"[ZipSaber][MapProc - {mapNameForLog}] Folder exists. Finding unique...");
                    int copyNumber = 1; string potentialName;
                    do { potentialName = $"{sanitizedBaseName}_{copyNumber}"; currentExtractionDir = Path.Combine(targetBasePath, potentialName); copyNumber++; if (copyNumber > 100) { Console.WriteLine($"[ZipSaber][MapProc - {mapNameForLog}] Unique name abort."); return false; } }
                    while (Directory.Exists(currentExtractionDir));
                     Console.WriteLine($"[ZipSaber][MapProc - {mapNameForLog}] Using unique: {potentialName}");
                }
                finalExtractionDir = currentExtractionDir;

                Console.WriteLine($"[ZipSaber][MapProc - {mapNameForLog}] Final target: {finalExtractionDir}");
                Console.WriteLine($"[ZipSaber][MapProc - {mapNameForLog}] Extracting...");
                try
                {
                    Directory.CreateDirectory(finalExtractionDir);
                    ZipFile.ExtractToDirectory(zipFilePath, finalExtractionDir);
                     Console.WriteLine($"[ZipSaber][MapProc - {mapNameForLog}] Extracted.");
                    extractionOk = true;
                }
                catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException)
                { Console.WriteLine($"[ZipSaber][MapProc - {mapNameForLog}] Extract fail: {ex.GetType().Name} - {ex.Message}"); }
                catch (Exception ex)
                { Console.WriteLine($"[ZipSaber][MapProc - {mapNameForLog}] Extract unexpected error: {ex.GetType().Name} - {ex.Message}"); }

                if (extractionOk)
                {
                    Console.WriteLine($"[ZipSaber][MapProc - {mapNameForLog}] Validating...");
                    validationOk = IsValidMapFolder(finalExtractionDir);
                     if (!validationOk) { Console.WriteLine($"[ZipSaber][MapProc - {mapNameForLog}] Validation failed."); }
                     else { Console.WriteLine($"[ZipSaber][MapProc - {mapNameForLog}] Validation passed."); }
                }
            }
            catch (Exception ex) { Console.WriteLine($"[ZipSaber][MapProc - {mapNameForLog}] Outer error: {ex.Message}"); }
            finally
            {
                if (finalExtractionDir != null && extractionOk && !validationOk && Directory.Exists(finalExtractionDir))
                {
                      Console.WriteLine($"[ZipSaber][MapProc - {mapNameForLog}] Validation failed. Cleaning up.");
                     TryDeleteDirectory(finalExtractionDir);
                }
            }
            return extractionOk && validationOk;
        }

        private bool IsValidMapFolder(string folderPath)
        {
             if (!Directory.Exists(folderPath)) { return false; }
             try {
                 bool hasInfo = Directory.EnumerateFiles(folderPath, "info.dat", SearchOption.TopDirectoryOnly).Any(f => Path.GetFileName(f).Equals("info.dat", StringComparison.OrdinalIgnoreCase));
                 bool hasAudio = Directory.EnumerateFiles(folderPath, "*.*", SearchOption.TopDirectoryOnly).Any(f => { var ext = Path.GetExtension(f); return ext.Equals(".egg", StringComparison.OrdinalIgnoreCase) || ext.Equals(".ogg", StringComparison.OrdinalIgnoreCase) || ext.Equals(".wav", StringComparison.OrdinalIgnoreCase); });
                 bool hasDifficulty = Directory.EnumerateFiles(folderPath, "*.dat", SearchOption.TopDirectoryOnly).Any(f => !Path.GetFileName(f).Equals("info.dat", StringComparison.OrdinalIgnoreCase));
                 return hasInfo && hasAudio && hasDifficulty;
             } catch (Exception ex) { Console.WriteLine($"[ZipSaber][Validation] Error: {ex.Message}"); return false; }
        }

        private string SanitizeFolderName(string name)
        {
            char[] invalidChars = Path.GetInvalidFileNameChars(); string sanitized = string.Join("_", name.Split(invalidChars, StringSplitOptions.RemoveEmptyEntries)).TrimEnd('.', ' '); if (string.IsNullOrWhiteSpace(sanitized)) { string fallbackName = "ImportedMap_" + Guid.NewGuid().ToString("N").Substring(0, 8); return fallbackName; } return sanitized;
        }
        #endregion

        #region Utilities
        /// <summary>Requests SongCore song refresh using direct call.</summary>
        internal void RequestSongRefresh()
        {
            try
            {
                if (Loader.Instance != null)
                {
                     Console.WriteLine("[ZipSaber][Refresh] Requesting refresh (Direct Call).");
                     try
                     {
                          Console.WriteLine("[ZipSaber][Refresh] WARNING: Calling RefreshSongs directly.");
                          Loader.Instance.RefreshSongs(false);
                     }
                     catch (Exception ex) { Console.WriteLine($"[ZipSaber][Refresh] Error calling RefreshSongs directly: {ex.Message}"); }
                }
                else { Console.WriteLine("[ZipSaber][Refresh] SongCore Loader.Instance is null."); }
            } catch (Exception ex) { Console.WriteLine($"[ZipSaber][Refresh] Error requesting refresh: {ex.Message}"); }
        }

        /// <summary>Safely attempts to delete a directory recursively.</summary>
        internal void TryDeleteDirectory(string path)
        {
             if (string.IsNullOrEmpty(path)) return; string dirName = Path.GetFileName(path); try { if (Directory.Exists(path)) { /*Console.WriteLine($"[ZipSaber][Cleanup] Deleting: {dirName}");*/ Directory.Delete(path, true); } } catch (Exception cleanEx) { Console.WriteLine($"[ZipSaber][Cleanup] Failed delete '{dirName}': {cleanEx.Message}"); }
        }
        #endregion
    }
}