// K2 Ring Remote for Windows
// Language: C# (.NET Framework 4) using Windows Forms + Win32 APIs. Build with build.cmd - nothing to install.
//
// The K2 / K02 ring pretends to be a touch screen: every button is a fake finger swipe or tap.
// This app reads those swipes, turns them into the actions of the active profile
// (single press / double press / hold), and blocks the fake touches from reaching the screen.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace K2RingRemote
{
    // =====================================================================  entry point

    static class Program
    {
        public static readonly string Dir = AppDomain.CurrentDomain.BaseDirectory;
        public static readonly string LogPath = Path.Combine(Dir, "K2Remote.log");
        public static readonly string SettingsPath = Path.Combine(Dir, "K2Remote.settings.ini");
        static readonly string LegacyIniPath = Path.Combine(Dir, "K2Remote.ini");

        [STAThread]
        static void Main(string[] args)
        {
            bool created;
            using (var mutex = new Mutex(true, "Local\\K2RingRemote_SingleInstance", out created))
            {
                if (!created)
                {
                    MessageBox.Show("K2 Ring Remote is already running. Double-click the ring icon in the system tray to open its settings.",
                        "K2 Ring Remote", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                // The touch blockers must line up with real screen pixels, so the engine thread is per-monitor DPI aware.
                try { Native.SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                bool quiet = Array.IndexOf(args, "--startup") >= 0;
                bool openSettings = Array.IndexOf(args, "--settings") >= 0;

                Log.Open(LogPath);
                Log.Write("==== K2 Ring Remote started ====");
                Settings settings = Settings.LoadOrCreate(SettingsPath, LegacyIniPath);
                var engine = new Engine(settings.Clone());

                // Tray icon + settings window live on their own thread, system-DPI aware, so WinForms scales them normally.
                var uiThread = new Thread(() =>
                {
                    try { Native.SetThreadDpiAwarenessContext(new IntPtr(-2)); } catch { }
                    var ui = new TrayUi(engine, settings, quiet || openSettings);
                    if (openSettings) ui.ShowSettings();
                    Application.Run();
                    ui.Dispose();
                });
                uiThread.SetApartmentState(ApartmentState.STA);
                uiThread.Start();

                Application.Run();          // engine loop: ring input, touch blockers, key sending
                engine.Dispose();
                uiThread.Join(3000);
                Log.Write("==== K2 Ring Remote stopped ====");
            }
        }
    }

    static class Log
    {
        static readonly object gate = new object();
        static StreamWriter writer;

        public static void Open(string path)
        {
            try { if (File.Exists(path) && new FileInfo(path).Length > 1024 * 1024) File.Delete(path); } catch { }
            try
            {
                writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), Encoding.UTF8);
                writer.AutoFlush = true;
            }
            catch { }
        }

        public static void Write(string s)
        {
            lock (gate)
            {
                try { if (writer != null) writer.WriteLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + s); } catch { }
            }
        }
    }

    // =====================================================================  settings model

    enum HoldMode { Repeat, Once, KeyDown }

    sealed class ButtonBinding
    {
        public string Press = "None", Double = "None", Hold = "None";
        public HoldMode Mode = HoldMode.Repeat;

        public ButtonBinding() { }
        public ButtonBinding(string press, string dbl, string hold, HoldMode mode) { Press = press; Double = dbl; Hold = hold; Mode = mode; }
        public ButtonBinding Clone() { return (ButtonBinding)MemberwiseClone(); }
        public string Get(int slot) { return slot == 0 ? Press : slot == 1 ? Double : Hold; }
        public void Set(int slot, string spec) { if (slot == 0) Press = spec; else if (slot == 1) Double = spec; else Hold = spec; }
    }

    sealed class Profile
    {
        public string Name;
        readonly Dictionary<string, ButtonBinding> map = new Dictionary<string, ButtonBinding>(StringComparer.OrdinalIgnoreCase);

        public Profile(string name) { Name = name; }

        public ButtonBinding this[string button]
        {
            get
            {
                ButtonBinding b;
                if (!map.TryGetValue(button, out b)) { b = new ButtonBinding(); map[button] = b; }
                return b;
            }
            set { map[button] = value; }
        }

        public Profile Clone(string newName)
        {
            var p = new Profile(newName ?? Name);
            foreach (var kv in map) p.map[kv.Key] = kv.Value.Clone();
            return p;
        }
    }

    sealed class Settings
    {
        public static readonly string[] Buttons = { "Up", "Down", "Left", "Right", "Center" };

        public List<Profile> Profiles = new List<Profile>();
        public string Active = "Media";
        public int DoublePressMs = 400, HoldMs = 500, RepeatMs = 150, VolumeSteps = 2;

        public Profile ActiveProfile { get { return Find(Active) ?? Profiles[0]; } }

        public Profile Find(string name)
        {
            foreach (var p in Profiles) if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return p;
            return null;
        }

        public Settings Clone()
        {
            var s = (Settings)MemberwiseClone();
            s.Profiles = new List<Profile>();
            foreach (var p in Profiles) s.Profiles.Add(p.Clone(null));
            return s;
        }

        public static Settings Defaults()
        {
            var s = new Settings();

            var media = new Profile("Media");
            media["Up"] = new ButtonBinding("VolumeUp", "None", "VolumeUp", HoldMode.Repeat);
            media["Down"] = new ButtonBinding("VolumeDown", "None", "VolumeDown", HoldMode.Repeat);
            media["Left"] = new ButtonBinding("Left", "None", "Left", HoldMode.Repeat);
            media["Right"] = new ButtonBinding("Right", "None", "Right", HoldMode.Repeat);
            media["Center"] = new ButtonBinding("PlayPause", "F", "Mute", HoldMode.Once);

            var work = new Profile("Work");
            work["Up"] = new ButtonBinding("PageUp", "None", "Mouse:ScrollUp", HoldMode.Repeat);
            work["Down"] = new ButtonBinding("PageDown", "None", "Mouse:ScrollDown", HoldMode.Repeat);
            work["Left"] = new ButtonBinding("Left", "Alt+Left", "Left", HoldMode.Repeat);
            work["Right"] = new ButtonBinding("Right", "Alt+Right", "Right", HoldMode.Repeat);
            work["Center"] = new ButtonBinding("Alt+Tab", "Win+D", "None", HoldMode.Once);

            var gaming = new Profile("Gaming");
            gaming["Up"] = new ButtonBinding("W", "None", "W", HoldMode.KeyDown);
            gaming["Down"] = new ButtonBinding("S", "None", "S", HoldMode.KeyDown);
            gaming["Left"] = new ButtonBinding("A", "None", "A", HoldMode.KeyDown);
            gaming["Right"] = new ButtonBinding("D", "None", "D", HoldMode.KeyDown);
            gaming["Center"] = new ButtonBinding("Space", "None", "Space", HoldMode.KeyDown);

            s.Profiles.Add(media);
            s.Profiles.Add(work);
            s.Profiles.Add(gaming);
            s.Active = "Media";
            return s;
        }

        public static Settings LoadOrCreate(string path, string legacyPath)
        {
            Settings s = null;
            if (File.Exists(path))
            {
                try { s = Parse(File.ReadAllLines(path)); }
                catch (Exception ex) { Log.Write("Settings file could not be read (" + ex.Message + "), using defaults"); }
            }
            if (s == null)
            {
                s = Defaults();
                if (File.Exists(legacyPath)) MigrateLegacy(s, legacyPath);
                try { s.Save(path); } catch (Exception ex) { Log.Write("Could not save settings: " + ex.Message); }
            }
            Log.Write("Settings loaded: " + s.Profiles.Count + " profiles, active = " + s.Active);
            return s;
        }

        static int Clamp(int v, int lo, int hi) { return Math.Max(lo, Math.Min(hi, v)); }

        static Settings Parse(string[] lines)
        {
            var s = new Settings();
            Profile cur = null;
            bool inGeneral = false;
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == ';' || line[0] == '#') continue;
                if (line[0] == '[')
                {
                    string sec = line.Trim('[', ']', ' ');
                    inGeneral = sec.Equals("General", StringComparison.OrdinalIgnoreCase);
                    cur = null;
                    if (sec.Equals("Profile", StringComparison.OrdinalIgnoreCase))
                    {
                        cur = new Profile("Profile " + (s.Profiles.Count + 1));
                        s.Profiles.Add(cur);
                    }
                    continue;
                }
                int eq = line.IndexOf('=');
                if (eq < 0) continue;
                string k = line.Substring(0, eq).Trim().ToLowerInvariant(), v = line.Substring(eq + 1).Trim();
                if (inGeneral)
                {
                    int n;
                    if (k == "activeprofile") s.Active = v;
                    else if (int.TryParse(v, out n))
                    {
                        if (k == "doublepressms") s.DoublePressMs = Clamp(n, 100, 2000);
                        else if (k == "holdms") s.HoldMs = Clamp(n, 200, 5000);
                        else if (k == "repeatms") s.RepeatMs = Clamp(n, 30, 2000);
                        else if (k == "volumesteps") s.VolumeSteps = Clamp(n, 1, 25);
                    }
                }
                else if (cur != null)
                {
                    if (k == "name") { if (v.Length > 0) cur.Name = v; continue; }
                    int dot = k.IndexOf('.');
                    if (dot < 0) continue;
                    string btnKey = k.Substring(0, dot), field = k.Substring(dot + 1);
                    int bi = Array.FindIndex(Buttons, b => b.Equals(btnKey, StringComparison.OrdinalIgnoreCase));
                    if (bi < 0) continue;
                    ButtonBinding bind = cur[Buttons[bi]];
                    if (field == "press") bind.Press = v;
                    else if (field == "double") bind.Double = v;
                    else if (field == "hold") bind.Hold = v;
                    else if (field == "holdmode")
                    {
                        HoldMode m;
                        if (Enum.TryParse(v, true, out m)) bind.Mode = m;
                    }
                }
            }
            if (s.Profiles.Count == 0) return null;
            if (s.Find(s.Active) == null) s.Active = s.Profiles[0].Name;
            return s;
        }

        public void Save(string path)
        {
            var sb = new StringBuilder();
            sb.AppendLine("; K2 Ring Remote settings - easiest to change in the Settings window (double-click the tray icon).");
            sb.AppendLine("; Actions: a key or shortcut such as VolumeUp, PlayPause, F, Ctrl+Shift+S, Alt+Tab;");
            sb.AppendLine(";          Mouse:LeftClick / RightClick / MiddleClick / ScrollUp / ScrollDown; NextProfile; None");
            sb.AppendLine(";          Add \" x3\" to send it 3 times per press, e.g. Down x3 or Mouse:LeftClick x2");
            sb.AppendLine("; HoldMode: Repeat (repeat while held), Once, KeyDown (keep the key pressed while held, repeating like a real key)");
            sb.AppendLine("; Holding Left / Right / Center makes the ring itself do Previous / Next / Play-Pause; the app can't change those.");
            sb.AppendLine();
            sb.AppendLine("[General]");
            sb.AppendLine("ActiveProfile=" + Active);
            sb.AppendLine("DoublePressMs=" + DoublePressMs);
            sb.AppendLine("HoldMs=" + HoldMs);
            sb.AppendLine("RepeatMs=" + RepeatMs);
            sb.AppendLine("VolumeSteps=" + VolumeSteps);
            foreach (var p in Profiles)
            {
                sb.AppendLine();
                sb.AppendLine("[Profile]");
                sb.AppendLine("Name=" + p.Name);
                foreach (string b in Buttons)
                {
                    ButtonBinding bind = p[b];
                    sb.AppendLine(b + ".Press=" + bind.Press);
                    sb.AppendLine(b + ".Double=" + bind.Double);
                    sb.AppendLine(b + ".Hold=" + bind.Hold);
                    sb.AppendLine(b + ".HoldMode=" + bind.Mode);
                }
            }
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, sb.ToString(), Encoding.UTF8);
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }

        // The first version of the app used K2Remote.ini (one action per button). Fold it into the Media profile.
        static void MigrateLegacy(Settings s, string legacyPath)
        {
            try
            {
                Profile media = s.Find("Media");
                foreach (string raw in File.ReadAllLines(legacyPath))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == ';') continue;
                    int eq = line.IndexOf('=');
                    if (eq < 0) continue;
                    string k = line.Substring(0, eq).Trim(), v = line.Substring(eq + 1).Trim();
                    int sc = v.IndexOf(';');
                    if (sc >= 0) v = v.Substring(0, sc).Trim();
                    int n;
                    if (k.Equals("VolumeSteps", StringComparison.OrdinalIgnoreCase) && int.TryParse(v, out n)) { s.VolumeSteps = Clamp(n, 1, 25); continue; }
                    int bi = Array.FindIndex(Buttons, b => b.Equals(k, StringComparison.OrdinalIgnoreCase));
                    string err;
                    ActionSpec.Parse(v, out err);
                    if (bi >= 0 && err == null) media[Buttons[bi]].Press = v;
                }
                File.Delete(legacyPath);
                Log.Write("Moved the old K2Remote.ini button settings into the Media profile");
            }
            catch (Exception ex) { Log.Write("Could not read old K2Remote.ini: " + ex.Message); }
        }
    }

    // =====================================================================  actions

    enum ActionKind { None, Keys, Mouse, NextProfile }

    sealed class ActionSpec
    {
        public const int MaxTimes = 50;

        public ActionKind Kind;
        public ushort Vk;
        public ushort[] Mods = new ushort[0];
        public string Mouse = "";
        public int Times = 1;   // one ring press sends the action this many times (spec suffix " x3")

        public static readonly string[] MouseActions = { "LeftClick", "RightClick", "MiddleClick", "ScrollUp", "ScrollDown" };
        static readonly string[] MouseFriendly = { "Left click", "Right click", "Middle click", "Scroll up", "Scroll down" };
        static readonly ushort[] ModifierOrder = { 0x11, 0x10, 0x12, 0x5B };   // Ctrl, Shift, Alt, Win
        static readonly Regex TimesSuffix = new Regex(@"^(.*\S)\s+[xX](\d{1,3})$");

        public bool IsVolume { get { return Kind == ActionKind.Keys && Mods.Length == 0 && (Vk == 0xAF || Vk == 0xAE); } }
        public bool CanRepeat { get { return Kind == ActionKind.Keys || Kind == ActionKind.Mouse; } }

        public static ActionSpec Parse(string spec)
        {
            string err;
            return Parse(spec, out err);
        }

        public static ActionSpec Parse(string spec, out string err)
        {
            string s = (spec ?? "").Trim();
            int times = 1;
            Match m = TimesSuffix.Match(s);
            if (m.Success)
            {
                s = m.Groups[1].Value;
                times = Math.Max(1, Math.Min(MaxTimes, int.Parse(m.Groups[2].Value)));
            }
            ActionSpec a = ParseOne(s, out err);
            if (a.CanRepeat) a.Times = times;
            return a;
        }

        // The same spec, sent 'times' times per press.
        public static string WithTimes(string spec, int times)
        {
            string s = (spec ?? "").Trim();
            Match m = TimesSuffix.Match(s);
            if (m.Success) s = m.Groups[1].Value;
            times = Math.Max(1, Math.Min(MaxTimes, times));
            return times > 1 ? s + " x" + times : s;
        }

        static ActionSpec ParseOne(string s, out string err)
        {
            err = null;
            var a = new ActionSpec();
            if (s.Length == 0 || s.Equals("None", StringComparison.OrdinalIgnoreCase)) return a;
            if (s.Equals("NextProfile", StringComparison.OrdinalIgnoreCase)) { a.Kind = ActionKind.NextProfile; return a; }
            if (s.StartsWith("Mouse:", StringComparison.OrdinalIgnoreCase))
            {
                string m = s.Substring(6).Trim();
                int i = Array.FindIndex(MouseActions, x => x.Equals(m, StringComparison.OrdinalIgnoreCase));
                if (i < 0) { err = "unknown mouse action '" + m + "'"; return a; }
                a.Kind = ActionKind.Mouse;
                a.Mouse = MouseActions[i];
                return a;
            }
            string[] t = s.Split('+');
            var mods = new List<ushort>();
            for (int i = 0; i < t.Length - 1; i++)
            {
                ushort m = KeyNames.Modifier(t[i].Trim());
                if (m == 0) { err = "unknown modifier '" + t[i].Trim() + "'"; return a; }
                if (!mods.Contains(m)) mods.Add(m);
            }
            ushort vk;
            string keyName = t[t.Length - 1].Trim();
            if (!KeyNames.TryParse(keyName, out vk)) { err = "unknown key '" + keyName + "'"; return a; }
            a.Kind = ActionKind.Keys;
            a.Vk = vk;
            a.Mods = mods.ToArray();
            return a;
        }

        public string ToFriendly()
        {
            return BaseFriendly() + (Times > 1 ? " ×" + Times : "");
        }

        // The action's name without the repeat count.
        public string BaseFriendly()
        {
            switch (Kind)
            {
                case ActionKind.None: return "Nothing";
                case ActionKind.NextProfile: return "Next profile";
                case ActionKind.Mouse: return MouseFriendly[Array.IndexOf(MouseActions, Mouse)];
            }
            var parts = new List<string>();
            foreach (ushort m in Mods) parts.Add(KeyNames.Friendly(m));
            parts.Add(KeyNames.Friendly(Vk));
            return string.Join(" + ", parts);
        }

        public static string Describe(string spec)
        {
            string err;
            ActionSpec a = Parse(spec, out err);
            return err != null ? "(invalid: " + spec + ")" : a.ToFriendly();
        }

        // Canonical spec text for a recorded key combination.
        public static string Build(ICollection<ushort> heldModifiers, ushort vk)
        {
            var parts = new List<string>();
            foreach (ushort m in ModifierOrder)
                if (m != vk && heldModifiers.Contains(m)) parts.Add(KeyNames.NameOf(m));
            parts.Add(KeyNames.NameOf(vk));
            return string.Join("+", parts);
        }
    }

    static class KeyNames
    {
        sealed class Entry
        {
            public readonly string Name, Friendly;
            public readonly ushort Vk;
            public Entry(string name, ushort vk, string friendly) { Name = name; Vk = vk; Friendly = friendly; }
        }

        static readonly Dictionary<string, ushort> byName = new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<ushort, Entry> byVk = new Dictionary<ushort, Entry>();

        static KeyNames()
        {
            Add("VolumeUp", 0xAF, "Volume Up"); Add("VolumeDown", 0xAE, "Volume Down"); Add("Mute", 0xAD, "Mute");
            Add("PlayPause", 0xB3, "Play/Pause"); Add("NextTrack", 0xB0, "Next Track"); Add("PrevTrack", 0xB1, "Previous Track");
            Add("Stop", 0xB2, "Stop Media");
            Add("Left", 0x25, "Left Arrow"); Add("Up", 0x26, "Up Arrow"); Add("Right", 0x27, "Right Arrow"); Add("Down", 0x28, "Down Arrow");
            Add("Space", 0x20, "Space"); Add("Enter", 0x0D, "Enter"); Add("Esc", 0x1B, "Esc"); Add("Tab", 0x09, "Tab");
            Add("Backspace", 0x08, "Backspace"); Add("Delete", 0x2E, "Delete"); Add("Insert", 0x2D, "Insert");
            Add("Home", 0x24, "Home"); Add("End", 0x23, "End"); Add("PageUp", 0x21, "Page Up"); Add("PageDown", 0x22, "Page Down");
            Add("PrintScreen", 0x2C, "Print Screen"); Add("CapsLock", 0x14, "Caps Lock"); Add("Pause", 0x13, "Pause");
            Add("ScrollLock", 0x91, "Scroll Lock"); Add("NumLock", 0x90, "Num Lock"); Add("Apps", 0x5D, "Menu Key");
            Add("Shift", 0x10, "Shift"); Add("Ctrl", 0x11, "Ctrl"); Add("Alt", 0x12, "Alt"); Add("Win", 0x5B, "Win");
            Add("Comma", 0xBC, ","); Add("Period", 0xBE, "."); Add("Minus", 0xBD, "-"); Add("Equals", 0xBB, "=");
            Add("Semicolon", 0xBA, ";"); Add("Slash", 0xBF, "/"); Add("Backtick", 0xC0, "`"); Add("LBracket", 0xDB, "[");
            Add("RBracket", 0xDD, "]"); Add("Backslash", 0xDC, "\\"); Add("Quote", 0xDE, "'");
            Add("BrowserBack", 0xA6, "Browser Back"); Add("BrowserForward", 0xA7, "Browser Forward"); Add("BrowserRefresh", 0xA8, "Browser Refresh");
            for (int i = 0; i <= 9; i++) Add("Num" + i, 0x60 + i, "Num " + i);
            Add("NumMultiply", 0x6A, "Num *"); Add("NumAdd", 0x6B, "Num +"); Add("NumSubtract", 0x6D, "Num -");
            Add("NumDecimal", 0x6E, "Num ."); Add("NumDivide", 0x6F, "Num /");
            byName["Escape"] = 0x1B; byName["Return"] = 0x0D; byName["Control"] = 0x11; byName["Windows"] = 0x5B;
            byName["PgUp"] = 0x21; byName["PgDn"] = 0x22; byName["Del"] = 0x2E;
        }

        static void Add(string name, int vk, string friendly)
        {
            var e = new Entry(name, (ushort)vk, friendly);
            byName[name] = e.Vk;
            if (!byVk.ContainsKey(e.Vk)) byVk[e.Vk] = e;
        }

        public static ushort Modifier(string s)
        {
            switch (s.ToLowerInvariant())
            {
                case "ctrl": case "control": return 0x11;
                case "shift": return 0x10;
                case "alt": return 0x12;
                case "win": case "windows": return 0x5B;
            }
            return 0;
        }

        public static bool TryParse(string s, out ushort vk)
        {
            if (byName.TryGetValue(s, out vk)) return true;
            if (s.Length == 1)
            {
                char c = char.ToUpperInvariant(s[0]);
                if ((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')) { vk = c; return true; }
            }
            int n;
            if (s.Length >= 2 && (s[0] == 'F' || s[0] == 'f') && int.TryParse(s.Substring(1), out n) && n >= 1 && n <= 24)
            {
                vk = (ushort)(0x70 + n - 1);
                return true;
            }
            if (s.Length > 2 && s.StartsWith("VK", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(s.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out n) && n > 0 && n < 256)
            {
                vk = (ushort)n;
                return true;
            }
            vk = 0;
            return false;
        }

        public static string NameOf(ushort vk)
        {
            Entry e;
            if (byVk.TryGetValue(vk, out e)) return e.Name;
            if ((vk >= 'A' && vk <= 'Z') || (vk >= '0' && vk <= '9')) return ((char)vk).ToString();
            if (vk >= 0x70 && vk <= 0x87) return "F" + (vk - 0x6F);
            return "VK" + vk.ToString("X2");
        }

        public static string Friendly(ushort vk)
        {
            Entry e;
            if (byVk.TryGetValue(vk, out e)) return e.Friendly;
            string n = NameOf(vk);
            return n.StartsWith("VK") ? "Key " + n.Substring(2) : n;
        }

        public static bool IsExtended(ushort vk)
        {
            return (vk >= 0x21 && vk <= 0x28) || vk == 0x2C || vk == 0x2D || vk == 0x2E || vk == 0x5B || vk == 0x5C ||
                   vk == 0x5D || vk == 0x6F || vk == 0x90 || (vk >= 0xA6 && vk <= 0xB7);
        }
    }

    static class Sender
    {
        public const int Signature = 0x4B32;   // dwExtraInfo on everything this app types or clicks

        // Press and release, 'count' times, in one batch so nothing else can slip in between.
        public static void Tap(ActionSpec a, int count)
        {
            var l = new List<Native.INPUT>();
            for (int n = 0; n < Math.Max(1, count); n++)
            {
                if (a.Kind == ActionKind.Keys)
                {
                    foreach (ushort m in a.Mods) l.Add(Key(m, false));
                    l.Add(Key(a.Vk, false));
                    l.Add(Key(a.Vk, true));
                    for (int i = a.Mods.Length - 1; i >= 0; i--) l.Add(Key(a.Mods[i], true));
                }
                else if (a.Kind == ActionKind.Mouse)
                {
                    switch (a.Mouse)
                    {
                        case "LeftClick": l.Add(MouseInput(0x0002, 0)); l.Add(MouseInput(0x0004, 0)); break;
                        case "RightClick": l.Add(MouseInput(0x0008, 0)); l.Add(MouseInput(0x0010, 0)); break;
                        case "MiddleClick": l.Add(MouseInput(0x0020, 0)); l.Add(MouseInput(0x0040, 0)); break;
                        case "ScrollUp": l.Add(MouseInput(0x0800, 120)); break;
                        case "ScrollDown": l.Add(MouseInput(0x0800, -120)); break;
                    }
                }
            }
            if (l.Count > 0) Send(l.ToArray());
        }

        // Press and keep holding (for "Keep key held down").
        public static void Down(ActionSpec a)
        {
            if (a.Kind == ActionKind.Keys)
            {
                var l = new List<Native.INPUT>();
                foreach (ushort m in a.Mods) l.Add(Key(m, false));
                l.Add(Key(a.Vk, false));
                Send(l.ToArray());
            }
            else if (a.Kind == ActionKind.Mouse)
            {
                switch (a.Mouse)
                {
                    case "LeftClick": Send(MouseInput(0x0002, 0)); break;
                    case "RightClick": Send(MouseInput(0x0008, 0)); break;
                    case "MiddleClick": Send(MouseInput(0x0020, 0)); break;
                    default: Tap(a, 1); break;
                }
            }
        }

        public static void Up(ActionSpec a)
        {
            if (a.Kind == ActionKind.Keys)
            {
                var l = new List<Native.INPUT>();
                l.Add(Key(a.Vk, true));
                for (int i = a.Mods.Length - 1; i >= 0; i--) l.Add(Key(a.Mods[i], true));
                Send(l.ToArray());
            }
            else if (a.Kind == ActionKind.Mouse)
            {
                switch (a.Mouse)
                {
                    case "LeftClick": Send(MouseInput(0x0004, 0)); break;
                    case "RightClick": Send(MouseInput(0x0010, 0)); break;
                    case "MiddleClick": Send(MouseInput(0x0040, 0)); break;
                }
            }
        }

        static Native.INPUT Key(ushort vk, bool up)
        {
            var inp = new Native.INPUT { type = 1 };
            inp.u.ki.wVk = vk;
            inp.u.ki.wScan = (ushort)Native.MapVirtualKey(vk, 0);
            inp.u.ki.dwFlags = (KeyNames.IsExtended(vk) ? 1u : 0u) | (up ? 2u : 0u);
            inp.u.ki.dwExtraInfo = new IntPtr(Signature);
            return inp;
        }

        static Native.INPUT MouseInput(uint flags, int data)
        {
            var inp = new Native.INPUT { type = 0 };
            inp.u.mi.dwFlags = flags;
            inp.u.mi.mouseData = unchecked((uint)data);
            inp.u.mi.dwExtraInfo = new IntPtr(Signature);
            return inp;
        }

        static void Send(params Native.INPUT[] arr)
        {
            Native.SendInput((uint)arr.Length, arr, Marshal.SizeOf(typeof(Native.INPUT)));
        }
    }

    // =====================================================================  speaker volume (Windows Core Audio)

    // Reads and sets the default speaker's volume, so the app can undo the ring's own Volume Up / Down.
    sealed class AudioVolume
    {
        IAudioEndpointVolume ep;
        DateTime gotAt = DateTime.MinValue;
        Guid ctx = Guid.NewGuid();

        IAudioEndpointVolume Get()
        {
            if (ep != null && (DateTime.Now - gotAt).TotalSeconds < 5) return ep;   // re-check now and then: the default device can change
            gotAt = DateTime.Now;
            try
            {
                var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
                IMMDevice dev;
                if (en.GetDefaultAudioEndpoint(0 /*eRender*/, 0 /*eConsole*/, out dev) != 0 || dev == null) { ep = null; return null; }
                Guid iid = typeof(IAudioEndpointVolume).GUID;
                object o;
                ep = dev.Activate(ref iid, 0x17 /*CLSCTX_ALL*/, IntPtr.Zero, out o) == 0 ? o as IAudioEndpointVolume : null;
            }
            catch { ep = null; }
            return ep;
        }

        public bool Read(out float level, out bool mute)
        {
            level = 0; mute = false;
            try
            {
                IAudioEndpointVolume v = Get();
                return v != null && v.GetMasterVolumeLevelScalar(out level) == 0 && v.GetMute(out mute) == 0;
            }
            catch { ep = null; return false; }
        }

        public void SetLevel(float level)
        {
            try { IAudioEndpointVolume v = Get(); if (v != null) v.SetMasterVolumeLevelScalar(level, ref ctx); } catch { ep = null; }
        }

        public void SetMute(bool mute)
        {
            try { IAudioEndpointVolume v = Get(); if (v != null) v.SetMute(mute, ref ctx); } catch { ep = null; }
        }
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    class MMDeviceEnumeratorCom { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float levelDb, ref Guid ctx);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid ctx);
        [PreserveSig] int GetMasterVolumeLevel(out float levelDb);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float levelDb, ref Guid ctx);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid ctx);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float levelDb);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    // =====================================================================  engine (ring input -> actions)

    sealed class Engine : IDisposable
    {
        const string RingAddress = "41424bab3958";   // Bluetooth address of the K02 ring
        const int TapThreshold = 80;                  // ring units (0..1000): smaller movement both ways = a tap
        const int LearnMargin = 4;                    // px a new touch point must be inside an existing blocker
        const int BounceMs = 40;                      // identical presses closer than this are contact bounce
        const int StuckMs = 8000;                     // a touch with no reports for this long is treated as released
        const int MaxSpots = 16;

        // Where each ring button first hovers / touches the screen (ring units 0..1000), from a capture of this ring.
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

        readonly Control invoker = new Control();
        readonly InputWindow inputWin;
        readonly List<Absorber> absorbers = new List<Absorber>();
        readonly Dictionary<IntPtr, HidDev> devs = new Dictionary<IntPtr, HidDev>();
        readonly System.Windows.Forms.Timer timer;
        readonly Native.LowLevelProc hookProc;
        IntPtr hook = IntPtr.Zero;
        Settings settings;
        bool enabled = true;
        Native.RECT display;
        DateTime lastMaintenance = DateTime.MinValue;

        public event Action<string> Activity;          // raised on the engine thread
        public event Action<string> ProfileSwitched;   // raised on the engine thread

        // fake-touch state
        bool down, hovering, holdChecked;
        int sx, sy, lx, ly, reports;
        DateTime downAt, lastReport;
        DateTime lastAbsorbed = DateTime.MinValue, lastPointerSeen = DateTime.MinValue, lastTouchEnd = DateTime.MinValue;

        // press / double-press / hold state
        string pending;                // button whose single press waits for a possible double press
        DateTime pendingStart;
        string lastPress = "";
        DateTime lastPressStart = DateTime.MinValue;
        bool holding;
        string holdButton;
        ActionSpec holdAction;
        HoldMode holdMode;
        DateTime nextRepeat;
        int holdCount;

        // touch details for the log
        bool sawHover, hoverFirst, firstConfident = true;
        DateTime lastTouchLeak = DateTime.MinValue;
        Native.POINT leakPt;

        // The ring's second HID part sends real media keys. From a capture of this ring: a touch-pad button kept pressed
        // sends no swipe, only a media key - Up: Volume Up repeated about every 0.35 s, Down: Volume Down repeated,
        // Left / Right / Center: Previous Track / Next Track / Play-Pause once. The camera button sends one Volume Up
        // (ignored by the app). Windows' HID service acts on these itself (they never pass the keyboard hook), so they
        // can't be blocked; for the volume keys the app puts the volume back and runs the user's Hold action instead.
        const int VolUpDecideMs = 500;       // another Volume Up this soon = Up is held; none = camera button, ignored
        const int MediaHoldEndMs = 600;      // an Up / Down hold ends when the ring's repeats stop for this long
        string mediaHold;                    // "Up" or "Down" while the ring reports that button held
        DateTime lastMediaRepeat;
        bool volUpPending;                   // a Volume Up came in: the camera button, or the start of an Up hold?
        DateTime volUpPendingAt;
        readonly AudioVolume volume = new AudioVolume();
        bool guardOn, guardMute, sampleMute;    // volume guard: puts the volume back after the ring's own volume keys
        float guardLevel, sampleLevel;
        DateTime guardUntil, sampleAt = DateTime.MinValue;
        int guardFixes;
        static readonly uint ourPid = (uint)Process.GetCurrentProcess().Id;

        public Engine(Settings s)
        {
            settings = s;
            IntPtr unused = invoker.Handle;   // so other threads can BeginInvoke onto this one

            inputWin = new InputWindow(this);
            Listen(0x0D, 0x04, "touch input");
            Listen(0x0C, 0x01, "media keys");

            UpdateDisplayMapping(true);
            foreach (var p in DefaultSpots) AddSpot(p[0], p[1]);

            hookProc = MouseHook;
            hook = Native.SetWindowsHookEx(14 /*WH_MOUSE_LL*/, hookProc, Native.GetModuleHandle(null), 0);
            if (hook == IntPtr.Zero) Log.Write("WARNING: mouse hook failed (error " + Marshal.GetLastWin32Error() + ")");

            timer = new System.Windows.Forms.Timer { Interval = 15 };
            timer.Tick += OnTick;
            timer.Start();
        }

        void Listen(ushort page, ushort usage, string what)
        {
            var rid = new[] { new Native.RAWINPUTDEVICE { usUsagePage = page, usUsage = usage, dwFlags = Native.RIDEV_INPUTSINK, hwndTarget = inputWin.Handle } };
            if (!Native.RegisterRawInputDevices(rid, 1, (uint)Marshal.SizeOf(typeof(Native.RAWINPUTDEVICE))))
                Log.Write("ERROR: could not listen for " + what + " (error " + Marshal.GetLastWin32Error() + ")");
        }

        // ---------------- called from the UI thread

        public void Post(Action a)
        {
            try { invoker.BeginInvoke(a); } catch { }
        }

        public void ApplySettings(Settings s)
        {
            Post(() =>
            {
                StopHold();
                pending = null;
                bool profileChanged = settings == null || settings.Active != s.Active;
                settings = s;
                if (profileChanged) Log.Write("Active profile: " + s.Active);
            });
        }

        public void SetEnabled(bool on)
        {
            Post(() =>
            {
                if (enabled == on) return;
                StopHold();
                pending = null;
                mediaHold = null;
                volUpPending = false;
                enabled = on;
                foreach (var a in absorbers) { if (on) a.Show(); else a.Hide(); }
                Log.Write(on ? "Remote turned ON" : "Remote turned OFF (ring acts as a touch screen again)");
            });
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
                if (!d.IsRing || !(d.HasXY || d.IsMedia)) return;
                int sizeHid = Marshal.ReadInt32(buf, (int)hdr);
                int count = Marshal.ReadInt32(buf, (int)hdr + 4);
                for (int i = 0; i < count; i++)
                {
                    var rep = new byte[sizeHid];
                    Marshal.Copy(buf + (int)hdr + 8 + i * sizeHid, rep, 0, sizeHid);
                    if (d.IsMedia) DecodeMedia(d, rep);
                    else Decode(d, rep);
                }
            }
            catch (Exception ex) { Log.Write("input error: " + ex.Message); }
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
            if (caps.UsagePage == 0x0C)
            {
                d.IsMedia = true;
                Log.Write("Ring media-key part detected (camera button / long presses)");
                return d;
            }
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
            Log.Write("Ring detected" + (d.HasXY ? "" : " (but no X/Y found - unexpected)"));
            return d;
        }

        void Decode(HidDev d, byte[] rep)
        {
            uint n = d.MaxUsages;
            var list = new ushort[n * 2];
            bool tip = false, inRange = false, confident = false;
            if (Native.HidP_GetUsagesEx(Native.HidP_Input, 0, list, ref n, d.Pp, rep, (uint)rep.Length) == Native.HIDP_STATUS_SUCCESS)
                for (int i = 0; i < n; i++)
                {
                    if (list[i * 2 + 1] == 0x0D && list[i * 2] == 0x42) tip = true;
                    if (list[i * 2 + 1] == 0x0D && list[i * 2] == 0x32) inRange = true;
                    if (list[i * 2 + 1] == 0x0D && list[i * 2] == 0x47) confident = true;
                }
            uint x, y;
            if (Native.HidP_GetUsageValue(Native.HidP_Input, 0x01, d.LinkXY, 0x30, out x, d.Pp, rep, (uint)rep.Length) != Native.HIDP_STATUS_SUCCESS) return;
            if (Native.HidP_GetUsageValue(Native.HidP_Input, 0x01, d.LinkXY, 0x31, out y, d.Pp, rep, (uint)rep.Length) != Native.HIDP_STATUS_SUCCESS) return;
            OnRingReport(tip, inRange, confident, (int)x, (int)y);
        }

        void OnRingReport(bool tip, bool inRange, bool confident, int x, int y)
        {
            lastReport = DateTime.Now;
            // The first point where the "finger" appears (hover or touch) is where Windows aims the touch: block it.
            if ((tip || inRange) && !hovering) { hovering = true; LearnSpot(x, y); }
            else if (!tip && !inRange) { hovering = false; if (!down) sawHover = false; }
            if (inRange && !tip && !down) sawHover = true;

            if (tip)
            {
                if (!down)
                {
                    down = true; holdChecked = false;
                    sx = lx = x; sy = ly = y; downAt = lastReport; reports = 1;
                    hoverFirst = sawHover; firstConfident = confident;
                }
                else { lx = x; ly = y; reports++; }
            }
            else if (down) TouchEnded();
        }

        // ---------------- the ring's media keys (camera button, long presses)

        void DecodeMedia(HidDev d, byte[] rep)
        {
            uint n = Math.Max(4u, d.MaxUsages);
            var list = new ushort[n * 2];
            var now = new List<ushort>();
            if (Native.HidP_GetUsagesEx(Native.HidP_Input, 0, list, ref n, d.Pp, rep, (uint)rep.Length) == Native.HIDP_STATUS_SUCCESS)
                for (int i = 0; i < n; i++)
                    if (list[i * 2 + 1] == 0x0C && list[i * 2] != 0 && !now.Contains(list[i * 2])) now.Add(list[i * 2]);
            string raw = BitConverter.ToString(rep);
            foreach (ushort u in new List<ushort>(d.MediaDown))
                if (!now.Contains(u)) { d.MediaDown.Remove(u); OnRingKey(u, false, raw); }
            foreach (ushort u in now)
                if (!d.MediaDown.Contains(u)) { d.MediaDown.Add(u); OnRingKey(u, true, raw); }
        }

        void OnRingKey(ushort usage, bool isDown, string raw)
        {
            if (!isDown) return;          // the ring always lets go of its media keys at once: only the press matters
            DateTime now = DateTime.Now;
            string name = MediaName(usage);
            Log.Write(string.Format("  ring key: {0}   [report {1}]", name, raw));
            if (!enabled) return;

            if (usage != 0xE9 && volUpPending) SingleVolumeUp();
            switch (usage)
            {
                case 0xE9:   // Volume Up: Up held (then it repeats), or the camera button (just once)
                    if (mediaHold == "Up") RingRepeat("Up", now);
                    else if (volUpPending && (now - volUpPendingAt).TotalMilliseconds <= VolUpDecideMs) { volUpPending = false; BeginMediaHold("Up", now); }
                    else
                    {
                        if (volUpPending) SingleVolumeUp();
                        volUpPending = true;
                        volUpPendingAt = now;
                        if (!IsVolumeAction(settings.ActiveProfile["Up"].Hold)) GuardVolume(now, true);
                    }
                    break;
                case 0xEA:   // Volume Down: Down held
                    if (mediaHold == "Down") RingRepeat("Down", now);
                    else BeginMediaHold("Down", now);
                    break;
                case 0xB6: RingLongPress("Left", name); break;
                case 0xB5: RingLongPress("Right", name); break;
                case 0xCD: RingLongPress("Center", name); break;
                default: Report("Ring", "key", name + " (unknown to the app - Windows handles it)"); break;
            }
        }

        // A Volume Up that wasn't followed by another one: the camera button (or a very short hold). Ignored.
        void SingleVolumeUp()
        {
            volUpPending = false;
            Log.Write("  (single Volume Up - camera button or a very short hold: ignored)");
        }

        void BeginMediaHold(string button, DateTime now)
        {
            EndMediaHold();
            mediaHold = button;
            lastMediaRepeat = now;
            ButtonBinding b = settings.ActiveProfile[button];
            ActionSpec a = ActionSpec.Parse(b.Hold);
            if (IsRingsOwn(button, b.Hold)) { StopGuard(); Report(button, "hold", a.BaseFriendly() + " (the ring does this itself)"); return; }
            if (IsVolumeAction(b.Hold)) StopGuard(); else GuardVolume(now, button == "Up");
            if (a.Kind == ActionKind.None) { Report(button, "hold", "nothing (no hold action set)"); return; }
            FlushPending();
            StopHold();
            StartHold(button, a, b.Mode);
        }

        // The ring repeats Volume Up / Down while Up / Down is held.
        void RingRepeat(string button, DateTime now)
        {
            lastMediaRepeat = now;
            if (!IsVolumeAction(settings.ActiveProfile[button].Hold)) GuardVolume(now, button == "Up");
        }

        void EndMediaHold()
        {
            if (mediaHold == null) return;
            string b = mediaHold;
            mediaHold = null;
            if (holding && holdButton == b) StopHold();
        }

        void RingLongPress(string button, string name)
        {
            EndMediaHold();
            Report(button, "hold", name + " (built into the ring - Windows does it, the app can't change it)");
        }

        static bool IsVolumeAction(string spec)
        {
            ActionSpec a = ActionSpec.Parse(spec);
            return a.Kind == ActionKind.Keys && a.Mods.Length == 0 && (a.Vk == 0xAF || a.Vk == 0xAE || a.Vk == 0xAD);
        }

        // The action is exactly what the ring sends anyway (Volume Up for Up, Volume Down for Down).
        static bool IsRingsOwn(string button, string spec)
        {
            ActionSpec a = ActionSpec.Parse(spec);
            ushort own = button == "Down" ? (ushort)0xAE : (ushort)0xAF;
            return a.Kind == ActionKind.Keys && a.Mods.Length == 0 && a.Vk == own && a.Times == 1;
        }

        // ---------------- volume guard: put the volume back after the ring's own Volume Up / Down

        void GuardVolume(DateTime now, bool up)
        {
            if (!guardOn)
            {
                float cur;
                bool mute;
                if (!volume.Read(out cur, out mute)) return;
                // If Windows already applied the ring's key, use the sample from just before it.
                bool landed = (now - sampleAt).TotalMilliseconds < 300 &&
                              (up ? (cur > sampleLevel + 0.001f || mute != sampleMute) : cur < sampleLevel - 0.001f);
                guardLevel = landed ? sampleLevel : cur;
                guardMute = landed ? sampleMute : mute;
                guardOn = true;
                guardFixes = 0;
            }
            guardUntil = now.AddMilliseconds(MediaHoldEndMs);
        }

        void StopGuard()
        {
            guardOn = false;
            sampleAt = DateTime.MinValue;
        }

        // Every tick: while guarding, put the volume back; otherwise keep a recent sample of it.
        void TickVolume(DateTime now)
        {
            if (guardOn)
            {
                float cur;
                bool mute;
                if (volume.Read(out cur, out mute))
                {
                    bool fixedIt = false;
                    if (Math.Abs(cur - guardLevel) > 0.001f) { volume.SetLevel(guardLevel); fixedIt = true; }
                    if (mute != guardMute) { volume.SetMute(guardMute); fixedIt = true; }
                    if (fixedIt) guardFixes++;
                }
                if (now > guardUntil)
                {
                    if (guardFixes > 0) Log.Write("  put the volume back to " + (int)Math.Round(guardLevel * 100) + "%");
                    StopGuard();
                }
            }
            else if (enabled && (now - sampleAt).TotalMilliseconds >= 100)
            {
                if (volume.Read(out sampleLevel, out sampleMute)) sampleAt = now;
            }
        }

        static string MediaName(ushort u)
        {
            switch (u)
            {
                case 0xE9: return "Volume Up";
                case 0xEA: return "Volume Down";
                case 0xE2: return "Mute";
                case 0xCD: return "Play/Pause";
                case 0xB5: return "Next Track";
                case 0xB6: return "Previous Track";
                case 0xB7: return "Stop";
                case 0x30: return "Power";
                case 0x40: return "Menu";
                case 0x183: return "Media Player";
                case 0x196: return "Web Browser";
                case 0x223: return "Home";
                case 0x224: return "Back";
                case 0x22D: return "Zoom In";
                case 0x22E: return "Zoom Out";
            }
            return "media key 0x" + u.ToString("X3");
        }

        static bool OurWindowInFront()
        {
            IntPtr h = Native.GetForegroundWindow();
            if (h == IntPtr.Zero) return false;
            uint pid;
            Native.GetWindowThreadProcessId(h, out pid);
            return pid == ourPid;
        }

        string Classify()
        {
            int dx = lx - sx, dy = ly - sy;
            if (Math.Abs(dx) < TapThreshold && Math.Abs(dy) < TapThreshold) return "Center";
            if (Math.Abs(dy) >= Math.Abs(dx)) return dy > 0 ? "Up" : "Down";   // the Up button swipes the finger downward
            return dx > 0 ? "Left" : "Right";                                    // the Left button swipes the finger rightward
        }

        void TouchEnded()
        {
            down = false;
            DateTime now = DateTime.Now;
            string button = Classify();
            DateTime since = lastTouchEnd > downAt.AddMilliseconds(-500) ? lastTouchEnd : downAt.AddMilliseconds(-500);
            string touch = lastAbsorbed > since ? "blocked"
                         : lastTouchLeak > since ? string.Format("REACHED THE SCREEN at {0},{1}", leakPt.X, leakPt.Y)
                         : lastPointerSeen > since ? "hover only (blocked)"
                         : "not seen by blockers";
            lastTouchEnd = now;
            Log.Write(string.Format("  ring gesture: {0,-6} from {1},{2} dx={3} dy={4} lasted {5} ms, {6} reports{7}{8}, touch {9}",
                button, sx, sy, lx - sx, ly - sy, (int)(now - downAt).TotalMilliseconds, reports,
                hoverFirst ? ", hover first" : "", firstConfident ? "" : ", low confidence", touch));
            sawHover = false;

            if (holding && holdButton != mediaHold) { StopHold(); return; }   // end of a touch hold (not one the ring's media keys run)
            if (!enabled) { Report(button, "press", "ignored (remote is off)"); return; }
            OnPress(button, downAt);
        }

        void OnPress(string button, DateTime start)
        {
            if (button == lastPress && (start - lastPressStart).TotalMilliseconds < BounceMs) return;
            lastPress = button;
            lastPressStart = start;
            ButtonBinding b = settings.ActiveProfile[button];

            if (pending != null)
            {
                if (pending == button && (start - pendingStart).TotalMilliseconds <= settings.DoublePressMs)
                {
                    pending = null;
                    Run(button, "double", b.Double);
                    return;
                }
                FlushPending();
            }
            if (ActionSpec.Parse(b.Double).Kind != ActionKind.None)
            {
                pending = button;           // wait: a second press may follow
                pendingStart = start;
                return;
            }
            Run(button, "press", b.Press);
        }

        void FlushPending()
        {
            if (pending == null) return;
            string p = pending;
            pending = null;
            Run(p, "press", settings.ActiveProfile[p].Press);
        }

        void Run(string button, string kind, string spec)
        {
            Report(button, kind, Fire(ActionSpec.Parse(spec)));
        }

        string Fire(ActionSpec a)
        {
            switch (a.Kind)
            {
                case ActionKind.None: return "nothing";
                case ActionKind.NextProfile: return SwitchToNextProfile();
            }
            int times = a.Times * (a.IsVolume ? settings.VolumeSteps : 1);
            string what = a.BaseFriendly() + (times > 1 ? " x" + times : "");
            // Keys sent now would land in our own Settings window (and change its dropdowns), so don't send them.
            if (OurWindowInFront()) return what + "  (not sent: the Settings window is in front - click another window to use the ring)";
            Sender.Tap(a, times);
            return what;
        }

        void StartHold(string button, ActionSpec a, HoldMode mode)
        {
            if (a.Kind != ActionKind.NextProfile && OurWindowInFront())
            {
                Report(button, "hold", a.BaseFriendly() + "  (not sent: the Settings window is in front)");
                return;
            }
            if (a.Kind == ActionKind.NextProfile) mode = HoldMode.Once;
            if (mode == HoldMode.KeyDown && !(a.Kind == ActionKind.Keys || (a.Kind == ActionKind.Mouse && a.Mouse.EndsWith("Click"))))
                mode = HoldMode.Repeat;
            holding = true; holdButton = button; holdAction = a; holdMode = mode; holdCount = 0;
            string what;
            if (mode == HoldMode.Once) what = Fire(a);
            else if (mode == HoldMode.KeyDown) { Sender.Down(a); what = a.BaseFriendly() + " (held down)"; }
            else
            {
                what = Fire(a) + " (repeating)";
                holdCount = 1;
                nextRepeat = DateTime.Now.AddMilliseconds(settings.RepeatMs);
            }
            Report(button, "hold", what);
        }

        void StopHold()
        {
            if (!holding) return;
            holding = false;
            if (holdMode == HoldMode.KeyDown)
            {
                Sender.Up(holdAction);
                Report(holdButton, "release", "let go of " + holdAction.BaseFriendly());
            }
            else if (holdMode == HoldMode.Repeat && holdCount > 1) Report(holdButton, "release", "repeated " + holdCount + " times");
        }

        string SwitchToNextProfile()
        {
            List<Profile> list = settings.Profiles;
            int i = list.IndexOf(settings.ActiveProfile);
            Profile next = list[(i + 1) % list.Count];
            settings.Active = next.Name;
            var h = ProfileSwitched;
            if (h != null) h(next.Name);
            return "switched to profile '" + next.Name + "'";
        }

        void Report(string button, string kind, string what)
        {
            string line = string.Format("{0,-6} {1,-7} -> {2}", button, kind, what);
            Log.Write(line);
            var h = Activity;
            if (h != null) h(DateTime.Now.ToString("HH:mm:ss") + "  " + line);
        }

        void OnTick(object sender, EventArgs e)
        {
            DateTime now = DateTime.Now;

            if (down && (now - lastReport).TotalMilliseconds > StuckMs)
            {
                Log.Write("Ring stopped reporting in the middle of a press - treating it as released");
                TouchEnded();
            }

            // Hold: the ring button is still pressed after HoldMs.
            if (down && !holdChecked && enabled && !holding && (now - downAt).TotalMilliseconds >= settings.HoldMs)
            {
                holdChecked = true;
                string button = Classify();
                ButtonBinding b = settings.ActiveProfile[button];
                ActionSpec a = ActionSpec.Parse(b.Hold);
                if (a.Kind != ActionKind.None) { FlushPending(); StartHold(button, a, b.Mode); }
            }

            // A Volume Up with no second one soon after: the camera button.
            if (volUpPending && (now - volUpPendingAt).TotalMilliseconds > VolUpDecideMs) SingleVolumeUp();
            // An Up / Down hold ends when the ring stops repeating its key.
            if (mediaHold != null && (now - lastMediaRepeat).TotalMilliseconds > MediaHoldEndMs) EndMediaHold();
            TickVolume(now);

            if (holding && holdMode == HoldMode.Repeat && now >= nextRepeat)
            {
                Fire(holdAction);
                holdCount++;
                nextRepeat = now.AddMilliseconds(settings.RepeatMs);
            }

            // No second press came in time: run the single-press action.
            if (pending != null && (now - pendingStart).TotalMilliseconds > settings.DoublePressMs &&
                !(down && (downAt - pendingStart).TotalMilliseconds <= settings.DoublePressMs))
                FlushPending();

            if (enabled)
            {
                Native.POINT cur;
                bool haveCur = Native.GetCursorPos(out cur);
                foreach (var a in absorbers)
                    if (!a.Visible && now >= a.HiddenUntil && (!haveCur || !a.Contains(cur, 3))) a.Show();
            }

            if ((now - lastMaintenance).TotalMilliseconds >= 1000)
            {
                lastMaintenance = now;
                UpdateDisplayMapping(false);
                if (enabled) foreach (var a in absorbers) if (a.Visible) a.KeepOnTop();
            }
        }

        // ---------------- touch blockers

        void LearnSpot(int x, int y)
        {
            Native.POINT p = Absorber.ToScreen(display, x, y);
            foreach (var a in absorbers)
                if (a.Contains(p, -LearnMargin)) return;
            if (absorbers.Count >= MaxSpots) return;
            Log.Write(string.Format("New touch spot seen at ring point {0},{1} - blocking it from now on", x, y));
            AddSpot(x, y);
        }

        void AddSpot(int x, int y)
        {
            var a = new Absorber(this, x, y);
            absorbers.Add(a);
            a.Place(display);
            if (enabled) a.Show();
        }

        public void OnAbsorbed()
        {
            lastAbsorbed = lastPointerSeen = DateTime.Now;
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
                    // Mouse input made from a touch means a touch reached some window that isn't one of our blockers.
                    if (fromTouch) { lastTouchLeak = DateTime.Now; leakPt = info.pt; }
                    else
                        foreach (var a in absorbers)
                            if (a.Visible && a.Contains(info.pt, 3)) a.HideFor(1500);   // real mouse here: get out of its way
                }
            }
            catch { }
            return Native.CallNextHookEx(hook, nCode, wParam, lParam);
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
                                      : arr[i].pointerDeviceType == 3 ? 1 : 0;
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
            catch (Exception ex) { if (logIt) Log.Write("pointer device lookup failed: " + ex.Message); }

            if (source == null)
            {
                // Ask Windows directly: Screen.PrimaryScreen caches sizes from whichever thread asked first,
                // and the (DPI-scaled) UI thread would give 1920x1080 for a 2560x1440 screen.
                var mi = new Native.MONITORINFO { cbSize = Marshal.SizeOf(typeof(Native.MONITORINFO)) };
                IntPtr mon = Native.MonitorFromPoint(new Native.POINT { X = 0, Y = 0 }, 1 /*MONITOR_DEFAULTTOPRIMARY*/);
                if (mon != IntPtr.Zero && Native.GetMonitorInfo(mon, ref mi)) rect = mi.rcMonitor;
                else
                {
                    var b = Screen.PrimaryScreen.Bounds;
                    rect = new Native.RECT { Left = b.Left, Top = b.Top, Right = b.Right, Bottom = b.Bottom };
                }
                source = "primary screen (fallback)";
            }

            bool changed = rect.Left != display.Left || rect.Top != display.Top || rect.Right != display.Right || rect.Bottom != display.Bottom;
            if (!changed && !logIt) return;
            display = rect;
            Log.Write(string.Format("Ring touches map to screen area {0},{1} - {2},{3} ({4})", rect.Left, rect.Top, rect.Right, rect.Bottom, source));
            foreach (var a in absorbers) { a.Place(display); if (a.Visible) a.Show(); }
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
            StopHold();      // never leave a key stuck down
            if (hook != IntPtr.Zero) { Native.UnhookWindowsHookEx(hook); hook = IntPtr.Zero; }
            foreach (var a in absorbers) a.DestroyHandle();
        }
    }

    sealed class HidDev
    {
        public string Name;
        public bool IsRing, HasXY, IsMedia;
        public readonly List<ushort> MediaDown = new List<ushort>();   // media usages currently pressed (media part)
        public IntPtr Pp;
        public uint MaxUsages;
        public ushort LinkXY;
    }

    // Hidden window that receives the ring's raw input.
    sealed class InputWindow : NativeWindow
    {
        readonly Engine engine;
        public InputWindow(Engine engine) { this.engine = engine; CreateHandle(new CreateParams()); }
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_INPUT) engine.OnRawInput(m.LParam);
            else if (m.Msg == Native.WM_DISPLAYCHANGE) engine.UpdateDisplayMapping(true);
            base.WndProc(ref m);
        }
    }

    // A tiny invisible, always-on-top window sitting exactly where one ring button touches the screen.
    // It swallows that fake touch so it never reaches the video player / browser underneath.
    sealed class Absorber : NativeWindow
    {
        readonly Engine engine;
        public readonly int LX, LY;   // ring units 0..1000
        public Native.RECT Rect;
        public bool Visible;
        public DateTime HiddenUntil = DateTime.MinValue;

        public Absorber(Engine engine, int lx, int ly)
        {
            this.engine = engine; LX = lx; LY = ly;
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
            Native.SetWindowPos(Handle, new IntPtr(-1), Rect.Left, Rect.Top, Rect.Right - Rect.Left, Rect.Bottom - Rect.Top, 0x0010 | 0x0040);
            Visible = true;
        }

        public void KeepOnTop()
        {
            Native.SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
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
                    engine.OnAbsorbed();
                    m.Result = IntPtr.Zero;
                    return;
                case Native.WM_POINTERUPDATE:
                case Native.WM_POINTERENTER:
                    engine.OnPointerSeen();
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

    // =====================================================================  tray icon (UI thread)

    sealed class TrayUi : IDisposable
    {
        readonly Engine engine;
        readonly Control invoker = new Control();
        readonly NotifyIcon tray;
        readonly ContextMenuStrip menu;
        public readonly Icon IconOn, IconOff;
        public Settings Settings;
        public bool Enabled = true;
        public readonly List<string> Recent = new List<string>();
        SettingsForm form;

        public TrayUi(Engine engine, Settings settings, bool quiet)
        {
            this.engine = engine;
            Settings = settings;
            IntPtr unused = invoker.Handle;
            IconOn = Icons.Make(true);
            IconOff = Icons.Make(false);
            menu = new ContextMenuStrip();
            menu.Opening += (s, e) => BuildMenu();
            BuildMenu();
            tray = new NotifyIcon { Icon = IconOn, Visible = true, ContextMenuStrip = menu };
            tray.DoubleClick += (s, e) => ShowSettings();
            UpdateTray();
            engine.Activity += line => Post(() => OnActivity(line));
            engine.ProfileSwitched += name => Post(() => OnRingSwitchedProfile(name));
            if (!quiet)
                tray.ShowBalloonTip(5000, "K2 Ring Remote is running",
                    "Profile: " + Settings.Active + ". Double-click the tray icon for settings; right-click to switch profiles.", ToolTipIcon.Info);
        }

        void Post(Action a)
        {
            try { invoker.BeginInvoke(a); } catch { }
        }

        void BuildMenu()
        {
            menu.Items.Clear();
            menu.Items.Add(new ToolStripMenuItem("K2 Ring Remote") { Enabled = false });
            menu.Items.Add(new ToolStripSeparator());
            foreach (var p in Settings.Profiles)
            {
                string name = p.Name;
                menu.Items.Add(new ToolStripMenuItem(name, null, (s, e) => SetActive(name)) { Checked = name == Settings.ActiveProfile.Name });
            }
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("Remote on", null, (s, e) => SetEnabled(!Enabled)) { Checked = Enabled });
            menu.Items.Add(new ToolStripMenuItem("Start with Windows", null, (s, e) => { Startup.Set(!Startup.IsOn()); if (form != null) form.LoadFromSettings(); }) { Checked = Startup.IsOn() });
            menu.Items.Add(new ToolStripSeparator());
            var settingsItem = new ToolStripMenuItem("Settings...", null, (s, e) => ShowSettings());
            settingsItem.Font = new Font(settingsItem.Font, FontStyle.Bold);
            menu.Items.Add(settingsItem);
            menu.Items.Add(new ToolStripMenuItem("Open activity log", null, (s, e) => OpenInNotepad(Program.LogPath)));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("Exit", null, (s, e) => Exit()));
        }

        public static void OpenInNotepad(string path)
        {
            try { Process.Start("notepad.exe", "\"" + path + "\""); } catch { }
        }

        public void SetActive(string name)
        {
            Settings.Active = name;
            Commit(null);
        }

        public void SetEnabled(bool on)
        {
            Enabled = on;
            engine.SetEnabled(on);
            UpdateTray();
            if (form != null) form.LoadFromSettings();
        }

        // Save the settings and hand a copy to the engine. 'source' = the window that made the change (not refreshed).
        public void Commit(object source)
        {
            try { Settings.Save(Program.SettingsPath); }
            catch (Exception ex) { Log.Write("Could not save settings: " + ex.Message); }
            engine.ApplySettings(Settings.Clone());
            UpdateTray();
            if (form != null && !ReferenceEquals(source, form)) form.LoadFromSettings();
        }

        void UpdateTray()
        {
            tray.Icon = Enabled ? IconOn : IconOff;
            string t = "K2 Ring Remote - " + Settings.ActiveProfile.Name + (Enabled ? "" : " (off)");
            tray.Text = t.Length > 63 ? t.Substring(0, 63) : t;
        }

        void OnActivity(string line)
        {
            Recent.Add(line);
            if (Recent.Count > 200) Recent.RemoveAt(0);
            if (form != null) form.AddLive(line);
        }

        void OnRingSwitchedProfile(string name)
        {
            Settings.Active = name;
            try { Settings.Save(Program.SettingsPath); } catch { }
            UpdateTray();
            if (form != null) form.LoadFromSettings();
            tray.ShowBalloonTip(1500, "K2 Ring Remote", "Profile: " + name, ToolTipIcon.Info);
        }

        public void ShowSettings()
        {
            if (form == null || form.IsDisposed)
            {
                form = new SettingsForm(this);
                form.FormClosed += (s, e) => form = null;
                form.Show();
            }
            else
            {
                if (form.WindowState == FormWindowState.Minimized) form.WindowState = FormWindowState.Normal;
                form.Activate();
            }
        }

        void Exit()
        {
            if (form != null) form.Close();
            tray.Visible = false;
            engine.Post(() => Application.ExitThread());
            Application.ExitThread();
        }

        public void Dispose()
        {
            try { tray.Visible = false; tray.Dispose(); menu.Dispose(); } catch { }
        }
    }

    static class Startup
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunValue = "K2RingRemote";

        public static bool IsOn()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey, false))
                return k != null && k.GetValue(RunValue) != null;
        }

        public static void Set(bool on)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (on) k.SetValue(RunValue, "\"" + Application.ExecutablePath + "\" --startup");
                else k.DeleteValue(RunValue, false);
            }
            Log.Write(on ? "Start with Windows: ON" : "Start with Windows: OFF");
        }
    }

    static class Icons
    {
        public static Icon Make(bool on)
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
    }

    // =====================================================================  settings window (UI thread)

    sealed class SettingsForm : Form
    {
        static readonly string[] SlotTitles = { "Single press", "Double press", "Hold" };
        static readonly string[] ModeTitles = { "Repeat while held", "Do once", "Keep key held down" };   // index = HoldMode

        // Quick picks: category, then label/spec pairs.
        static readonly object[] QuickPicks =
        {
            "Media", new[] { "Volume Up", "VolumeUp", "Volume Down", "VolumeDown", "Mute", "Mute", "Play / Pause", "PlayPause",
                             "Next track", "NextTrack", "Previous track", "PrevTrack", "Stop", "Stop" },
            "Video players", new[] { "Fullscreen (F)", "F", "Exit fullscreen (Esc)", "Esc", "Seek back (Left Arrow)", "Left",
                             "Seek forward (Right Arrow)", "Right", "Pause (Space)", "Space", "YouTube: back 10 s (J)", "J",
                             "YouTube: forward 10 s (L)", "L", "YouTube: faster (Shift + .)", "Shift+Period",
                             "YouTube: slower (Shift + ,)", "Shift+Comma", "Mute in player (M)", "M" },
            "Navigation", new[] { "Up Arrow", "Up", "Down Arrow", "Down", "Left Arrow", "Left", "Right Arrow", "Right",
                             "Page Up", "PageUp", "Page Down", "PageDown", "Home", "Home", "End", "End",
                             "Space", "Space", "Enter", "Enter", "Tab", "Tab", "Esc", "Esc" },
            "Windows", new[] { "Switch window (Alt + Tab)", "Alt+Tab", "Show desktop (Win + D)", "Win+D",
                             "Browser back (Alt + Left)", "Alt+Left", "Browser forward (Alt + Right)", "Alt+Right",
                             "Next tab (Ctrl + Tab)", "Ctrl+Tab", "Previous tab (Ctrl + Shift + Tab)", "Ctrl+Shift+Tab",
                             "Screenshot (Win + Shift + S)", "Win+Shift+S" },
            "Mouse", new[] { "Left click", "Mouse:LeftClick", "Right click", "Mouse:RightClick", "Middle click", "Mouse:MiddleClick",
                             "Scroll up", "Mouse:ScrollUp", "Scroll down", "Mouse:ScrollDown" },
            "K2 Remote", new[] { "Switch to next profile", "NextProfile" },
        };

        readonly TrayUi ui;
        readonly ComboBox cbProfile = new ComboBox();
        readonly CheckBox chkOn = new CheckBox(), chkStartup = new CheckBox();
        readonly Button[,] cells = new Button[Settings.Buttons.Length, 3];
        readonly ComboBox[] modes = new ComboBox[Settings.Buttons.Length];
        readonly NumericUpDown nDouble = new NumericUpDown(), nHold = new NumericUpDown(), nRepeat = new NumericUpDown(), nVol = new NumericUpDown();
        readonly ListBox lbLive = new ListBox();
        bool loading;
        KeyRecorder recorder;
        Button recordingCell;

        public SettingsForm(TrayUi ui)
        {
            this.ui = ui;
            SuspendLayout();
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Segoe UI", 9F);
            Text = "K2 Ring Remote - Settings";
            Icon = ui.IconOn;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(14);

            var root = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill };
            Controls.Add(root);

            // --- profile row
            var top = Flow();
            top.Controls.Add(Lbl("Profile:", 0, 7));
            cbProfile.DropDownStyle = ComboBoxStyle.DropDownList;
            cbProfile.Width = 170;
            cbProfile.Margin = new Padding(6, 3, 6, 3);
            cbProfile.SelectedIndexChanged += (s, e) => { if (!loading && cbProfile.SelectedItem != null) ui.SetActive((string)cbProfile.SelectedItem); };
            top.Controls.Add(cbProfile);
            top.Controls.Add(Btn("New...", (s, e) => NewProfile()));
            top.Controls.Add(Btn("Rename...", (s, e) => RenameProfile()));
            top.Controls.Add(Btn("Delete", (s, e) => DeleteProfile()));
            chkOn.Text = "Remote on";
            chkOn.AutoSize = true;
            chkOn.Margin = new Padding(28, 7, 0, 0);
            chkOn.CheckedChanged += (s, e) => { if (!loading) ui.SetEnabled(chkOn.Checked); };
            top.Controls.Add(chkOn);
            root.Controls.Add(top);

            // --- button grid
            var grid = new TableLayoutPanel { ColumnCount = 5, RowCount = Settings.Buttons.Length + 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0, 12, 0, 6) };
            grid.Controls.Add(Header("Ring button"), 0, 0);
            for (int c = 0; c < 3; c++) grid.Controls.Add(Header(SlotTitles[c]), c + 1, 0);
            grid.Controls.Add(Header("While held"), 4, 0);
            var bold = new Font(Font, FontStyle.Bold);
            for (int r = 0; r < Settings.Buttons.Length; r++)
            {
                grid.Controls.Add(new Label { Text = Settings.Buttons[r], AutoSize = true, Font = bold, Margin = new Padding(0, 10, 14, 0) }, 0, r + 1);
                for (int c = 0; c < 3; c++)
                {
                    var b = new Button { Width = 196, Height = 30, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(3), Tag = new Point(r, c), UseVisualStyleBackColor = true };
                    b.Click += (s, e) => ShowActionMenu((Button)s);
                    if (FixedCell(Settings.Buttons[r], c) != null) b.Enabled = false;
                    cells[r, c] = b;
                    grid.Controls.Add(b, c + 1, r + 1);
                }
                var cb = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150, Margin = new Padding(3, 7, 3, 3), Tag = r };
                cb.Items.AddRange(ModeTitles);
                cb.SelectedIndexChanged += (s, e) =>
                {
                    if (loading) return;
                    var box = (ComboBox)s;
                    ui.Settings.ActiveProfile[Settings.Buttons[(int)box.Tag]].Mode = (HoldMode)box.SelectedIndex;
                    ui.Commit(this);
                };
                modes[r] = cb;
                grid.Controls.Add(cb, 4, r + 1);
            }
            root.Controls.Add(grid);

            root.Controls.Add(new Label
            {
                AutoSize = true,
                MaximumSize = new Size(820, 0),
                ForeColor = SystemColors.GrayText,
                Margin = new Padding(0, 0, 0, 8),
                Text = "Click an action to pick from the list or record your own key / shortcut, and to set how many times one press sends it. " +
                       "A button that has a double-press action waits for the double-press window before doing its single press. " +
                       "Hold = keep the ring button pressed (the ring reports it after about a second). Camera = the side button: it sends " +
                       "Volume Up, so the app puts the volume back (the volume pop-up may flash) and its action runs half a second later. " +
                       "While this window is in front the ring's keys are only shown below, not sent. Changes save automatically."
            });

            // --- timing
            var timing = new GroupBox { Text = "Timing", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill, Margin = new Padding(0, 4, 0, 6) };
            var tf = Flow();
            tf.Location = new Point(10, 22);
            AddNumber(tf, "Double-press window", nDouble, 150, 1000, 50, "ms");
            AddNumber(tf, "Hold starts after", nHold, 250, 2000, 50, "ms");
            AddNumber(tf, "Repeat every", nRepeat, 50, 1000, 10, "ms");
            AddNumber(tf, "Volume step", nVol, 1, 10, 1, "x 2%");
            timing.Controls.Add(tf);
            root.Controls.Add(timing);

            // --- live test
            var live = new GroupBox { Text = "Live test - press the ring buttons and see what each one did", Dock = DockStyle.Fill, Height = 190, Margin = new Padding(0, 4, 0, 6), Padding = new Padding(8) };
            lbLive.Dock = DockStyle.Fill;
            lbLive.IntegralHeight = false;
            lbLive.Font = new Font("Consolas", 9F);
            live.Controls.Add(lbLive);
            root.Controls.Add(live);

            // --- bottom row
            var bottom = new TableLayoutPanel { ColumnCount = 3, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill, Margin = new Padding(0, 4, 0, 0) };
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            chkStartup.Text = "Start with Windows";
            chkStartup.AutoSize = true;
            chkStartup.Margin = new Padding(0, 7, 0, 0);
            chkStartup.CheckedChanged += (s, e) => { if (!loading) Startup.Set(chkStartup.Checked); };
            bottom.Controls.Add(chkStartup, 0, 0);
            bottom.Controls.Add(Btn("Open activity log", (s, e) => TrayUi.OpenInNotepad(Program.LogPath)), 1, 0);
            bottom.Controls.Add(Btn("Close", (s, e) => Close()), 2, 0);
            root.Controls.Add(bottom);

            ResumeLayout(true);

            LoadFromSettings();
            foreach (string l in ui.Recent) lbLive.Items.Add(l);
            if (lbLive.Items.Count > 0) lbLive.TopIndex = lbLive.Items.Count - 1;
            FormClosing += (s, e) => StopRecording();
        }

        // ---------------- helpers to build the layout

        static FlowLayoutPanel Flow()
        {
            return new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = new Padding(0) };
        }

        static Label Lbl(string text, int left, int top)
        {
            return new Label { Text = text, AutoSize = true, Margin = new Padding(left, top, 0, 0) };
        }

        static Label Header(string text)
        {
            return new Label { Text = text, AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(3, 0, 3, 2) };
        }

        static Button Btn(string text, EventHandler onClick)
        {
            var b = new Button { Text = text, AutoSize = true, MinimumSize = new Size(78, 0), Margin = new Padding(3), UseVisualStyleBackColor = true };
            b.Click += onClick;
            return b;
        }

        void AddNumber(FlowLayoutPanel flow, string caption, NumericUpDown nud, int min, int max, int step, string unit)
        {
            flow.Controls.Add(new Label { Text = caption, AutoSize = true, Margin = new Padding(0, 6, 4, 0) });
            nud.Minimum = min;
            nud.Maximum = max;
            nud.Increment = step;
            nud.Width = 64;
            nud.Margin = new Padding(0, 3, 3, 0);
            nud.ValueChanged += (s, e) => OnNumberChanged();
            flow.Controls.Add(nud);
            flow.Controls.Add(new Label { Text = unit, AutoSize = true, Margin = new Padding(0, 6, 20, 0) });
        }

        static decimal Fit(NumericUpDown n, int v) { return Math.Max(n.Minimum, Math.Min(n.Maximum, v)); }

        // ---------------- data <-> controls

        public void LoadFromSettings()
        {
            loading = true;
            try
            {
                Settings s = ui.Settings;
                Profile prof = s.ActiveProfile;
                cbProfile.Items.Clear();
                foreach (var p in s.Profiles) cbProfile.Items.Add(p.Name);
                cbProfile.SelectedItem = prof.Name;
                for (int r = 0; r < Settings.Buttons.Length; r++)
                {
                    string name = Settings.Buttons[r];
                    ButtonBinding b = prof[name];
                    for (int c = 0; c < 3; c++)
                    {
                        string fixedText = FixedCell(name, c);
                        if (fixedText != null) { cells[r, c].Text = fixedText; cells[r, c].ForeColor = SystemColors.GrayText; }
                        else if (cells[r, c] != recordingCell) SetCellText(cells[r, c], b.Get(c));
                    }
                    modes[r].SelectedIndex = (int)b.Mode;
                    modes[r].Enabled = FixedCell(name, 2) == null && ActionSpec.Parse(b.Hold).Kind != ActionKind.None;
                }
                nDouble.Value = Fit(nDouble, s.DoublePressMs);
                nHold.Value = Fit(nHold, s.HoldMs);
                nRepeat.Value = Fit(nRepeat, s.RepeatMs);
                nVol.Value = Fit(nVol, s.VolumeSteps);
                chkOn.Checked = ui.Enabled;
                chkStartup.Checked = Startup.IsOn();
            }
            finally { loading = false; }
        }

        void OnNumberChanged()
        {
            if (loading) return;
            Settings s = ui.Settings;
            s.DoublePressMs = (int)nDouble.Value;
            s.HoldMs = (int)nHold.Value;
            s.RepeatMs = (int)nRepeat.Value;
            s.VolumeSteps = (int)nVol.Value;
            ui.Commit(this);
        }

        // Cells the ring decides itself (null = the user can set it). Holding Left / Right / Center makes the ring
        // send Previous / Next / Play-Pause, which Windows acts on directly; the camera button only reports a click.
        static string FixedCell(string button, int slot)
        {
            if (slot == 2 && button == "Left") return "Previous track (ring's own)";
            if (slot == 2 && button == "Right") return "Next track (ring's own)";
            if (slot == 2 && button == "Center") return "Play / Pause (ring's own)";
            if (slot > 0 && button == "Camera") return "(the ring can't do this)";
            return null;
        }

        static void SetCellText(Button b, string spec)
        {
            ActionSpec a = ActionSpec.Parse(spec);
            b.Text = ActionSpec.Describe(spec) + "   ▾";
            b.ForeColor = a.Kind == ActionKind.None ? SystemColors.GrayText : SystemColors.ControlText;
        }

        void SetSpec(Button btn, string spec)
        {
            var rc = (Point)btn.Tag;
            ButtonBinding b = ui.Settings.ActiveProfile[Settings.Buttons[rc.X]];
            b.Set(rc.Y, spec);
            SetCellText(btn, spec);
            if (rc.Y == 2) modes[rc.X].Enabled = ActionSpec.Parse(spec).Kind != ActionKind.None;
            ui.Commit(this);
        }

        public void AddLive(string line)
        {
            lbLive.Items.Add(line);
            while (lbLive.Items.Count > 200) lbLive.Items.RemoveAt(0);
            lbLive.TopIndex = lbLive.Items.Count - 1;
        }

        // ---------------- action picker + key recording

        void ShowActionMenu(Button btn)
        {
            StopRecording();
            var m = new ContextMenuStrip();
            var rec = new ToolStripMenuItem("Record a key or shortcut...", null, (s, e) => StartRecording(btn));
            rec.Font = new Font(rec.Font, FontStyle.Bold);
            m.Items.Add(rec);
            m.Items.Add(new ToolStripSeparator());
            for (int i = 0; i < QuickPicks.Length; i += 2)
            {
                var sub = new ToolStripMenuItem((string)QuickPicks[i]);
                var pairs = (string[])QuickPicks[i + 1];
                for (int j = 0; j < pairs.Length; j += 2)
                {
                    string spec = pairs[j + 1];
                    sub.DropDownItems.Add(pairs[j], null, (s, e) => SetSpec(btn, spec));
                }
                m.Items.Add(sub);
            }
            m.Items.Add(new ToolStripSeparator());

            // How many times one press sends the action.
            var rc = (Point)btn.Tag;
            string cur = ui.Settings.ActiveProfile[Settings.Buttons[rc.X]].Get(rc.Y);
            ActionSpec ca = ActionSpec.Parse(cur);
            var times = new ToolStripMenuItem(ca.CanRepeat ? "Times per press" : "Times per press (pick an action first)") { Enabled = ca.CanRepeat };
            bool listed = false;
            foreach (int n in new[] { 1, 2, 3, 4, 5, 10 })
            {
                int count = n;
                bool on = ca.Times == n;
                listed |= on;
                times.DropDownItems.Add(new ToolStripMenuItem(n == 1 ? "1 (normal)" : n + " times", null,
                    (s, e) => SetSpec(btn, ActionSpec.WithTimes(cur, count))) { Checked = on });
            }
            times.DropDownItems.Add(new ToolStripMenuItem(listed ? "Other number..." : "Other number... (now " + ca.Times + ")", null,
                (s, e) => AskTimes(btn, cur, ca)) { Checked = !listed });
            m.Items.Add(times);

            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("Nothing", null, (s, e) => SetSpec(btn, "None"));
            m.Closed += (s, e) => BeginInvoke(new Action(() => m.Dispose()));
            m.Show(btn, new Point(0, btn.Height));
        }

        void AskTimes(Button btn, string cur, ActionSpec a)
        {
            string v = InputBox.Show(this, "Times per press",
                "How many times should one press send " + a.BaseFriendly() + "? (1 - " + ActionSpec.MaxTimes + ")", a.Times.ToString());
            int n;
            if (v != null && int.TryParse(v, out n)) SetSpec(btn, ActionSpec.WithTimes(cur, n));
        }

        void StartRecording(Button btn)
        {
            StopRecording();
            var r = new KeyRecorder(this, spec => OnRecorded(btn, spec));
            if (!r.Start())
            {
                MessageBox.Show(this, "Could not start key recording. Pick an action from the list instead.", "K2 Ring Remote",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            recorder = r;
            recordingCell = btn;
            btn.Text = "Press a key or shortcut...  (Esc = cancel)";
            btn.ForeColor = SystemColors.ControlText;
            btn.BackColor = Color.FromArgb(255, 236, 179);
        }

        void OnRecorded(Button btn, string spec)
        {
            if (recordingCell != btn) return;     // recording was cancelled or replaced
            recorder = null;
            recordingCell = null;
            btn.BackColor = SystemColors.Control;
            btn.UseVisualStyleBackColor = true;
            if (spec != null) SetSpec(btn, spec);
            else RestoreCell(btn);
        }

        void StopRecording()
        {
            if (recorder == null) return;
            KeyRecorder r = recorder;
            Button btn = recordingCell;
            recorder = null;
            recordingCell = null;
            r.Cancel();
            if (btn != null)
            {
                btn.BackColor = SystemColors.Control;
                btn.UseVisualStyleBackColor = true;
                RestoreCell(btn);
            }
        }

        void RestoreCell(Button btn)
        {
            var rc = (Point)btn.Tag;
            SetCellText(btn, ui.Settings.ActiveProfile[Settings.Buttons[rc.X]].Get(rc.Y));
        }

        // ---------------- profiles

        void NewProfile()
        {
            string name = InputBox.Show(this, "New profile",
                "Name for the new profile (it starts as a copy of '" + ui.Settings.ActiveProfile.Name + "'):", "My profile");
            if (string.IsNullOrEmpty(name) || !ValidName(name, null)) return;
            ui.Settings.Profiles.Add(ui.Settings.ActiveProfile.Clone(name));
            ui.Settings.Active = name;
            ui.Commit(null);
        }

        void RenameProfile()
        {
            Profile p = ui.Settings.ActiveProfile;
            string name = InputBox.Show(this, "Rename profile", "New name for '" + p.Name + "':", p.Name);
            if (string.IsNullOrEmpty(name) || name == p.Name || !ValidName(name, p)) return;
            p.Name = name;
            ui.Settings.Active = name;
            ui.Commit(null);
        }

        void DeleteProfile()
        {
            Settings s = ui.Settings;
            if (s.Profiles.Count <= 1)
            {
                MessageBox.Show(this, "You need at least one profile.", "K2 Ring Remote", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            Profile p = s.ActiveProfile;
            if (MessageBox.Show(this, "Delete the profile '" + p.Name + "'?", "K2 Ring Remote", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;
            s.Profiles.Remove(p);
            s.Active = s.Profiles[0].Name;
            ui.Commit(null);
        }

        bool ValidName(string name, Profile self)
        {
            if (name.Trim().Length == 0 || name.IndexOfAny(new[] { '[', ']', '\r', '\n', '=' }) >= 0)
            {
                MessageBox.Show(this, "Please use a name without [ ] or = characters.", "K2 Ring Remote", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return false;
            }
            Profile other = ui.Settings.Find(name);
            if (other != null && other != self)
            {
                MessageBox.Show(this, "A profile called '" + name + "' already exists.", "K2 Ring Remote", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return false;
            }
            return true;
        }
    }

    // Captures the next key or shortcut pressed anywhere (even Alt+Tab or Win+D) and swallows it while recording.
    sealed class KeyRecorder
    {
        readonly Control owner;
        readonly Action<string> done;
        readonly Native.LowLevelProc proc;
        IntPtr hook = IntPtr.Zero;
        readonly HashSet<ushort> mods = new HashSet<ushort>();
        readonly HashSet<uint> downKeys = new HashSet<uint>();
        bool finished, nonModifierPressed;
        System.Windows.Forms.Timer safety;

        public KeyRecorder(Control owner, Action<string> done)
        {
            this.owner = owner;
            this.done = done;
            proc = HookProc;
        }

        public bool Start()
        {
            hook = Native.SetWindowsHookEx(13 /*WH_KEYBOARD_LL*/, proc, Native.GetModuleHandle(null), 0);
            return hook != IntPtr.Zero;
        }

        public void Cancel()
        {
            finished = true;
            Unhook();
        }

        static ushort Normalize(uint vk)
        {
            switch (vk)
            {
                case 0xA0: case 0xA1: return 0x10;   // L/R Shift
                case 0xA2: case 0xA3: return 0x11;   // L/R Ctrl
                case 0xA4: case 0xA5: return 0x12;   // L/R Alt
                case 0x5C: return 0x5B;              // R Win
            }
            return (ushort)vk;
        }

        static bool IsModifier(ushort vk) { return vk == 0x10 || vk == 0x11 || vk == 0x12 || vk == 0x5B; }

        IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode < 0 || hook == IntPtr.Zero) return Native.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
            var k = (Native.KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(Native.KBDLLHOOKSTRUCT));
            if ((k.flags & 0x10) != 0) return Native.CallNextHookEx(hook, nCode, wParam, lParam);   // injected (e.g. by the ring): let it through
            int msg = wParam.ToInt32();
            bool isDown = msg == 0x100 || msg == 0x104, isUp = msg == 0x101 || msg == 0x105;
            ushort vk = Normalize(k.vkCode);
            if (isDown)
            {
                downKeys.Add(k.vkCode);
                if (!finished)
                {
                    if (IsModifier(vk)) mods.Add(vk);
                    else if (vk == 0x1B && mods.Count == 0) Finish(null);           // Esc alone = cancel
                    else { nonModifierPressed = true; Finish(ActionSpec.Build(mods, vk)); }
                }
            }
            else if (isUp)
            {
                downKeys.Remove(k.vkCode);
                if (!finished && IsModifier(vk) && !nonModifierPressed) Finish(ActionSpec.Build(mods, vk));   // a modifier on its own
                mods.Remove(vk);
                if (finished && downKeys.Count == 0) Later(Unhook);
            }
            return new IntPtr(1);   // swallow while recording so the shortcut doesn't trigger anything
        }

        void Finish(string spec)
        {
            if (finished) return;
            finished = true;
            Later(() => done(spec));
            Later(() =>
            {
                if (hook == IntPtr.Zero) return;
                safety = new System.Windows.Forms.Timer { Interval = 3000 };   // in case a key-up never arrives
                safety.Tick += (s, e) => Unhook();
                safety.Start();
            });
            if (downKeys.Count == 0) Later(Unhook);
        }

        void Later(Action a)
        {
            try { if (owner.IsHandleCreated && !owner.IsDisposed) owner.BeginInvoke(a); else a(); } catch { }
        }

        void Unhook()
        {
            if (hook != IntPtr.Zero) { Native.UnhookWindowsHookEx(hook); hook = IntPtr.Zero; }
            if (safety != null) { safety.Stop(); safety.Dispose(); safety = null; }
        }
    }

    static class InputBox
    {
        public static string Show(IWin32Window owner, string title, string prompt, string value)
        {
            using (var f = new Form())
            {
                f.AutoScaleDimensions = new SizeF(96F, 96F);
                f.AutoScaleMode = AutoScaleMode.Dpi;
                f.Font = new Font("Segoe UI", 9F);
                f.Text = title;
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.MinimizeBox = false;
                f.MaximizeBox = false;
                f.ShowInTaskbar = false;
                f.StartPosition = FormStartPosition.CenterParent;
                f.AutoSize = true;
                f.AutoSizeMode = AutoSizeMode.GrowAndShrink;
                f.Padding = new Padding(12);
                var t = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill };
                t.Controls.Add(new Label { Text = prompt, AutoSize = true, MaximumSize = new Size(360, 0) });
                var tb = new TextBox { Text = value, Width = 300, Margin = new Padding(0, 8, 0, 12) };
                t.Controls.Add(tb);
                var row = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, Margin = new Padding(0) };
                var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true, UseVisualStyleBackColor = true };
                var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true, UseVisualStyleBackColor = true };
                row.Controls.Add(cancel);
                row.Controls.Add(ok);
                t.Controls.Add(row);
                f.Controls.Add(t);
                f.AcceptButton = ok;
                f.CancelButton = cancel;
                f.Shown += (s, e) => { tb.SelectAll(); tb.Focus(); };
                return f.ShowDialog(owner) == DialogResult.OK ? tb.Text.Trim() : null;
            }
        }
    }

    // =====================================================================  Win32

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
        [StructLayout(LayoutKind.Sequential)] public struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr dwExtraInfo; }
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
        [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr value);
        [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr h);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] public static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
        [DllImport("user32.dll")] public static extern bool GetMonitorInfo(IntPtr mon, ref MONITORINFO mi);
        [StructLayout(LayoutKind.Sequential)] public struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }
    }
}
