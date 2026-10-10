using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace IdleMafiaBot
{
    static class Native
    {
        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] public struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

        [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
        [DllImport("shcore.dll")] public static extern int SetProcessDpiAwareness(int value);
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] public static extern bool IsProcessDPIAware();
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern uint GetLongPathName(string path, System.Text.StringBuilder longPath, uint size);
        [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref POINT p);
        [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, IntPtr pid);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool attach);
        [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
        [DllImport("user32.dll")] public static extern uint MapVirtualKey(uint code, uint type);
        [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] public static extern void mouse_event(uint flags, int dx, int dy, int data, UIntPtr extra);
        [DllImport("user32.dll")] public static extern bool GetLastInputInfo(ref LASTINPUTINFO lii);
        [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
        [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr h, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, System.Text.StringBuilder s, int max);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, System.Text.StringBuilder s, int max);
        [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint vk);
        [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr h, int id);

        public const uint MOUSE_MOVE = 0x0001, MOUSE_LDOWN = 0x0002, MOUSE_LUP = 0x0004, MOUSE_WHEEL = 0x0800;

        public static uint LastInputTick()
        {
            var lii = new LASTINPUTINFO();
            lii.cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO));
            GetLastInputInfo(ref lii);
            return lii.dwTime;
        }

        public static uint Now { get { return unchecked((uint)Environment.TickCount); } }

        public static string LongPath(string path)
        {
            string full = System.IO.Path.GetFullPath(path);
            var sb = new System.Text.StringBuilder(1024);
            uint n = GetLongPathName(full, sb, (uint)sb.Capacity);
            return n > 0 && n < sb.Capacity ? sb.ToString() : full;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct MEMORYSTATUSEX
        {
            public uint Length, MemoryLoad;
            public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
        }
        [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);

        public static bool PcMemory(out long total, out long free, out long more)
        {
            total = free = more = -1;
            var m = new MEMORYSTATUSEX { Length = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX)) };
            try { if (!GlobalMemoryStatusEx(ref m)) return false; }
            catch (Exception) { return false; }
            total = (long)m.TotalPhys;
            free = (long)m.AvailPhys;
            more = (long)m.AvailPageFile;
            return true;
        }

        public static long BotMemory()
        {
            try { using (var p = Process.GetCurrentProcess()) return p.PrivateMemorySize64; }
            catch (Exception) { return -1; }
        }

        public static string Size(long bytes)
        {
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            double gb = bytes / (1024.0 * 1024 * 1024);
            if (gb < 1) return (bytes >> 20).ToString(ci) + " MB";
            return gb.ToString(gb >= 10 ? "0" : "0.0", ci) + " GB";
        }

        public static string MemoryText()
        {
            long total, free, more, own = BotMemory();
            string bot = own >= 0 ? "the bot uses " + Size(own) : "the bot's own memory isn't known";
            if (!PcMemory(out total, out free, out more)) return bot + "; Windows doesn't say how much memory this PC has";
            return bot + "; this PC has " + Size(free) + " of " + Size(total) + " free (" + Size(more) + " more can be handed out)";
        }
    }

    sealed class OutOfMemoryPause : Exception { public OutOfMemoryPause(Exception e) : base("the PC ran out of memory", e) { } }

    sealed class WindowChangedException : Exception { public WindowChangedException(string m) : base(m) { } }

    sealed class UserBusyException : Exception { public UserBusyException() : base("you started using the PC") { } }

    sealed class RobloxWindow
    {
        public IntPtr Handle { get; private set; }
        public int Width { get; private set; }
        public int Height { get; private set; }

        uint lastBotInput;
        IntPtr returnTo = IntPtr.Zero;

        public bool Find()
        {
            if (Handle == IntPtr.Zero || !Native.IsWindow(Handle))
            {
                Handle = IntPtr.Zero;

                foreach (var p in Process.GetProcessesByName("RobloxPlayerBeta").Concat(Process.GetProcessesByName("Windows10Universal")))
                {
                    if (p.MainWindowHandle == IntPtr.Zero) continue;
                    if (p.ProcessName == "Windows10Universal" && p.MainWindowTitle != "Roblox") continue;
                    Handle = p.MainWindowHandle;
                    break;
                }
                if (Handle == IntPtr.Zero) return false;
            }
            Native.RECT r;
            Native.GetClientRect(Handle, out r);
            Width = r.Right - r.Left;
            Height = r.Bottom - r.Top;
            return true;
        }

        public bool Minimized { get { return Handle != IntPtr.Zero && Native.IsIconic(Handle); } }

        public Frame Capture()
        {

            if (Handle == IntPtr.Zero || !Native.IsWindow(Handle)) throw new WindowChangedException("Roblox was closed");
            if (Native.IsIconic(Handle)) throw new WindowChangedException("Roblox was minimized");
            if (ScreenFirst) return ScreenFirstCapture();
            bool ok;
            var f = Printed(out ok);
            if (ok && !f.LooksEmpty()) return f;
            var s = InSight() ? FromScreen() : null;
            if (s != null && !s.LooksEmpty())
            {

                f.Dispose();
                EmptyPictures++;
                ScreenCopies++;
                ScreenFirst = true;
                return s;
            }
            if (s != null) { s.Dispose(); return f; }
            f.Dispose();
            Thread.Sleep(100);
            f = Printed(out ok);
            if (ok && !f.LooksEmpty()) return f;
            EmptyPictures++;
            return f;
        }

        public static volatile bool ScreenFirst;

        public bool ScreenOnly;

        public static int EmptyPictures, ScreenCopies, Asked;

        static int emptyAt = Environment.TickCount - 60000;

        internal static bool TestWhite = false;

        Frame ScreenFirstCapture()
        {
            Frame s;
            if (InSight() && (s = FromScreen()) != null) return s;

            if (ScreenOnly || unchecked(Environment.TickCount - emptyAt) < 60000) return White();
            bool ok;
            var f = Printed(out ok);
            if (ok && !f.LooksEmpty()) return f;
            EmptyPictures++;
            emptyAt = Environment.TickCount;
            return f;
        }

        Frame White()
        {
            var bmp = new Bitmap(Math.Max(1, Width), Math.Max(1, Height), PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp)) g.Clear(Color.White);
            return new Frame(bmp);
        }

        Frame Printed(out bool ok)
        {
            Interlocked.Increment(ref Asked);
            var bmp = new Bitmap(Math.Max(1, Width), Math.Max(1, Height), PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                IntPtr dc = g.GetHdc();
                ok = Native.PrintWindow(Handle, dc, 3);
                g.ReleaseHdc(dc);
                if (TestWhite) g.Clear(Color.White);
            }
            return new Frame(bmp);
        }

        public bool InSight()
        {
            if (Width < 50 || Height < 50) return false;
            IntPtr root = Native.GetAncestor(Handle, 2);
            for (int i = 0; i < 4; i++)
                for (int j = 0; j < 3; j++)
                {
                    var p = new Native.POINT { X = Width * (2 * i + 1) / 8, Y = Height * (2 * j + 1) / 6 };
                    if (!Native.ClientToScreen(Handle, ref p)) return false;
                    IntPtr at = Native.WindowFromPoint(p);
                    if (at == IntPtr.Zero || Native.GetAncestor(at, 2) != root) return false;
                }
            return true;
        }

        Frame FromScreen()
        {
            var p = new Native.POINT();
            if (!Native.ClientToScreen(Handle, ref p)) return null;
            Bitmap bmp = null;
            try
            {
                bmp = new Bitmap(Width, Height, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(p.X, p.Y, 0, 0, new Size(Width, Height));
                return new Frame(bmp);
            }
            catch (Exception) { if (bmp != null) bmp.Dispose(); return null; }
        }

        public bool UserActiveWithin(int ms)
        {
            uint last = Native.LastInputTick();

            bool ours = lastBotInput != 0 && unchecked((int)(last - lastBotInput)) <= 400;
            return !ours && unchecked(Native.Now - last) < (uint)ms;
        }

        public bool UserInputSinceBot()
        {
            return unchecked((int)(Native.LastInputTick() - lastBotInput)) > 250;
        }

        public bool BeginAct()
        {
            lastBotInput = Native.Now;
            IntPtr fg = Native.GetForegroundWindow();
            if (Minimized) { Native.ShowWindow(Handle, 9); Thread.Sleep(600); Find(); }
            if (fg == Handle) return true;
            returnTo = fg;
            bool ok = Activate(Handle);
            lastBotInput = Native.Now;
            if (!ok) return false;
            Thread.Sleep(250);
            return true;
        }

        public void EndAct(bool giveFocusBack, bool park = true)
        {
            bool userMoved = UserInputSinceBot();
            if (park) Park();
            if (giveFocusBack && IsForeground && returnTo != IntPtr.Zero && Native.IsWindow(returnTo) && Native.IsWindowVisible(returnTo))
            {
                Activate(returnTo);
                if (!userMoved) lastBotInput = Native.Now;
            }
            returnTo = IntPtr.Zero;
        }

        public bool IsForeground { get { return Handle != IntPtr.Zero && Native.GetForegroundWindow() == Handle; } }

        public const string PlaceId = "73897506680154";

        public static void StartGame()
        {
            using (Process.Start(new ProcessStartInfo("roblox://experiences/start?placeId=" + PlaceId) { UseShellExecute = true })) { }
        }

        public void GiveFocusBack(IntPtr w)
        {
            if (w == IntPtr.Zero || w == Handle || !IsForeground || !Native.IsWindow(w) || !Native.IsWindowVisible(w)) return;
            Activate(w);
            lastBotInput = Native.Now;
        }

        static bool Activate(IntPtr h)
        {
            if (!Native.IsWindow(h) || Native.IsIconic(h)) return false;
            IntPtr fg = Native.GetForegroundWindow();
            if (fg == h) return true;
            uint fgThread = Native.GetWindowThreadProcessId(fg, IntPtr.Zero);
            uint me = Native.GetCurrentThreadId();
            Native.AttachThreadInput(me, fgThread, true);
            Native.BringWindowToTop(h);
            Native.SetForegroundWindow(h);
            Native.AttachThreadInput(me, fgThread, false);
            if (Native.GetForegroundWindow() == h) return true;
            Native.keybd_event(0x12, 0, 0, UIntPtr.Zero);
            Native.SetForegroundWindow(h);
            Native.keybd_event(0x12, 0, 2, UIntPtr.Zero);
            Thread.Sleep(50);
            return Native.GetForegroundWindow() == h;
        }

        Native.POINT ToScreen(int x, int y)
        {
            if (Handle == IntPtr.Zero || !Native.IsWindow(Handle)) throw new WindowChangedException("Roblox was closed");
            if (Native.IsIconic(Handle)) throw new WindowChangedException("Roblox was minimized");
            Native.RECT r;
            if (!Native.GetClientRect(Handle, out r) || r.Right - r.Left != Width || r.Bottom - r.Top != Height)
                throw new WindowChangedException("the Roblox window changed size");
            var p = new Native.POINT { X = x, Y = y };
            if (!Native.ClientToScreen(Handle, ref p)) throw new WindowChangedException("Roblox was closed");
            return p;
        }

        void CheckSpot(Native.POINT p)
        {
            IntPtr at = Native.WindowFromPoint(p);

            if (at != IntPtr.Zero && Native.GetAncestor(at, 2) == Native.GetAncestor(Handle, 2)) return;
            var title = new System.Text.StringBuilder(80);
            if (at != IntPtr.Zero)
            {

                Native.GetClassName(Native.GetAncestor(at, 2), title, title.Capacity);
                string cls = title.ToString();
                if (cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd")
                    throw new WindowChangedException("the bottom of the Roblox window is behind the taskbar - maximize Roblox or move it up");
                title.Clear();
                Native.GetWindowText(Native.GetAncestor(at, 2), title, title.Capacity);
            }
            throw new WindowChangedException("another window" + (title.Length > 0 ? " (\"" + title + "\")" : "") + " covers the game where it has to click");
        }

        Native.POINT MoveTo(int x, int y)
        {
            var p = ToScreen(x, y);
            if (!IsForeground)
            {
                bool ok = Activate(Handle);
                lastBotInput = Native.Now;
                if (!ok) throw new WindowChangedException("couldn't bring Roblox to the front");
                Thread.Sleep(200);
            }
            CheckSpot(p);
            Native.SetCursorPos(p.X, p.Y);
            Native.POINT c;
            if (!Native.GetCursorPos(out c) || c.X != p.X || c.Y != p.Y)
                throw new WindowChangedException("part of the Roblox window is off the screen");
            Thread.Sleep(25);

            Native.mouse_event(Native.MOUSE_MOVE, 1, 0, 0, UIntPtr.Zero);
            Thread.Sleep(25);
            Native.mouse_event(Native.MOUSE_MOVE, -1, 0, 0, UIntPtr.Zero);
            Thread.Sleep(45);
            lastBotInput = Native.Now;
            return p;
        }

        void StillOn(Native.POINT p)
        {
            Native.POINT c;
            if (!Native.GetCursorPos(out c) || Math.Abs(c.X - p.X) > 3 || Math.Abs(c.Y - p.Y) > 3) throw new UserBusyException();
            CheckSpot(p);
        }

        public void Click(int x, int y)
        {
            StillOn(MoveTo(x, y));
            Native.mouse_event(Native.MOUSE_LDOWN, 0, 0, 0, UIntPtr.Zero);
            Thread.Sleep(40);
            Native.mouse_event(Native.MOUSE_LUP, 0, 0, 0, UIntPtr.Zero);
            lastBotInput = Native.Now;
            LastInput = DateTime.UtcNow;
            Thread.Sleep(60);
            Park();
        }

        public void TypeKeys(string keys)
        {
            foreach (char ch in keys ?? "")
            {
                if (!IsForeground) throw new WindowChangedException("another window took the keyboard");
                if (UserInputSinceBot()) throw new UserBusyException();
                byte vk = ch == (char)8 ? (byte)0x08 : ch == (char)13 ? (byte)0x0D : ch == ' ' ? (byte)0x20
                        : char.IsLetterOrDigit(ch) && ch < 128 ? (byte)char.ToUpperInvariant(ch) : (byte)0;
                if (vk == 0) continue;
                byte sc = (byte)Native.MapVirtualKey(vk, 0);
                Native.keybd_event(vk, sc, 0, UIntPtr.Zero);
                Thread.Sleep(30);
                Native.keybd_event(vk, sc, 2, UIntPtr.Zero);
                lastBotInput = Native.Now;
                LastInput = DateTime.UtcNow;
                Thread.Sleep(50);
            }
        }

        public void Hover(int x, int y) { StillOn(MoveTo(x, y)); }

        public void SweepFromTop()
        {
            var top = ToScreen(Width / 2, 2);
            if (!IsForeground)
            {
                bool ok = Activate(Handle);
                lastBotInput = Native.Now;
                if (!ok) throw new WindowChangedException("couldn't bring Roblox to the front");
                Thread.Sleep(200);
            }
            if (!RobloxAt(top)) CheckSpot(top);
            var end = ToScreen(ParkAt.X, ParkAt.Y);
            CheckSpot(end);
            Native.POINT at = top;
            for (int i = 0; i <= 12; i++)
            {
                if (i > 0)
                {

                    Native.POINT c;
                    if (!Native.GetCursorPos(out c) || Math.Abs(c.X - at.X) > 3 || Math.Abs(c.Y - at.Y) > 3) throw new UserBusyException();
                    at = new Native.POINT { X = top.X + (end.X - top.X) * i / 12, Y = top.Y + (end.Y - top.Y) * i / 12 };
                }
                Native.SetCursorPos(at.X, at.Y);
                Native.mouse_event(Native.MOUSE_MOVE, 1, 0, 0, UIntPtr.Zero);
                Thread.Sleep(15);
                Native.mouse_event(Native.MOUSE_MOVE, -1, 0, 0, UIntPtr.Zero);
                lastBotInput = Native.Now;
                Thread.Sleep(i == 0 ? 600 : 30);
            }
            lastBotInput = Native.Now;
        }

        bool RobloxAt(Native.POINT p)
        {
            IntPtr at = Native.WindowFromPoint(p);
            if (at == IntPtr.Zero) return false;
            if (Native.GetAncestor(at, 2) == Native.GetAncestor(Handle, 2)) return true;
            uint mine, theirs;
            Native.GetWindowThreadProcessId(Handle, out mine);
            Native.GetWindowThreadProcessId(at, out theirs);
            return mine != 0 && mine == theirs;
        }

        public DateTime LastInput { get; private set; }

        public void Scroll(int x, int y, int notches)
        {
            var p = MoveTo(x, y);
            int step = notches > 0 ? 120 : -120;
            for (int i = 0; i < Math.Abs(notches); i++)
            {
                StillOn(p);
                Native.mouse_event(Native.MOUSE_WHEEL, 0, 0, step, UIntPtr.Zero);
                lastBotInput = Native.Now;
                LastInput = DateTime.UtcNow;
                Thread.Sleep(12);
            }
            Thread.Sleep(350);
            Park();
        }

        public Point ParkAt = new Point(600, 170);

        public void Park()
        {
            if (Handle == IntPtr.Zero || !Native.IsWindow(Handle) || Native.IsIconic(Handle)) return;
            if (UserInputSinceBot()) return;
            var p = new Native.POINT { X = ParkAt.X, Y = ParkAt.Y };
            if (!Native.ClientToScreen(Handle, ref p)) return;
            Native.SetCursorPos(p.X, p.Y);

            Native.mouse_event(Native.MOUSE_MOVE, 1, 0, 0, UIntPtr.Zero);
            Thread.Sleep(10);
            Native.mouse_event(Native.MOUSE_MOVE, -1, 0, 0, UIntPtr.Zero);
            lastBotInput = Native.Now;
        }
    }
}
