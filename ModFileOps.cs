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
            var markers = SafeMarkers();
            if (markers.Length == 0 && !relaunch) return false;
            // Stream window open: it does the restart, so the Discord share and game audio carry over
            if (StreamWindow.RequestRestart(relaunch, MarkerExt)) { _helperLaunched = true; return true; }
            if (LaunchPowerShellHelper(relaunch, markers.Length)) { _helperLaunched = true; return true; }
            if (LaunchBatchHelper(relaunch, markers)) { _helperLaunched = true; return true; }
            return false;
        }

        private static string[] SafeMarkers()
        {
            try { return Directory.GetFiles(PluginsDir, "*" + MarkerExt); } catch { return new string[0]; }
        }

        private static string RelaunchArgs() => string.Join(" ", Environment.GetCommandLineArgs().Skip(1)
                                                   .Select(a => a.Contains(' ') ? $"\"{a}\"" : a));

        /// <summary>
        /// PowerShell helper: wakes the moment the game exits (no polling), force-closes a quit
        /// that hangs (restart only), re-scans for .zs_del markers at that point, then relaunches.
        /// </summary>
        private static bool LaunchPowerShellHelper(bool relaunch, int markerCount)
        {
            try
            {
                var me = Process.GetCurrentProcess();
                int pid = me.Id;
                string exe = me.MainModule?.FileName;
                string ps1 = Path.Combine(TrashDir, $"zs_helper_{pid}.ps1");
                string Q(string v) => "'" + (v ?? "").Replace("'", "''") + "'";

                var sb = new StringBuilder();
                sb.AppendLine("$ErrorActionPreference = 'SilentlyContinue'");
                sb.AppendLine($"$p = Get-Process -Id {pid}");
                sb.AppendLine("if ($p) {");
                // Normal quit takes 1-4s. Modded Beat Saber sometimes hangs on exit: when restarting,
                // give it 8s (player data is saved early in quit) and then end it.
                sb.AppendLine(relaunch
                    ? "  if (-not $p.WaitForExit(8000)) { Stop-Process -Id $p.Id -Force; [void]$p.WaitForExit(5000) }"
                    : "  [void]$p.WaitForExit(120000)");
                sb.AppendLine("}");
                sb.AppendLine("Start-Sleep -Milliseconds 250");   // let the OS drop file locks / ports
                sb.AppendLine($"Get-ChildItem -LiteralPath {Q(PluginsDir)} -Filter '*{MarkerExt}' | ForEach-Object {{");
                sb.AppendLine($"  $t = $_.FullName.Substring(0, $_.FullName.Length - {MarkerExt.Length})");
                sb.AppendLine("  for ($i = 0; $i -lt 20 -and (Test-Path -LiteralPath $t); $i++) { Remove-Item -LiteralPath $t -Force; if (Test-Path -LiteralPath $t) { Start-Sleep -Milliseconds 250 } }");
                sb.AppendLine("  if (-not (Test-Path -LiteralPath $t)) { Remove-Item -LiteralPath $_.FullName -Force }");
                sb.AppendLine("}");
                if (relaunch && !string.IsNullOrEmpty(exe))
                {
                    string args = RelaunchArgs();
                    sb.AppendLine($"Start-Process -FilePath {Q(exe)} -WorkingDirectory {Q(Path.GetDirectoryName(exe))}"
                                  + (string.IsNullOrWhiteSpace(args) ? "" : $" -ArgumentList {Q(args)}"));
                }
                sb.AppendLine("Remove-Item -LiteralPath $MyInvocation.MyCommand.Path -Force");
                File.WriteAllText(ps1, sb.ToString(), new UTF8Encoding(true));

                Process.Start(new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{ps1}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = TrashDir
                });
                Plugin.Log?.Info($"[Restart] Helper started ({markerCount} file(s) to delete, relaunch={relaunch}).");
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Log?.Warn($"[Restart] PowerShell helper failed ({ex.Message}), using batch fallback.");
                return false;
            }
        }

        private static bool LaunchBatchHelper(bool relaunch, string[] markers)
        {
            try
            {
                int pid = Process.GetCurrentProcess().Id;
                string exe = Process.GetCurrentProcess().MainModule?.FileName;
                string bat = Path.Combine(TrashDir, $"zs_cleanup_{pid}.bat");

                var sb = new StringBuilder();
                sb.AppendLine("@echo off");
                sb.AppendLine("setlocal EnableDelayedExpansion");
                sb.AppendLine(":wait");
                sb.AppendLine($"tasklist /NH /FI \"PID eq {pid}\" 2>nul | findstr /R /C:\" {pid} \" >nul");
                sb.AppendLine("if not errorlevel 1 (ping -n 1 -w 300 192.0.2.1 >nul & goto wait)");
                foreach (var marker in markers)
                {
                    string target = marker.Substring(0, marker.Length - MarkerExt.Length);
                    sb.AppendLine("set n=0");
                    string lbl = "d" + Math.Abs(target.GetHashCode());
                    sb.AppendLine($":{lbl}");
                    sb.AppendLine($"del /f /q \"{BatEsc(target)}\" >nul 2>&1");
                    sb.AppendLine($"if exist \"{BatEsc(target)}\" (set /a n+=1 & ping -n 1 -w 300 192.0.2.1 >nul & if !n! LSS 15 goto {lbl})");
                    sb.AppendLine($"if not exist \"{BatEsc(target)}\" del /f /q \"{BatEsc(marker)}\" >nul 2>&1");
                }
                if (relaunch && !string.IsNullOrEmpty(exe))
                    sb.AppendLine($"start \"\" /D \"{BatEsc(Path.GetDirectoryName(exe))}\" \"{BatEsc(exe)}\" {BatEsc(RelaunchArgs())}");
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
                Plugin.Log?.Info($"[Restart] Batch helper started ({markers.Length} file(s), relaunch={relaunch}).");
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Log?.Warn($"[Restart] Batch helper failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>Quit and come back, after pending deletes are done.</summary>
        internal static void Restart()
        {
            Plugin.Log?.Info("[Restart] Restarting Beat Saber.");
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
