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
using IPA.Config;
using IPA.Config.Stores;
using IPA.Logging; // Using BSIPA Logger
using SongCore;
using UnityEngine; // Using UnityEngine
using BeatSaberMarkupLanguage.Settings;
using BeatSaberMarkupLanguage.Attributes;
using BeatSaberMarkupLanguage.ViewControllers;

#nullable disable

namespace ZipSaber
{
    [Plugin(RuntimeOptions.SingleStartInit)]
    public class Plugin
    {
        #region Constants
        private const string UnityWindowClass = "UnityWndClass";
        private const string BeatSaberWindowTitle = "Beat Saber";
        private const string CustomWipLevelsFolderName = "CustomWipLevels";
        private const string BeatSaberDataFolderName = "Beat Saber_Data";
        #endregion

        #region Static Plugin Instance, Logger & Config
        internal static Plugin Instance { get; private set; }
        internal static PluginConfig Config { get; private set; }
        internal static IPA.Logging.Logger Log { get; private set; } // Explicitly specified
        #endregion

        #region Session Tracking
        private static List<string> _importedFoldersThisSession = new List<string>();
        private static readonly object _folderListLock = new object();
        #endregion

        #region WinAPI Imports and Constants
        private const int GWLP_WNDPROC = -4;
        private const uint WM_DROPFILES = 0x233;
        internal delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)] private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)] private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern void DragAcceptFiles(IntPtr hWnd, bool fAccept);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern uint DragQueryFile(IntPtr hDrop, uint iFile, [Out] StringBuilder lpszFile, uint cch);
        [DllImport("shell32.dll")] private static extern void DragFinish(IntPtr hDrop);
        [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern IntPtr DefWindowProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)] private static extern IntPtr FindWindow(string lpClassName, string lpWindowName);
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
        public Plugin(Config conf, IPA.Logging.Logger logger) // Explicitly specified
        {
            Instance = this;
            Log = logger;
            Config = conf.Generated<PluginConfig>();
            Log.Info("Initializing ZipSaber...");
            // Ensure settings view controller instance is created after config is loaded.
            var _ = SettingsViewController.instance;
            Log.Debug("SettingsViewController instance potentially initialized.");
            lock (_folderListLock) { _importedFoldersThisSession.Clear(); }
        }

        [OnEnable]
        public void OnEnable()
        {
            Log.Info("OnEnable called.");
            CalculatePaths();

            if (Config == null) { Log.Error("Config object is null after Init! Settings/Deletion may not work."); }

            // Register BSML settings menu (Inlined)
            try
            {
                if (BSMLSettings.instance != null)
                {
                    Log.Debug("Registering BSML Settings Menu...");
                    BSMLSettings.instance.AddSettingsMenu("ZipSaber", "ZipSaber.settings.bsml", SettingsViewController.instance);
                    Log.Info("BSML Settings Menu Registered.");
                }
                else { Log.Warn("BSMLSettings.instance is null. Cannot register settings menu."); }
            }
            catch (Exception ex) { Log.Error($"Error registering BSML Settings: {ex.Message}\n{ex}"); }

            // Schedule hook setup (Inlined)
            if (!hooksAttempted && !hooksActive)
            {
                Log.Info("Scheduling delayed hook setup.");
                hooksAttempted = true;
                Task.Run(async () => { await Task.Delay(3000); AttemptFindAndHookWindow(); });
            }
            else { Log.Debug($"Hook setup already attempted (Attempted: {hooksAttempted}, Active: {hooksActive}). Skipping."); }
        }

        [OnDisable]
        public void OnDisable()
        {
            Log.Info("OnDisable called.");
            CleanupWindowHook();
            hooksAttempted = false;

            // Process Map Deletion (Inlined)
            bool shouldDelete = false;
            if (Config != null) { shouldDelete = Config.DeleteOnClose; Log.Info($"Plugin disabling. DeleteOnClose setting = {shouldDelete}."); }
            else { Log.Warn("Plugin disabling. Config is null, cannot check DeleteOnClose setting. Maps will not be deleted."); }

            if (shouldDelete)
            {
                int deleteCount = 0; List<string> foldersToDelete;
                lock (_folderListLock) { foldersToDelete = new List<string>(_importedFoldersThisSession); _importedFoldersThisSession.Clear(); }
                if (!foldersToDelete.Any()) { Log.Info("No maps imported this session to delete."); }
                else
                {
                    Log.Info($"Attempting to delete {foldersToDelete.Count} folder(s)...");
                    foreach (string folderPath in foldersToDelete) { if (TryDeleteDirectory(folderPath)) { deleteCount++; } }
                    Log.Info($"Finished cleanup. Deleted {deleteCount} folder(s).");
                }
            }
            else { Log.Info("DeleteOnClose disabled or Config was null. Keeping maps."); lock (_folderListLock) { _importedFoldersThisSession.Clear(); } }

            // Unregister BSML settings (Inlined)
            try
            {
                if (BSMLSettings.instance != null)
                {
                    Log.Debug("Removing BSML Settings Menu...");
                    BSMLSettings.instance.RemoveSettingsMenu(SettingsViewController.instance);
                    Log.Info("BSML Settings Menu Removed.");
                }
            } catch (Exception ex) { Log.Error($"Error removing BSML Settings: {ex.Message}\n{ex}"); }
        }
        #endregion

        #region Path Calculation (Keep existing shorter version)
        private void CalculatePaths()
        {
            if (!string.IsNullOrEmpty(CustomWipLevelsPath)) return;
            try { string gdp = Application.dataPath; if (string.IsNullOrEmpty(gdp)) { Log.Error("App dataPath null!"); CustomWipLevelsPath = null; return; } DirectoryInfo gdd = new DirectoryInfo(gdp); DirectoryInfo gdi = gdd.Parent; if (gdi == null) { Log.Error("Parent dir null!"); CustomWipLevelsPath = null; return; } string gd = gdi.FullName; CustomWipLevelsPath = Path.Combine(gd, BeatSaberDataFolderName, CustomWipLevelsFolderName); Log.Info($"Target WIP Path: {CustomWipLevelsPath}"); EnsureDirectoryExists(CustomWipLevelsPath, "Pathing"); } catch (Exception e) { Log.Error($"PathFail: {e.Message}\n{e}"); CustomWipLevelsPath = null; }
        }
        private bool EnsureDirectoryExists(string p, string ctx) { if (string.IsNullOrEmpty(p)) { Log.Warn($"{ctx}: null path"); return false; } if (!Directory.Exists(p)) { Log.Info($"{ctx}: Creating {p}"); try { Directory.CreateDirectory(p); return true; } catch (Exception ex) { Log.Error($"{ctx}: CreateFail: {ex.Message}\n{ex}"); return false; } } return true; }
        #endregion

        #region Window Hooking Logic (Keep existing shorter version)
        private void AttemptFindAndHookWindow()
        {
             Log.Debug("[Hooking] Finding window..."); IntPtr fHwnd = FindWindow(UnityWindowClass, null); if (fHwnd == IntPtr.Zero) fHwnd = FindWindow(null, BeatSaberWindowTitle); if (fHwnd == IntPtr.Zero) { try { fHwnd = Process.GetCurrentProcess().MainWindowHandle; } catch { Log.Warn("[Hooking] Failed get process handle."); } } if (fHwnd != IntPtr.Zero) { Log.Info($"[Hooking] Found {fHwnd}. Setting hook."); SetupWindowHook(fHwnd); } else { Log.Error("[Hooking] Failed find window."); hooksAttempted = false; }
        }
        private void SetupWindowHook(IntPtr wh) { if (hooksActive || Hwnd != IntPtr.Zero) { Log.Warn("Hook already active."); return; } try { Hwnd = wh; Log.Debug($"Hooking {Hwnd}"); CalculatePaths(); if (string.IsNullOrEmpty(CustomWipLevelsPath)) { Log.Error("Hook Abort: Path null."); Hwnd = IntPtr.Zero; return; } wndProcDelegate = StaticWndProc; IntPtr ptr = Marshal.GetFunctionPointerForDelegate(wndProcDelegate); oldWndProc = SetWindowLongPtr(Hwnd, GWLP_WNDPROC, ptr); int err = Marshal.GetLastWin32Error(); if (oldWndProc == IntPtr.Zero && err != 0) { Log.Error($"SetFail: {err}"); CleanupWindowHook(); return; } DragAcceptFiles(Hwnd, true); Log.Info("Hook OK"); hooksActive = true; } catch (Exception ex) { Log.Error($"Hook Setup Err: {ex.Message}\n{ex}"); CleanupWindowHook(); } }
        private void CleanupWindowHook() { Log.Debug("Hook Cleanup..."); IntPtr cHwnd = Hwnd, cOld = oldWndProc; if (cHwnd != IntPtr.Zero) { try { DragAcceptFiles(cHwnd, false); } catch { } if (cOld != IntPtr.Zero) { try { SetWindowLongPtr(cHwnd, GWLP_WNDPROC, cOld); } catch (Exception ex){ Log.Warn($"Failed restore hook: {ex.Message}"); } } } wndProcDelegate = null; oldWndProc = IntPtr.Zero; Hwnd = IntPtr.Zero; hooksActive = false; Log.Info("Hook Cleanup OK"); }
        internal static IntPtr StaticWndProc(IntPtr h, uint m, IntPtr w, IntPtr l) { if (m == WM_DROPFILES) { Log.Debug("WM_DROPFILES received"); Task.Run(() => StaticHandleDroppedFiles(w)); return IntPtr.Zero; } IntPtr currentOld = oldWndProc; if (currentOld != IntPtr.Zero) { try { return CallWindowProc(currentOld, h, m, w, l); } catch (Exception ex) { Log.Error($"CallOrigErr: {ex.Message}\n{ex}"); return DefWindowProc(h, m, w, l); } } Log.Warn("Orig WndProc Zero"); return DefWindowProc(h, m, w, l); }
        #endregion

        #region Dropped File Handling (Keep existing shorter version)
        private static void StaticHandleDroppedFiles(IntPtr hDrop) { List<string> files = new List<string>(); try { uint count = DragQueryFile(hDrop, 0xFFFFFFFF, null, 0); Log.Info($"Drop: {count} items"); for (uint i = 0; i < count; i++) { uint len = DragQueryFile(hDrop, i, null, 0); if (len > 0) { StringBuilder sb = new StringBuilder((int)len + 1); if (DragQueryFile(hDrop, i, sb, (uint)sb.Capacity) > 0) files.Add(sb.ToString()); } } } catch (Exception ex) { Log.Error($"DropQueryErr: {ex.Message}\n{ex}"); } finally { try { DragFinish(hDrop); } catch { } } if (files.Any()) { if (Instance != null) Instance.ProcessDroppedFilesBatch(files); else Log.Error("Instance null processing drop."); } else Log.Info("Drop: No valid files"); }
        private void ProcessDroppedFilesBatch(List<string> paths) { bool anyOK = false; int ok = 0, fail = 0, skip = 0; Log.Info($"Batch: {paths.Count} files"); foreach (string p in paths) { try { if (Path.GetExtension(p).Equals(".zip", StringComparison.OrdinalIgnoreCase)) { Log.Debug($"Batch ZIP: {Path.GetFileName(p)}"); if (ProcessMapZip(p)) { ok++; anyOK = true; } else { fail++; } } else { skip++; Log.Info($"Batch Skip: {Path.GetFileName(p)}"); } } catch (Exception ex) { Log.Error($"Batch Err: {Path.GetFileName(p)}: {ex.Message}\n{ex}"); fail++; } } Log.Info($"Batch OK: {ok}, Fail: {fail}, Skip: {skip}."); if (anyOK) { Log.Info("Requesting SongCore refresh..."); RequestSongRefresh(); } }
        #endregion

        #region Map Processing Logic (Keep existing shorter version)
        internal bool ProcessMapZip(string zip) { string mapName = Path.GetFileNameWithoutExtension(zip), targetBase = CustomWipLevelsPath, finalDir = null; bool extOK = false, valOK = false; Log.Debug($"MapProc '{mapName}'"); try { if (string.IsNullOrEmpty(targetBase) || !EnsureDirectoryExists(targetBase, $"MapProc {mapName}")) { Log.Error("Target Path Invalid"); return false; } string sanName = SanitizeFolderName(mapName), curDir = Path.Combine(targetBase, sanName); if (Directory.Exists(curDir)) { Log.Warn("Folder exists, find unique..."); int num = 1; string potName; do { potName = $"{sanName}_{num++}"; curDir = Path.Combine(targetBase, potName); if (num > 100) { Log.Error("Unique abort"); return false; } } while (Directory.Exists(curDir)); Log.Info($"Using unique: {potName}"); } finalDir = curDir; Log.Debug($"Final target: {finalDir}"); Log.Debug("Extracting..."); try { Directory.CreateDirectory(finalDir); ZipFile.ExtractToDirectory(zip, finalDir); Log.Info("Extracted."); extOK = true; } catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException) { Log.Error($"ExtractFail {ex.GetType().Name}: {ex.Message}"); } catch (Exception ex) { Log.Error($"ExtractErr {ex.GetType().Name}: {ex.Message}\n{ex}"); } if (extOK) { Log.Debug("Validating..."); valOK = IsValidMapFolder(finalDir); if (!valOK) Log.Warn("Validation failed."); else { Log.Info("Validation OK."); lock (_folderListLock) { _importedFoldersThisSession.Add(finalDir); } Log.Debug($"Added '{Path.GetFileName(finalDir)}' to tracking."); } } } catch (Exception ex) { Log.Error($"OuterErr: {ex.Message}\n{ex}"); } finally { if (finalDir != null && extOK && !valOK && Directory.Exists(finalDir)) { Log.Warn("Validation failed cleanup."); TryDeleteDirectory(finalDir); } else if (finalDir != null && !extOK && Directory.Exists(finalDir)) { Log.Warn("Extraction failed cleanup."); TryDeleteDirectory(finalDir); } } return extOK && valOK; }
        private bool IsValidMapFolder(string p) { if (!Directory.Exists(p)) return false; try { bool i = Directory.EnumerateFiles(p, "info.dat", SearchOption.TopDirectoryOnly).Any(f => Path.GetFileName(f).Equals("info.dat", StringComparison.OrdinalIgnoreCase)), a = Directory.EnumerateFiles(p, "*.*", SearchOption.TopDirectoryOnly).Any(f => { var e = Path.GetExtension(f); return e.Equals(".egg", StringComparison.OrdinalIgnoreCase) || e.Equals(".ogg", StringComparison.OrdinalIgnoreCase) || e.Equals(".wav", StringComparison.OrdinalIgnoreCase); }), d = Directory.EnumerateFiles(p, "*.dat", SearchOption.TopDirectoryOnly).Any(f => !Path.GetFileName(f).Equals("info.dat", StringComparison.OrdinalIgnoreCase)); return i && a && d; } catch (Exception ex) { Log.Error($"ValidationErr: {ex.Message}\n{ex}"); return false; } }
        private string SanitizeFolderName(string n) { char[] inv = Path.GetInvalidFileNameChars(); string san = string.Join("_", n.Split(inv, StringSplitOptions.RemoveEmptyEntries)).TrimEnd('.', ' '); if (string.IsNullOrWhiteSpace(san)) return "ImportedMap_" + Guid.NewGuid().ToString("N").Substring(0, 8); return san; }
        #endregion

        #region Utilities (Keep existing shorter version)
        internal void RequestSongRefresh() { Log.Debug("Attempting SongCore refresh..."); try { if (Loader.Instance != null) { Log.Info("Calling Loader.Instance.RefreshSongs(false)..."); Loader.Instance.RefreshSongs(false); Log.Info("SongCore refresh requested."); } else { Log.Warn("Cannot refresh: SongCore.Loader.Instance is null."); } } catch (Exception ex) { Log.Error($"Refresh ReqErr: {ex.Message}\n{ex}"); } }
        internal bool TryDeleteDirectory(string p) { if (string.IsNullOrEmpty(p)) return false; string n = Path.GetFileName(p); try { if (Directory.Exists(p)) { Log.Debug($"Cleanup Del: {n}"); Directory.Delete(p, true); return true; } else { Log.Warn($"Cleanup Skip: Not found {n}"); return false; } } catch (Exception ex) { Log.Error($"Cleanup Fail: {n}: {ex.Message}\n{ex}"); return false; } }
        #endregion

    } // End Plugin Class


    // --- Settings View Controller ---
    [ViewDefinition("ZipSaber.settings.bsml")]
    [HotReload(RelativePathToLayout = @"settings.bsml")]
    internal class SettingsViewController : BSMLAutomaticViewController
    {
        private static SettingsViewController _instance;
        public static SettingsViewController instance
        {
            get { if (_instance == null) { Plugin.Log?.Debug("[Settings] Creating SettingsViewController instance."); _instance = new SettingsViewController(); _instance.InitializeValue(); } return _instance; }
            private set => _instance = value;
        }

        public SettingsViewController() { }

        private bool _uiDeleteOnCloseValue;

        // Load initial value when instance is first created
        private void InitializeValue()
        {
            if (Plugin.Config != null) { this._uiDeleteOnCloseValue = Plugin.Config.DeleteOnClose; Plugin.Log?.Info($"[Settings] Initialized value: {_uiDeleteOnCloseValue}"); }
            else { this._uiDeleteOnCloseValue = false; Plugin.Log?.Warn("[Settings] Config NULL during instance init."); }
        }

        [UIValue("delete-on-close")]
        public bool DeleteOnClose_UI
        {
            get => _uiDeleteOnCloseValue;
            set { if (_uiDeleteOnCloseValue == value) return; _uiDeleteOnCloseValue = value; if (Plugin.Config != null) { Plugin.Config.DeleteOnClose = value; Plugin.Log?.Info($"[Settings] Config set: {value}"); } else { Plugin.Log?.Error("[Settings] Config NULL on UI set!"); } NotifyPropertyChanged(); }
        }

        protected override void DidActivate(bool firstActivation, bool addedToHierarchy, bool screenSystemEnabling)
        {
            base.DidActivate(firstActivation, addedToHierarchy, screenSystemEnabling);
            Plugin.Log?.Debug($"[Settings] DidActivate. firstActivation={firstActivation}");
            if (Plugin.Config != null) { _uiDeleteOnCloseValue = Plugin.Config.DeleteOnClose; Plugin.Log?.Info($"[Settings] Activated value reload: {_uiDeleteOnCloseValue}"); }
            else { Plugin.Log?.Error("[Settings] Config NULL during Activate!"); }
            NotifyPropertyChanged(nameof(DeleteOnClose_UI));
            Plugin.Log?.Debug($"[Settings] Notified UI.");
        }
    } // End SettingsViewController Class

} // End Namespace ZipSaber