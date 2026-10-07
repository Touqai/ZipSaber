using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace ZipSaber
{
    /// <summary>
    /// Removing mod files the running game has loaded.
    ///   1. delete straight away if Windows lets us
    ///   2. otherwise move it out of Plugins into a trash folder (allowed for loaded DLLs)
    ///   3. otherwise leave a .zs_del marker; a helper script deletes it once the game has exited
    /// Restarts go through the helper too, so the new game only starts after the files are gone.
    /// </summary>
    internal static class ModFileOps
    {
        private const string MarkerExt = ".zs_del";
        private static bool _helperLaunched;

        internal static string GameDir =>
            Path.GetDirectoryName(Process.GetCurrentProcess().MainModule?.FileName ?? "") ?? "";

        internal static string PluginsDir =>
            Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);

        internal static string TrashDir
        {
            get
            {
                string d = Path.Combine(GameDir, "UserData", "ZipSaber", "Trash");
                Directory.CreateDirectory(d);
                return d;
            }
        }

        /// <summary>True when the file is gone from its folder now. False = marked for after exit.</summary>
        internal static bool Remove(string path, string label)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return true;
            try
            {
                File.Delete(path);
                Plugin.Log?.Info($"[Delete] Deleted {label}: {Path.GetFileName(path)}");
                return true;
            }
            catch { /* locked, try moving */ }

            try
            {
                string dest = Path.Combine(TrashDir, $"{Path.GetFileName(path)}.{DateTime.Now.Ticks}.old");
                File.Move(path, dest);
                Plugin.Log?.Info($"[Delete] Moved {label} out of Plugins (in use): {Path.GetFileName(path)}");
                return true;
            }
            catch (Exception ex) { Plugin.Log?.Warn($"[Delete] Can't move {Path.GetFileName(path)} yet ({ex.Message}); will delete after exit."); }

            try { File.WriteAllText(path + MarkerExt, "pending"); }
            catch (Exception ex) { Plugin.Log?.Error($"[Delete] Mark failed ({label}): {ex.Message}"); }
            return false;
        }

        /// <summary>Run once at startup: empty the trash and retry anything still marked.</summary>
        internal static void StartupCleanup()
        {
            try
            {
                string trash = Path.Combine(GameDir, "UserData", "ZipSaber", "Trash");
                if (Directory.Exists(trash))
                    foreach (var f in Directory.GetFiles(trash))
                        try { File.Delete(f); } catch { }

                foreach (var marker in Directory.GetFiles(PluginsDir, "*" + MarkerExt))
                {
                    string target = marker.Substring(0, marker.Length - MarkerExt.Length);
                    try { File.Delete(marker); } catch { }
                    if (File.Exists(target))
                    {
                        Plugin.Log?.Warn($"[Delete] {Path.GetFileName(target)} survived the last exit, retrying.");
                        Remove(target, "leftover");
                    }
                }
                foreach (var bat in Directory.GetFiles(PluginsDir, "zs_cleanup*.bat"))
                    try { File.Delete(bat); } catch { }
            }
            catch (Exception ex) { Plugin.Log?.Warn($"[Delete] Startup cleanup failed: {ex.Message}"); }
        }

        /// <summary>
        /// Start the after-exit helper (once). It waits for this game process to close,
        /// deletes marked files, then relaunches the game if asked.
        /// Returns false if there was nothing to do and no relaunch was requested.
        /// </summary>
        internal static bool LaunchHelper(bool relaunch)
        {
            if (_helperLaunched) return true;
            try
            {
                var markers = Directory.GetFiles(PluginsDir, "*" + MarkerExt);
                if (markers.Length == 0 && !relaunch) return false;

                int pid = Process.GetCurrentProcess().Id;
                string exe = Process.GetCurrentProcess().MainModule?.FileName;
                string bat = Path.Combine(TrashDir, $"zs_cleanup_{pid}.bat");

                var sb = new StringBuilder();
                sb.AppendLine("@echo off");
                sb.AppendLine("setlocal EnableDelayedExpansion");
                // wait for the game to close (ping as a sleep: timeout.exe fails without console input)
                sb.AppendLine(":wait");
                sb.AppendLine($"tasklist /NH /FI \"PID eq {pid}\" 2>nul | findstr /R /C:\" {pid} \" >nul");
                sb.AppendLine("if not errorlevel 1 (ping -n 2 127.0.0.1 >nul & goto wait)");
                sb.AppendLine("ping -n 2 127.0.0.1 >nul");
                foreach (var marker in markers)
                {
                    string target = marker.Substring(0, marker.Length - MarkerExt.Length);
                    // retry a few times in case antivirus or the OS still holds it
                    sb.AppendLine("set n=0");
                    string lbl = "d" + Math.Abs(target.GetHashCode());
                    sb.AppendLine($":{lbl}");
                    sb.AppendLine($"del /f /q \"{BatEsc(target)}\" >nul 2>&1");
                    sb.AppendLine($"if exist \"{BatEsc(target)}\" (set /a n+=1 & ping -n 2 127.0.0.1 >nul & if !n! LSS 10 goto {lbl})");
                    sb.AppendLine($"if not exist \"{BatEsc(target)}\" del /f /q \"{BatEsc(marker)}\" >nul 2>&1");
                }
                if (relaunch && !string.IsNullOrEmpty(exe))
                {
                    string args = string.Join(" ", Environment.GetCommandLineArgs().Skip(1)
                                    .Select(a => a.Contains(' ') ? $"\"{a}\"" : a));
                    sb.AppendLine($"start \"\" /D \"{BatEsc(Path.GetDirectoryName(exe))}\" \"{BatEsc(exe)}\" {BatEsc(args)}");
                }
                sb.AppendLine("(goto) 2>nul & del /f /q \"%~f0\"");
                File.WriteAllText(bat, sb.ToString());

                Process.Start(new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/C \"\"{bat}\"\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = TrashDir
                });
                _helperLaunched = true;
                Plugin.Log?.Info($"[Delete] After-exit helper started ({markers.Length} file(s), relaunch={relaunch}).");
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Log?.Warn($"[Delete] Helper failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>Quit and come back, after pending deletes are done.</summary>
        internal static void Restart()
        {
            if (!LaunchHelper(relaunch: true))
            {
                // fall back to the old direct relaunch
                try
                {
                    string exe = Process.GetCurrentProcess().MainModule?.FileName;
                    string args = string.Join(" ", Environment.GetCommandLineArgs().Skip(1).Select(a => a.Contains(' ') ? $"\"{a}\"" : a));
                    Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe) });
                }
                catch (Exception ex) { Plugin.Log?.Error($"[Delete] Restart failed: {ex.Message}"); }
            }
            UnityEngine.Application.Quit();
        }

        private static string BatEsc(string s) => (s ?? "").Replace("%", "%%");
    }
}
