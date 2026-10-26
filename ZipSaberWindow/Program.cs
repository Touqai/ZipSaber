using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ZipSaberWindow
{
    /// <summary>
    /// ZipSaber window host. Owns a top-level window that looks like Beat Saber's (same title and
    /// icon) and holds the game's real window inside it as a child. When ZipSaber restarts the game,
    /// only the game process is swapped: this window never closes, so a Discord window share stays
    /// live. The relaunched game is started by this process, so Discord's app audio follows it too.
    ///
    /// Lifecycle: game quits normally -> host closes. Host closed with X -> game is asked to quit.
    /// </summary>
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            using (var mutex = new Mutex(true, "Local\\ZipSaberWindowHost", out bool created))
            {
                if (!created) return;
                var o = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i + 1 < args.Length; i += 2)
                    if (args[i].StartsWith("--")) o[args[i].Substring(2)] = args[i + 1];
                int I(string k) => o.TryGetValue(k, out var v) && int.TryParse(v, out int n) ? n : 0;

                try { Native.SetProcessDPIAware(); } catch { }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new HostForm(
                    o.TryGetValue("dir", out var d) ? d : AppDomain.CurrentDomain.BaseDirectory,
                    o.TryGetValue("game", out var g) ? g : null,
                    I("pid"), new Rectangle(I("x"), I("y"), I("w"), I("h")),
                    o.TryGetValue("title", out var t) ? t : "Beat Saber",
                    I("fullscreen") == 1));
            }
        }
    }

    internal sealed class HostForm : Form
    {
        private readonly string _dir;
        private string _gameExe;
        private Process _game;
        private IntPtr _child = IntPtr.Zero;
        private volatile bool _restarting;
        private bool _closingGame;
        private Bitmap _lastFrame;   // shown dimmed behind the loading animation while the game swaps underneath
        private readonly System.Windows.Forms.Timer _animTick = new System.Windows.Forms.Timer { Interval = 16 };
        private readonly Stopwatch _animClock = new Stopwatch();
        private readonly System.Windows.Forms.Timer _tick = new System.Windows.Forms.Timer { Interval = 100 };

        private string RequestPath => Path.Combine(_dir, "restart.req");
        private string InfoPath => Path.Combine(_dir, "host.info");

        private bool _fullscreen;
        private Rectangle _windowedBounds;
        private string CmdPath => Path.Combine(_dir, "host.cmd");

        internal HostForm(string dir, string gameExe, int pid, Rectangle gameRect, string title, bool fullscreen)
        {
            _dir = dir;
            _gameExe = gameExe;
            Text = title;
            BackColor = Color.Black;
            DoubleBuffered = true;
            ResizeRedraw = true;
            try { Icon = Icon.ExtractAssociatedIcon(gameExe ?? Application.ExecutablePath); } catch { }

            if (gameRect.Width > 100 && gameRect.Height > 100)
            {
                // gameRect is the game's client area on screen: line our client area up with it
                StartPosition = FormStartPosition.Manual;
                ClientSize = gameRect.Size;
                var client = RectangleToScreen(ClientRectangle);
                Location = new Point(gameRect.X - (client.X - Left), gameRect.Y - (client.Y - Top));
            }
            else { ClientSize = new Size(1280, 720); StartPosition = FormStartPosition.CenterScreen; }

            if (pid > 0) try { _game = Process.GetProcessById(pid); } catch { }

            if (fullscreen) Shown += (s, e) => SetFullscreen(true);
            _tick.Tick += (s, e) => OnTick();
            _tick.Start();
            _animTick.Tick += (s, e) =>
            {
                if (_child == IntPtr.Zero) Invalidate();
                else { _animTick.Stop(); _animClock.Reset(); }
            };
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try { File.WriteAllText(InfoPath, $"hwnd={Handle.ToInt64()}\npid={Process.GetCurrentProcess().Id}"); } catch { }
        }

        // ── Game tracking ───────────────────────────────────────────────────────
        private void OnTick()
        {
            CheckRestartRequest();
            CheckCommands();
            if (_restarting) return;

            bool alive = false;
            if (_game != null) { try { alive = !_game.HasExited; } catch { alive = false; } }

            if (!alive)
            {
                // a restart request is always written before the game quits, so check once more
                if (File.Exists(RequestPath)) return;
                _child = IntPtr.Zero;
                if (_game != null || _closingGame) { Close(); return; }   // game closed for real -> so do we
                return;
            }

            if (_child == IntPtr.Zero || !Native.IsWindow(_child))
            {
                _child = IntPtr.Zero;
                var w = FindGameWindow(_game.Id, Handle);
                if (w != IntPtr.Zero) Adopt(w);
                if (_child != IntPtr.Zero) ClearLastFrame();
            }
            else if (Native.GetWindowRect(_child, out var r))
            {
                // Unity may resize/move its own window (resolution changes): keep it filling ours
                var c = ClientRectangle;
                var tl = PointToScreen(Point.Empty);
                if (r.left != tl.X || r.top != tl.Y || r.right - r.left != c.Width || r.bottom - r.top != c.Height) FitChild();
            }
        }

        private static IntPtr FindGameWindow(int pid, IntPtr host)
        {
            IntPtr found = IntPtr.Zero;
            // launched with -parentHWND: Unity created its window inside ours already
            Native.EnumChildWindows(host, (h, l) =>
            {
                Native.GetWindowThreadProcessId(h, out uint p);
                if (p == pid && Native.GetParent(h) == host) { found = h; return false; }
                return true;
            }, IntPtr.Zero);
            if (found != IntPtr.Zero) return found;
            Native.EnumWindows((h, l) =>
            {
                Native.GetWindowThreadProcessId(h, out uint p);
                if (p != pid || !Native.IsWindowVisible(h)) return true;
                var cls = new StringBuilder(64);
                Native.GetClassName(h, cls, cls.Capacity);
                if (cls.ToString() == "UnityWndClass") { found = h; return false; }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        private void Adopt(IntPtr w)
        {
            long style = Native.GetWindowLongPtr(w, Native.GWL_STYLE).ToInt64();
            style &= ~(Native.WS_POPUP | Native.WS_CAPTION | Native.WS_THICKFRAME | Native.WS_MINIMIZEBOX | Native.WS_MAXIMIZEBOX | Native.WS_SYSMENU);
            style |= Native.WS_CHILD | Native.WS_VISIBLE;
            Native.SetWindowLongPtr(w, Native.GWL_STYLE, new IntPtr(style));
            long ex = Native.GetWindowLongPtr(w, Native.GWL_EXSTYLE).ToInt64();
            ex &= ~(Native.WS_EX_APPWINDOW | Native.WS_EX_WINDOWEDGE | Native.WS_EX_DLGMODALFRAME);
            Native.SetWindowLongPtr(w, Native.GWL_EXSTYLE, new IntPtr(ex));
            Native.SetParent(w, Handle);
            _child = w;
            FitChild();
            Native.SetWindowPos(w, IntPtr.Zero, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_FRAMECHANGED);
            ActivateChild();
        }

        private void FitChild()
        {
            if (_child == IntPtr.Zero) return;
            var c = ClientRectangle;
            Native.MoveWindow(_child, 0, 0, Math.Max(c.Width, 1), Math.Max(c.Height, 1), true);
        }

        private void ActivateChild()
        {
            if (_child == IntPtr.Zero) return;
            // Unity's embedded-window contract: forward activation, and give the game keyboard focus
            Native.SendMessage(_child, Native.WM_ACTIVATE, new IntPtr(Native.WA_ACTIVE), IntPtr.Zero);
            uint childThread = Native.GetWindowThreadProcessId(_child, out _);
            uint myThread = Native.GetCurrentThreadId();
            if (childThread != 0 && childThread != myThread && Native.AttachThreadInput(myThread, childThread, true))
            {
                Native.SetFocus(_child);
                Native.AttachThreadInput(myThread, childThread, false);
            }
        }

        protected override void OnResize(EventArgs e) { base.OnResize(e); FitChild(); }
        protected override void OnActivated(EventArgs e) { base.OnActivated(e); ActivateChild(); }
        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            if (_child != IntPtr.Zero) Native.SendMessage(_child, Native.WM_ACTIVATE, new IntPtr(Native.WA_INACTIVE), IntPtr.Zero);
        }

        // ── Fullscreen (the game can't fullscreen a child window, so the host does it) ──
        private void SetFullscreen(bool on)
        {
            if (on == _fullscreen) return;
            _fullscreen = on;
            if (on)
            {
                _windowedBounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
                WindowState = FormWindowState.Normal;
                FormBorderStyle = FormBorderStyle.None;
                Bounds = Screen.FromHandle(Handle).Bounds;
            }
            else
            {
                FormBorderStyle = FormBorderStyle.Sizable;
                if (_windowedBounds.Width > 0) Bounds = _windowedBounds;
            }
            FitChild();
            ActivateChild();
        }

        private void CheckCommands()
        {
            if (!File.Exists(CmdPath)) return;
            string[] lines;
            try { lines = File.ReadAllLines(CmdPath); File.Delete(CmdPath); } catch { return; }
            foreach (var l in lines)
            {
                if (l == "fullscreen=1") SetFullscreen(true);
                else if (l == "fullscreen=0") SetFullscreen(false);
                else if (l == "fullscreen=toggle") SetFullscreen(!_fullscreen);
                else if (l == "activate") ActivateChild();
            }
        }

        private void SnapshotChild()
        {
            try
            {
                if (_child == IntPtr.Zero || !Native.GetWindowRect(_child, out var r)) return;
                int w = r.right - r.left, h = r.bottom - r.top;
                if (w <= 0 || h <= 0) return;
                var bmp = new Bitmap(w, h);
                using (var g = Graphics.FromImage(bmp))
                {
                    IntPtr hdc = g.GetHdc();
                    try { Native.PrintWindow(_child, hdc, 0x2 /*PW_RENDERFULLCONTENT*/); }
                    finally { g.ReleaseHdc(hdc); }
                }
                _lastFrame?.Dispose();
                _lastFrame = bmp;
            }
            catch { }
        }

        private void ClearLastFrame()
        {
            if (_lastFrame == null) return;
            _lastFrame.Dispose(); _lastFrame = null;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Color.Black);
            if (_child != IntPtr.Zero) return;   // the game covers us
            var c = ClientRectangle;
            if (_lastFrame != null)
            {
                g.DrawImage(_lastFrame, c);
                using (var dim = new SolidBrush(Color.FromArgb(170, 0, 0, 0))) g.FillRectangle(dim, c);
            }
            if (_animClock.IsRunning) SaberAnim.Draw(g, c, _animClock.Elapsed.TotalSeconds);
        }

        // ── Restart (requested by ZipSaber) ─────────────────────────────────────
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
            catch { return; }   // still being written

            _restarting = true;
            _animClock.Restart(); _animTick.Start();
            SnapshotChild();
            _child = IntPtr.Zero;   // old window is going away; paint the held frame behind it
            Invalidate();
            Task.Run(() => DoRestart(req)).ContinueWith(t => BeginInvoke((Action)(() =>
            {
                _child = IntPtr.Zero;
                _game = t.Result;
                _restarting = false;
                if (_game == null) Close();
                else Invalidate();
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
                        try { File.Delete(target); } catch { Thread.Sleep(250); }
                    if (!File.Exists(target)) try { File.Delete(marker); } catch { }
                }

            if (!relaunch) return null;
            string exe = Get("exe") ?? _gameExe;
            if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) return null;
            _gameExe = exe;
            try
            {
                // started by us = our child process, so Discord's app audio for this window includes it
                string args = (Get("args") ?? "").Trim();
                IntPtr host = IntPtr.Zero;
                try { host = (IntPtr)Invoke((Func<IntPtr>)(() => Handle)); } catch { }
                // -parentHWND: Unity creates its window inside ours and handles focus/cursor as an embedded player
                if (host != IntPtr.Zero && args.IndexOf("-parentHWND", StringComparison.OrdinalIgnoreCase) < 0)
                    args = (args + $" -parentHWND {host.ToInt64()}").Trim();
                return Process.Start(new ProcessStartInfo(exe, args)
                {
                    UseShellExecute = false,
                    WorkingDirectory = Get("workdir") ?? Path.GetDirectoryName(exe)
                });
            }
            catch { return null; }
        }

        // ── Closing: closing this window closes the game ────────────────────────
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            bool alive = false;
            if (_game != null) { try { alive = !_game.HasExited; } catch { } }
            if (alive && !_closingGame)
            {
                // the game's window lives inside ours and would be destroyed with it, so ask the game to quit first
                e.Cancel = true;
                _closingGame = true;
                if (_child != IntPtr.Zero) Native.PostMessage(_child, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                var g = _game;
                Task.Run(() =>
                {
                    try { if (!g.WaitForExit(15000)) g.Kill(); } catch { }
                }).ContinueWith(_ => BeginInvoke((Action)Close));
                return;
            }
            if (alive && _child != IntPtr.Zero)
            {
                // still alive after all that: hand the window back rather than destroying it
                Native.SetParent(_child, IntPtr.Zero);
            }
            try { File.Delete(InfoPath); } catch { }
            base.OnFormClosing(e);
        }
    }

    /// <summary>
    /// Loading animation: a note with a down arrow gets cut by a saber swinging down, then a note with
    /// an up arrow gets cut swinging up. Loops. Pure GDI+, no assets.
    /// </summary>
    internal static class SaberAnim
    {
        private static readonly Color NoteRed = Color.FromArgb(214, 36, 46);
        private static readonly Color NoteRedDark = Color.FromArgb(120, 14, 22);
        private static readonly Color SaberBlue = Color.FromArgb(52, 160, 255);
        private const double Period = 1.8;   // seconds for down + up

        private static float Clamp01(double v) => (float)Math.Max(0, Math.Min(1, v));
        private static float Ease(float t) => t < 0.5f ? 4 * t * t * t : 1 - (float)Math.Pow(-2 * t + 2, 3) / 2;

        internal static void Draw(Graphics g, Rectangle c, double seconds)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float s = Math.Min(c.Width, c.Height) * 0.13f;           // note size
            if (s < 8) return;
            float cx = c.X + c.Width / 2f, cy = c.Y + c.Height / 2f;

            double t = (seconds % Period) / Period;
            bool down = t < 0.5;
            double u = (t % 0.5) / 0.5;                               // 0..1 inside this half

            float appear = Clamp01(u / 0.18);
            float swing = Ease(Clamp01((u - 0.18) / 0.38));
            const double cutAt = 0.37;                                // when the blade crosses the note
            float split = Clamp01((u - cutAt) / 0.5);

            // ── note ─────────────────────────────────────────────────────────
            if (u < cutAt)
            {
                float k = 0.75f + 0.25f * Ease(appear);
                DrawNote(g, cx, cy, s * k, down, (int)(255 * appear), 0, 0, null);
            }
            else
            {
                float off = s * 0.85f * Ease(split);
                float rot = 14f * split;
                int a = (int)(255 * (1 - split));
                DrawNote(g, cx - off, cy + off * 0.15f, s, down, a, -rot, -1, null);   // left half
                DrawNote(g, cx + off, cy + off * 0.15f, s, down, a, rot, 1, null);     // right half
            }

            // ── saber: vertical swing through the note, with a fading trail ─────
            float travel = s * 2.6f;
            float y0 = down ? cy - travel : cy + travel, y1 = down ? cy + travel : cy - travel;
            if (u >= 0.18 && u <= 0.85)
            {
                float head = y0 + (y1 - y0) * swing;
                float trailStart = y0 + (y1 - y0) * Math.Max(0f, swing - 0.45f);
                float fade = Clamp01(1 - (u - 0.56) / 0.29);           // trail fades after the swing ends
                float w = s * 0.13f;

                using (var glow = new LinearGradientBrush(new PointF(cx, trailStart - 1), new PointF(cx, head + (down ? 1 : -1)),
                           Color.FromArgb(0, SaberBlue), Color.FromArgb((int)(150 * fade), SaberBlue)))
                {
                    var r = RectangleF.FromLTRB(cx - w * 1.6f, Math.Min(trailStart, head), cx + w * 1.6f, Math.Max(trailStart, head));
                    if (r.Height > 1) g.FillRectangle(glow, r);
                }
                using (var core = new LinearGradientBrush(new PointF(cx, trailStart - 1), new PointF(cx, head + (down ? 1 : -1)),
                           Color.FromArgb(0, Color.White), Color.FromArgb((int)(230 * fade), Color.White)))
                {
                    var r = RectangleF.FromLTRB(cx - w * 0.25f, Math.Min(trailStart, head), cx + w * 0.25f, Math.Max(trailStart, head));
                    if (r.Height > 1) g.FillRectangle(core, r);
                }
                if (swing < 1f)
                {
                    // the blade itself: a short bright bar across the swing, slightly tilted
                    float bl = s * 1.9f, tilt = down ? -0.18f : 0.18f;
                    float[] widths = { w * 4.2f, w * 2.6f, w * 1.6f }; int[] alphas = { 40, 80, 170 };
                    for (int i = 0; i < 3; i++)
                        using (var p = new Pen(Color.FromArgb(alphas[i], SaberBlue), widths[i]) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                            g.DrawLine(p, cx - bl / 2, head - bl / 2 * tilt, cx + bl / 2, head + bl / 2 * tilt);
                    using (var p = new Pen(Color.White, w * 0.7f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                        g.DrawLine(p, cx - bl / 2, head - bl / 2 * tilt, cx + bl / 2, head + bl / 2 * tilt);
                }
            }
        }

        /// <param name="half">0 = whole note, -1 = left half, 1 = right half</param>
        private static void DrawNote(Graphics g, float x, float y, float size, bool arrowDown, int alpha, float rotDeg, int half, object _)
        {
            if (alpha <= 0) return;
            var state = g.Save();
            g.TranslateTransform(x, y);
            g.RotateTransform(rotDeg);
            if (half != 0) g.SetClip(new RectangleF(half < 0 ? -size : 0, -size, size, size * 2));

            float h = size / 2f, r = size * 0.18f;
            using (var path = RoundRect(-h, -h, size, size, r))
            {
                using (var fill = new LinearGradientBrush(new PointF(0, -h), new PointF(0, h),
                           Color.FromArgb(alpha, NoteRed), Color.FromArgb(alpha, NoteRedDark)))
                    g.FillPath(fill, path);
                using (var edge = new Pen(Color.FromArgb(alpha / 2, 255, 120, 120), size * 0.03f)) g.DrawPath(edge, path);
            }
            // chevron arrow near the edge it points to
            float aw = size * 0.30f, ah = size * 0.14f, ay = arrowDown ? h * 0.42f : -h * 0.42f;
            var tri = arrowDown
                ? new[] { new PointF(-aw, ay - ah), new PointF(aw, ay - ah), new PointF(0, ay + ah) }
                : new[] { new PointF(-aw, ay + ah), new PointF(aw, ay + ah), new PointF(0, ay - ah) };
            using (var b = new SolidBrush(Color.FromArgb(alpha, Color.White))) g.FillPolygon(b, tri);

            g.Restore(state);
        }

        private static GraphicsPath RoundRect(float x, float y, float w, float h, float r)
        {
            var p = new GraphicsPath();
            float d = r * 2;
            p.AddArc(x, y, d, d, 180, 90);
            p.AddArc(x + w - d, y, d, d, 270, 90);
            p.AddArc(x + w - d, y + h - d, d, d, 0, 90);
            p.AddArc(x, y + h - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    internal static class Native
    {
        internal const int GWL_STYLE = -16, GWL_EXSTYLE = -20;
        internal const long WS_CHILD = 0x40000000L, WS_POPUP = 0x80000000L, WS_VISIBLE = 0x10000000L, WS_CAPTION = 0x00C00000L,
            WS_THICKFRAME = 0x00040000L, WS_MINIMIZEBOX = 0x00020000L, WS_MAXIMIZEBOX = 0x00010000L, WS_SYSMENU = 0x00080000L;
        internal const long WS_EX_APPWINDOW = 0x00040000L, WS_EX_WINDOWEDGE = 0x00000100L, WS_EX_DLGMODALFRAME = 0x00000001L;
        internal const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_FRAMECHANGED = 0x20;
        internal const int WM_ACTIVATE = 0x0006, WM_CLOSE = 0x0010, WA_INACTIVE = 0, WA_ACTIVE = 1;

        internal delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);
        [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumProc cb, IntPtr lParam);
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetClassName(IntPtr hwnd, StringBuilder sb, int max);
        [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] internal static extern bool IsWindow(IntPtr hwnd);
        [DllImport("user32.dll")] internal static extern IntPtr SetParent(IntPtr child, IntPtr parent);
        [DllImport("user32.dll")] internal static extern bool MoveWindow(IntPtr hwnd, int x, int y, int w, int h, bool repaint);
        [DllImport("user32.dll")] internal static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);
        [DllImport("user32.dll")] internal static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] internal static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] internal static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
        [DllImport("user32.dll")] internal static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] internal static extern bool AttachThreadInput(uint a, uint b, bool attach);
        [DllImport("user32.dll")] internal static extern IntPtr SetFocus(IntPtr hwnd);
        [DllImport("user32.dll")] internal static extern IntPtr GetParent(IntPtr hwnd);
        [DllImport("user32.dll")] internal static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr l);
        [DllImport("kernel32.dll")] internal static extern uint GetCurrentThreadId();
        [StructLayout(LayoutKind.Sequential)] internal struct RECT { public int left, top, right, bottom; }
        [DllImport("user32.dll")] internal static extern bool GetWindowRect(IntPtr hwnd, out RECT rc);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] internal static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int idx);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] internal static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int idx, IntPtr val);
    }
}
