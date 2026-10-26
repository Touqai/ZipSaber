using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace ZipSaber
{
    /// <summary>
    /// ZipSaber Stream: a separate window that mirrors the game and survives restarts, so a Discord
    /// window share doesn't drop. While it's open it also performs ZipSaber restarts.
    /// The exe is embedded in ZipSaber.dll and extracted to UserData/ZipSaber.
    /// </summary>
    internal static class StreamWindow
    {
        private const string ResourceName = "ZipSaber.ZipSaberStream.exe";
        private const string ProcName = "ZipSaberStream";

        private static string Dir
        {
            get
            {
                string d = Path.Combine(ModFileOps.GameDir, "UserData", "ZipSaber");
                Directory.CreateDirectory(d);
                return d;
            }
        }

        private static string ExePath => Path.Combine(Dir, ProcName + ".exe");

        internal static bool IsRunning
        {
            get
            {
                try { return Process.GetProcessesByName(ProcName).Any(p => { try { return !p.HasExited; } catch { return false; } }); }
                catch { return false; }
            }
        }

        /// <summary>Open the stream window (or bring it to the front). Returns an error message or null.</summary>
        internal static string Open()
        {
            try
            {
                if (!Extract()) return "Couldn't unpack the stream window (see log).";
                var me = Process.GetCurrentProcess();
                string game = me.MainModule?.FileName ?? "";
                Process.Start(new ProcessStartInfo(ExePath,
                    $"--dir \"{Dir}\" --game \"{game}\" --pid {me.Id}")
                {
                    UseShellExecute = false,
                    WorkingDirectory = Dir
                });
                Plugin.Log?.Info("[Stream] Stream window opened.");
                return null;
            }
            catch (Exception ex)
            {
                Plugin.Log?.Error($"[Stream] Open failed: {ex.Message}");
                return "Couldn't open the stream window: " + ex.Message;
            }
        }

        /// <summary>Hand the restart to the stream window. False if it isn't running.</summary>
        internal static bool RequestRestart(bool relaunch, string markerExt, int killAfterMs = 8000)
        {
            if (!IsRunning) return false;
            try
            {
                var me = Process.GetCurrentProcess();
                string exe = me.MainModule?.FileName ?? "";
                string args = string.Join(" ", Environment.GetCommandLineArgs().Skip(1)
                                .Select(a => a.Contains(' ') ? $"\"{a}\"" : a));
                var sb = new StringBuilder();
                sb.AppendLine("pid=" + me.Id);
                sb.AppendLine("relaunch=" + (relaunch ? "1" : "0"));
                sb.AppendLine("killAfterMs=" + (relaunch ? killAfterMs : 120000));
                sb.AppendLine("exe=" + exe);
                sb.AppendLine("args=" + args);
                sb.AppendLine("workdir=" + Path.GetDirectoryName(exe));
                sb.AppendLine("plugins=" + ModFileOps.PluginsDir);
                sb.AppendLine("markerExt=" + markerExt);

                string req = Path.Combine(Dir, "restart.req"), tmp = req + ".tmp";
                File.WriteAllText(tmp, sb.ToString());
                if (File.Exists(req)) File.Delete(req);
                File.Move(tmp, req);   // atomic hand-off
                Plugin.Log?.Info($"[Stream] Restart handed to stream window (relaunch={relaunch}).");
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Log?.Warn($"[Stream] Restart hand-off failed: {ex.Message}");
                return false;
            }
        }

        private static bool Extract()
        {
            try
            {
                byte[] data;
                using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName))
                {
                    if (s == null) { Plugin.Log?.Error("[Stream] Embedded exe missing."); return false; }
                    data = new byte[s.Length];
                    int read = 0; while (read < data.Length) read += s.Read(data, read, data.Length - read);
                }
                if (File.Exists(ExePath))
                {
                    var existing = File.ReadAllBytes(ExePath);
                    if (existing.Length == data.Length && existing.SequenceEqual(data)) return true;
                    if (IsRunning) return true;   // older copy in use; it still works
                }
                File.WriteAllBytes(ExePath, data);
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Log?.Error($"[Stream] Extract failed: {ex.Message}");
                return File.Exists(ExePath);
            }
        }
    }
}
