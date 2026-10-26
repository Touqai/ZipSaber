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
                // fullscreen = Unity says so, or the window covers its whole monitor
                bool fullscreen = UnityEngine.Screen.fullScreen;
                try
                {
                    GetWindowRect(w, out RECT wr);
                    var mi = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
                    if (GetMonitorInfo(MonitorFromWindow(w, 2), ref mi) &&
                        wr.left <= mi.rcMonitor.left && wr.top <= mi.rcMonitor.top &&
                        wr.right >= mi.rcMonitor.right && wr.bottom >= mi.rcMonitor.bottom) fullscreen = true;
                }
                catch { }
                if (IsZoomed(w) || IsIconic(w)) { /* use defaults; host centers itself */ rc = new RECT(); }

                if (!Extract()) return;
                var me = Process.GetCurrentProcess();
                string args = $"--dir \"{Dir}\" --game \"{me.MainModule?.FileName}\" --pid {me.Id} " +
                              $"--x {tl.x} --y {tl.y} --w {rc.right - rc.left} --h {rc.bottom - rc.top} " +
                              $"--title \"{(title.Length > 0 ? title.ToString().Replace("\"", "") : "Beat Saber")}\" " +
                              $"--fullscreen {(fullscreen ? 1 : 0)}";
                ModFileOps.StartDetached(ExePath, args, Dir, noWindow: false);   // must not inherit our sockets/files
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
                // drop any old -parentHWND <n>; the host adds its own
                var argList = Environment.GetCommandLineArgs().Skip(1).ToList();
                int ph = argList.FindIndex(a => a.Equals("-parentHWND", StringComparison.OrdinalIgnoreCase));
                if (ph >= 0) argList.RemoveRange(ph, Math.Min(2, argList.Count - ph));
                string args = string.Join(" ", argList.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));
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

        internal static IntPtr FindOwnWindow()
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

        /// <summary>Send a one-line command to the host (fullscreen=toggle, activate...).</summary>
        internal static void SendCommand(string cmd)
        {
            try { File.AppendAllText(Path.Combine(Dir, "host.cmd"), cmd + "\n"); } catch { }
        }

        private static IntPtr _child, _host;
        private static float _nextCheck;
        private static bool _clipped, _comboHeld;

        /// <summary>
        /// Per frame while hosted. Unity doesn't lock the cursor for a window it didn't create as
        /// embedded, so do it here: keep the cursor inside the game while it's locked (FPFC), and
        /// route Alt+Enter / F11 to the host since a child window can't go fullscreen.
        /// </summary>
        internal static void Tick()
        {
            if (UnityEngine.Time.unscaledTime >= _nextCheck)
            {
                _nextCheck = UnityEngine.Time.unscaledTime + 1f;
                _child = FindOwnWindow();
                _host = _child != IntPtr.Zero ? GetParent(_child) : IntPtr.Zero;
            }
            if (_host == IntPtr.Zero) { Unclip(); return; }

            if (GetForegroundWindow() == _host)
            {
                bool alt = (GetAsyncKeyState(0x12 /*VK_MENU*/) & 0x8000) != 0;
                bool combo = (alt && (GetAsyncKeyState(0x0D /*VK_RETURN*/) & 0x8000) != 0) || (GetAsyncKeyState(0x7A /*VK_F11*/) & 0x8000) != 0;
                if (combo && !_comboHeld) SendCommand("fullscreen=toggle");
                _comboHeld = combo;
            }

            bool locked = UnityEngine.Cursor.lockState != UnityEngine.CursorLockMode.None;
            if (!locked || GetForegroundWindow() != _host) { Unclip(); return; }

            GetClientRect(_child, out RECT rc);
            var tl = new POINT(); ClientToScreen(_child, ref tl);
            var r = new RECT { left = tl.x, top = tl.y, right = tl.x + rc.right, bottom = tl.y + rc.bottom };
            if (r.right <= r.left || r.bottom <= r.top) return;
            ClipCursor(ref r);
            _clipped = true;
        }

        private static void Unclip()
        {
            if (!_clipped) return;
            ClipCursor(IntPtr.Zero);
            _clipped = false;
        }

        // ── Win32 ────────────────────────────────────────────────────────────────
        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
        [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr mon, ref MONITORINFO mi);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT rc);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vk);
        [DllImport("user32.dll")] private static extern bool ClipCursor(ref RECT r);
        [DllImport("user32.dll")] private static extern bool ClipCursor(IntPtr r);
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
