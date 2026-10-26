using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace ZipSaber
{
    /// <summary>
    /// "Keep window through restarts": a tiny host process (ZipSaberWindow.exe, embedded in this DLL)
    /// owns the top-level "Beat Saber" window and holds the game's real window inside it. Restarts swap
    /// the game process underneath, so the window - and a Discord share of it - never closes.
    /// </summary>
    internal static class WindowHost
    {
        private const string ResourceName = "ZipSaber.ZipSaberWindow.exe";
        private const string ProcName = "ZipSaberWindow";
        private static bool _attachTried;

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

        internal static bool Enabled => Plugin.Config?.KeepWindowOnRestart ?? true;

        internal static bool IsRunning
        {
            get
            {
                try { return Process.GetProcessesByName(ProcName).Any(p => { try { return !p.HasExited; } catch { return false; } }); }
                catch { return false; }
            }
        }

        /// <summary>True when our game window currently sits inside the host window.</summary>
        internal static bool IsHosted
        {
            get
            {
                IntPtr w = FindOwnWindow();
                return w != IntPtr.Zero && GetParent(w) != IntPtr.Zero && IsRunning;
            }
        }

        /// <summary>
        /// Called once the menu is up. If the setting is on and we're not inside a host yet
        /// (i.e. this launch came from Steam), start the host and let it take our window.
        /// After a ZipSaber restart the host started us, and adopts our window by itself.
        /// </summary>
        internal static void EnsureHosted()
        {
            if (_attachTried || !Enabled) return;
            _attachTried = true;
            try
            {
                if (IsRunning) return;   // host already running (it launched us, or will adopt us)
                IntPtr w = FindOwnWindow();
                if (w == IntPtr.Zero) { Plugin.Log?.Warn("[Window] Couldn't find the game window."); return; }
                if (GetParent(w) != IntPtr.Zero) return;

                GetClientRect(w, out RECT rc);
                var tl = new POINT { x = 0, y = 0 };
                ClientToScreen(w, ref tl);
                var title = new StringBuilder(256);
                GetWindowText(w, title, title.Capacity);
                if (IsZoomed(w) || IsIconic(w)) { /* use defaults; host centers itself */ rc = new RECT(); }

                if (!Extract()) return;
                var me = Process.GetCurrentProcess();
                string args = $"--dir \"{Dir}\" --game \"{me.MainModule?.FileName}\" --pid {me.Id} " +
                              $"--x {tl.x} --y {tl.y} --w {rc.right - rc.left} --h {rc.bottom - rc.top} " +
                              $"--title \"{(title.Length > 0 ? title.ToString().Replace("\"", "") : "Beat Saber")}\"";
                Process.Start(new ProcessStartInfo(ExePath, args) { UseShellExecute = false, WorkingDirectory = Dir });
                Plugin.Log?.Info("[Window] Window host started - the game window now survives restarts.");
            }
            catch (Exception ex) { Plugin.Log?.Warn($"[Window] Host start failed: {ex.Message}"); }
        }

        /// <summary>Hand the restart to the host. False if no host is running.</summary>
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
                Plugin.Log?.Info($"[Window] Restart handed to window host (relaunch={relaunch}).");
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Log?.Warn($"[Window] Restart hand-off failed: {ex.Message}");
                return false;
            }
        }

        private static IntPtr FindOwnWindow()
        {
            uint pid = (uint)Process.GetCurrentProcess().Id;
            IntPtr found = IntPtr.Zero;
            EnumWindows((h, l) =>
            {
                GetWindowThreadProcessId(h, out uint p);
                if (p != pid) return true;
                var cls = new StringBuilder(64);
                GetClassName(h, cls, cls.Capacity);
                if (cls.ToString() == "UnityWndClass") { found = h; return false; }
                return true;
            }, IntPtr.Zero);
            if (found != IntPtr.Zero) return found;
            // already a child of the host: top-level enumeration won't see it
            foreach (var host in Process.GetProcessesByName(ProcName))
            {
                try
                {
                    IntPtr hw = host.MainWindowHandle;
                    if (hw == IntPtr.Zero) continue;
                    EnumChildWindows(hw, (h, l) =>
                    {
                        GetWindowThreadProcessId(h, out uint p);
                        if (p == pid) { found = h; return false; }
                        return true;
                    }, IntPtr.Zero);
                }
                catch { }
            }
            return found;
        }

        private static bool Extract()
        {
            try
            {
                byte[] data;
                using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName))
                {
                    if (s == null) { Plugin.Log?.Error("[Window] Embedded host missing."); return false; }
                    data = new byte[s.Length];
                    int read = 0; while (read < data.Length) read += s.Read(data, read, data.Length - read);
                }
                if (File.Exists(ExePath) && File.ReadAllBytes(ExePath).SequenceEqual(data)) return true;
                File.WriteAllBytes(ExePath, data);
                // clean up the old stream-window build
                try { File.Delete(Path.Combine(Dir, "ZipSaberStream.exe")); File.Delete(Path.Combine(Dir, "stream.cfg")); } catch { }
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Log?.Error($"[Window] Extract failed: {ex.Message}");
                return File.Exists(ExePath);
            }
        }

        // ── Win32 ────────────────────────────────────────────────────────────────
        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int left, top, right, bottom; }
        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int x, y; }
        private delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr l);
        [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr l);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, StringBuilder sb, int max);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);
        [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr h);
        [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr h, out RECT rc);
        [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr h, ref POINT p);
        [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr h);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
    }
}
