using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ZipSaberStream
{
    /// <summary>
    /// ZipSaber Stream: a window that mirrors Beat Saber's desktop window and stays open when the
    /// game restarts. Share THIS window on Discord and the stream survives ZipSaber restarts.
    /// It also performs the restart (wait for exit, delete marked files, relaunch), so the new
    /// game runs as its child and Discord's app-audio capture follows it.
    /// </summary>
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            using (var mutex = new Mutex(true, "Local\\ZipSaberStreamWindow", out bool created))
            {
                if (!created) { BringExistingToFront(); return; }
                var opts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i + 1 < args.Length; i += 2)
                    if (args[i].StartsWith("--")) opts[args[i].Substring(2)] = args[i + 1];

                try { Native.SetProcessDPIAware(); } catch { }   // so the mirror isn't blurry/misplaced on scaled displays
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new StreamForm(
                    opts.TryGetValue("dir", out var d) ? d : AppDomain.CurrentDomain.BaseDirectory,
                    opts.TryGetValue("game", out var g) ? g : null,
                    opts.TryGetValue("pid", out var p) && int.TryParse(p, out int pid) ? pid : 0));
            }
        }

        private static void BringExistingToFront()
        {
            foreach (var pr in Process.GetProcessesByName(Process.GetCurrentProcess().ProcessName))
                if (pr.Id != Process.GetCurrentProcess().Id && pr.MainWindowHandle != IntPtr.Zero)
                {
                    Native.ShowWindow(pr.MainWindowHandle, 9 /*SW_RESTORE*/);
                    Native.SetForegroundWindow(pr.MainWindowHandle);
                }
        }
    }

    internal sealed class StreamForm : Form
    {
        private readonly string _dir;
        private readonly string _gameName;
        private string _gameExe;

        private Process _game;
        private IntPtr _gameHwnd = IntPtr.Zero;
        private IntPtr _thumb = IntPtr.Zero;
        private Size _srcSize;
        private string _status = "Waiting for Beat Saber...";
        private volatile bool _restarting;

        private bool _compat;
        private Bitmap _frame;
        private readonly System.Windows.Forms.Timer _tick = new System.Windows.Forms.Timer { Interval = 100 };
        private readonly System.Windows.Forms.Timer _copyTick = new System.Windows.Forms.Timer { Interval = 33 };
        private int _tickCount;

        private string RequestPath => Path.Combine(_dir, "restart.req");
        private string SettingsPath => Path.Combine(_dir, "stream.cfg");

        internal StreamForm(string dir, string gameExe, int pid)
        {
            _dir = dir;
            _gameExe = gameExe;
            _gameName = !string.IsNullOrEmpty(gameExe) ? Path.GetFileNameWithoutExtension(gameExe) : "Beat Saber";

            Text = "ZipSaber Stream - Beat Saber";
            BackColor = Color.Black;
            ForeColor = Color.Gainsboro;
            ClientSize = new Size(1280, 720);
            MinimumSize = new Size(320, 200);
            StartPosition = FormStartPosition.CenterScreen;
            DoubleBuffered = true;
            ResizeRedraw = true;
            try { Icon = Icon.ExtractAssociatedIcon(gameExe ?? Application.ExecutablePath); } catch { }

            var menu = new ContextMenuStrip();
            var compat = new ToolStripMenuItem("Compatibility mode (use if Discord shows black)") { CheckOnClick = true };
            compat.CheckedChanged += (s, e) => SetCompat(compat.Checked);
            var top = new ToolStripMenuItem("Always on top") { CheckOnClick = true };
            top.CheckedChanged += (s, e) => { TopMost = top.Checked; SaveSettings(); };
            var size720 = new ToolStripMenuItem("Size: 1280 x 720", null, (s, e) => ClientSize = new Size(1280, 720));
            var size1080 = new ToolStripMenuItem("Size: 1920 x 1080", null, (s, e) => ClientSize = new Size(1920, 1080));
            menu.Items.AddRange(new ToolStripItem[] { compat, top, new ToolStripSeparator(), size720, size1080,
                new ToolStripSeparator(), new ToolStripMenuItem("Close stream window", null, (s, e) => Close()) });
            ContextMenuStrip = menu;

            LoadSettings(compat, top);

            if (pid > 0) try { _game = Process.GetProcessById(pid); } catch { }

            _tick.Tick += (s, e) => OnTick();
            _copyTick.Tick += (s, e) => CopyFrame();
            _tick.Start();
            if (_compat) _copyTick.Start();
        }

        // ── Game tracking ───────────────────────────────────────────────────────
        private void OnTick()
        {
            CheckRestartRequest();
            if (++_tickCount % 5 != 0 && _gameHwnd != IntPtr.Zero) return;   // full scan every 0.5s

            if (_game != null) { try { if (_game.HasExited) _game = null; } catch { _game = null; } }
            if (_game == null && !_restarting)
            {
                _game = Process.GetProcessesByName(_gameName).FirstOrDefault(p => { try { return !p.HasExited; } catch { return false; } });
                if (_game != null && _gameExe == null) try { _gameExe = _game.MainModule?.FileName; } catch { }
            }

            IntPtr hwnd = IntPtr.Zero;
            if (_game != null)
            {
                try { _game.Refresh(); hwnd = _game.MainWindowHandle; } catch { }
                if (hwnd != IntPtr.Zero && !Native.IsWindowVisible(hwnd)) hwnd = IntPtr.Zero;
            }

            if (hwnd != _gameHwnd)
            {
                _gameHwnd = hwnd;
                Attach();
            }

            string s = _restarting ? "Restarting Beat Saber..."
                     : _game == null ? "Waiting for Beat Saber..."
                     : _gameHwnd == IntPtr.Zero ? "Beat Saber is starting..." : null;
            if (s != _status) { _status = s; Invalidate(); }
            if (_gameHwnd != IntPtr.Zero && Native.IsIconic(_gameHwnd) && _status == null) { _status = "Beat Saber's window is minimized"; Invalidate(); }
        }

        private void Attach()
        {
            Detach();
            if (_gameHwnd == IntPtr.Zero || _compat) { Invalidate(); return; }
            if (Native.DwmRegisterThumbnail(Handle, _gameHwnd, out _thumb) != 0) { _thumb = IntPtr.Zero; return; }
            UpdateThumb();
        }

        private void Detach()
        {
            if (_thumb != IntPtr.Zero) { Native.DwmUnregisterThumbnail(_thumb); _thumb = IntPtr.Zero; }
        }

        private Rectangle FitRect(Size src)
        {
            var c = ClientRectangle;
            if (src.Width <= 0 || src.Height <= 0) return c;
            float scale = Math.Min(c.Width / (float)src.Width, c.Height / (float)src.Height);
            int w = (int)(src.Width * scale), h = (int)(src.Height * scale);
            return new Rectangle(c.X + (c.Width - w) / 2, c.Y + (c.Height - h) / 2, w, h);
        }

        private void UpdateThumb()
        {
            if (_thumb == IntPtr.Zero) return;
            Native.DwmQueryThumbnailSourceSize(_thumb, out var sz);
            _srcSize = new Size(sz.cx, sz.cy);
            var r = FitRect(_srcSize);
            var props = new Native.DWM_THUMBNAIL_PROPERTIES
            {
                dwFlags = Native.DWM_TNP_VISIBLE | Native.DWM_TNP_RECTDESTINATION | Native.DWM_TNP_OPACITY | Native.DWM_TNP_SOURCECLIENTAREAONLY,
                fVisible = true, opacity = 255, fSourceClientAreaOnly = true,
                rcDestination = new Native.RECT { left = r.Left, top = r.Top, right = r.Right, bottom = r.Bottom }
            };
            Native.DwmUpdateThumbnailProperties(_thumb, ref props);
        }

        protected override void OnResize(EventArgs e) { base.OnResize(e); UpdateThumb(); }

        // ── Compatibility mode: copy frames with PrintWindow ────────────────────
        private void SetCompat(bool on)
        {
            _compat = on;
            SaveSettings();
            if (on) { Detach(); _copyTick.Start(); }
            else { _copyTick.Stop(); _frame?.Dispose(); _frame = null; Attach(); }
            Invalidate();
        }

        private void CopyFrame()
        {
            if (_gameHwnd == IntPtr.Zero || WindowState == FormWindowState.Minimized) return;
            if (!Native.GetClientRect(_gameHwnd, out var rc)) return;
            int w = rc.right - rc.left, h = rc.bottom - rc.top;
            if (w <= 0 || h <= 0) return;
            if (_frame == null || _frame.Width != w || _frame.Height != h) { _frame?.Dispose(); _frame = new Bitmap(w, h); }
            using (var g = Graphics.FromImage(_frame))
            {
                IntPtr hdc = g.GetHdc();
                try { Native.PrintWindow(_gameHwnd, hdc, 0x1 /*CLIENTONLY*/ | 0x2 /*RENDERFULLCONTENT*/); }
                finally { g.ReleaseHdc(hdc); }
            }
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Color.Black);
            if (_compat && _frame != null && _gameHwnd != IntPtr.Zero)
            {
                e.Graphics.InterpolationMode = InterpolationMode.Bilinear;
                e.Graphics.DrawImage(_frame, FitRect(_frame.Size));
            }
            if (_status != null)
            {
                using (var f = new Font("Segoe UI", 16f, FontStyle.Bold))
                using (var small = new Font("Segoe UI", 10f))
                {
                    var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                    var c = ClientRectangle;
                    e.Graphics.DrawString(_status, f, Brushes.Gainsboro, new RectangleF(c.X, c.Y - 20, c.Width, c.Height), sf);
                    e.Graphics.DrawString("ZipSaber Stream keeps your Discord share alive through restarts", small, Brushes.Gray,
                        new RectangleF(c.X, c.Y + 24, c.Width, c.Height), sf);
                }
            }
        }

        // ── Restart requests from ZipSaber ──────────────────────────────────────
        private void CheckRestartRequest()
        {
            if (_restarting || !File.Exists(RequestPath)) return;
            Dictionary<string, string> req;
            try
            {
                req = File.ReadAllLines(RequestPath).Select(l => l.Split(new[] { '=' }, 2)).Where(a => a.Length == 2)
                          .ToDictionary(a => a[0].Trim(), a => a[1], StringComparer.OrdinalIgnoreCase);
                File.Delete(RequestPath);
            }
            catch { return; }   // still being written; try next tick

            _restarting = true;
            Detach(); _gameHwnd = IntPtr.Zero; Invalidate();
            Task.Run(() => DoRestart(req)).ContinueWith(t => BeginInvoke((Action)(() =>
            {
                if (t.Result != null) _game = t.Result;
                _restarting = false;
            })));
        }

        private Process DoRestart(Dictionary<string, string> req)
        {
            string Get(string k) => req.TryGetValue(k, out var v) ? v : null;
            int.TryParse(Get("pid"), out int pid);
            bool relaunch = Get("relaunch") != "0";
            int.TryParse(Get("killAfterMs") ?? "8000", out int killAfter);

            try
            {
                var old = Process.GetProcessById(pid);
                if (!old.WaitForExit(killAfter)) { try { old.Kill(); } catch { } old.WaitForExit(5000); }
            }
            catch { /* already gone */ }
            Thread.Sleep(250);

            string plugins = Get("plugins"), ext = Get("markerExt") ?? ".zs_del";
            if (!string.IsNullOrEmpty(plugins) && Directory.Exists(plugins))
                foreach (var marker in Directory.GetFiles(plugins, "*" + ext))
                {
                    string target = marker.Substring(0, marker.Length - ext.Length);
                    for (int i = 0; i < 20 && File.Exists(target); i++)
                    {
                        try { File.Delete(target); } catch { Thread.Sleep(250); }
                    }
                    if (!File.Exists(target)) try { File.Delete(marker); } catch { }
                }

            if (!relaunch) return null;
            string exe = Get("exe") ?? _gameExe;
            if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) return null;
            _gameExe = exe;
            try
            {
                // UseShellExecute=false: the game becomes our child, so Discord's app audio follows it
                return Process.Start(new ProcessStartInfo(exe, Get("args") ?? "")
                {
                    UseShellExecute = false,
                    WorkingDirectory = Get("workdir") ?? Path.GetDirectoryName(exe)
                });
            }
            catch { return null; }
        }

        // ── Settings ────────────────────────────────────────────────────────────
        private void LoadSettings(ToolStripMenuItem compat, ToolStripMenuItem top)
        {
            try
            {
                if (!File.Exists(SettingsPath)) return;
                foreach (var line in File.ReadAllLines(SettingsPath))
                {
                    var kv = line.Split(new[] { '=' }, 2); if (kv.Length != 2) continue;
                    if (kv[0] == "compat") { _compat = kv[1] == "1"; compat.Checked = _compat; }
                    if (kv[0] == "top") top.Checked = kv[1] == "1";
                    if (kv[0] == "bounds")
                    {
                        var n = kv[1].Split(',').Select(int.Parse).ToArray();
                        var r = new Rectangle(n[0], n[1], n[2], n[3]);
                        if (Screen.AllScreens.Any(sc => sc.WorkingArea.IntersectsWith(r)))
                        { StartPosition = FormStartPosition.Manual; Bounds = r; }
                    }
                }
            }
            catch { }
        }

        private void SaveSettings()
        {
            try
            {
                var b = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
                File.WriteAllLines(SettingsPath, new[]
                {
                    "compat=" + (_compat ? "1" : "0"),
                    "top=" + (TopMost ? "1" : "0"),
                    $"bounds={b.X},{b.Y},{b.Width},{b.Height}"
                });
            }
            catch { }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            SaveSettings();
            Detach();
            base.OnFormClosing(e);
        }
    }

    internal static class Native
    {
        internal const int DWM_TNP_RECTDESTINATION = 0x1, DWM_TNP_OPACITY = 0x4, DWM_TNP_VISIBLE = 0x8, DWM_TNP_SOURCECLIENTAREAONLY = 0x10;

        [StructLayout(LayoutKind.Sequential)] internal struct RECT { public int left, top, right, bottom; }
        [StructLayout(LayoutKind.Sequential)] internal struct PSIZE { public int cx, cy; }

        [StructLayout(LayoutKind.Sequential)]
        internal struct DWM_THUMBNAIL_PROPERTIES
        {
            public int dwFlags;
            public RECT rcDestination;
            public RECT rcSource;
            public byte opacity;
            [MarshalAs(UnmanagedType.Bool)] public bool fVisible;
            [MarshalAs(UnmanagedType.Bool)] public bool fSourceClientAreaOnly;
        }

        [DllImport("dwmapi.dll")] internal static extern int DwmRegisterThumbnail(IntPtr dest, IntPtr src, out IntPtr thumb);
        [DllImport("dwmapi.dll")] internal static extern int DwmUnregisterThumbnail(IntPtr thumb);
        [DllImport("dwmapi.dll")] internal static extern int DwmUpdateThumbnailProperties(IntPtr thumb, ref DWM_THUMBNAIL_PROPERTIES props);
        [DllImport("dwmapi.dll")] internal static extern int DwmQueryThumbnailSourceSize(IntPtr thumb, out PSIZE size);
        [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] internal static extern bool IsIconic(IntPtr hwnd);
        [DllImport("user32.dll")] internal static extern bool GetClientRect(IntPtr hwnd, out RECT rc);
        [DllImport("user32.dll")] internal static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
        [DllImport("user32.dll")] internal static extern bool ShowWindow(IntPtr hwnd, int cmd);
        [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32.dll")] internal static extern bool SetProcessDPIAware();
    }
}
