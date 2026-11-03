// K2 Ring Remote for Windows
// Turns the K2 / K02 Bluetooth ring's fake touch-screen swipes into media keys,
// and stops those fake touches from clicking or scrolling whatever is on screen.
//
// Build: build.cmd  (uses the C# compiler that ships with Windows / .NET Framework 4)

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace K2RingRemote
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            bool created;
            using (var mutex = new Mutex(true, "Local\\K2RingRemote_SingleInstance", out created))
            {
                if (!created)
                {
                    MessageBox.Show("K2 Ring Remote is already running. Look for the ring icon in the system tray.",
                        "K2 Ring Remote", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                try { Native.SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { } // per-monitor v2: work in real pixels
                Application.EnableVisualStyles();
                bool quiet = Array.IndexOf(args, "--startup") >= 0;
                var app = new RemoteApp(quiet);
                Application.Run();
                app.Dispose();
            }
        }
    }

    // ------------------------------------------------------------------ main logic

    sealed class RemoteApp : IDisposable
    {
        const string RingAddress = "41424bab3958";   // Bluetooth address of the K02 ring
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunValue = "K2RingRemote";
        const int TapThreshold = 80;                  // ring units (0..1000); below this both ways = a tap
        const int LearnMargin = 4;                    // pixels a point must be inside an existing blocker

        // Where each ring button first hovers / touches the screen (ring units 0..1000), from a capture of this ring.
        // Left/Right hover slightly above their touch point first, and Windows targets the hover point.
        static readonly int[][] DefaultSpots =
        {
            new[] { 500, 350 },  // Up
            new[] { 500, 590 },  // Down
            new[] { 300, 250 },  // Left  (hover)
            new[] { 300, 255 },  // Left  (touch)
            new[] { 700, 250 },  // Right (hover)
            new[] { 700, 255 },  // Right (touch)
            new[] { 500, 400 },  // Center
        };

        readonly string logPath, iniPath;
        readonly StreamWriter log;
        readonly Config config;
        readonly InputWindow inputWin;
        readonly List<Absorber> absorbers = new List<Absorber>();
        readonly Dictionary<IntPtr, HidDev> devs = new Dictionary<IntPtr, HidDev>();
        readonly NotifyIcon tray;
        readonly System.Windows.Forms.Timer timer;
        readonly Native.LowLevelProc hookProc;
        readonly Icon iconOn, iconOff;
        IntPtr hook = IntPtr.Zero;
        ToolStripMenuItem miEnabled, miStartup;

        bool enabled = true;
        Native.RECT display;
        int tick;

        // current ring gesture
        bool down, hovering;
        int sx, sy, lx, ly;
        DateTime downAt, lastReport;
        DateTime lastAbsorbed = DateTime.MinValue, lastGestureEnd = DateTime.MinValue, lastPointerSeen = DateTime.MinValue;
        string lastButton = "";
        DateTime lastButtonAt = DateTime.MinValue;
        const int DuplicateMs = 250;                  // the ring sometimes sends a second identical tap ~100 ms later
        int absorbedX, absorbedY;

        public RemoteApp(bool quiet)
        {
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            logPath = Path.Combine(dir, "K2Remote.log");
            iniPath = Path.Combine(dir, "K2Remote.ini");
            try { if (File.Exists(logPath) && new FileInfo(logPath).Length > 512 * 1024) File.Delete(logPath); } catch { }
            log = new StreamWriter(new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), Encoding.UTF8);
            log.AutoFlush = true;
            Log("==== K2 Ring Remote started ====");

            config = new Config(iniPath, Log);
            config.Reload();

            inputWin = new InputWindow(this);
            var rid = new[] { new Native.RAWINPUTDEVICE { usUsagePage = 0x0D, usUsage = 0x04, dwFlags = Native.RIDEV_INPUTSINK, hwndTarget = inputWin.Handle } };
            if (!Native.RegisterRawInputDevices(rid, 1, (uint)Marshal.SizeOf(typeof(Native.RAWINPUTDEVICE))))
                Log("ERROR: could not listen for touch input (error " + Marshal.GetLastWin32Error() + ")");

            UpdateDisplayMapping(true);
            foreach (var s in DefaultSpots) AddSpot(s[0], s[1]);

            hookProc = MouseHook;
            hook = Native.SetWindowsHookEx(14 /*WH_MOUSE_LL*/, hookProc, Native.GetModuleHandle(null), 0);
            if (hook == IntPtr.Zero) Log("WARNING: mouse hook failed (error " + Marshal.GetLastWin32Error() + ")");

            iconOn = MakeIcon(true);
            iconOff = MakeIcon(false);
            tray = new NotifyIcon { Icon = iconOn, Text = "K2 Ring Remote - on", Visible = true, ContextMenuStrip = BuildMenu() };
            tray.DoubleClick += (s, e) => SetEnabled(!enabled);

            timer = new System.Windows.Forms.Timer { Interval = 100 };
            timer.Tick += OnTick;
            timer.Start();

            if (!quiet)
                tray.ShowBalloonTip(5000, "K2 Ring Remote is running",
                    "Up/Down = volume, Left/Right = skip back/forward, Center = play/pause. Right-click the tray icon for options.",
                    ToolTipIcon.Info);
        }

        public void Log(string s)
        {
            try { log.WriteLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + s); } catch { }
        }

        // ---------------- raw input from the ring

        public void OnRawInput(IntPtr lParam)
        {
            uint hdr = (uint)Marshal.SizeOf(typeof(Native.RAWINPUTHEADER));
            uint size = 0;
            Native.GetRawInputData(lParam, Native.RID_INPUT, IntPtr.Zero, ref size, hdr);
            if (size == 0) return;
            IntPtr buf = Marshal.AllocHGlobal((int)size);
            try
            {
                if (Native.GetRawInputData(lParam, Native.RID_INPUT, buf, ref size, hdr) != size) return;
                var h = (Native.RAWINPUTHEADER)Marshal.PtrToStructure(buf, typeof(Native.RAWINPUTHEADER));
                if (h.dwType != Native.RIM_TYPEHID) return;
                HidDev d = GetDev(h.hDevice);
                if (!d.IsRing || !d.HasXY) return;
                int sizeHid = Marshal.ReadInt32(buf, (int)hdr);
                int count = Marshal.ReadInt32(buf, (int)hdr + 4);
                for (int i = 0; i < count; i++)
                {
                    var rep = new byte[sizeHid];
                    Marshal.Copy(buf + (int)hdr + 8 + i * sizeHid, rep, 0, sizeHid);
                    Decode(d, rep);
                }
            }
            catch (Exception ex) { Log("input error: " + ex.Message); }
            finally { Marshal.FreeHGlobal(buf); }
        }

        HidDev GetDev(IntPtr h)
        {
            HidDev d;
            if (devs.TryGetValue(h, out d)) return d;
            d = new HidDev { Name = RawName(h) ?? "" };
            d.IsRing = d.Name.ToLowerInvariant().Contains(RingAddress);
            devs[h] = d;
            if (!d.IsRing) return d;

            uint sz = 0;
            Native.GetRawInputDeviceInfo(h, Native.RIDI_PREPARSEDDATA, IntPtr.Zero, ref sz);
            d.Pp = Marshal.AllocHGlobal((int)sz);
            Native.GetRawInputDeviceInfo(h, Native.RIDI_PREPARSEDDATA, d.Pp, ref sz);
            Native.HIDP_CAPS caps;
            Native.HidP_GetCaps(d.Pp, out caps);
            d.MaxUsages = Math.Max(1u, Native.HidP_MaxUsageListLength(Native.HidP_Input, 0, d.Pp));
            ushort n = caps.NumberInputValueCaps;
            if (n > 0)
            {
                var vb = new byte[72 * n];
                Native.HidP_GetValueCaps(Native.HidP_Input, vb, ref n, d.Pp);
                for (int i = 0; i < n; i++)
                {
                    int o = i * 72;
                    ushort page = BitConverter.ToUInt16(vb, o);
                    ushort link = BitConverter.ToUInt16(vb, o + 6);
                    bool isRange = vb[o + 12] != 0;
                    ushort umin = BitConverter.ToUInt16(vb, o + 56);
                    ushort umax = isRange ? BitConverter.ToUInt16(vb, o + 58) : umin;
                    if (page == 0x01 && umin <= 0x30 && 0x30 <= umax) { d.LinkXY = link; d.HasXY = true; }
                }
            }
            Log("Ring detected" + (d.HasXY ? "" : " (but no X/Y found - unexpected)"));
            return d;
        }

        void Decode(HidDev d, byte[] rep)
        {
            uint n = d.MaxUsages;
            var list = new ushort[n * 2];
            bool tip = false, inRange = false;
            if (Native.HidP_GetUsagesEx(Native.HidP_Input, 0, list, ref n, d.Pp, rep, (uint)rep.Length) == Native.HIDP_STATUS_SUCCESS)
                for (int i = 0; i < n; i++)
                {
                    if (list[i * 2 + 1] == 0x0D && list[i * 2] == 0x42) tip = true;
                    if (list[i * 2 + 1] == 0x0D && list[i * 2] == 0x32) inRange = true;
                }
            uint x, y;
            if (Native.HidP_GetUsageValue(Native.HidP_Input, 0x01, d.LinkXY, 0x30, out x, d.Pp, rep, (uint)rep.Length) != Native.HIDP_STATUS_SUCCESS) return;
            if (Native.HidP_GetUsageValue(Native.HidP_Input, 0x01, d.LinkXY, 0x31, out y, d.Pp, rep, (uint)rep.Length) != Native.HIDP_STATUS_SUCCESS) return;
            OnRingReport(tip, inRange, (int)x, (int)y);
        }

        void OnRingReport(bool tip, bool inRange, int x, int y)
        {
            lastReport = DateTime.Now;
            // The first point where the "finger" appears (hover or touch) is where Windows aims the touch: block it.
            if ((tip || inRange) && !hovering) { hovering = true; LearnSpot(x, y); }
            else if (!tip && !inRange) hovering = false;
            if (tip)
            {
                if (!down)
                {
                    down = true; sx = lx = x; sy = ly = y; downAt = lastReport;
                    LearnSpot(x, y);
                }
                else { lx = x; ly = y; }
            }
            else if (down) FinishGesture();
        }

        void FinishGesture()
        {
            down = false;
            int dx = lx - sx, dy = ly - sy;
            string button;
            if (Math.Abs(dx) < TapThreshold && Math.Abs(dy) < TapThreshold) button = "Center";
            else if (Math.Abs(dy) >= Math.Abs(dx)) button = dy > 0 ? "Up" : "Down";   // Up button swipes the finger downward
            else button = dx > 0 ? "Left" : "Right";                                  // Left button swipes the finger rightward

            DateTime since = lastGestureEnd > downAt.AddMilliseconds(-500) ? lastGestureEnd : downAt.AddMilliseconds(-500);
            bool blocked = lastAbsorbed > since;
            bool hoverOnly = !blocked && lastPointerSeen > since;
            DateTime now = DateTime.Now;
            lastGestureEnd = now;

            string result;
            if (!enabled) result = "ignored (remote is turned off)";
            else if (button == lastButton && (now - lastButtonAt).TotalMilliseconds < DuplicateMs) result = "ignored (duplicate tap from the ring)";
            else
            {
                config.ReloadIfChanged();
                result = Perform(button);
                lastButton = button;
                lastButtonAt = now;
            }

            string touch = !enabled ? "" :
                blocked ? string.Format("   [touch blocked at {0},{1}]", absorbedX, absorbedY)
                : hoverOnly ? "   [touch blocked (hover only; Windows never pressed it)]"
                : string.Format("   [touch not seen by blockers - ring point {0},{1}; Windows may have ignored it]", sx, sy);
            Log(string.Format("{0,-6} button -> {1}{2}", button, result, touch));
        }

        string Perform(string button)
        {
            KeyAction a = config.Get(button);
            if (a == null) return "nothing (set to None)";
            int times = a.IsVolume ? config.VolumeSteps : 1;
            for (int i = 0; i < times; i++) SendCombo(a);
            return a.Text + (times > 1 ? " x" + times : "");
        }

        static void SendCombo(KeyAction a)
        {
            var list = new List<Native.INPUT>();
            foreach (ushort m in a.Mods) list.Add(KeyInput(m, false));
            list.Add(KeyInput(a.Vk, false));
            list.Add(KeyInput(a.Vk, true));
            for (int i = a.Mods.Length - 1; i >= 0; i--) list.Add(KeyInput(a.Mods[i], true));
            Native.SendInput((uint)list.Count, list.ToArray(), Marshal.SizeOf(typeof(Native.INPUT)));
        }

        static Native.INPUT KeyInput(ushort vk, bool up)
        {
            var inp = new Native.INPUT { type = 1 /*INPUT_KEYBOARD*/ };
            inp.u.ki.wVk = vk;
            inp.u.ki.wScan = (ushort)Native.MapVirtualKey(vk, 0);
            inp.u.ki.dwFlags = (IsExtended(vk) ? 1u : 0u) | (up ? 2u : 0u);
            inp.u.ki.dwExtraInfo = new IntPtr(0x4B32);
            return inp;
        }

        static bool IsExtended(ushort vk)
        {
            return (vk >= 0x21 && vk <= 0x28) || vk == 0x2D || vk == 0x2E || vk == 0x5B || vk == 0x5C ||
                   (vk >= 0xAD && vk <= 0xB7);
        }

        // ---------------- touch blockers

        void LearnSpot(int x, int y)
        {
            Native.POINT p = Absorber.ToScreen(display, x, y);
            foreach (var a in absorbers)
                if (a.Contains(p, -LearnMargin)) return;   // already well inside an existing blocker
            if (absorbers.Count >= 16) return;
            Log(string.Format("New touch spot seen at ring point {0},{1} - blocking it from now on", x, y));
            AddSpot(x, y);
        }

        void AddSpot(int x, int y)
        {
            var a = new Absorber(this, x, y);
            absorbers.Add(a);
            a.Place(display);
            if (enabled) a.Show();
        }

        public void OnAbsorbed(int px, int py)
        {
            lastAbsorbed = lastPointerSeen = DateTime.Now;
            absorbedX = px; absorbedY = py;
        }

        public void OnPointerSeen()
        {
            lastPointerSeen = DateTime.Now;
        }

        IntPtr MouseHook(int nCode, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (nCode >= 0 && enabled)
                {
                    var info = (Native.MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(Native.MSLLHOOKSTRUCT));
                    long extra = info.dwExtraInfo.ToInt64();
                    bool fromTouch = (extra & 0xFFFFFF00L) == 0xFF515700L;
                    if (!fromTouch)
                        foreach (var a in absorbers)
                            if (a.Visible && a.Contains(info.pt, 3)) a.HideFor(1500);   // real mouse here: get out of its way
                }
            }
            catch { }
            return Native.CallNextHookEx(hook, nCode, wParam, lParam);
        }

        void OnTick(object sender, EventArgs e)
        {
            tick++;
            if (down && (DateTime.Now - lastReport).TotalMilliseconds > 400) FinishGesture();

            if (enabled)
            {
                Native.POINT cur;
                bool haveCur = Native.GetCursorPos(out cur);
                foreach (var a in absorbers)
                    if (!a.Visible && DateTime.Now >= a.HiddenUntil && (!haveCur || !a.Contains(cur, 3))) a.Show();
            }

            if (tick % 10 == 0)
            {
                UpdateDisplayMapping(false);
                if (enabled) foreach (var a in absorbers) if (a.Visible) a.KeepOnTop();
            }
        }

        public void UpdateDisplayMapping(bool logIt)
        {
            Native.RECT rect = new Native.RECT();
            string source = null;
            try
            {
                uint n = 0;
                Native.GetPointerDevices(ref n, null);
                if (n > 0)
                {
                    var arr = new Native.POINTER_DEVICE_INFO[n];
                    if (Native.GetPointerDevices(ref n, arr))
                    {
                        int best = -1, bestScore = 0;
                        for (int i = 0; i < n; i++)
                        {
                            string name = (RawName(arr[i].device) ?? "").ToLowerInvariant();
                            string product = arr[i].productString ?? "";
                            int score = name.Contains(RingAddress) ? 3
                                      : product.IndexOf("K02", StringComparison.OrdinalIgnoreCase) >= 0 ? 2
                                      : arr[i].pointerDeviceType == 3 /*touch*/ ? 1 : 0;
                            if (logIt) Log(string.Format("  pointer device: type={0} product='{1}' match={2}", arr[i].pointerDeviceType, product, score));
                            if (score > bestScore) { bestScore = score; best = i; }
                        }
                        Native.RECT pr, dr;
                        if (best >= 0 && Native.GetPointerDeviceRects(arr[best].device, out pr, out dr) && dr.Right > dr.Left)
                        {
                            rect = dr;
                            source = bestScore == 3 ? "ring's own touch mapping" : "best-guess touch device";
                        }
                    }
                }
            }
            catch (Exception ex) { if (logIt) Log("pointer device lookup failed: " + ex.Message); }

            if (source == null)
            {
                var b = Screen.PrimaryScreen.Bounds;
                rect = new Native.RECT { Left = b.Left, Top = b.Top, Right = b.Right, Bottom = b.Bottom };
                source = "primary screen (fallback)";
            }

            bool changed = rect.Left != display.Left || rect.Top != display.Top || rect.Right != display.Right || rect.Bottom != display.Bottom;
            if (!changed && !logIt) return;
            display = rect;
            Log(string.Format("Ring touches map to screen area {0},{1} - {2},{3} ({4})", rect.Left, rect.Top, rect.Right, rect.Bottom, source));
            foreach (var a in absorbers) { a.Place(display); if (a.Visible) a.Show(); }
        }

        // ---------------- tray

        ContextMenuStrip BuildMenu()
        {
            var m = new ContextMenuStrip();
            var title = new ToolStripMenuItem("K2 Ring Remote") { Enabled = false };
            miEnabled = new ToolStripMenuItem("Remote on", null, (s, e) => SetEnabled(!enabled)) { Checked = true };
            miStartup = new ToolStripMenuItem("Start with Windows", null, (s, e) => SetStartup(!IsStartup()));
            var miSettings = new ToolStripMenuItem("Change button actions...", null, (s, e) => OpenInNotepad(iniPath));
            var miLog = new ToolStripMenuItem("Open activity log", null, (s, e) => OpenInNotepad(logPath));
            var miExit = new ToolStripMenuItem("Exit", null, (s, e) => Application.Exit());
            m.Items.AddRange(new ToolStripItem[] { title, new ToolStripSeparator(), miEnabled, miStartup, new ToolStripSeparator(), miSettings, miLog, new ToolStripSeparator(), miExit });
            m.Opening += (s, e) => { miEnabled.Checked = enabled; miStartup.Checked = IsStartup(); };
            return m;
        }

        static void OpenInNotepad(string path)
        {
            try { Process.Start("notepad.exe", "\"" + path + "\""); } catch { }
        }

        void SetEnabled(bool on)
        {
            enabled = on;
            foreach (var a in absorbers) { if (on) a.Show(); else a.Hide(); }
            tray.Icon = on ? iconOn : iconOff;
            tray.Text = on ? "K2 Ring Remote - on" : "K2 Ring Remote - off";
            Log(on ? "Remote turned ON" : "Remote turned OFF (ring acts as a touch screen again)");
        }

        static bool IsStartup()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey, false))
                return k != null && k.GetValue(RunValue) != null;
        }

        void SetStartup(bool on)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (on) k.SetValue(RunValue, "\"" + Application.ExecutablePath + "\" --startup");
                else k.DeleteValue(RunValue, false);
            }
            Log(on ? "Start with Windows: ON" : "Start with Windows: OFF");
        }

        static Icon MakeIcon(bool on)
        {
            using (var bmp = new Bitmap(32, 32))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    Color ring = on ? Color.FromArgb(37, 99, 235) : Color.FromArgb(128, 128, 128);
                    Color gem = on ? Color.White : Color.FromArgb(190, 190, 190);
                    using (var p = new Pen(ring, 5f)) g.DrawEllipse(p, 4.5f, 4.5f, 23f, 23f);
                    using (var b = new SolidBrush(gem)) g.FillEllipse(b, 11f, 11f, 10f, 10f);
                }
                IntPtr h = bmp.GetHicon();
                var icon = (Icon)Icon.FromHandle(h).Clone();
                Native.DestroyIcon(h);
                return icon;
            }
        }

        static string RawName(IntPtr h)
        {
            uint sz = 0;
            if (Native.GetRawInputDeviceInfo(h, Native.RIDI_DEVICENAME, IntPtr.Zero, ref sz) != 0 || sz == 0) return null;
            IntPtr nb = Marshal.AllocHGlobal((int)(sz + 1) * 2);
            try
            {
                if (Native.GetRawInputDeviceInfo(h, Native.RIDI_DEVICENAME, nb, ref sz) == uint.MaxValue) return null;
                return Marshal.PtrToStringUni(nb);
            }
            finally { Marshal.FreeHGlobal(nb); }
        }

        public void Dispose()
        {
            try { timer.Stop(); } catch { }
            if (hook != IntPtr.Zero) { Native.UnhookWindowsHookEx(hook); hook = IntPtr.Zero; }
            foreach (var a in absorbers) a.DestroyHandle();
            try { tray.Visible = false; tray.Dispose(); } catch { }
            Log("==== K2 Ring Remote stopped ====");
            try { log.Dispose(); } catch { }
        }
    }

    sealed class HidDev
    {
        public string Name;
        public bool IsRing, HasXY;
        public IntPtr Pp;
        public uint MaxUsages;
        public ushort LinkXY;
    }

    // Hidden window that receives the ring's raw input.
    sealed class InputWindow : NativeWindow
    {
        readonly RemoteApp app;
        public InputWindow(RemoteApp app) { this.app = app; CreateHandle(new CreateParams()); }
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_INPUT) app.OnRawInput(m.LParam);
            else if (m.Msg == Native.WM_DISPLAYCHANGE) app.UpdateDisplayMapping(true);
            base.WndProc(ref m);
        }
    }

    // A tiny invisible, always-on-top window sitting exactly where one ring button touches the screen.
    // It swallows that fake touch so it never reaches the video player / browser underneath.
    sealed class Absorber : NativeWindow
    {
        readonly RemoteApp app;
        public readonly int LX, LY;   // ring units 0..1000
        public Native.RECT Rect;
        public bool Visible;
        public DateTime HiddenUntil = DateTime.MinValue;

        public Absorber(RemoteApp app, int lx, int ly)
        {
            this.app = app; LX = lx; LY = ly;
            var cp = new CreateParams
            {
                Caption = "",
                X = -32000, Y = -32000, Width = 1, Height = 1,
                Style = unchecked((int)0x80000000),                       // WS_POPUP
                ExStyle = 0x00000008 | 0x00080000 | 0x00000080 | 0x08000000 // TOPMOST | LAYERED | TOOLWINDOW | NOACTIVATE
            };
            CreateHandle(cp);
            Native.SetLayeredWindowAttributes(Handle, 0, 1, 2 /*LWA_ALPHA*/); // alpha 1/255: invisible but still touchable
        }

        public static Native.POINT ToScreen(Native.RECT d, int x, int y)
        {
            int w = d.Right - d.Left, h = d.Bottom - d.Top;
            return new Native.POINT
            {
                X = d.Left + (int)Math.Round(x * (w - 1) / 1000.0),
                Y = d.Top + (int)Math.Round(y * (h - 1) / 1000.0)
            };
        }

        public void Place(Native.RECT d)
        {
            int size = Math.Max(20, (d.Right - d.Left) * 8 / 1000);
            Native.POINT c = ToScreen(d, LX, LY);
            Rect = new Native.RECT { Left = c.X - size / 2, Top = c.Y - size / 2, Right = c.X - size / 2 + size, Bottom = c.Y - size / 2 + size };
        }

        public bool Contains(Native.POINT p, int margin)
        {
            return p.X >= Rect.Left - margin && p.X < Rect.Right + margin && p.Y >= Rect.Top - margin && p.Y < Rect.Bottom + margin;
        }

        public void Show()
        {
            Native.SetWindowPos(Handle, new IntPtr(-1), Rect.Left, Rect.Top, Rect.Right - Rect.Left, Rect.Bottom - Rect.Top, 0x0010 | 0x0040); // NOACTIVATE | SHOWWINDOW
            Visible = true;
        }

        public void KeepOnTop()
        {
            Native.SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010); // NOSIZE | NOMOVE | NOACTIVATE
        }

        public void Hide()
        {
            Native.ShowWindow(Handle, 0);
            Visible = false;
        }

        public void HideFor(int ms)
        {
            Hide();
            HiddenUntil = DateTime.Now.AddMilliseconds(ms);
        }

        protected override void WndProc(ref Message m)
        {
            switch (m.Msg)
            {
                case Native.WM_POINTERDOWN:
                    long lp = m.LParam.ToInt64();
                    app.OnAbsorbed((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF));
                    m.Result = IntPtr.Zero;
                    return;
                case Native.WM_POINTERUPDATE:
                case Native.WM_POINTERENTER:
                    app.OnPointerSeen();
                    m.Result = IntPtr.Zero;
                    return;
                case Native.WM_POINTERUP:
                case Native.WM_POINTERLEAVE:
                case Native.WM_POINTERCAPTURECHANGED:
                case Native.WM_TOUCH:
                case Native.WM_GESTURE:
                    m.Result = IntPtr.Zero;          // swallow: no mouse emulation, no gestures
                    return;
                case Native.WM_POINTERACTIVATE:
                    m.Result = new IntPtr(3);        // PA_NOACTIVATE: keep focus on the video player
                    return;
                case Native.WM_MOUSEACTIVATE:
                    m.Result = new IntPtr(3);        // MA_NOACTIVATE
                    return;
            }
            base.WndProc(ref m);
        }
    }

    // ------------------------------------------------------------------ settings file

    sealed class KeyAction
    {
        public ushort Vk;
        public ushort[] Mods = new ushort[0];
        public string Text;
        public bool IsVolume;
    }

    sealed class Config
    {
        public const string DefaultText =
@"; K2 Ring Remote - button settings
; Save this file and the change applies on your next ring press.
;
; What each ring button does. Options:
;   VolumeUp, VolumeDown, Mute, PlayPause, NextTrack, PrevTrack, Stop,
;   Left, Right, Up, Down, Space, Enter, Esc, PageUp, PageDown, Home, End,
;   any letter or number (e.g. J, K, L, F), F1-F12, or None.
; Put Ctrl+, Shift+, Alt+ or Win+ in front for shortcuts, e.g. Shift+Right.
;
; Left/Right arrow skips back/forward in YouTube, Netflix and most video players
; (the video window needs to be the active window). In YouTube, J / L skip 10 s.

Up=VolumeUp
Down=VolumeDown
Left=Left
Right=Right
Center=PlayPause

; Volume change per press (each step is 2%)
VolumeSteps=2
";

        static readonly string[] Buttons = { "Up", "Down", "Left", "Right", "Center" };
        static readonly Dictionary<string, ushort> Named = new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase)
        {
            {"VolumeUp",0xAF},{"VolumeDown",0xAE},{"Mute",0xAD},{"PlayPause",0xB3},{"NextTrack",0xB0},{"PrevTrack",0xB1},{"Stop",0xB2},
            {"Left",0x25},{"Up",0x26},{"Right",0x27},{"Down",0x28},{"Space",0x20},{"Enter",0x0D},{"Esc",0x1B},{"Escape",0x1B},{"Tab",0x09},
            {"PageUp",0x21},{"PageDown",0x22},{"End",0x23},{"Home",0x24},{"Comma",0xBC},{"Period",0xBE},{"Backspace",0x08},{"Delete",0x2E}
        };
        static readonly Dictionary<string, ushort> Modifiers = new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase)
        {
            {"Ctrl",0x11},{"Control",0x11},{"Shift",0x10},{"Alt",0x12},{"Win",0x5B}
        };

        readonly string path;
        readonly Action<string> log;
        DateTime stamp = DateTime.MinValue;
        Dictionary<string, KeyAction> map = new Dictionary<string, KeyAction>(StringComparer.OrdinalIgnoreCase);
        public int VolumeSteps = 2;

        public Config(string path, Action<string> log) { this.path = path; this.log = log; }

        public KeyAction Get(string button)
        {
            KeyAction a;
            return map.TryGetValue(button, out a) ? a : null;
        }

        public void ReloadIfChanged()
        {
            try { if (File.GetLastWriteTimeUtc(path) != stamp) Reload(); } catch { }
        }

        public void Reload()
        {
            var m = new Dictionary<string, KeyAction>(StringComparer.OrdinalIgnoreCase);
            string err;
            m["Up"] = Parse("VolumeUp", out err);
            m["Down"] = Parse("VolumeDown", out err);
            m["Left"] = Parse("Left", out err);
            m["Right"] = Parse("Right", out err);
            m["Center"] = Parse("PlayPause", out err);
            int steps = 2;
            try
            {
                if (!File.Exists(path)) File.WriteAllText(path, DefaultText);
                stamp = File.GetLastWriteTimeUtc(path);
                foreach (string raw in File.ReadAllLines(path))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == ';' || line[0] == '#' || line[0] == '[') continue;
                    int eq = line.IndexOf('=');
                    if (eq < 0) continue;
                    string k = line.Substring(0, eq).Trim(), v = line.Substring(eq + 1).Trim();
                    int sc = v.IndexOf(';');
                    if (sc >= 0) v = v.Substring(0, sc).Trim();
                    if (k.Equals("VolumeSteps", StringComparison.OrdinalIgnoreCase))
                    {
                        int n;
                        if (int.TryParse(v, out n) && n >= 1 && n <= 25) steps = n;
                        else log("Settings: VolumeSteps=" + v + " not understood, using 2");
                        continue;
                    }
                    if (Array.FindIndex(Buttons, b => b.Equals(k, StringComparison.OrdinalIgnoreCase)) < 0) continue;
                    KeyAction a = Parse(v, out err);
                    if (err != null) log("Settings: " + k + "=" + v + " not understood (" + err + "), keeping default");
                    else m[k] = a;
                }
            }
            catch (Exception ex) { log("Settings file problem: " + ex.Message + " - using defaults"); }
            map = m;
            VolumeSteps = steps;
            var parts = new List<string>();
            foreach (string b in Buttons) { KeyAction a = Get(b); parts.Add(b + "=" + (a == null ? "None" : a.Text)); }
            log("Settings: " + string.Join(", ", parts) + ", VolumeSteps=" + VolumeSteps);
        }

        static KeyAction Parse(string text, out string err)
        {
            err = null;
            if (text.Equals("None", StringComparison.OrdinalIgnoreCase) || text.Length == 0) return null;
            string[] tokens = text.Split('+');
            var mods = new List<ushort>();
            for (int i = 0; i < tokens.Length - 1; i++)
            {
                ushort mv;
                if (!Modifiers.TryGetValue(tokens[i].Trim(), out mv)) { err = "unknown modifier '" + tokens[i].Trim() + "'"; return null; }
                mods.Add(mv);
            }
            string key = tokens[tokens.Length - 1].Trim();
            ushort vk;
            if (!Named.TryGetValue(key, out vk))
            {
                if (key.Length == 1 && char.IsLetterOrDigit(key[0]) && key[0] < 128) vk = (ushort)char.ToUpperInvariant(key[0]);
                else if (key.Length >= 2 && (key[0] == 'F' || key[0] == 'f'))
                {
                    int f;
                    if (int.TryParse(key.Substring(1), out f) && f >= 1 && f <= 12) vk = (ushort)(0x70 + f - 1);
                    else { err = "unknown key '" + key + "'"; return null; }
                }
                else { err = "unknown key '" + key + "'"; return null; }
            }
            return new KeyAction { Vk = vk, Mods = mods.ToArray(), Text = text, IsVolume = vk == 0xAF || vk == 0xAE };
        }
    }

    // ------------------------------------------------------------------ Win32

    static class Native
    {
        public const int WM_INPUT = 0x00FF, WM_DISPLAYCHANGE = 0x007E, WM_MOUSEACTIVATE = 0x0021, WM_GESTURE = 0x0119, WM_TOUCH = 0x0240;
        public const int WM_POINTERUPDATE = 0x0245, WM_POINTERDOWN = 0x0246, WM_POINTERUP = 0x0247, WM_POINTERENTER = 0x0249,
                         WM_POINTERLEAVE = 0x024A, WM_POINTERACTIVATE = 0x024B, WM_POINTERCAPTURECHANGED = 0x024C;
        public const uint RIDEV_INPUTSINK = 0x100, RID_INPUT = 0x10000003, RIDI_DEVICENAME = 0x20000007, RIDI_PREPARSEDDATA = 0x20000005, RIM_TYPEHID = 2;
        public const int HidP_Input = 0, HIDP_STATUS_SUCCESS = 0x00110000;

        [StructLayout(LayoutKind.Sequential)] public struct RAWINPUTDEVICE { public ushort usUsagePage, usUsage; public uint dwFlags; public IntPtr hwndTarget; }
        [StructLayout(LayoutKind.Sequential)] public struct RAWINPUTHEADER { public uint dwType, dwSize; public IntPtr hDevice, wParam; }
        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] public struct MSLLHOOKSTRUCT { public POINT pt; public uint mouseData, flags, time; public IntPtr dwExtraInfo; }
        [StructLayout(LayoutKind.Sequential)] public struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
        [StructLayout(LayoutKind.Sequential)] public struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
        [StructLayout(LayoutKind.Explicit)] public struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
        [StructLayout(LayoutKind.Sequential)] public struct INPUT { public uint type; public InputUnion u; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct POINTER_DEVICE_INFO
        {
            public uint displayOrientation;
            public IntPtr device;
            public int pointerDeviceType;
            public IntPtr monitor;
            public uint startingCursorId;
            public ushort maxActiveContacts;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 520)] public string productString;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct HIDP_CAPS
        {
            public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
            public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices,
                NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices,
                NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
        }

        public delegate IntPtr LowLevelProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)] public static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] p, uint n, uint cb);
        [DllImport("user32.dll")] public static extern uint GetRawInputData(IntPtr hRaw, uint cmd, IntPtr data, ref uint size, uint cbHeader);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern uint GetRawInputDeviceInfo(IntPtr h, uint cmd, IntPtr data, ref uint size);
        [DllImport("hid.dll")] public static extern int HidP_GetCaps(IntPtr pp, out HIDP_CAPS caps);
        [DllImport("hid.dll")] public static extern int HidP_GetValueCaps(int type, [Out] byte[] caps, ref ushort len, IntPtr pp);
        [DllImport("hid.dll")] public static extern int HidP_GetUsageValue(int type, ushort page, ushort link, ushort usage, out uint value, IntPtr pp, byte[] report, uint len);
        [DllImport("hid.dll")] public static extern int HidP_GetUsagesEx(int type, ushort link, [Out] ushort[] list, ref uint len, IntPtr pp, byte[] report, uint reportLen);
        [DllImport("hid.dll")] public static extern uint HidP_MaxUsageListLength(int type, ushort page, IntPtr pp);
        [DllImport("user32.dll", SetLastError = true)] public static extern bool GetPointerDevices(ref uint count, [In, Out] POINTER_DEVICE_INFO[] devices);
        [DllImport("user32.dll", SetLastError = true)] public static extern bool GetPointerDeviceRects(IntPtr device, out RECT pointerDeviceRect, out RECT displayRect);
        [DllImport("user32.dll")] public static extern bool SetLayeredWindowAttributes(IntPtr h, uint key, byte alpha, uint flags);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] public static extern uint SendInput(uint n, INPUT[] inputs, int cb);
        [DllImport("user32.dll")] public static extern uint MapVirtualKey(uint code, uint mapType);
        [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr SetWindowsHookEx(int id, LowLevelProc proc, IntPtr hMod, uint threadId);
        [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr h);
        [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr h, int n, IntPtr w, IntPtr l);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandle(string name);
        [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
        [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr h);
    }
}
