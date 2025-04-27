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
using IPA.Logging; // Using statement included
using IPA.Utilities.Async; // Using statement included
using SongCore;
using UnityEngine;

namespace ZipSaber
{
    /// <summary>
    /// Main BSIPA Plugin class for ZipSaber. Handles window hooking, drag-and-drop to CustomWipLevels, and duplicate folder renaming.
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

        #region Static Plugin Instance & Logger
        internal static Plugin Instance { get; private set; }
        internal static IPA.Logging.Logger Log { get; private set; } // Logger included
        #endregion

        #region WinAPI Imports and Constants
        private const int GWLP_WNDPROC = -4;
        private const uint WM_DROPFILES = 0x233;
        internal delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)] private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)] private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern void DragAcceptFiles(IntPtr hWnd, bool fAccept);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern uint DragQueryFile(IntPtr hDrop, uint iFile, [Out] StringBuilder lpszFile, uint cch);
        [DllImport("shell32.dll")] private static extern void DragFinish(IntPtr hDrop);
        [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern IntPtr DefWindowProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)] private static extern IntPtr FindWindow(string lpClassName, string lpWindowName);
        #endregion

        #region Hook State Fields
        private static IntPtr Hwnd = IntPtr.Zero;
        private static WndProcDelegate wndProcDelegate;
        private static IntPtr oldWndProc = IntPtr.Zero;
        private static string CustomWipLevelsPath; // Only WIP path needed
        private static bool hooksAttempted = false;
        private static bool hooksActive = false;
        #endregion

        #region BSIPA Plugin Lifecycle Methods
        [Init]
        public Plugin(IPA.Logging.Logger logger)
        {
            Instance = this;
            Log = logger;
            Log?.Info("ZipSaber initializing (Duplicate Handling Enabled).");
        }

        [OnEnable]
        public void OnEnable()
        {
            Log?.Info("ZipSaber OnEnable called.");
            CalculatePaths();

            if (!hooksAttempted && !hooksActive)
            {
                Log?.Debug("Scheduling delayed hook setup.");
                hooksAttempted = true;
                Task.Run(async () => { await Task.Delay(3000); AttemptFindAndHookWindow(); });
            }
            else { Log?.Warn($"Hook setup already attempted/active (Attempted={hooksAttempted}, Active={hooksActive}). Skipping OnEnable setup."); }
        }

        [OnDisable]
        public void OnDisable()
        {
            Log?.Info("ZipSaber OnDisable called.");
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
                if (string.IsNullOrEmpty(gameDataPath)) { Log?.Error("[Pathing] Application.dataPath is null or empty!"); CustomWipLevelsPath = null; return; }
                DirectoryInfo gameDataDir = new DirectoryInfo(gameDataPath);
                DirectoryInfo gameDirInfo = gameDataDir.Parent;
                if (gameDirInfo == null) { Log?.Error("[Pathing] Could not get parent directory!"); CustomWipLevelsPath = null; return; }
                string gameDir = gameDirInfo.FullName;

                CustomWipLevelsPath = Path.Combine(gameDir, BeatSaberDataFolderName, CustomWipLevelsFolderName);
                Log?.Info($"[Pathing] Target CustomWipLevels path: {CustomWipLevelsPath}");
                EnsureDirectoryExists(CustomWipLevelsPath, "[Pathing]");
            }
            catch (Exception e) { Log?.Error($"[Pathing] Failed: {e.Message}"); Log?.Debug(e); CustomWipLevelsPath = null; }
        }

        private bool EnsureDirectoryExists(string path, string logContext)
        {
             if (string.IsNullOrEmpty(path)) return false;
             if (!Directory.Exists(path)) { Log?.Warn($"{logContext} Directory not found. Creating: {path}"); try { Directory.CreateDirectory(path); Log?.Info($"{logContext} Created: {Path.GetFileName(path)}"); return true; } catch (Exception ex) { Log?.Error($"{logContext} Failed create '{Path.GetFileName(path)}': {ex.Message}"); return false; } }
             return true;
        }
        #endregion

        #region Window Hooking Logic
        private void AttemptFindAndHookWindow()
        {
            Log?.Info("[Hooking] Finding Beat Saber window...");
            IntPtr foundHwnd = FindWindow(UnityWindowClass, null);
            if (foundHwnd == IntPtr.Zero) foundHwnd = FindWindow(null, BeatSaberWindowTitle);
            if (foundHwnd == IntPtr.Zero) { try { foundHwnd = Process.GetCurrentProcess().MainWindowHandle; } catch { /* Ignore */ } }

            if (foundHwnd != IntPtr.Zero) { Log?.Info($"[Hooking] Found window {foundHwnd}. Setting up hook."); SetupWindowHook(foundHwnd); }
            else { Log?.Error("[Hooking] Failed to find window handle."); }
        }

        private void SetupWindowHook(IntPtr windowHandle)
        {
            if (hooksActive || Hwnd != IntPtr.Zero) { Log?.Warn($"[Hooking] Already active/hooked. Skipping."); return; }
            try
            {
                Hwnd = windowHandle; Log?.Info($"[Hooking] Setting hook for HWND: {Hwnd}"); CalculatePaths();
                if (string.IsNullOrEmpty(CustomWipLevelsPath)) { Log?.Error("[Hooking] Aborting: CustomWipLevelsPath not set."); Hwnd = IntPtr.Zero; return; }
                wndProcDelegate = new WndProcDelegate(StaticWndProc); IntPtr wndProcPtr = Marshal.GetFunctionPointerForDelegate(wndProcDelegate); oldWndProc = SetWindowLongPtr(Hwnd, GWLP_WNDPROC, wndProcPtr); int err = Marshal.GetLastWin32Error();
                if (oldWndProc == IntPtr.Zero && err != 0) { Log?.Error($"[Hooking] SetWindowLongPtr failed: {err} ({new Win32Exception(err).Message})"); CleanupWindowHook(); return; }
                DragAcceptFiles(Hwnd, true); err = Marshal.GetLastWin32Error();
                if (err != 0) { Log?.Error($"[Hooking] DragAcceptFiles(true) failed: {err} ({new Win32Exception(err).Message})"); CleanupWindowHook(); return; }
                Log?.Info("[Hooking] Hook setup successful."); hooksActive = true;
            } catch (Exception ex) { Log?.Error($"[Hooking] Critical setup error: {ex.Message}"); Log?.Debug(ex); CleanupWindowHook(); }
        }

        private void CleanupWindowHook()
        {
            Log?.Info("[Hooking] Cleaning up hooks..."); IntPtr currentHwnd = Hwnd; IntPtr currentOldWndProc = oldWndProc;
            if (currentHwnd != IntPtr.Zero) { try { DragAcceptFiles(currentHwnd, false); } catch { } if (currentOldWndProc != IntPtr.Zero) { try { SetWindowLongPtr(currentHwnd, GWLP_WNDPROC, currentOldWndProc); } catch { } } }
            wndProcDelegate = null; oldWndProc = IntPtr.Zero; Hwnd = IntPtr.Zero; hooksActive = false; Log?.Info("[Hooking] Cleanup finished.");
        }

        internal static IntPtr StaticWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == WM_DROPFILES) { Log?.Info($"[WndProc] WM_DROPFILES received. Processing async."); Task.Run(() => StaticHandleDroppedFiles(wParam)); return IntPtr.Zero; }
            IntPtr currentOldWndProc = oldWndProc; if (currentOldWndProc != IntPtr.Zero) { try { return CallWindowProc(currentOldWndProc, hWnd, msg, wParam, lParam); } catch (Exception ex) { Log?.Error($"[WndProc] Error calling original: {ex.Message}. Using DefWindowProc."); return DefWindowProc(hWnd, msg, wParam, lParam); } }
            Log?.Warn($"[WndProc] Original WndProc Zero. Using DefWindowProc."); return DefWindowProc(hWnd, msg, wParam, lParam);
        }
        #endregion

        #region Dropped File Handling
        private static void StaticHandleDroppedFiles(IntPtr hDrop)
        {
            List<string> filesToProcess = new List<string>();
            try
            {
                uint fileCount = DragQueryFile(hDrop, 0xFFFFFFFF, null, 0); Log?.Info($"[Drop Handling] {fileCount} files dropped.");
                for (uint i = 0; i < fileCount; i++) { uint len = DragQueryFile(hDrop, i, null, 0); if (len > 0) { StringBuilder sb = new StringBuilder((int)len + 1); if (DragQueryFile(hDrop, i, sb, (uint)sb.Capacity) > 0) filesToProcess.Add(sb.ToString()); } }
            } catch (Exception ex) { Log?.Error($"[Drop Handling] Error querying files: {ex.Message}"); Log?.Debug(ex); }
            finally { try { DragFinish(hDrop); Log?.Debug("[Drop Handling] DragFinish called."); } catch { } }

            if (filesToProcess.Any()) { ProcessDroppedFilesBatch(filesToProcess); } // Already background
            else { Log?.Info("[Drop Handling] No files retrieved."); }
        }

        private static void ProcessDroppedFilesBatch(List<string> filePaths)
        {
             bool anyProcessedSuccessfully = false; int successCount = 0; int failCount = 0; int skipCount = 0;
             Log?.Info($"[File Processing] Starting batch for {filePaths.Count} files.");
             foreach (string filePath in filePaths) { try { if (Path.GetExtension(filePath).Equals(".zip", StringComparison.OrdinalIgnoreCase)) { Log?.Info($"[File Processing] Processing ZIP: {Path.GetFileName(filePath)}"); if (Plugin.Instance?.ProcessMapZip(filePath) == true) { successCount++; anyProcessedSuccessfully = true; } else { failCount++; } } else { skipCount++; Log?.Warn($"[File Processing] Skipping non-zip: {Path.GetFileName(filePath)}"); } } catch (Exception ex) { Log?.Error($"[File Processing] Batch error for '{Path.GetFileName(filePath)}': {ex.Message}"); Log?.Debug(ex); failCount++; } }
             Log?.Info($"[File Processing] Batch finished. Succeeded={successCount}, Failed/Skipped-Zip={failCount}, Skipped-NonZip={skipCount}.");
             if (anyProcessedSuccessfully) { Plugin.Instance?.RequestSongRefresh(); }
        }
        #endregion

        #region Map Processing Logic (with Duplicate Handling)
        /// <summary>
        /// Extracts, validates, and moves a single map zip ONLY to CustomWipLevels, handling duplicates by appending numbers.
        /// </summary>
        /// <param name="zipFilePath">The full path to the zip file.</param>
        /// <returns>True if the zip was successfully extracted and validated, false otherwise.</returns>
        internal bool ProcessMapZip(string zipFilePath)
        {
            string mapNameForLog = Path.GetFileNameWithoutExtension(zipFilePath);
            string targetBasePath = CustomWipLevelsPath; // Target is always WIP levels
            string finalExtractionDir = null; // Initialize to null
            bool extractionOk = false;
            bool validationOk = false;

            Log?.Debug($"[ProcessMapZip - {mapNameForLog}] Starting processing for target: CustomWipLevels");

            try
            {
                // Ensure WIP path exists
                if (string.IsNullOrEmpty(targetBasePath) || !EnsureDirectoryExists(targetBasePath, $"[ProcessMapZip - {mapNameForLog}]"))
                {
                    Log?.Error($"[ProcessMapZip - {mapNameForLog}] Target path 'CustomWipLevels' is invalid or could not be created.");
                    return false;
                }

                // --- Duplicate Check and Renaming Logic ---
                string sanitizedBaseName = SanitizeFolderName(mapNameForLog);
                string currentExtractionDir = Path.Combine(targetBasePath, sanitizedBaseName);

                if (Directory.Exists(currentExtractionDir))
                {
                    Log?.Warn($"[ProcessMapZip - {mapNameForLog}] Folder '{sanitizedBaseName}' already exists. Finding unique name...");
                    int copyNumber = 1;
                    string potentialName;
                    do
                    {
                        potentialName = $"{sanitizedBaseName}_{copyNumber}"; // Append _1, _2, etc.
                        currentExtractionDir = Path.Combine(targetBasePath, potentialName);
                        Log?.Debug($"[ProcessMapZip - {mapNameForLog}] Checking potential name: {potentialName}");
                        copyNumber++;
                        if (copyNumber > 100) // Safety break to prevent potential infinite loop
                        {
                             Log?.Error($"[ProcessMapZip - {mapNameForLog}] Could not find unique name after 100 attempts. Aborting.");
                             return false;
                        }
                    } while (Directory.Exists(currentExtractionDir));
                    Log?.Info($"[ProcessMapZip - {mapNameForLog}] Using unique name: {potentialName}");
                }
                finalExtractionDir = currentExtractionDir; // Set the final path (original or numbered)
                // --- End Duplicate Check ---


                Log?.Info($"[ProcessMapZip - {mapNameForLog}] Final target directory: {finalExtractionDir}");
                Log?.Debug($"[ProcessMapZip - {mapNameForLog}] Attempting extraction...");
                try
                {
                    Directory.CreateDirectory(finalExtractionDir);
                    ZipFile.ExtractToDirectory(zipFilePath, finalExtractionDir);
                    Log?.Info($"[ProcessMapZip - {mapNameForLog}] Extracted successfully.");
                    extractionOk = true;
                }
                catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException)
                { Log?.Error($"[ProcessMapZip - {mapNameForLog}] Extraction failed: {ex.GetType().Name} - {ex.Message}"); }
                catch (Exception ex)
                { Log?.Error($"[ProcessMapZip - {mapNameForLog}] Unexpected extraction error: {ex.GetType().Name} - {ex.Message}"); Log?.Debug(ex); }

                if (extractionOk)
                {
                     Log?.Debug($"[ProcessMapZip - {mapNameForLog}] Validating extracted contents...");
                    validationOk = IsValidMapFolder(finalExtractionDir);
                     if (!validationOk) { Log?.Warn($"[ProcessMapZip - {mapNameForLog}] Validation failed."); }
                     else { Log?.Info($"[ProcessMapZip - {mapNameForLog}] Validation passed."); }
                }
            }
            catch (Exception ex) { Log?.Error($"[ProcessMapZip - {mapNameForLog}] Outer processing error: {ex.Message}"); Log?.Debug(ex); }
            finally
            {
                // Cleanup if extraction happened but validation failed
                if (finalExtractionDir != null && extractionOk && !validationOk && Directory.Exists(finalExtractionDir))
                {
                     Log?.Warn($"[ProcessMapZip - {mapNameForLog}] Validation failed. Cleaning up target directory: {Path.GetFileName(finalExtractionDir)}");
                     TryDeleteDirectory(finalExtractionDir);
                }
                 // No need to clean up if extraction failed, as TryDeleteDirectory isn't robust against partial creation inside the loop
            }
            return extractionOk && validationOk; // Return true only if both steps succeeded
        }

        /// <summary>Checks if a folder contains essential Beat Saber map files.</summary>
        private bool IsValidMapFolder(string folderPath) // (Unchanged)
        {
            string context = $"[Validation - {Path.GetFileName(folderPath)}]"; if (!Directory.Exists(folderPath)) { Log?.Warn($"{context} Folder not found: {folderPath}"); return false; } try { bool hasInfo = Directory.EnumerateFiles(folderPath, "info.dat", SearchOption.TopDirectoryOnly).Any(f => Path.GetFileName(f).Equals("info.dat", StringComparison.OrdinalIgnoreCase)); bool hasAudio = Directory.EnumerateFiles(folderPath, "*.*", SearchOption.TopDirectoryOnly).Any(f => { var ext = Path.GetExtension(f); return ext.Equals(".egg", StringComparison.OrdinalIgnoreCase) || ext.Equals(".ogg", StringComparison.OrdinalIgnoreCase) || ext.Equals(".wav", StringComparison.OrdinalIgnoreCase); }); bool hasDifficulty = Directory.EnumerateFiles(folderPath, "*.dat", SearchOption.TopDirectoryOnly).Any(f => !Path.GetFileName(f).Equals("info.dat", StringComparison.OrdinalIgnoreCase)); Log?.Debug($"{context} Checks: Info={hasInfo}, Audio={hasAudio}, Difficulty={hasDifficulty}"); if (!hasInfo) Log?.Warn($"{context} Missing info.dat"); if (!hasAudio) Log?.Warn($"{context} Missing audio"); if (!hasDifficulty) Log?.Warn($"{context} Missing difficulty"); return hasInfo && hasAudio && hasDifficulty; } catch (Exception ex) { Log?.Error($"{context} Validation error: {ex.Message}"); Log?.Debug(ex); return false; }
        }

        /// <summary>Sanitizes a string for use as a folder name.</summary>
        private string SanitizeFolderName(string name) // (Unchanged)
        {
            char[] invalidChars = Path.GetInvalidFileNameChars(); string sanitized = string.Join("_", name.Split(invalidChars, StringSplitOptions.RemoveEmptyEntries)).TrimEnd('.', ' '); if (string.IsNullOrWhiteSpace(sanitized)) { string fallbackName = "ImportedMap_" + Guid.NewGuid().ToString("N").Substring(0, 8); Log?.Warn($"[Sanitize] Name '{name}' empty post-sanitize. Fallback: {fallbackName}"); return fallbackName; } if (sanitized != name) { Log?.Debug($"[Sanitize] '{name}' -> '{sanitized}'"); } return sanitized;
        }
        #endregion

        #region Utilities
        /// <summary>Requests SongCore song refresh. Attempts use of main thread scheduler.</summary>
        internal void RequestSongRefresh() // (Unchanged from previous attempt)
        {
            try { if (Loader.Instance != null) { Log?.Info("[Refresh] Requesting SongCore refresh."); try { UnityMainThreadTaskScheduler.Factory.StartNew(() => { try { Log?.Debug("[Refresh] Executing RefreshSongs(false) on main thread."); Loader.Instance.RefreshSongs(false); Log?.Info("[Refresh] RefreshSongs executed via scheduler."); } catch (Exception ex) { Log?.Error($"[Refresh] Error calling RefreshSongs via scheduler: {ex.Message}"); Log?.Debug(ex); } }); } catch (TypeLoadException tlEx) { Log?.Error($"[Refresh] Could not use Scheduler (BSIPA.Utilities missing?): {tlEx.Message}. Attempting direct call..."); try { Log?.Warn("[Refresh] WARNING: Calling RefreshSongs directly."); Loader.Instance.RefreshSongs(false); } catch (Exception ex) { Log?.Error($"[Refresh] Error calling RefreshSongs directly: {ex.Message}"); Log?.Debug(ex); } } catch (Exception schedEx) { Log?.Error($"[Refresh] Error using Scheduler: {schedEx.Message}"); Log?.Debug(schedEx); } } else { Log?.Warn("[Refresh] SongCore Loader.Instance is null."); } } catch (Exception ex) { Log?.Error($"[Refresh] Error requesting refresh: {ex.Message}"); Log?.Debug(ex); }
        }

        /// <summary>Safely attempts to delete a directory recursively.</summary>
        internal void TryDeleteDirectory(string path) // (Unchanged)
        {
             if (string.IsNullOrEmpty(path)) return; string dirName = Path.GetFileName(path); try { if (Directory.Exists(path)) { Log?.Debug($"[Cleanup] Deleting directory: {dirName}"); Directory.Delete(path, true); Log?.Debug($"[Cleanup] Deleted: {dirName}"); } } catch (Exception cleanEx) { Log?.Warn($"[Cleanup] Failed delete '{dirName}': {cleanEx.Message}"); Log?.Debug(cleanEx); }
        }
        #endregion
    }
}