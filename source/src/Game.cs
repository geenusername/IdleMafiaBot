using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace IdleMafiaBot
{
    enum Tab { Safehouse, Jobs, Properties, Inventory, Collection, Shop, Fight, Bank, Operations, Contracts, Crew, Bosses, Family, Territory, Rankings, Trade, BlackMarket, Heists }
    enum ButtonState { None, Ready, Dim, Locked }

    sealed class Header
    {
        public string Name, Family;
        public Rectangle NameBox;
        public int Level = -1, Xp = -1, XpNext = -1, SkillPoints = -1;
        public int Energy = -1, EnergyMax = -1, EnergyTick = -1;
        public int Stamina = -1, StaminaMax = -1, StaminaTick = -1;
        public int Health = -1, HealthMax = -1;
        public double Cash = -1, Banked = -1;
        public bool HealthAtLeast(int pct) { return Health >= 0 && HealthMax > 0 && Health * 100 >= pct * HealthMax; }
    }

    sealed class JobCard
    {
        public JobInfo Info;
        public string Name, ButtonText = "", Mastery;
        public Rectangle NameBox, MasteryBar;
        public bool BarGuessed;
        public Point ButtonAt;
        public Point StateAt;
        public int Cost = -1, Xp = -1;
        public ButtonState Button;
        public int NameY { get { return NameBox.Y; } }
    }

    sealed class HeistRow { public int Top; public Point HelpAt; public string Name = ""; public string HelpText = ""; public int XpPerHelp = -1, Energy = -1; public bool CanHelp, YourFamily; }
    sealed class FightRow { public int Index; public Point AttackAt, NameAt; public string AttackText = "", Name = ""; public int Level = -1; public int Steal = -1; public int StaminaCost = 1; public bool Ready, War, NoFamily; }
    sealed class OpSlot { public int Index; public string Name = ""; public int SecondsLeft = -1; public bool Running, Locked, Done, Pass; public OcrLine Collect; }
    sealed class OpRow { public string Name; public int Y; public Point StartAt; public string StartText = ""; public int Seconds = -1; public int Xp = -1; public bool CanStart; public int Page; }
    sealed class BossCard { public string Name = ""; public int Level = -1; public int ButtonX, ButtonY, Order; public string ButtonText = ""; public int RespawnSeconds = -1; }
    sealed class CityHeader { public int Index; public int Y; public bool Collapsed; public Point ButtonAt; }

    sealed class StopException : Exception { public StopException() : base("stopped") { } }
    sealed class NeedUserException : Exception { public NeedUserException(string m) : base(m) { } }

    sealed partial class Game
    {
        public const int RED = 0xBA4A3E, BLUE = 0x5B8FB9, HEADER_BG = 0x1A1A1C;
        public readonly RobloxWindow Win = new RobloxWindow();
        readonly Action<string> log;
        readonly Func<bool> stopRequested;
        readonly Dictionary<string, int> jobScroll = new Dictionary<string, int>();

        public Game(Action<string> log, Func<bool> stopRequested)
        {
            this.log = log;
            this.stopRequested = stopRequested;
        }

        public Func<bool> UserBack = () => false;

        public void Check()
        {
            if (stopRequested()) throw new StopException();
            if (UserBack()) throw new UserBusyException();
        }

        public void Wait(int ms)
        {
            int end = Environment.TickCount + ms;
            while (end - Environment.TickCount > 0) { Check(); Thread.Sleep(Math.Min(100, Math.Max(1, end - Environment.TickCount))); }
        }

        public Frame Capture() { Check(); return Win.Capture(); }

        public string ProblemsDir;

        public Action<string, string> Saved;

        public Action<string, bool> Failed;

        public void NoteFailure(string reason) { var h = Failed; if (h != null) h(reason, true); }

        readonly Dictionary<string, DateTime> lastShot = new Dictionary<string, DateTime>();

        public int Shots { get; private set; }

        public void Snapshot(string reason, int minMinutes = 0, bool keep = false, bool failure = true)
        {
            var failed = Failed;
            if (failure && !keep && failed != null) failed(reason, false);
            if (string.IsNullOrEmpty(ProblemsDir)) return;
            try
            {
                string slug = Regex.Replace(reason ?? "problem", @"[^A-Za-z0-9]+", "-").Trim('-');
                if (slug.Length > 60) slug = slug.Substring(0, 60);
                DateTime shot;
                if (minMinutes > 0 && lastShot.TryGetValue(slug, out shot) && DateTime.UtcNow < shot.AddMinutes(minMinutes)) return;
                string active = System.IO.Path.Combine(ProblemsDir, "active");
                if (keep && SeenBefore(slug)) return;
                string dir = keep ? System.IO.Path.Combine(active, "first seen") : active;
                System.IO.Directory.CreateDirectory(dir);
                string file = System.IO.Path.Combine(dir, DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + slug + ".png");
                using (var f = Win.Capture())
                {
                    if (GameLoading(f))
                    {

                        log("(The game was loading just then - after an update, a teleport or a reconnect. No picture kept.)");
                        return;
                    }
                    f.Bitmap.Save(file, System.Drawing.Imaging.ImageFormat.Png);
                }
                lastShot[slug] = DateTime.UtcNow;
                if (!keep) Shots++;
                log("Saved a screenshot of this: problems\\active\\" + (keep ? "first seen\\" : "") + System.IO.Path.GetFileName(file));
                var saved = Saved;
                if (!keep && failure && saved != null) saved(reason ?? "problem", file);

                var reasons = new HashSet<string>();
                int kept = 0, most = keep ? 200 : 80;
                foreach (var o in new System.IO.DirectoryInfo(dir).GetFiles("*.png").OrderByDescending(x => x.Name))
                {
                    bool newest = reasons.Add(o.Name.Length > 16 ? o.Name.Substring(16) : o.Name);
                    if (++kept > most && !newest) o.Delete();
                }
            }
            catch (Exception) { }
        }

        public static bool GameLoading(Frame f)
        {
            var area = Rectangle.FromLTRB(f.Width / 4, f.Height * 55 / 100, f.Width * 3 / 4, f.Height * 90 / 100);
            foreach (var l in Ocr.Read(f, area, f.Width < 1300 ? 2 : 1, Prep.None))
            {
                string k = Parse.Key(l.Text);
                if (k.Contains("IOADINGYOURSAVE") || k.Contains("JOININGTHEIATESTVERSION") || k.Contains("PREPARINGYOURINTERFACE") || k == "GAMEUPDATED") return true;
            }
            return RobloxJoining(f);
        }

        internal static bool RobloxJoining(Frame f)
        {
            var area = Rectangle.FromLTRB(f.Width / 4, f.Height * 40 / 100, f.Width * 3 / 4, f.Height * 70 / 100);
            var lines = Ocr.Read(f, area, f.Width < 1300 ? 2 : 1, Prep.None);

            var name = lines.FirstOrDefault(l => Parse.Key(l.Text).StartsWith("IDIEMAFIAGAM"));
            if (name == null) return false;
            int h = Math.Max(6, name.Box.Height);
            return lines.Any(l => l != name && l.Box.Y > name.Box.Bottom && l.Box.Y - name.Box.Bottom < 6 * h
                                  && Math.Abs(l.CenterX - name.CenterX) < name.Box.Width && Parse.Key(l.Text).EndsWith("CASH"));
        }

        static readonly Regex VersionRx = new Regex(@"VERS[I1l]ON\s*([0-9]+(?:\.[0-9]+){1,3})", RegexOptions.IgnoreCase);

        public static string LoadingVersion(Frame f)
        {
            var area = Rectangle.FromLTRB(f.Width * 70 / 100, f.Height * 90 / 100, f.Width, f.Height);
            foreach (int s in new[] { f.Width < 1300 ? 3 : 2, 4 })
            {
                var m = VersionRx.Match(Ocr.ReadText(f, area, s, Prep.None));
                if (m.Success) return m.Groups[1].Value;
            }
            return null;
        }

        public static bool GameUpdated(Frame f)
        {
            var area = Rectangle.FromLTRB(f.Width / 4, f.Height * 55 / 100, f.Width * 3 / 4, f.Height * 90 / 100);
            return Ocr.Read(f, area, f.Width < 1300 ? 2 : 1, Prep.None).Any(l => Parse.Key(l.Text).Contains("GAMEUPDATED"));
        }

        bool SeenBefore(string slug)
        {

            foreach (var sub in new[] { @"active\first seen", @"fixed\first seen", @"reported\first seen", "first seen" })
            {
                string d = System.IO.Path.Combine(ProblemsDir, sub);

                if (System.IO.Directory.Exists(d) && System.IO.Directory.GetFiles(d, "????????-??????-" + slug + ".png").Length > 0) return true;
            }
            return false;
        }

        void Click(int x, int y, int waitMs) { Check(); NoteLateFrame(); Win.Click(x, y); Wait(waitMs + (frameLagMs > 0 ? 800 : 0)); }

        void Scroll(int x, int y, int notches)
        {
            Check();
            byte[] before = NoteLateFrame() ?? PageSample();
            Win.Scroll(x, y, notches);

            byte[] now = PageSample();

            if (frameLagMs > 0 && Moved(before, now) && ++quickRolls >= 10)
            {
                frameLagMs = 0; lateSeen = 0; quickRolls = 0;
                log("Roblox's picture comes right away again - the bot no longer waits for it after a scroll");
            }
            for (int waited = 0; waited < Math.Max(frameLagMs, 0) && !Moved(before, now); waited += 150) { Wait(150); now = PageSample(); }
            lastScrollStill = Moved(before, now) ? null : now;
            stillAt = Win.LastInput;
        }

        int frameLagMs;
        byte[] lastScrollStill;
        DateTime stillAt;
        int lateSeen;
        int quickRolls;

        byte[] NoteLateFrame()
        {
            if (lastScrollStill == null) return null;
            var now = PageSample();
            if (Win.LastInput != stillAt) { lastScrollStill = null; return now; }
            if (Moved(lastScrollStill, now)) LateSeen();
            lastScrollStill = null;
            return now;
        }

        void LateSeen()
        {
            quickRolls = 0;
            if (++lateSeen >= 2 && frameLagMs < 2500)
            {

                frameLagMs = 2500;
                log("Roblox's picture comes late on this PC (seen after a scroll; a remote desktop that was closed does this) - the bot waits for it from now on");
                Snapshot("late picture", 0, true);
            }
            else if (frameLagMs >= 2500 && frameLagMs < 6000 && lateSeen >= 6)
            {

                frameLagMs = 6000;
                log("Roblox's picture comes even later on this PC - the bot waits up to 6 s for it after a scroll from now on");
            }
        }

        internal bool WaitLateRoll(int ms)
        {
            var still = lastScrollStill;
            if (still == null) return false;
            for (int waited = 0; waited < ms; waited += 250)
            {
                Wait(250);
                if (Win.LastInput != stillAt) return false;
                if (Moved(still, PageSample())) { lastScrollStill = null; LateSeen(); Wait(300); return true; }
            }
            return false;
        }

        byte[] PageSample()
        {
            if (Win.Minimized) return null;
            using (var f = Capture())
            {
                var a = view != null && view.Problem == null && view.Width == f.Width && view.Height == f.Height ? view.Page
                      : new Rectangle(f.Width / 5, f.Height / 6, f.Width * 3 / 4, f.Height * 3 / 4);
                var s = new byte[32 * 24];
                for (int j = 0; j < 24; j++)
                    for (int i = 0; i < 32; i++)
                    {
                        var c = f.Pixel(a.X + a.Width * (2 * i + 1) / 64, a.Y + a.Height * (2 * j + 1) / 48);
                        s[j * 32 + i] = (byte)((c.R + c.G + c.B) / 3);
                    }
                return s;
            }
        }

        static bool Moved(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            int n = 0;
            for (int i = 0; i < a.Length; i++) if (Math.Abs(a[i] - b[i]) > 24) n++;
            return n * 4 > a.Length;
        }

        internal int ListNotches(int max)
        {
            int h = view != null && view.Problem == null ? view.Page.Height : 891;
            double k = Math.Max(1, notchPx / 98);
            return Math.Max(1, Math.Min(max, (int)((h / k - 150) / 98)));
        }

        double notchPx = 98;
        Size notchFor = Size.Empty;
        int notchTries;
        Size lastNotchTry = Size.Empty;

        void MeasureNotch(List<JobInfo> catalog)
        {
            if (view == null || view.Problem != null) return;
            var size = new Size(view.Width, view.Height);
            if (size == notchFor) return;
            if (notchTries >= 3 && size == lastNotchTry) return;
            if (size != lastNotchTry) { lastNotchTry = size; notchTries = 0; }
            notchTries++;
            List<JobCard> before, after;
            using (var f = Capture()) before = ReadJobCards(f, catalog, false);
            if (before.Count == 0) return;
            ScrollPage(0.54, -1);
            using (var f = Capture()) after = ReadJobCards(f, catalog, false);
            ScrollPage(0.54, 1);
            var shifts = new List<int>();
            foreach (var a in after)
            {
                var b = before.FirstOrDefault(x => a.Info != null ? x.Info == a.Info : x.Name == a.Name);
                if (b != null && b.NameY > a.NameY) shifts.Add(b.NameY - a.NameY);
            }
            if (shifts.Count == 0) return;
            shifts.Sort();
            int px = shifts[shifts.Count / 2];
            if (px < 60) return;
            notchFor = size;
            if (Math.Abs(px - notchPx) < 12) return;
            notchPx = Math.Max(30, px);
            jobScroll.Clear();
            if (notchPx > 120) log("Lists: a mouse-wheel notch moves them " + px + " px on this screen (98 at Windows' 100% scale) - the bot rolls them by less from now on");
        }

        internal void ScrollPage(double fy, int notches)
        {
            var p = view != null && view.Problem == null ? view.PagePoint(0.5, fy) : new Point(1085, 118 + (int)(891 * fy));
            Scroll(p.X, p.Y, notches);
        }

        public static bool IsDim(Frame f, int x, int y)
        {
            var c = f.Pixel(x, y);
            int max = Math.Max(c.R, Math.Max(c.G, c.B)), min = Math.Min(c.R, Math.Min(c.G, c.B));
            return max <= 95 && min >= 20 && max - min <= 22;
        }

        public static bool GoldIsh(Frame f, int x, int y)
        {
            var c = f.Pixel(x, y);
            return c.R >= 120 && c.G >= 95 && c.B <= 90 && c.R - c.B >= 70;
        }

        View view;
        int viewAt, remeasures;
        readonly int[] barMax = { -1, -1, -1 };
        readonly int[] barShort = { 0, 0, 0 };
        IntPtr viewWindow;
        View lastGood;
        IntPtr lastGoodWindow;

        public View ViewOf(Frame f)
        {
            bool sameWindow = view != null && view.Width == f.Width && view.Height == f.Height && viewWindow == Win.Handle;

            if (sameWindow && view.Problem != null && Environment.TickCount - viewAt < 10000) return view;
            if (!sameWindow || view.Problem != null)
            {
                view = View.Read(f);
                viewAt = Environment.TickCount; remeasures = 0;
                viewWindow = Win.Handle;
                if (view.Problem == null) { Win.ParkAt = view.ParkPoint; lastGood = view; lastGoodWindow = Win.Handle; }
                else
                {
                    var kept = KeptUnderNotification(f);
                    if (kept != null) view = kept;
                }
            }
            else if (view.EnergyPlaced && remeasures < 20 && Environment.TickCount - viewAt > 2000)
            {

                remeasures++; viewAt = Environment.TickCount;
                var again = View.Read(f);
                if (again.Problem == null) { view = again; Win.ParkAt = view.ParkPoint; }
            }
            return view;
        }

        public void ForgetView() { view = null; }

        View KeptUnderNotification(Frame f)
        {
            var g = lastGood;
            if (g == null || g.Width != f.Width || g.Height != f.Height || lastGoodWindow != Win.Handle) return null;
            return UnderNotification(g, f) ? g : null;
        }

        public static bool UnderNotification(View good, Frame f)
        {
            bool covered = !f.Near(good.ProbeX, good.Energy.CenterY, HEADER_BG, 14) || !f.Near(good.ProbeX, good.Stamina.CenterY, HEADER_BG, 14)
                           || !GoldToastBox(f, good, good.Energy.CenterY).IsEmpty || !GoldToastBox(f, good, good.Stamina.CenterY).IsEmpty;
            return covered && good.ReadMenu(f);
        }

        public Header ReadHeader(Frame f)
        {
            var v = ViewOf(f);
            return v.Problem == null ? ReadHeader(f, v) : new Header();
        }

        public volatile bool RobloxBar;

        internal static bool RobloxBarOver(Frame f)
        {
            foreach (double fy in new[] { 0.014, 0.028 })
            {
                int y = Math.Max(1, (int)(f.Height * fy)), hits = 0;
                for (int i = 0; i < 25; i++)
                    if (f.Near((int)(f.Width * (0.02 + i * 0.04)), y, 0x262930, 10)) hits++;
                if (hits < 23) return false;
            }

            int below = Math.Max(1, (int)(f.Height * 0.12)), same = 0;
            for (int i = 0; i < 25; i++)
                if (f.Near((int)(f.Width * (0.02 + i * 0.04)), below, 0x262930, 10)) same++;
            return same < 20;
        }

        public bool ClearRobloxBar()
        {
            for (int i = 0; i < 2; i++)
            {
                Check();
                Win.SweepFromTop();
                Wait(1500);
                using (var f = Capture()) if (!RobloxBarOver(f)) return true;
            }
            return false;
        }

        public Header ReadHeader(Frame f, View v)
        {
            RobloxBar = RobloxBarOver(f);
            if (RobloxBar) return new Header();
            var h = new Header();
            int a, b;
            double money, row = 34.5 * v.HeaderScale;
            string raw;

            int probeX = ToastProbeX(v);

            bool toast1 = !f.Near(probeX, v.Energy.CenterY, HEADER_BG, 14) || !GoldToastBox(f, v, v.Energy.CenterY).IsEmpty;
            bool toast2 = !f.Near(probeX, v.Stamina.CenterY, HEADER_BG, 14) || !GoldToastBox(f, v, v.Stamina.CenterY).IsEmpty;
            bool toast3 = !f.Near(probeX, v.Health.CenterY, HEADER_BG, 14) || !GoldToastBox(f, v, v.Health.CenterY).IsEmpty;

            int barScale = View.ScaleFor(20 * v.HeaderScale);

            var bar = BarRow(v, v.Energy);
            if (!toast1 && ReadTopBar(f, bar, barScale, 10, 0, out a, out b, out raw))
            {
                bool full = Parse.Has(raw, "FULL");
                h.Energy = full ? b : a; h.EnergyMax = barMax[0] = b;
                h.EnergyTick = full ? 0 : Parse.RegenTimer(raw);
            }
            bar = BarRow(v, v.Stamina);
            if (!toast2 && ReadTopBar(f, bar, barScale, 5, 1, out a, out b, out raw))
            {
                bool full = Parse.Has(raw, "FULL");
                h.Stamina = full ? b : a; h.StaminaMax = barMax[1] = b;
                h.StaminaTick = full ? 0 : Parse.RegenTimer(raw);
            }
            bar = BarRow(v, v.Health);
            if (!toast3 && ReadTopBar(f, bar, barScale, 10, 2, out a, out b, out raw)) { h.Health = Parse.Has(raw, "FULL") ? b : a; h.HealthMax = barMax[2] = b; }

            if (v.Cash != null)
            {
                h.Cash = ReadCash(f, v);
                string text;
                if (TryRead(f, BankedArea(v), View.ScaleFor(18 * v.HeaderScale), t => Parse.Money(t, out money), out text))
                    { Parse.Money(text, out money); h.Banked = money; }
            }

            ReadPlayerPanel(f, v, h);
            return h;
        }

        public bool ReadHealthBar(Frame f, out int cur, out int max)
        {
            cur = max = -1;
            var v = ViewOf(f);
            if (v.Problem != null || !f.Near(ToastProbeX(v), v.Health.CenterY, HEADER_BG, 14) || !GoldToastBox(f, v, v.Health.CenterY).IsEmpty) return false;
            int a, b;
            string raw;
            var bar = BarRow(v, v.Health);
            if (!ReadTopBar(f, bar, View.ScaleFor(20 * v.HeaderScale), 10, 2, out a, out b, out raw)) return false;
            cur = Parse.Has(raw, "FULL") ? b : a;
            max = barMax[2] = b;
            return cur >= 0 && max > 0;
        }

        static Rectangle CashArea(View v)
        {
            double row = 34.5 * v.HeaderScale;
            int x = v.Cash.Box.X - 6, right = Math.Min(v.Energy.Box.X - (int)(row / 2), x + (int)(7 * row));
            int y = v.Cash.Box.Bottom, split = y + (int)(1.6 * row);
            return new Rectangle(x, y, right - x, split - y);
        }

        static Rectangle BankedArea(View v)
        {
            var c = CashArea(v);
            return new Rectangle(c.X, c.Bottom, c.Width, v.Header.Bottom - c.Bottom);
        }

        public double ReadCash(Frame f, View v, bool other = false)
        {
            if (v == null || v.Problem != null || v.Cash == null) return -1;
            var area = CashArea(v);
            int scale = View.ScaleFor(26 * v.HeaderScale), lo = Math.Max(1, scale - 1);
            var tries = new List<Tuple<int, Prep>> { Tuple.Create(scale, Prep.None), Tuple.Create(scale + 1, Prep.None), Tuple.Create(scale, Prep.DarkText), Tuple.Create(lo, Prep.None) };
            if (other) tries = new List<Tuple<int, Prep>> { tries[1], tries[3], tries[2], tries[0] };

            tries.AddRange(new[] { Tuple.Create(scale + 1, Prep.DarkText), Tuple.Create(lo, Prep.Contrast), Tuple.Create(scale + 2, Prep.None), Tuple.Create(lo, Prep.DarkText) });
            var reads = new List<Tuple<double, bool, string>>();
            for (int i = 0; i < tries.Count; i++)
            {
                if (i == 4 && (reads.Any(r => r.Item2) || !reads.Any(r => Regex.IsMatch(r.Item3.Trim(), "8\\W*$")))) break;
                string text = Ocr.ReadText(f, area, tries[i].Item1, tries[i].Item2);
                double money;
                if (!Parse.Money(text, out money)) continue;
                bool suffix = MoneySuffix.IsMatch(text);
                reads.Add(Tuple.Create(money, suffix, text));

                if (suffix && !CommaB.IsMatch(text) && reads.Count(r => r.Item2 && Math.Abs(r.Item1 - money) <= money * 0.002) >= 2) return money;
            }
            if (reads.Count == 0) return ReadLoneMoney(f, area);
            return PickCash(reads);
        }

        static readonly Regex CommaB = new Regex(@"\$\s?\d{1,3},\s?\d{2}\s?B(?![A-Za-z])", RegexOptions.IgnoreCase);

        internal static double PickCash(List<Tuple<double, bool, string>> reads)
        {
            if (reads == null || reads.Count == 0) return -1;
            var plain = reads.Where(r => !r.Item2).Select(r => Regex.Match(r.Item3, @"\$\s?(\d{1,3}),(\d{2})8(?![0-9,.])")).Where(m => m.Success).ToList();
            var lettered = reads.Where(r => r.Item2).ToList();
            if (plain.Count > 0 && lettered.Count > 0
                && lettered.All(r => plain.Any(m => Regex.IsMatch(r.Item3, @"\$\s?" + m.Groups[1].Value + @",\s?" + m.Groups[2].Value + @"\s?B(?![A-Za-z])", RegexOptions.IgnoreCase))))
                lettered.Clear();
            var pool = lettered.Count > 0 ? lettered : reads.Where(r => !r.Item2).ToList();
            return pool.GroupBy(r => r.Item1).OrderByDescending(g => g.Count()).First().Key;
        }

        static double ReadLoneMoney(Frame f, Rectangle area)
        {
            var r = Rectangle.Intersect(area, new Rectangle(0, 0, f.Width, f.Height));
            int x0 = int.MaxValue, y0 = int.MaxValue, x1 = -1, y1 = -1;
            for (int y = r.Top; y < r.Bottom; y++)
                for (int x = r.Left; x < r.Right; x++)
                {
                    var c = f.Pixel(x, y);
                    if (c.G < 110 || c.G - c.B < 35 || c.G - c.R < 15) continue;
                    x0 = Math.Min(x0, x); x1 = Math.Max(x1, x); y0 = Math.Min(y0, y); y1 = Math.Max(y1, y);
                }
            if (x1 < 0 || y1 - y0 < 6) return -1;
            var crop = Rectangle.Intersect(Rectangle.FromLTRB(x0 - 3, y0 - 3, x1 + 4, y1 + 4), new Rectangle(0, 0, f.Width, f.Height));
            var back = f.Pixel(crop.X, crop.Y);
            int m = Math.Max(6, (y1 - y0) / 2);
            foreach (int copies in new[] { 2, 3 })
            {
                var bmp = new Bitmap(2 * m + copies * crop.Width + (copies - 1) * 2 * m, crop.Height + 2 * m, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(back);
                    for (int k = 0; k < copies; k++)
                        g.DrawImage(f.Bitmap, new Rectangle(m + k * (crop.Width + 2 * m), m, crop.Width, crop.Height), crop, GraphicsUnit.Pixel);
                }
                using (var copy = new Frame(bmp))
                    foreach (var t in new[] { Tuple.Create(1, Prep.None), Tuple.Create(2, Prep.None), Tuple.Create(1, Prep.DarkText), Tuple.Create(2, Prep.Contrast) })
                    {
                        string text = Ocr.ReadText(copy, new Rectangle(0, 0, copy.Width, copy.Height), t.Item1, t.Item2);
                        var found = Regex.Matches(text, @"\$\s?[0-9OoIlS]{1,3}(?:[.,][0-9OoIlS]{1,3})*\s?[KMBT]?(?![A-Za-z])", RegexOptions.IgnoreCase)
                                         .Cast<Match>().Select(x => x.Value.Replace(" ", "").ToUpperInvariant()).ToList();
                        double v;
                        if (found.Count == copies && found.Distinct().Count() == 1 && Parse.Money(found[0], out v)) return v;
                    }
            }
            return -1;
        }

        static readonly Regex MoneySuffix = new Regex(@"\$\s?[0-9OoIlS]{1,3}(?:[.,][0-9OoIlS]{1,2})?\s?[KMBT](?![A-Za-z])", RegexOptions.IgnoreCase);

        public double ReadCash(Frame f, bool other = false) { return ReadCash(f, ViewOf(f), other); }

        static int ToastProbeX(View v) { return v.ProbeX; }

        static string ToastText(Frame f, View v)
        {
            double row = 34.5 * v.HeaderScale;
            int x = ToastProbeX(v) - 8;
            return Ocr.ReadText(f, new Rectangle(x, (int)(v.Energy.CenterY - row / 2), v.Width - x, (int)row), View.ScaleFor(20 * v.HeaderScale), Prep.None);
        }

        static Rectangle GoldToastBox(Frame f, View v, int y)
        {
            double row = 34.5 * v.HeaderScale;
            int probe = ToastProbeX(v), limit = (int)(4 * row), run = (int)(2 * row);
            if (y < 1 || y >= f.Height - 1) return Rectangle.Empty;
            for (int left = Math.Min(f.Width - 2, probe + (int)(row / 3)); left > Math.Max(1, probe - (int)(2 * row)); left--)
            {

                if (!IsToastGold(f.Pixel(left, y)) || IsToastGold(f.Pixel(left - 1, y)) || IsToastGold(f.Pixel(Math.Min(f.Width - 1, left + 4), y))) continue;
                int top = y, bottom = y;
                while (top > 0 && y - top < limit && IsToastGold(f.Pixel(left, top - 1))) top--;
                while (bottom < f.Height - 1 && bottom - y < limit && IsToastGold(f.Pixel(left, bottom + 1))) bottom++;
                if (bottom - top < 0.8 * row || bottom - top >= limit) continue;
                int gold = 0, right = left;
                for (int x = left; x < Math.Min(f.Width, left + run); x++) if (IsToastGold(f.Pixel(x, top))) gold++;
                if (gold < run * 9 / 10) continue;
                for (int x = left, miss = 0; x < f.Width && miss < 3; x++)
                    if (IsToastGold(f.Pixel(x, top))) { right = x; miss = 0; } else miss++;
                return Rectangle.FromLTRB(left, top, right + 1, bottom + 1);
            }
            return Rectangle.Empty;
        }

        static bool IsToastGold(Color c) { return c.R >= 150 && c.G >= 120 && c.B <= 90 && c.R - c.B >= 90 && c.R - c.G <= 60; }

        static string GoldToastText(Frame f, View v, Rectangle box)
        {
            var inside = Rectangle.FromLTRB(box.Left + 3, box.Top + 3, box.Right - 3, box.Bottom - 3);
            return Ocr.ReadText(f, inside, View.ScaleFor(20 * v.HeaderScale), Prep.None);
        }

        internal Rectangle ToastBox(Frame f)
        {
            var v = ViewOf(f);
            return v.Problem != null ? Rectangle.Empty : GoldToastBox(f, v, v.Energy.CenterY);
        }

        static Rectangle BarRow(View v, OcrLine label)
        {
            double row = 34.5 * v.HeaderScale;
            int x = label.Box.Right + 2;
            return new Rectangle(x, (int)(label.CenterY - row * 0.48), v.Width - x - 2, (int)(row * 0.96));
        }

        void ReadPlayerPanel(Frame f, View v, Header h)
        {
            double row = 34.5 * v.HeaderScale;
            int right = v.Cash != null ? v.Cash.Box.X - 4 : v.Width / 2;
            if (right < 200) return;
            int s = View.ScaleFor(f, 12, 12 * v.HeaderScale);
            var lines = Ocr.Read(f, new Rectangle(168, 0, right - 168, v.Header.Bottom), s, Prep.None);
            if (!lines.Any(l => l.CenterY < row && Regex.Matches(l.Text, "[A-Za-z]").Count >= 2))
                lines = Ocr.Read(f, new Rectangle(168, 0, right - 168, v.Header.Bottom), s + 1, Prep.None);
            var points = lines.FirstOrDefault(l => Parse.Has(l.Text, "POINTS"));
            if (points != null) h.SkillPoints = Math.Max(0, Parse.FirstInt(points.Text));
            var words = lines.Where(l => l != points && Regex.Matches(l.Text, "[A-Za-z]").Count >= 2 && !Parse.Has(l.Text, "XP")).OrderBy(l => l.Box.Y).ToList();
            var name = words.FirstOrDefault(l => l.CenterY < row);
            if (name != null) { h.Name = name.Text.Trim(); h.NameBox = name.Box; }
            var family = words.FirstOrDefault(l => name == null || l.Box.Y > name.Box.Bottom);
            if (family != null && family.CenterY < 2 * row) h.Family = family.Text.Trim();

            int left = name != null ? name.Box.X : family != null ? family.Box.X : -1;

            var xpLine = points == null ? lines.FirstOrDefault(l => Parse.Has(l.Text, "XP") && Regex.IsMatch(l.Text, @"\d\s*/")) : null;
            Rectangle xpArea = Rectangle.Empty;
            if (points != null && left > 0) xpArea = new Rectangle(left - 4, (int)(points.CenterY - row * 0.45), points.Box.X - left, (int)(row * 0.9));
            else if (xpLine != null) xpArea = new Rectangle(xpLine.Box.X - 8, (int)(xpLine.CenterY - row * 0.45), xpLine.Box.Width + 16, (int)(row * 0.9));
            var xpTries = new[] { new { S = s + 1, P = Prep.WhiteText }, new { S = s + 2, P = Prep.None } };
            if (xpArea.Width > 0)
                foreach (var t in xpTries)
                {
                    int a, b;
                    if (Parse.Fraction(Ocr.ReadText(f, xpArea, t.S, t.P), out a, out b) && b > 0 && a <= b * 2) { h.Xp = a; h.XpNext = b; break; }
                }

            if (left > 0)
            {
                double side = 2.66 * row;
                int top = (int)Math.Max(0, (name ?? family).Box.Y - 0.12 * side - (name == null ? row : 0));
                var badge = new Rectangle((int)(left - 1.2 * side), (int)(top + 0.45 * side), (int)(1.2 * side) - 2, (int)(0.62 * side));

                int bs = View.ScaleFor(20 * v.HeaderScale);
                var seen = new List<int>();
                foreach (var t in new[]
                {
                    new { S = bs, P = Prep.None }, new { S = bs + 1, P = Prep.None }, new { S = bs, P = Prep.DarkText }, new { S = bs + 2, P = Prep.None },
                    new { S = bs + 1, P = Prep.DarkText }, new { S = bs + 3, P = Prep.None }, new { S = Math.Max(2, bs - 1), P = Prep.None }, new { S = bs + 4, P = Prep.None },
                })
                {
                    var m = Regex.Matches(Ocr.ReadText(f, badge, t.S, t.P), @"\d+");
                    if (m.Count == 0) continue;
                    string d = m[m.Count - 1].Value;
                    int lv;
                    if (d.Length > 3) d = d.Substring(d.Length - 3);
                    if (!int.TryParse(d, out lv) || lv <= 0) continue;
                    if (seen.Contains(lv)) { h.Level = lv; break; }
                    seen.Add(lv);
                }

                if (h.Level < 0)
                {
                    var ink = BadgeInk(f, badge, side);
                    int lv = ink.IsEmpty ? -1 : ReadLoneNumber(f, ink);
                    if (lv > 0 && lv < 1000) h.Level = lv;
                }
            }

            if (h.Level < 0 && h.XpNext > 0 && h.Xp >= 0 && h.Xp < h.XpNext)
            {
                int lv = Array.IndexOf(XpToNext, h.XpNext) + 1, a, b;
                if (lv > 0 && Parse.Fraction(Ocr.ReadText(f, xpArea, s + 3, Prep.None), out a, out b) && b == h.XpNext) h.Level = lv;
            }
        }

        internal static readonly int[] XpToNext = { 10, 18, 30, 45, 50, 70, 120, 155, 195, 240, 284, 333, 384, 439, 497 };

        static Rectangle BadgeInk(Frame f, Rectangle badge, double side)
        {
            var r = Rectangle.Intersect(badge, new Rectangle(0, 0, f.Width, f.Height));
            int reach = Math.Max(3, (int)(0.22 * side));
            Func<int, int, bool> gold = (x, y) =>
            {
                if (x < 0 || y < 0 || x >= f.Width || y >= f.Height) return false;
                var c = f.Pixel(x, y);
                return Math.Abs(c.R - 212) <= 22 && Math.Abs(c.G - 175) <= 22 && c.B <= 95;
            };
            Func<int, int, int, int, bool> goldAlong = (x, y, dx, dy) =>
            {
                for (int k = 1; k <= reach; k++) if (gold(x + k * dx, y + k * dy)) return true;
                return false;
            };
            int x0 = int.MaxValue, y0 = int.MaxValue, x1 = -1, y1 = -1;
            for (int y = r.Top; y < r.Bottom; y++)
                for (int x = r.Left; x < r.Right; x++)
                {
                    var c = f.Pixel(x, y);
                    if ((c.R + c.G + c.B) / 3 >= 90) continue;
                    if (!goldAlong(x, y, -1, 0) || !goldAlong(x, y, 1, 0) || !goldAlong(x, y, 0, -1) || !goldAlong(x, y, 0, 1)) continue;
                    x0 = Math.Min(x0, x); x1 = Math.Max(x1, x); y0 = Math.Min(y0, y); y1 = Math.Max(y1, y);
                }
            if (x1 < 0 || x1 - x0 > 0.42 * side || y1 - y0 > 0.35 * side || y1 - y0 < 0.06 * side) return Rectangle.Empty;
            return Rectangle.FromLTRB(x0, y0, x1 + 1, y1 + 1);
        }

        static bool TryRead(Frame f, Rectangle area, int scale, Func<string, bool> ok, out string text)
        {
            var tries = new[] { new { S = scale, P = Prep.None }, new { S = scale + 1, P = Prep.None }, new { S = scale, P = Prep.DarkText }, new { S = Math.Max(1, scale - 1), P = Prep.None } };
            foreach (var t in tries)
            {
                text = Ocr.ReadText(f, area, t.S, t.P);
                if (ok(text)) return true;
            }
            text = null;
            return false;
        }

        static bool ReadBar(Frame f, Rectangle area, int scale, int minMax, double fill, int knownMax, out int cur, out int max, out string raw)
        {
            bool lost;
            return ReadBar(f, area, scale, minMax, fill, knownMax, false, out cur, out max, out raw, out lost);
        }

        bool ReadTopBar(Frame f, Rectangle bar, int scale, int minMax, int i, out int cur, out int max, out string raw)
        {
            bool lost;
            if (ReadBar(f, bar, scale, minMax, BarFill(f, bar), barMax[i], barShort[i] < 3, out cur, out max, out raw, out lost)) { barShort[i] = 0; return true; }
            if (lost) barShort[i]++;
            return false;
        }

        internal static bool DigitLost(int known, int max)
        {
            if (known <= 0 || max <= 0 || max >= known) return false;
            string k = known.ToString(), m = max.ToString();
            if (m.Length != k.Length - 1) return false;
            for (int i = 0; i < k.Length; i++) if (k.Remove(i, 1) == m) return true;
            return false;
        }

        static bool ReadBar(Frame f, Rectangle area, int scale, int minMax, double fill, int knownMax, bool refuseLost, out int cur, out int max, out string raw, out bool lost)
        {
            lost = false;
            int lo = Math.Max(2, scale - 1);

            var left = new Rectangle(area.X, area.Y, area.Width * 45 / 100, area.Height);
            var tries = new[]
            {
                new { R = left, S = scale + 2, P = Prep.Contrast }, new { R = left, S = scale + 2, P = Prep.None }, new { R = left, S = scale + 1, P = Prep.Contrast },
                new { R = area, S = scale, P = Prep.WhiteText }, new { R = area, S = scale, P = Prep.WhiteBright }, new { R = area, S = scale, P = Prep.WhiteSoft },
                new { R = area, S = scale + 1, P = Prep.WhiteText }, new { R = area, S = scale + 1, P = Prep.WhiteBright }, new { R = area, S = lo, P = Prep.WhiteSoft },
                new { R = area, S = lo, P = Prep.WhiteBright }, new { R = area, S = scale, P = Prep.None }, new { R = left, S = scale, P = Prep.Contrast },

                new { R = left, S = scale, P = Prep.WhiteText }, new { R = left, S = scale, P = Prep.WhiteSoft }, new { R = left, S = scale + 1, P = Prep.WhiteBright },
            };

            var seen = new List<Tuple<int, int, string>>();
            int maxRead = -1;
            string maxRaw = "", allText = "";

            Func<string, string> withEnd = r => Parse.Has(r, "FULL") || Parse.RegenTimer(r) >= 0 ? r : r + " " + Ocr.ReadText(f, area, scale, Prep.WhiteSoft);
            foreach (var t in tries)
            {
                string text = Ocr.ReadText(f, t.R, t.S, t.P);
                allText += " " + text;
                int c, m;
                if (!Parse.Fraction(text, out c, out m, true) || m < minMax || c > m)
                {

                    var only = Regex.Match(text, @"[/|]\s*([0-9OoIlSB]{1,7})(?![0-9])");
                    if (maxRead < 0 && only.Success && int.TryParse(Parse.Digits(only.Groups[1].Value), out m) && m >= minMax && !(refuseLost && DigitLost(knownMax, m))) { maxRead = m; maxRaw = text; }
                    continue;
                }
                if (refuseLost && DigitLost(knownMax, m)) { lost = true; continue; }
                if (maxRead < 0) { maxRead = m; maxRaw = text; }
                if (fill >= 0 && !FitsFill(c, m, fill))
                {
                    string d = c.ToString();
                    if (d.Length > 1 && d[0] == '1' && FitsFill(int.Parse(d.Substring(1)), m, fill)) c = int.Parse(d.Substring(1));
                    else continue;
                }
                var same = seen.FirstOrDefault(s => s.Item1 == c && s.Item2 == m);
                if (same != null) { cur = c; max = m; raw = withEnd(same.Item3); return true; }
                seen.Add(Tuple.Create(c, m, text));
            }
            if (seen.Count > 0) { cur = seen[0].Item1; max = seen[0].Item2; raw = withEnd(seen[0].Item3); return true; }

            if (maxRead < 0 && fill >= 0 && knownMax > 0 && Regex.IsMatch(allText, @"(?<![0-9])" + knownMax + @"(?![0-9])")) maxRead = knownMax;
            if (fill >= 0 && maxRead >= minMax)
            {

                cur = (int)Math.Floor(fill * maxRead + 0.3); max = maxRead; raw = withEnd(maxRaw);
                return true;
            }
            cur = max = -1; raw = "";
            return false;
        }

        static bool FitsFill(int cur, int max, double fill) { return fill <= 0 ? cur == 0 : Math.Abs(cur - fill * max) <= Math.Max(1.5, 0.03 * max); }

        static double BarFill(Frame f, Rectangle row)
        {
            int cy = row.Y + row.Height / 2, x = Math.Max(0, row.Left), end = Math.Min(f.Width, row.Right);
            if (cy < 0 || cy >= f.Height) return -1;
            while (x < end && !IsBarFill(f.Pixel(x, cy)) && !IsBarTrack(f.Pixel(x, cy))) x++;
            if (x + 40 >= end) return -1;

            int top = cy, bottom = cy, col = x + 2;
            while (top > row.Top && (IsBarFill(f.Pixel(col, top - 1)) || IsBarTrack(f.Pixel(col, top - 1)))) top--;
            while (bottom < row.Bottom && (IsBarFill(f.Pixel(col, bottom + 1)) || IsBarTrack(f.Pixel(col, bottom + 1)))) bottom++;
            int h = bottom - top, maxGap = Math.Max(6, row.Height / 4);
            if (h < 8) return -1;
            double a = FillOnRow(f, x - 3, end, top + (int)Math.Round(h * 0.15), maxGap), b = FillOnRow(f, x - 3, end, bottom - (int)Math.Round(h * 0.15), maxGap);
            return a < 0 || b < 0 || Math.Abs(a - b) > 0.02 ? -1 : (a + b) / 2;
        }

        static double FillOnRow(Frame f, int from, int end, int y, int maxGap)
        {
            int x = Math.Max(1, from);
            end = Math.Min(end, f.Width - 2);
            while (x < end && !IsBarFill(f.Pixel(x, y)) && !IsBarTrack(f.Pixel(x, y))) x++;
            int left = x, fillEnd = x, gap = 0;
            if (!IsBarFrame(f.Pixel(left - 1, y))) return -1;
            bool track = false;
            for (; x < end && gap <= maxGap && !BarEnds(f, x, y); x++)
            {
                var c = f.Pixel(x, y);
                if (IsBarFill(c)) { if (track) return -1; fillEnd = x + 1; gap = 0; }
                else if (IsBarTrack(c)) { track = true; gap = 0; }
                else gap++;
            }
            if (x >= end || !BarEnds(f, x, y) || x - left < 40) return -1;
            return (double)(fillEnd - left) / (x - left);
        }

        static bool BarEnds(Frame f, int x, int y)
        {
            return IsBarFrame(f.Pixel(x, y)) && (IsHeaderBack(f.Pixel(x + 1, y)) || IsBarFrame(f.Pixel(x + 1, y)) && IsHeaderBack(f.Pixel(x + 2, y)));
        }

        static bool IsBarFrame(Color c) { return Math.Abs(c.R - 38) <= 6 && Math.Abs(c.G - 38) <= 6 && Math.Abs(c.B - 40) <= 6; }

        static bool IsHeaderBack(Color c) { return Math.Abs(c.R - 26) <= 5 && Math.Abs(c.G - 26) <= 5 && Math.Abs(c.B - 28) <= 5; }

        static bool IsBarFill(Color c)
        {
            int max = Math.Max(c.R, Math.Max(c.G, c.B)), min = Math.Min(c.R, Math.Min(c.G, c.B));
            return max >= 120 && max - min >= 60;
        }

        static bool IsBarTrack(Color c) { return Math.Max(c.R, Math.Max(c.G, c.B)) <= 20; }

        public string ToastIn(Frame f)
        {
            var v = ViewOf(f);
            if (v.Problem != null) return null;
            var gold = GoldToastBox(f, v, v.Energy.CenterY);
            if (!gold.IsEmpty) return GoldToastText(f, v, gold);
            if (f.Near(ToastProbeX(v), v.Energy.CenterY, HEADER_BG, 14)) return null;
            return ToastText(f, v);
        }

        public string ReadToast(int waitForItMs) { return ReadToast(waitForItMs, null); }

        public string ReadToast(int waitForItMs, Func<string, bool> isMine)
        {
            int end = Environment.TickCount + waitForItMs;
            do
            {
                using (var f = Capture())
                {
                    string t = ToastIn(f);
                    if (!string.IsNullOrWhiteSpace(t) && (isMine == null || isMine(t))) return t;
                }
                Wait(150);
            } while (end - Environment.TickCount > 0);
            return null;
        }

        public bool OnTab(Frame f, Tab t) { var v = ViewOf(f); return v.Problem == null && v.IsSelected(f, t); }

        public bool OpenTab(Tab t) { return OpenTab(t, false); }

        public bool OpenTab(Tab t, bool toBuy)
        {
            bool shop = toBuy && (t == Tab.Shop || t == Tab.Properties || t == Tab.BlackMarket);
            if (!shop) Guard(View.TabLabels[t]);
            if (NotInMenu(t)) return false;
            int rolls = 0, rolled = 0;
            Dictionary<Tab, int> before = null;
            bool chatTried = false, lookedAgain = false;
            for (int attempt = 0; attempt < 4; attempt++)
            {
                View v, cached = view;
                View.Place place;
                bool roll = false;
                using (var f = Capture())
                {
                    v = ViewOf(f);

                    if (v.Problem != null) { bool cleared = ClearPopupIfAny(f); ForgetView(); if (!cleared) Wait(1000); continue; }

                    if (v == cached && !v.ReadMenu(f)) { ForgetView(); Wait(1000); continue; }
                    if (v.IsSelected(f, t)) return true;
                    place = v.Find(f, t);
                    if (place.Label != null && IsLockedEntry(f, v, place.Label))
                    {
                        MarkNotInMenu(t, null);
                        return false;
                    }
                    if (place.Spot == View.Spot.Up || place.Spot == View.Spot.Down)
                    {

                        int dir = place.Spot == View.Spot.Down ? 1 : -1;
                        var now = v.MenuSignature();
                        if (rolled == dir && View.SameMenu(before, now)) place = v.AtEnd(f, t, dir);
                        else { roll = true; before = now; rolled = dir; }
                    }
                }
                if (roll)
                {

                    if (!chatTried)
                    {
                        chatTried = true;
                        using (var f = Capture()) if (HideChat(f)) { ForgetView(); rolled = 0; before = null; attempt--; continue; }
                    }

                    if (rolls++ >= 8) break;
                    Scroll(v.Menu.X + v.Menu.Width / 2, v.Menu.Y + v.Menu.Height / 2, rolled > 0 ? -5 : 5);
                    ForgetView();
                    attempt--;
                    continue;
                }
                if (place.Spot == View.Spot.Absent || place.Spot == View.Spot.Hidden)
                {

                    if (!chatTried)
                    {
                        chatTried = true;
                        using (var f = Capture()) if (HideChat(f)) { ForgetView(); continue; }
                    }
                    if (!lookedAgain) { lookedAgain = true; Wait(700); ForgetView(); continue; }
                    if (place.Spot == View.Spot.Absent) { MarkNotInMenu(t, place.Why); return false; }
                    log("Can't read " + View.TabLabels[t] + " in the game's menu (" + place.Why + ") - not pressing anything there");
                    Snapshot("menu entry unreadable " + t, 60);
                    return false;
                }
                var label = place.Label;
                if (label.CenterY < ChatZoneBottom || attempt > 0)
                    using (var f = Capture()) if (HideChat(f)) { ForgetView(); continue; }
                if (shop) PressShopEntry(label, t);
                else Press(label.Text, label.CenterX, label.CenterY, 650);
                using (var f = Capture())
                {
                    if (OnTab(f, t)) return true;
                    if (ClearPopupIfAny(f)) continue;
                }
                Win.Park();
                Wait(1500);
                ForgetView();
            }

            string language = null;
            using (var f = Capture()) { if (OnTab(f, t)) return true; language = ViewOf(f).Language; }
            if (language != null) { log("Could not open " + t + ": " + View.InLanguage(language)); Snapshot("game in " + language, 60); return false; }

            if (!openRetrying)
            {
                openRetrying = true;
                try { Wait(3000); ForgetView(); return OpenTab(t, toBuy); }
                finally { openRetrying = false; }
            }
            log("Could not open " + t + " - is something covering the game?");
            Snapshot("could not open " + t, 60);
            return false;
        }

        bool openRetrying;
        readonly Dictionary<Tab, DateTime> notInMenu = new Dictionary<Tab, DateTime>();
        readonly HashSet<Tab> saidNotInMenu = new HashSet<Tab>();

        public bool NotInMenu(Tab t)
        {
            DateTime until;
            return notInMenu.TryGetValue(t, out until) && DateTime.UtcNow < until;
        }

        void MarkNotInMenu(Tab t, string why)
        {
            notInMenu[t] = DateTime.UtcNow.AddHours(1);
            if (!saidNotInMenu.Add(t)) return;
            if (why == null) { log(View.TabLabels[t] + " is locked in the game's menu: it opens at a higher level. The bot looks again after each level-up."); return; }
            log(View.TabLabels[t] + " isn't in the game's menu (" + why + "). Maybe not open at your level yet. The bot looks again every hour and after a level-up.");
            Snapshot("not in the menu " + t, 0, true);
        }

        internal static bool IsLockedEntry(Frame f, View v, OcrLine label)
        {
            int ink = 0;
            var box = Rectangle.Intersect(label.Box, new Rectangle(0, 0, f.Width, f.Height));
            for (int y = box.Top; y < box.Bottom; y++)
                for (int x = box.Left; x < box.Right; x++) { var c = f.Pixel(x, y); ink = Math.Max(ink, (c.R + c.G + c.B) / 3); }
            if (ink >= 150) return false;
            int x0 = v.Menu.Right - (int)(v.Menu.Width * 0.22), x1 = Math.Min(f.Width, v.Menu.Right - 2);
            int grey = 0, gold = 0, n = 0;
            for (int y = Math.Max(0, label.CenterY - label.Box.Height); y <= Math.Min(f.Height - 1, label.CenterY + label.Box.Height); y++)
                for (int x = Math.Max(0, x0); x < x1; x++)
                {
                    var c = f.Pixel(x, y); n++;
                    int mx = Math.Max(c.R, Math.Max(c.G, c.B)), mn = Math.Min(c.R, Math.Min(c.G, c.B));
                    if (c.R > 150 && c.R - c.B > 80) gold++;
                    else if (mx - mn < 25 && mx >= 90 && mx < 200) grey++;
                }
            return n > 0 && grey * 100 >= 3 * n && gold * 10 < n;
        }

        public void MenuChanged() { notInMenu.Clear(); }

        public void MenuChanged(Tab t) { notInMenu.Remove(t); }

        public void ResetAccount()
        {
            notInMenu.Clear();
            saidNotInMenu.Clear();
            jobScroll.Clear();
            barMax[0] = barMax[1] = barMax[2] = -1;
            barShort[0] = barShort[1] = barShort[2] = 0;
            PointsCost = 1;
            cratesLeft.Clear();
        }

        const int ChatZoneBottom = 420;

        bool chatHidden;

        public bool HideChat(Frame f)
        {
            var lines = Ocr.Read(f, new Rectangle(0, 50, Math.Min(520, f.Width / 2), Math.Min(ChatZoneBottom, f.Height) - 50), 2, Prep.None);
            bool chat = lines.Any(l =>
            {
                string k = Parse.Key(l.Text);
                return k.EndsWith("GIOBAI") || k == "FRIENDS" || k.Contains("TOCHATCIICK") || k.Contains("SAYHITO") || k.Contains("JOINEDTHE");
            });
            if (!chat) return false;
            if (!chatHidden) log("Hiding Roblox's chat window: it covers the top of the menu. The speech bubble at the top left brings it back.");
            chatHidden = true;
            Click(138, 34, 700);
            Win.Park();
            Wait(400);
            return true;
        }

        static readonly HashSet<string> SafeCloseKeys = Keys("DONE", "OK", "OKAY", "CONTINUE", "CLOSE", "COLLECT", "CLAIM", "NICE", "AWESOME", "GOT IT", "COOL");

        static HashSet<string> Keys(params string[] words)
        {
            var set = new HashSet<string>();
            foreach (var w in words) set.Add(Parse.Key(w));
            return set;
        }

        public bool ClearPopup(Frame f)
        {
            if (CloseEventPopup(f)) return true;
            var lines = Ocr.Read(f, PopupArea(f), PopupScale(f), Prep.None);
            CheckForPurchasePrompt(lines);
            foreach (var l in lines)
            {
                if (!SafeCloseKeys.Contains(Parse.Key(l.Text))) continue;
                log("Closing a popup with \"" + l.Text + "\"");
                Snapshot("popup closed with " + l.Text, 0, false, false);
                Press(l, 800);
                return true;
            }
            return false;
        }

        internal static Point EventPopupX(Frame f)
        {
            var area = new Rectangle(f.Width * 25 / 100, f.Height * 15 / 100, f.Width * 50 / 100, f.Height * 70 / 100);
            foreach (int s in new[] { 1, 2 })
            {
                var title = Ocr.Read(f, area, s, Prep.None).FirstOrDefault(l => View.Distance(Parse.Key(l.Text), "JOINEVENT") <= 2);
                if (title == null) continue;
                int h = Math.Max(6, title.Box.Height), right = title.Box.X - h / 2, left = Math.Max(0, title.Box.X - 3 * Math.Max(title.Box.Width, 4 * h));
                int x0 = -1, x1 = -1, y0 = int.MaxValue, y1 = -1;
                for (int x = right; x >= left; x--)
                {
                    bool any = false;
                    for (int y = Math.Max(0, title.CenterY - h); y <= Math.Min(f.Height - 1, title.CenterY + h); y++)
                    {
                        var c = f.Pixel(x, y);
                        if (Math.Min(c.R, Math.Min(c.G, c.B)) < 190) continue;
                        any = true; y0 = Math.Min(y0, y); y1 = Math.Max(y1, y);
                    }
                    if (any) { if (x1 < 0) x1 = x; x0 = x; }
                    else if (x1 >= 0 && x1 - x > h / 3) break;
                }
                if (x1 < 0 || x1 - x0 > 2 * h) return Point.Empty;
                return new Point((x0 + x1) / 2, (y0 + y1) / 2);
            }
            return Point.Empty;
        }

        public bool CloseEventPopup(Frame f)
        {
            var x = EventPopupX(f);
            if (x.IsEmpty) return false;
            log("Closing Roblox's \"Join Event\" window with its X - it was over the game");
            Snapshot("roblox join event window", 0, true, false);
            Click(x.X, x.Y, 800);
            Win.Park();
            return true;
        }

        internal static Rectangle PopupArea(Frame f)
        {
            int top = Math.Min(f.Height * 11 / 100, f.Width * 88 / 1000);
            return new Rectangle(f.Width * 20 / 100, top, f.Width * 61 / 100, f.Height * 11 / 100 + f.Height * 87 / 100 - top);
        }

        internal static int PopupScale(Frame f) { return View.ScaleFor(16.0 * Math.Min(f.Height / 1009.0, f.Width / 1790.0)); }

        internal static List<OcrLine> JoinWrapped(List<OcrLine> lines, Func<string, bool> first, Func<string, bool> second)
        {
            var all = new List<OcrLine>(lines);
            foreach (var a in lines.Where(l => first(l.Text)))
            {
                int h = Math.Max(8, a.Box.Height);
                var b = all.Where(l => l != a && l.Box.Y >= a.Box.Bottom - h / 3 && l.Box.Y - a.Box.Bottom < h
                                       && l.Box.X < a.Box.Right && l.Box.Right > a.Box.X && second(l.Text))
                           .OrderBy(l => l.Box.Y).FirstOrDefault();
                if (b == null || !all.Contains(a)) continue;
                all.Remove(a); all.Remove(b);
                all.Add(new OcrLine { Text = a.Text.Trim() + " " + b.Text.Trim(), Box = Rectangle.Union(a.Box, b.Box) });
            }
            return all;
        }

        internal void CheckForPurchasePrompt(List<OcrLine> lines, bool shopInSight = false)
        {

            if (DisconnectIn(null, lines) != null) throw new WindowChangedException(Disconnected);
            bool robux = false, buy = false, dialog = false, shopPage = shopInSight, buyButton = false, cancel = false;
            foreach (var l in lines)
            {
                string k = Parse.Key(l.Text);
                if (k.Contains("ROBUX") || l.Text.Contains("R$")) robux = true;
                if (k.StartsWith("BUY") || k.Contains("PURCHASE")) buy = true;
                if (k == "BUY" || k == "BUYNOW") buyButton = true;
                if (k == "CANCEI") cancel = true;
                if (k.Contains("WOUIDYOUIIKE") || k.Contains("WANTTOBUY") || k.Contains("YOURBAIANCE") || k.Contains("BAIANCEAFTER") || k == "BUYITEM"
                    || k.Contains("CONFIRMPURCHASE") || k.Contains("BUYROBUX") || k.Contains("INSUFFICIENTROBUX") || k.Contains("ENOUGHROBUX")) dialog = true;

                if (k.Contains("ROBUXSH") || k.Contains("MENTCASH") || k.Contains("GAMEPASSES") || k.Contains("NEWSTOCKIN")) shopPage = true;
            }
            if (dialog || (!shopPage && (buyButton && cancel || robux && buy)))
            {
                Snapshot("robux purchase window");
                throw new NeedUserException("A Robux purchase window seems to be open. The bot stopped and won't touch it - close it yourself.");
            }
        }

        public void CheckForPurchaseWindow()
        {
            using (var f = Capture()) CheckForPurchasePrompt(Ocr.Read(f, PopupArea(f), PopupScale(f), Prep.None));
        }

        public void JobsTop()
        {
            string last = null;
            for (int batch = 0; batch < 4; batch++)
            {
                ScrollPage(0.54, 90);
                using (var f = Capture())
                {
                    var lines = Ocr.Read(f, JobsArea(f), 2, Prep.None);
                    if (lines.Any(l => GameData.CityIndex(l.Text) == 0)) return;
                    string sig = string.Join(",", lines.Select(l => l.Text + "@" + l.Box.Y));
                    if (sig == last) return;
                    last = sig;
                }
            }
        }

        Rectangle JobsArea(Frame f)
        {
            var v = ViewOf(f);
            return v.Problem == null ? v.Page : new Rectangle(300, 205, 1560, 785);
        }

        public List<JobCard> ReadJobCards(Frame f, List<JobInfo> catalog, bool readMastery)
        {
            return CardsFromLines(f, Ocr.Read(f, JobsArea(f), 2, Prep.None), catalog, readMastery);
        }

        static bool IsStats(string t) { return t.Contains("$") || Parse.Has(t, "XP") || Parse.Has(t, "ITEM"); }

        List<JobCard> CardsFromLines(Frame f, List<OcrLine> lines, List<JobInfo> catalog, bool readMastery)
        {
            var area = JobsArea(f);
            var cards = new List<JobCard>();
            foreach (var l in lines)
            {
                string t = l.Text;
                if (IsStats(t) || Parse.Has(t, "TIER") || Parse.Has(t, "MASTERY") || Parse.Key(t) == "DOJOB" || GameData.CityIndex(t) >= 0
                    || Regex.Matches(t, "[A-Za-z]").Count < 6) continue;
                double u = Math.Max(10, l.Box.Height);

                var stats = lines.FirstOrDefault(s => s != l && IsStats(s.Text) && s.Box.Y > l.Box.Y + 0.8 * u && s.Box.Y < l.Box.Y + 2.2 * u
                                                      && Math.Abs(s.Box.X - l.Box.X) < 3 * u);
                if (stats == null) continue;
                if (l.Box.Y < area.Top + 0.07 * area.Height) continue;
                if (l.Box.Y + 2.4 * u > area.Bottom) continue;

                var card = new JobCard { Name = t.Trim(), NameBox = l.Box };
                ParseJobStats(stats.Text, card);
                card.Info = MatchJob(t, card.Cost, -1, catalog);
                if (card.Info == null && card.Cost > 0 && card.Xp > 0)
                {

                    var same = catalog.Where(j => j.Cost == card.Cost && j.Xp == card.Xp).ToList();
                    if (same.Count == 1) card.Info = same[0];
                }
                var button = lines.Where(b => b.Box.X > l.Box.Right && b.CenterY > l.Box.Y && b.CenterY < l.Box.Y + 2.6 * u
                                              && (Parse.Key(b.Text) == "DOJOB" || Parse.Has(b.Text, "LEVEL") || Parse.Has(b.Text, "LOCKED")))
                                  .OrderBy(b => b.Box.X).LastOrDefault();
                if (button != null)
                {
                    card.ButtonText = button.Text;
                    card.ButtonAt = new Point(button.CenterX, button.CenterY);
                    card.StateAt = new Point(button.Box.X - Math.Max(4, button.Box.Height / 2), button.CenterY);
                }
                else
                {

                    card.ButtonAt = new Point(area.Right - (int)(area.Width * 0.16), stats.CenterY - (int)(0.2 * u));
                    card.StateAt = new Point(card.ButtonAt.X - (int)(3 * u), card.ButtonAt.Y);
                }
                card.Button = Parse.Has(card.ButtonText, "LEVEL") || Parse.Has(card.ButtonText, "LOCKED") ? ButtonState.Locked
                            : GoldIsh(f, card.StateAt.X, card.StateAt.Y) ? ButtonState.Ready
                            : IsDim(f, card.StateAt.X, card.StateAt.Y) ? ButtonState.Dim : ButtonState.Locked;
                var label = lines.FirstOrDefault(m => Parse.Has(m.Text, "MASTERY") && m.Box.Y > stats.Box.Y && m.Box.Y < l.Box.Y + 3.8 * u);
                card.MasteryBar = MasteryBarOf(f, card, label, stats, u);
                if (readMastery) card.Mastery = ReadMasteryText(f, card);
                cards.Add(card);
            }
            return cards;
        }

        static Rectangle MasteryBarOf(Frame f, JobCard c, OcrLine label, OcrLine stats, double u)
        {
            var found = BarByPixels(f, c.NameBox.X, stats.Box.Bottom, u, c.ButtonAt.X > 0 ? c.ButtonAt.X : f.Width);
            if (!found.IsEmpty) return found;
            int left = c.NameBox.X, top = c.NameBox.Y + (int)(2.45 * u), height = (int)Math.Max(8, 0.9 * u);
            if (label != null)
                return new Rectangle(left, label.CenterY - height / 2, Math.Max(10, 2 * (label.CenterX - left)), height);
            int right = c.ButtonAt.X > 0 ? left + (int)(0.55 * (c.ButtonAt.X + 3 * u - left)) : left + (int)(20 * u);
            c.BarGuessed = true;
            return new Rectangle(left, top, right - left, height);
        }

        static Rectangle BarByPixels(Frame f, int left, int fromY, double u, int maxRight)
        {
            int x = left + 6, runTop = -1;
            int minH = (int)Math.Max(6, 0.4 * u), maxH = (int)(2 * u), end = Math.Min(f.Height - 1, fromY + (int)(3 * u));
            for (int y = Math.Max(0, fromY); y <= end; y++)
            {
                bool bar = y < end && IsBarPixel(f.Pixel(x, y));
                if (bar) { if (runTop < 0) runTop = y; continue; }
                if (runTop < 0) continue;
                int h = y - runTop;
                if (h >= minH && h <= maxH)
                {
                    int row = runTop + 1, l = x, r = x, miss = 0;
                    while (l > 1 && IsBarPixel(f.Pixel(l - 1, row))) l--;
                    for (int xx = x; xx < f.Width - 1 && miss < 4; xx++)
                        if (IsBarPixel(f.Pixel(xx, row))) { r = xx; miss = 0; } else miss++;
                    if (r - l >= 10 * u && Math.Abs(l - left) <= u && r < maxRight) return new Rectangle(l, runTop, r - l + 1, h);
                }
                runTop = -1;
            }
            return Rectangle.Empty;
        }

        static bool IsBarPixel(Color c)
        {
            int max = Math.Max(c.R, Math.Max(c.G, c.B));
            return (c.R >= 150 && c.R - c.B >= 100) || max <= 18;
        }

        public static string ReadMasteryText(Frame f, JobCard c)
        {
            var bar = c.MasteryBar;
            bar.Inflate(0, 3);
            string first = null;
            foreach (var p in new[] { Prep.None, Prep.GreyText })
                foreach (int scale in p == Prep.None ? new[] { 4 } : new[] { 4, 3 })
                {
                    string t = Ocr.ReadText(f, bar, scale, p);
                    if (first == null) first = t;
                    if (IsMasteryLabel(t)) return t;
                }
            return first;
        }

        internal static bool IsMasteryLabel(string t)
        {
            if (string.IsNullOrEmpty(t)) return false;
            int a, b;
            if (Parse.Fraction(t, out a, out b) && (b == 25 || b == 50 || b == 100) && a <= b) return true;

            return t.ToUpperInvariant().Contains("GOLD") && !Regex.IsMatch(Regex.Replace(t, @"\+?\s*[0-9]+\s*%", ""), "[0-9]");
        }

        static void ParseJobStats(string s, JobCard c)
        {
            int dollar = s.IndexOf('$');
            int v;
            if (dollar > 0)
            {
                string d = Parse.Digits(s.Substring(0, dollar));
                if (d.Length >= 2 && d[0] == '0') d = d.Substring(1);
                if (int.TryParse(d, out v) && v > 0 && v < 1000) c.Cost = v;
            }
            var m = Regex.Match(s, @"([0-9OoIl,]+)\s*[xX][pP]");
            if (m.Success && int.TryParse(Parse.Digits(m.Groups[1].Value), out v)) c.Xp = v;
        }

        public static JobInfo MatchJob(string text, List<JobInfo> catalog) { return MatchJob(text, -1, -1, catalog); }

        public static JobInfo MatchJob(string text, int cost, int cityAtLeast, List<JobInfo> catalog)
        {
            string k = Parse.Key(text);
            if (k.Length < 6) return null;
            var near = catalog.Where(j => cityAtLeast < 0 || GameData.CityIndex(j.City) >= cityAtLeast)
                              .Select(j => new { J = j, D = Levenshtein(k, Parse.Key(j.Name)) })
                              .Where(x => x.D <= Math.Max(2, x.J.Name.Length / 7)).OrderBy(x => x.D);
            foreach (var x in near)
                if (x.D == 0 || !CostOff(cost, x.J.Cost)) return x.J;
            return null;
        }

        internal static bool CostOff(int read, int known) { return read > 0 && known > 0 && Math.Abs(read - known) * 10 > known * 3; }

        static int Levenshtein(string a, string b) { return View.Distance(a, b); }

        public static void ApplyMastery(JobInfo j, string text, Frame f = null, JobCard card = null)
        {
            if (j == null) return;
            if (string.IsNullOrEmpty(text)) { CountFromBar(j, f, card); return; }
            string u = text.ToUpperInvariant();
            int a, b;
            bool count = Parse.Fraction(text, out a, out b);
            if (count && b == 10 && a > 10 && a < 100) b = 100;
            if (count && (b == 25 || b == 50 || b == 100) && a <= b)
            {
                int rank = b == 25 ? 0 : b == 50 ? 1 : 2;
                if (f != null && card != null && !card.BarGuessed)
                {
                    int fromBar = MasteryFromBar(f, card.MasteryBar, b);
                    if (fromBar >= 0 && Math.Abs(fromBar - a) > 2) a = fromBar;
                }

                if (j.MasteryRank >= 3 && !(b == 100 && a >= 100) && (f == null || card == null || card.BarGuessed || MasteryFromBar(f, card.MasteryBar, 100) >= 97)) return;
                if (b == 100 && a >= 100) { j.MasteryRank = 3; j.MasteryCur = j.MasteryGoal = -1; return; }

                if (rank == j.MasteryRank && b == j.MasteryGoal && a < j.MasteryCur) a = j.MasteryCur;
                j.MasteryRank = rank; j.MasteryCur = a; j.MasteryGoal = b;
                return;
            }

            if (u.Contains("GOLD") && !u.Contains("SILVER") && !u.Contains("BRONZE")
                && (f == null || card == null || (card.BarGuessed ? Parse.Key(text).Contains("GOIDMASTERY") : MasteryFromBar(f, card.MasteryBar, 100) >= 97)))
            { j.MasteryRank = 3; j.MasteryCur = j.MasteryGoal = -1; return; }
            CountFromBar(j, f, card);
        }

        static void CountFromBar(JobInfo j, Frame f, JobCard card)
        {
            if (f == null || card == null || card.BarGuessed || j.MasteryRank < 0 || j.MasteryRank >= 3 || j.MasteryGoal <= 0) return;
            int fromBar = MasteryFromBar(f, card.MasteryBar, j.MasteryGoal);
            if (fromBar > j.MasteryCur && fromBar < j.MasteryGoal) j.MasteryCur = fromBar;
        }

        internal static string MasteryWording(string label)
        {
            return string.Join(" ", Regex.Matches(label ?? "", "[A-Za-z]{3,}").Cast<Match>().Select(m => m.Value.ToUpperInvariant()));
        }

        static int MasteryFromBar(Frame f, Rectangle bar, int goal)
        {
            if (bar.Width < 20) return -1;
            double best = 0;
            foreach (int y in new[] { bar.Top + 1, bar.Bottom - 2 })
            {
                int hit = 0, n = 0;
                for (int x = bar.Left + 3; x <= bar.Right - 3; x += 3)
                {
                    var c = f.Pixel(x, y);
                    n++;
                    if (c.R >= 100 && c.R - c.B >= 60) hit++;
                }
                if (n > 0) best = Math.Max(best, (double)hit / n);
            }
            return (int)Math.Round(best * goal);
        }

        public List<CityHeader> ReadCityHeaders(Frame f, List<OcrLine> lines = null)
        {
            var area = JobsArea(f);
            var result = new List<CityHeader>();
            foreach (var l in lines ?? Ocr.Read(f, area, 2, Prep.None))
            {
                int ci = GameData.CityIndex(l.Text);
                if (ci < 0) continue;

                string k = Parse.Key(l.Text);
                int cut = k.IndexOf("IEVEI");
                if (cut > 0) k = k.Substring(0, cut);
                if (k.Length > Parse.Key(GameData.Cities[ci]).Length + 3) continue;
                int y = l.CenterY, bestStart = -1, bestLen = 0, runStart = -1;
                for (int x = area.Right - 2; x > l.Box.Right + 40; x--)
                {
                    if (GoldIsh(f, x, y)) { if (runStart < 0) runStart = x; }
                    else if (runStart >= 0)
                    {
                        int len = runStart - x;
                        if (len > bestLen) { bestLen = len; bestStart = runStart; }
                        runStart = -1;
                    }
                }
                if (bestLen < 6) continue;
                int cx = bestStart - bestLen / 2;
                bool plus = GoldIsh(f, cx, y - (int)(bestLen * 0.35)) || GoldIsh(f, cx, y - (int)(bestLen * 0.25));
                result.Add(new CityHeader { Index = ci, Y = l.Box.Y, Collapsed = plus, ButtonAt = new Point(cx, y) });
            }
            return result;
        }

        public bool ExpandCity(int cityIndex)
        {
            JobsTop();
            MeasureNotch(new List<JobInfo>());
            string lastSeen = null;
            int notches = ListNotches(6);
            for (int page = 0, pages = 150 / notches + 1; page < pages; page++)
            {
                List<CityHeader> heads;
                Rectangle area;
                string sig;
                using (var f = Capture())
                {
                    area = JobsArea(f);
                    var lines = Ocr.Read(f, area, 2, Prep.None);
                    heads = ReadCityHeaders(f, lines);
                    sig = string.Join(",", lines.Select(x => x.Text + "@" + x.Box.Y));
                }
                foreach (var h in heads)
                {
                    if (h.Index != cityIndex || h.ButtonAt.Y > area.Bottom - 20) continue;
                    if (!h.Collapsed) return true;
                    log("Opening " + GameData.Cities[cityIndex] + " in the Jobs list");
                    Click(h.ButtonAt.X, h.ButtonAt.Y, 900);
                    jobScroll.Clear();
                    return true;
                }
                if (page > 0 && sig == lastSeen) break;
                lastSeen = sig;
                ScrollPage(0.54, -notches);
            }
            return false;
        }

        public readonly List<string> JobsSeen = new List<string>();

        public JobCard FindJob(JobInfo job, List<JobInfo> catalog)
        {

            using (var f = Capture())
            {
                var area = JobsArea(f);
                foreach (var c in ReadJobCards(f, catalog, false))
                    if (c.Info == job && c.NameY >= area.Top && c.NameY <= area.Top + 0.6 * area.Height) return c;
            }
            int remembered;
            if (jobScroll.TryGetValue(job.Name, out remembered))
            {
                JobsTop();
                if (remembered > 0) ScrollPage(0.54, -remembered);
                using (var f = Capture())
                    foreach (var c in ReadJobCards(f, catalog, false))
                        if (c.Info == job) return c;
            }
            JobsTop();
            MeasureNotch(catalog);
            int pos = 0;
            string lastSig = null;
            JobCard low = null;
            JobsSeen.Clear();
            for (int page = 0; page < 60; page++)
            {
                List<JobCard> cards;
                Rectangle area;
                using (var f = Capture()) { cards = ReadJobCards(f, catalog, false); area = JobsArea(f); }
                foreach (var c in cards) if (!JobsSeen.Contains(c.Name)) JobsSeen.Add(c.Name);
                low = null;
                foreach (var c in cards)
                {
                    if (c.Info != job) continue;
                    if (c.NameY > area.Top + 0.6 * area.Height) { low = c; break; }
                    jobScroll[job.Name] = pos;
                    return c;
                }
                string sig = string.Join(",", cards.Select(x => x.Name));
                if (page > 0 && sig == lastSig) break;
                lastSig = sig;
                int step = low != null ? 2 : ListNotches(4);
                ScrollPage(0.54, -step);
                pos += step;
            }
            if (low != null) jobScroll[job.Name] = pos;
            return low;
        }

        public int DoJob(JobCard card, int maxTimes, List<JobInfo> catalog = null)
        {
            int done = 0;
            bool nudged = false;
            var nameArea = card.NameBox;
            nameArea.Inflate(card.NameBox.Height, card.NameBox.Height / 3);
            for (int i = 0; i < maxTimes; i++)
            {
                bool ready = false, moved = false;
                for (int look = 0; look < 8 && !ready && !moved; look++)
                {
                    using (var f = Capture())
                    {
                        if (GoldIsh(f, card.StateAt.X, card.StateAt.Y))
                        {
                            var name = Ocr.ReadText(f, nameArea, 2, Prep.None);
                            if (card.Info != null && MatchJob(name, new List<JobInfo> { card.Info }) == null) moved = true;
                            else ready = true;
                        }
                        else if (look >= 2 && IsDim(f, card.StateAt.X, card.StateAt.Y)) break;
                    }
                    if (!ready && !moved) Wait(250);
                }
                if (moved && !nudged && catalog != null && card.Info != null)
                {

                    nudged = true;
                    ScrollPage(0.54, 1);
                    Wait(300);
                    JobCard again;
                    using (var f = Capture()) again = ReadJobCards(f, catalog, false).FirstOrDefault(c => c.Info == card.Info);
                    if (again != null)
                    {
                        card = again;
                        nameArea = card.NameBox;
                        nameArea.Inflate(card.NameBox.Height, card.NameBox.Height / 3);
                        jobScroll.Remove(card.Info.Name);
                        i--;
                        continue;
                    }
                }
                if (moved) { log("The job list moved - looking again next time"); NoteFailure("the job list moved under the job"); break; }
                if (!ready) break;
                Press(card.ButtonText, card.ButtonAt.X, card.ButtonAt.Y, 450);
                done++;
            }
            return done;
        }

        public int LearnJobs(List<JobInfo> catalog, int level, bool openNewCities)
        {
            if (level > 0)
                for (int ci = 0; ci < GameData.Cities.Length; ci++)
                {
                    if (GameData.CityLevels[ci] > level) continue;

                    if (openNewCities || catalog.Any(j => GameData.CityIndex(j.City) == ci)) ExpandCity(ci);
                }
            JobsTop();
            MeasureNotch(catalog);
            var seen = new HashSet<JobInfo>();
            string lastSig = null, city = GameData.Cities[0];
            int tier = 1, tierLevel = 1, step = ListNotches(6);
            bool reachedEnd = false, saidPast = false;
            List<string> lastNames = null;
            for (int page = 0; page < 60; page++)
            {
                List<OcrLine> lines;
                List<JobCard> cards;
                var frame = Capture();
                try
                {
                    lines = Ocr.Read(frame, JobsArea(frame), 2, Prep.None);
                    cards = CardsFromLines(frame, lines, catalog, true);

                    var events = new List<KeyValuePair<int, object>>();
                    foreach (var l in lines)
                        if (GameData.CityIndex(l.Text) >= 0 || Parse.Has(l.Text, "TIER")) events.Add(new KeyValuePair<int, object>(l.Box.Y, l));
                    foreach (var c in cards) events.Add(new KeyValuePair<int, object>(c.NameY, c));
                    foreach (var e in events.OrderBy(x => x.Key))
                    {
                        var l = e.Value as OcrLine;
                        if (l != null)
                        {
                            int ci = GameData.CityIndex(l.Text);
                            if (ci >= 0) { city = GameData.Cities[ci]; continue; }
                            var tm = Regex.Match(l.Text, @"TIER\s*([0-9IlO]+).*?LEVEL\s*([0-9IlOS]+)", RegexOptions.IgnoreCase);
                            int t, tl;
                            if (tm.Success && int.TryParse(Parse.Digits(tm.Groups[1].Value), out t) && int.TryParse(Parse.Digits(tm.Groups[2].Value), out tl))
                            {
                                tier = t;
                                tierLevel = tl >= 200 ? tl / 10 : tl;
                            }
                            continue;
                        }
                        var c = (JobCard)e.Value;
                        if (c.Info != null)
                        {

                            int here = GameData.CityIndex(city), its = GameData.CityIndex(c.Info.City);
                            if (its < here) c.Info = MatchJob(c.Name, c.Cost, here, catalog);
                            else if (its > here) city = GameData.Cities[its];
                        }
                        if (c.Info == null)
                        {

                            if (c.Cost <= 0 || c.Xp <= 0 || !Regex.IsMatch(c.Name, @"^[A-Za-z][A-Za-z' \-]+$") || c.Name.Split(' ').Count(w => w.Length >= 2) < 3) continue;
                            c.Info = new JobInfo { City = city, Tier = tier, TierLevel = tierLevel, Name = c.Name, Cost = c.Cost, Xp = c.Xp, Order = 100000 + catalog.Count };
                            catalog.Add(c.Info);
                            log(string.Format("New job found: \"{0}\" ({1}, {2} energy, {3} XP)", c.Name, city, c.Cost, c.Xp));
                        }
                        else
                        {
                            if (c.Cost > 0 && (c.Info.Cost <= 0 || Math.Abs(c.Cost - c.Info.Cost) * 10 <= c.Info.Cost * 3)) c.Info.Cost = c.Cost;
                            if (c.Xp > 0 && (c.Info.Xp <= 0 || Math.Abs(c.Xp - c.Info.Xp) * 10 <= c.Info.Xp * 3)) c.Info.Xp = c.Xp;
                        }

                        var lm = Regex.Match(c.ButtonText, @"LEVEL\s*([0-9OIl]+)", RegexOptions.IgnoreCase);
                        int need;
                        if (lm.Success && int.TryParse(Parse.Digits(lm.Groups[1].Value), out need) && GameData.InCityLevels(c.Info.City, need))
                            c.Info.TierLevel = Math.Max(c.Info.TierLevel, need);
                        ApplyMastery(c.Info, c.Mastery, frame, c);
                        seen.Add(c.Info);
                    }
                }
                finally { frame.Dispose(); }
                string sig = string.Join(",", cards.Select(x => x.Name));
                if (page > 0 && sig == lastSig) { reachedEnd = true; break; }
                lastSig = sig;

                var names = cards.Select(x => x.Info != null ? x.Info.Name : x.Name).ToList();
                if (lastNames != null && lastNames.Count > 0 && names.Count > 0 && !names.Any(lastNames.Contains) && step > 1)
                {
                    step--;
                    if (!saidPast) { saidPast = true; log("Jobs list: two looks in a row had no job in common - rolling it by less for the rest of this read"); }
                }
                lastNames = names;
                ScrollPage(0.54, -step);
            }
            jobScroll.Clear();

            if (seen.Count > 0)
            {
                int last = seen.Max(j => GameData.CityIndex(j.City));
                foreach (var j in catalog)
                {
                    int c = GameData.CityIndex(j.City);
                    if (seen.Contains(j)) j.Unseen = 0;
                    else if (seen.Any(x => GameData.CityIndex(x.City) == c) && (c < last || reachedEnd)) j.Unseen++;
                }
            }
            return seen.Count;
        }

        public Rectangle PageArea(Frame f)
        {
            var v = ViewOf(f);
            return v.Problem == null ? v.Page : new Rectangle(260, 118, f.Width - 260, f.Height - 118);
        }

        int PageScale(Frame f)
        {
            var v = ViewOf(f);
            return v.Problem == null ? View.ScaleFor(14 * v.HeaderScale) : 2;
        }

        public int ReadFavors(Frame f, int zoom = 0)
        {
            var p = PageArea(f);
            var top = new Rectangle(p.Left + p.Width / 2, p.Top, p.Width / 2, Math.Max(40, p.Height / 5));
            var line = Ocr.Read(f, top, PageScale(f) + zoom, Prep.None).FirstOrDefault(l => Parse.Has(l.Text, "FAVOR"));
            return line == null ? -1 : FavorsIn(line.Text);
        }

        internal static int FavorsIn(string text)
        {
            text = text ?? "";
            int at = text.IndexOf("FAVOR", StringComparison.OrdinalIgnoreCase);
            if (at < 0 || Regex.IsMatch(text.Substring(0, at), @"\d")) return -1;
            var m = Regex.Match(text.Substring(at), @"LEFT\W*([0-9]{1,3})(?![0-9])", RegexOptions.IgnoreCase);
            int n;
            if (m.Success && int.TryParse(m.Groups[1].Value, out n)) return n <= 99 ? n : -1;
            return Regex.IsMatch(text, @"LEFT\W*[OoD]\s*$", RegexOptions.IgnoreCase) ? 0 : -1;
        }

        public List<HeistRow> ReadHeists(Frame f)
        {
            var lines = Ocr.Read(f, PageArea(f), PageScale(f), Prep.None);
            ReadLostHelpButtons(f, lines);
            var rows = new List<HeistRow>();
            foreach (var b in lines.Where(l => Parse.Key(l.Text).Contains("HEIPOUT") || Parse.Key(l.Text).Contains("HEIPED")).OrderBy(l => l.Box.Y))
            {
                int h = Math.Max(8, b.Box.Height);
                var r = new HeistRow { HelpText = b.Text, HelpAt = new Point(b.CenterX, b.CenterY), Top = b.Box.Y };

                var name = lines.Where(l => l.Box.Right < b.Box.X && l.CenterY >= b.Box.Y - 1.5 * h && l.CenterY <= b.CenterY + 0.4 * h
                                            && Regex.Matches(l.Text, "[A-Za-z]").Count >= 6 && !Parse.Has(l.Text, "ENDS") && !Parse.Has(l.Text, "LEVEL"))
                                .OrderByDescending(l => l.Box.X).FirstOrDefault();
                if (name != null) r.Name = name.Text;

                var xp = lines.FirstOrDefault(l => Parse.Has(l.Text, "XP") && l.CenterY > b.Box.Bottom && l.CenterY < b.Box.Bottom + 4 * h
                                                   && l.CenterX > b.Box.X - b.Box.Width && l.CenterX < b.Box.Right + b.Box.Width / 3);

                r.XpPerHelp = KnownHeistXp(r.Name);
                var xm = xp != null ? Regex.Match(xp.Text, @"(\d[\d,]*)\s*XP", RegexOptions.IgnoreCase) : Match.Empty;
                if (r.XpPerHelp < 0 && xm.Success) r.XpPerHelp = int.Parse(xm.Groups[1].Value.Replace(",", ""));

                var en = lines.Where(l => Parse.Has(l.Text, "ENERGY") && l.CenterY > b.Box.Bottom && l.CenterY < b.Box.Bottom + 3 * h
                                          && l.Box.Right > b.Box.X - h && l.Box.X < b.Box.Right).OrderBy(l => l.Box.X).FirstOrDefault();
                var em = en != null ? Regex.Match(en.Text, @"([0-9]{1,2})\s*ENERGY", RegexOptions.IgnoreCase) : Match.Empty;
                if (em.Success) r.Energy = int.Parse(em.Groups[1].Value);
                string k = Parse.Key(r.HelpText);
                r.CanHelp = k.Contains("HEIPOUT") && !k.Contains("HEIPED") && GoldIsh(f, b.Box.X - Math.Max(4, h / 2), b.CenterY);

                var n = Regex.Match(k, @"HEIPOUT([0-9IOS]{1,2})OF([0-9IOS]{1,2})");
                if (n.Success && HelpCount(n.Groups[1].Value) >= HelpCount(n.Groups[2].Value) && HelpCount(n.Groups[2].Value) > 0) r.CanHelp = false;

                r.YourFamily = lines.Any(l => l.Box.Right < b.Box.X && l.CenterY > b.Box.Y - h && l.CenterY < b.Box.Bottom + 2.2 * h && Parse.Has(l.Text, "YOUR FAMILY"));
                rows.Add(r);
            }
            return rows;
        }

        static int HelpCount(string folded)
        {
            int v;
            return int.TryParse(folded.Replace('I', '1').Replace('O', '0').Replace('S', '5'), out v) ? v : -1;
        }

        void ReadLostHelpButtons(Frame f, List<OcrLine> lines)
        {
            var chips = lines.Where(l => Parse.Has(l.Text, "ENERGY")).ToList();
            foreach (var chip in chips)
            {

                if (chips.Any(c => c != chip && Math.Abs(c.CenterY - chip.CenterY) < chip.Box.Height && c.Box.X < chip.Box.X)) continue;
                int ch = Math.Max(8, chip.Box.Height);
                bool button = lines.Any(l => l.CenterY < chip.Box.Y && l.CenterY > chip.Box.Y - 3.5 * ch && Math.Abs(l.Box.X - chip.Box.X) < 6 * ch
                                             && (Parse.Key(l.Text).Contains("HEIPOUT") || Parse.Key(l.Text).Contains("HEIPED") || Parse.Has(l.Text, "LEVEL")));
                if (button) continue;

                var sab = lines.FirstOrDefault(l => Parse.Has(l.Text, "SABOTAGE") && Math.Abs(l.CenterY - (chip.Box.Y - 1.8 * ch)) < 1.5 * ch && l.Box.X > chip.Box.Right);
                int left = chip.Box.X - 2 * ch, right = sab != null ? sab.Box.X - ch : chip.Box.X + 14 * ch;
                var box = Rectangle.Intersect(new Rectangle(left, chip.Box.Y - (int)(3.4 * ch), right - left, (int)(3.1 * ch)), new Rectangle(0, 0, f.Width, f.Height));
                if (box.Width < 4 * ch || box.Height < ch) continue;
                foreach (var prep in new[] { Prep.Contrast, Prep.DarkText })
                {
                    var read = Ocr.Read(f, box, 2, prep).FirstOrDefault(l => Parse.Key(l.Text).Contains("HEIPOUT"));
                    if (read == null) continue;
                    lines.Add(read);
                    break;
                }
            }
        }

        static int KnownHeistXp(string name)
        {
            if (Parse.Has(name, "VAULT")) return 140;
            if (Parse.Has(name, "MUSEUM") || Parse.Has(name, "AFTERDARK")) return 60;
            if (Parse.Has(name, "TRUCK")) return 25;
            if (Parse.Has(name, "CORNER") || Parse.Has(name, "STORE")) return 12;
            return -1;
        }

        public void HelpHeist(HeistRow r) { Press(r.HelpText, r.HelpAt.X, r.HelpAt.Y, 1100); CheckForPurchaseWindow(); }
        public void HeistsTop() { ScrollPage(0.71, 10); }

        public bool Hospitalized(Frame f)
        {
            var p = PageArea(f);
            var right = Rectangle.FromLTRB(p.Left + (int)(p.Width * 0.6), p.Top, p.Right, p.Top + p.Height / 3);
            string text = string.Join(" ", Ocr.Read(f, right, PageScale(f), Prep.None).Select(l => l.Text));
            return Parse.Has(text, "HOSPITALIZED") || Parse.Has(text, "HEAL BEFORE FIGHTING");
        }

        public List<FightRow> ReadFightRows(Frame f) { bool war; return ReadFightRows(f, out war); }

        public List<FightRow> ReadFightRows(Frame f, out bool war)
        {
            var p = PageArea(f);
            var list = new Rectangle(p.Left, p.Top, (int)(p.Width * 0.62), p.Height);
            int scale = PageScale(f);
            var lines = Ocr.Read(f, list, scale, Prep.None);
            var rows = new List<FightRow>();
            war = false;

            if (lines.Any(l => ProfileTitleRx.IsMatch(l.Text))) return rows;
            int i = 0;
            foreach (var b in lines.Where(l => Parse.Key(l.Text) == "ATTACK").OrderBy(l => l.Box.Y))
            {
                int h = Math.Max(8, b.Box.Height);
                var r = new FightRow { Index = i++, AttackAt = new Point(b.CenterX, b.CenterY), AttackText = b.Text, Ready = f.Near(b.Box.X - Math.Max(4, h / 2), b.CenterY, RED, 30) };
                OcrLine nameLine = null;
                bool costRead = false;
                var warLabels = new List<OcrLine>();
                foreach (var l in lines)
                {
                    if (l == b || l.Box.Right > b.Box.X || l.CenterY < b.Box.Y - 1.3 * h || l.CenterY > b.Box.Bottom + 1.3 * h) continue;
                    if (Parse.Has(l.Text, "WAR TARGET")) warLabels.Add(l);

                    if (l.CenterY < b.CenterY + 0.3 * h && Parse.Key(l.Text).StartsWith("NOFAMI")) r.NoFamily = true;

                    if (l.CenterY < b.CenterY + 0.3 * h && !Regex.IsMatch(l.Text, @"L\s?eve|Steal|Stamina|WAR\s*TARGET", RegexOptions.IgnoreCase)
                        && (nameLine == null || l.Box.X < nameLine.Box.X)) nameLine = l;

                    string plain = Regex.Replace(l.Text, @"[^A-Za-z0-9% ]", "");
                    var m = Regex.Match(plain, @"L\s?eve(?>[l1I]?)\s?([0-9OoIlSB]{1,3})[0O]?\b", RegexOptions.IgnoreCase);
                    int lv;
                    if (m.Success && Regex.IsMatch(m.Groups[1].Value, @"\d") && int.TryParse(Parse.Digits(m.Groups[1].Value), out lv)) r.Level = lv;
                    m = Regex.Match(l.Text, @"Steal\s*([0-9]+)\s*%", RegexOptions.IgnoreCase);
                    if (m.Success) r.Steal = int.Parse(m.Groups[1].Value);
                    m = Regex.Match(l.Text, @"([1-9])\s*Stamina", RegexOptions.IgnoreCase);
                    if (m.Success) { r.StaminaCost = int.Parse(m.Groups[1].Value); costRead = true; }
                }

                var under = lines.Where(l => l.Box.Right <= b.Box.X && l.CenterY >= b.Box.Y && l.CenterY <= b.Box.Bottom + 1.3 * h
                                             && Regex.IsMatch(l.Text, @"L\s?eve|Steal|Stamina|WAR\s*TARGET", RegexOptions.IgnoreCase)).ToList();

                var chip = costRead ? null : under.FirstOrDefault(l => Regex.IsMatch(l.Text, "Stamina", RegexOptions.IgnoreCase));
                if (chip != null)
                {
                    int ch = Math.Max(8, chip.Box.Height);
                    var strip = Rectangle.FromLTRB(Math.Max(list.Left, under.Min(l => l.Box.X) - h), chip.Box.Y - ch / 2, b.Box.X - 2, chip.Box.Bottom + ch / 2);
                    var cost = Ocr.Read(f, strip, scale + 1, Prep.None).Select(l => Regex.Match(l.Text, @"([1-9])\s*Stamina", RegexOptions.IgnoreCase))
                                  .FirstOrDefault(x => x.Success);
                    if (cost != null) r.StaminaCost = int.Parse(cost.Groups[1].Value);
                }
                double nameMaxX = under.Count > 0 ? under.Min(l => l.Box.X) + 3 * h : list.Left + 0.45 * (b.Box.X - list.Left);
                if (nameLine != null && nameLine.Box.X > nameMaxX) nameLine = null;
                if (nameLine != null) { r.Name = nameLine.Text.Trim(); r.NameAt = new Point(nameLine.CenterX, nameLine.CenterY); }

                double below = (nameLine != null ? nameLine.CenterY : b.Box.Y - 0.5 * h) + 0.5 * h;
                r.War = warLabels.Any(l => l.CenterY > below && l.CenterY >= b.Box.Y);
                rows.Add(r);
            }

            var levels = rows.Where(r => r.Level > 0).Select(r => r.Level).OrderBy(x => x).ToList();
            if (levels.Count >= 3)
            {
                int median = levels[levels.Count / 2];
                foreach (var r in rows) if (r.Level > 0 && r.Level * 2 < median && median - r.Level >= 30) r.Level = -1;
            }

            war = rows.Any(r => r.War) || lines.Any(l => { string k = Parse.Key(l.Text); return k == "WARTARGETS" || k.StartsWith("WARTARGETSONI"); });
            return rows;
        }

        public void Attack(FightRow r) { Press(r.AttackText, r.AttackAt.X, r.AttackAt.Y, 400); }

        const string RefreshArrow = "the Fight page's round arrow";

        static bool Goldish(Color c) { return c.R >= 150 && c.G >= 115 && c.B <= 125 && c.R >= c.G && c.R - c.B >= 80; }

        static bool GoldTinted(Color c) { return c.R >= 90 && c.G >= 0.6 * c.R && c.G <= c.R + 5 && c.R - c.B >= 40; }

        internal static Rectangle FightRefreshBox(Frame f, IEnumerable<OcrLine> words)
        {
            foreach (var s in words.Where(o => Parse.Key(o.Text) == "SEARCH").OrderByDescending(o => o.Box.X))
            {
                var box = RefreshAbove(f, s);
                if (!box.IsEmpty) return box;
            }
            return Rectangle.Empty;
        }

        static Rectangle RefreshAbove(Frame f, OcrLine search)
        {
            int th = Math.Max(6, search.Box.Height), y = search.CenterY;

            int left = -1;
            for (int x = search.Box.Right + 1, run = 0; x < f.Width - 1; x++)
            {
                if (!Goldish(f.Pixel(x, y))) { run = 0; continue; }
                if (++run >= th) { left = x - run + 1; break; }
            }
            if (left < 0) return Rectangle.Empty;

            int col = left + 2, top = y, bottom = y;
            while (y - top < 3 * th && Goldish(f.Pixel(col, top - 1))) top--;
            while (bottom - y < 3 * th && Goldish(f.Pixel(col, bottom + 1))) bottom++;
            int bh = bottom - top + 1;
            if (bh < th || bh > 4 * th) return Rectangle.Empty;
            int row = top + Math.Max(2, bh / 8), right = col;
            while (right + 1 < f.Width && Goldish(f.Pixel(right + 1, row))) right++;
            if (right - left + 1 < 2.5 * bh) return Rectangle.Empty;

            var area = Rectangle.FromLTRB(Math.Max(0, right - 2 * bh), Math.Max(0, top - 2 * bh), Math.Min(f.Width - 1, right + bh / 2), top - 1);
            int l = int.MaxValue, t = int.MaxValue, r = -1, b = -1;
            for (int yy = area.Top; yy <= area.Bottom; yy++)
                for (int xx = area.Left; xx <= area.Right; xx++)
                    if (GoldTinted(f.Pixel(xx, yy))) { l = Math.Min(l, xx); t = Math.Min(t, yy); r = Math.Max(r, xx); b = Math.Max(b, yy); }
            if (r < 0) return Rectangle.Empty;
            var box = Rectangle.FromLTRB(l, t, r + 1, b + 1);
            int w = box.Width, h = box.Height;
            if (w < 0.6 * bh || w > 1.6 * bh || h < 0.6 * bh || h > 1.6 * bh || Math.Abs(w - h) > 0.25 * Math.Max(w, h) + 2) return Rectangle.Empty;
            if (Math.Abs(box.Right - 1 - right) > 0.3 * bh + 3) return Rectangle.Empty;

            int hitT = 0, hitB = 0, hitL = 0, hitR = 0;
            for (int xx = box.Left; xx < box.Right; xx++)
            {
                if (GoldInward(f, xx, box.Top, 0, 1)) hitT++;
                if (GoldInward(f, xx, box.Bottom - 1, 0, -1)) hitB++;
            }
            for (int yy = box.Top; yy < box.Bottom; yy++)
            {
                if (GoldInward(f, box.Left, yy, 1, 0)) hitL++;
                if (GoldInward(f, box.Right - 1, yy, -1, 0)) hitR++;
            }
            if (Math.Min(hitT, hitB) * 10 < w * 7 || Math.Min(hitL, hitR) * 10 < h * 7) return Rectangle.Empty;

            int inset = Math.Max(2, Math.Min(w, h) / 5), dark = 0, goldIn = 0, n = 0;
            for (int yy = box.Top + inset; yy < box.Bottom - inset; yy++)
                for (int xx = box.Left + inset; xx < box.Right - inset; xx++)
                {
                    var c = f.Pixel(xx, yy);
                    n++;
                    if (Goldish(c)) goldIn++;
                    else if ((c.R + c.G + c.B) / 3 < 80) dark++;
                }
            if (n == 0 || dark * 10 < n * 4 || goldIn * 100 < n * 3 || goldIn * 10 > n * 6) return Rectangle.Empty;
            return box;
        }

        static bool GoldInward(Frame f, int x, int y, int dx, int dy)
        {
            for (int k = 0; k < 3; k++) if (GoldTinted(f.Pixel(x + k * dx, y + k * dy))) return true;
            return false;
        }

        public Rectangle FightRefreshIn(Frame f)
        {
            var p = PageArea(f);
            var top = Rectangle.FromLTRB(p.Left, p.Top, p.Right, p.Top + p.Height / 4);
            var box = FightRefreshBox(f, Ocr.ReadWords(f, top, PageScale(f), Prep.None));

            if (box.IsEmpty) box = FightRefreshByButton(f);
            return box;
        }

        internal Rectangle FightRefreshByButton(Frame f)
        {
            var p = PageArea(f);
            var top = Rectangle.FromLTRB(p.Left, p.Top, p.Right, p.Top + p.Height / 4);
            foreach (var w in Ocr.ReadWords(f, top, PageScale(f), Prep.DarkText).Where(o => Parse.Key(o.Text) == "SEARCH").OrderByDescending(o => o.Box.X))
            {

                int x = w.Box.X - 1;
                while (x > 0 && Goldish(f.Pixel(x, w.CenterY))) x--;
                var box = RefreshAbove(f, new OcrLine { Text = w.Text, Box = new Rectangle(Math.Max(0, x - 3), w.Box.Y, 1, w.Box.Height) });
                if (!box.IsEmpty) return box;
            }
            return Rectangle.Empty;
        }

        public bool RefreshFightList()
        {
            Rectangle box;
            using (var f = Capture()) box = FightRefreshIn(f);
            if (box.IsEmpty) return false;
            Press(RefreshArrow, box.X + box.Width / 2, box.Y + box.Height / 2, 900);
            return true;
        }

        public sealed class PlayerProfile
        {
            public string Name = "";
            public int Respect = -1, Attack = -1, Defense = -1, Crew = -1;
            public OcrLine Title, Scout;
            public Point Close;
            public bool Family;
            public bool Read { get { return Attack >= 0 && Defense >= 0; } }
        }

        static readonly Regex ProfileTitleRx = new Regex(@"^\s*(PLAYER|FAMI[LI1]Y)\s*PROFI[LI1]E\s*[:;.]?\s*(.*)$", RegexOptions.IgnoreCase);

        public PlayerProfile ReadPlayerProfile(Frame f)
        {
            var popup = PopupArea(f);
            int scale = PopupScale(f);
            var lines = Ocr.Read(f, popup, scale, Prep.None);
            var title = lines.FirstOrDefault(l => ProfileTitleRx.IsMatch(l.Text));
            if (title == null) return null;
            var tm = ProfileTitleRx.Match(title.Text);
            int h = Math.Max(8, title.Box.Height);

            string rest = "";
            var whole = title.Box;
            foreach (var l in lines.Where(l => l != title && l.Box.X > title.Box.Right && Math.Abs(l.CenterY - title.CenterY) <= 0.5 * h
                                               && !Regex.IsMatch(l.Text.Trim(), "^[xX×]$")).OrderBy(l => l.Box.X))
            {
                if (l.Box.X - whole.Right > 2 * h) break;
                rest += " " + l.Text.Trim();
                whole = Rectangle.Union(whole, l.Box);
            }
            if (rest.Length > 0) title = new OcrLine { Text = title.Text + rest, Box = whole };
            var p = new PlayerProfile { Title = title, Family = !tm.Groups[1].Value.ToUpperInvariant().StartsWith("P") };

            p.Name = Regex.Replace(tm.Groups[2].Value + rest, @"\s*\(\s*@.*$", "").Trim();
            var x = lines.Where(l => Regex.IsMatch(l.Text.Trim(), "^[xX×]$") && l.Box.X > title.Box.Right && Math.Abs(l.CenterY - title.CenterY) <= 1.5 * h)
                         .OrderBy(l => l.Box.X).FirstOrDefault();
            p.Close = x != null ? new Point(x.CenterX, x.CenterY) : SkillWindowX(f, title, popup);

            if (p.Close.IsEmpty)
            {
                int mirror = f.Width - title.Box.X;
                var near = new OcrLine { Text = title.Text, Box = Rectangle.FromLTRB(title.Box.X, title.Box.Y, Math.Max(title.Box.Right, mirror - 4 * h), title.Box.Bottom) };
                p.Close = SkillWindowX(f, near, popup);
            }
            int left = title.Box.X - 3 * h, right = p.Close.IsEmpty ? popup.Right : p.Close.X + 2 * h;
            var inside = lines.Where(l => l != title && l.Box.X >= left && l.Box.Right <= right && l.Box.Y > title.Box.Bottom).ToList();

            var stats = inside.FirstOrDefault(l => Parse.Key(l.Text) == "STATS");
            Func<string, string, OcrLine> labelOf = (label, first) => stats == null
                ? inside.FirstOrDefault(l => Parse.Key(l.Text).Contains(label))
                : inside.FirstOrDefault(l => l.Box.X >= stats.Box.X - h && l.Box.Y > stats.Box.Bottom && Parse.Key(l.Text).Contains(first));
            var attack = labelOf("ATTACKPOWER", "ATTACK");
            var defense = labelOf("DEFENSEPOWER", "DEFENSE");

            Func<OcrLine, int> value = lab =>
            {
                if (lab == null) return -1;
                int v;
                var own = Regex.Match(lab.Text, @":\s*([0-9][0-9,.]*\s*[KMB]?)\s*$", RegexOptions.IgnoreCase);
                if (own.Success && (v = ProfileNumber(own.Groups[1].Value)) >= 0) return v;
                int lh = Math.Max(8, lab.Box.Height);
                var num = inside.Where(l => l != lab && l.Box.X > lab.Box.Right && Math.Abs(l.CenterY - lab.CenterY) <= 0.7 * lh)
                                .OrderBy(l => l.Box.X).FirstOrDefault();
                return num != null ? ProfileNumber(num.Text) : -1;
            };
            p.Respect = value(labelOf("RESPECT", "RESPECT"));
            p.Attack = value(attack);
            p.Defense = value(defense);
            p.Crew = value(labelOf("CREWSIZE", "CREW"));
            p.Scout = inside.FirstOrDefault(l => Parse.Key(l.Text).StartsWith("SCOUT"));

            if (p.Scout == null && !p.Read && attack != null && defense != null)
            {
                int lh = Math.Max(8, attack.Box.Height);
                var area = Rectangle.FromLTRB(Math.Max(attack.Box.Right, defense.Box.Right) + lh / 2, attack.Box.Y - lh / 2, right, defense.Box.Bottom + lh);
                foreach (var prep in new[] { Prep.None, Prep.DarkText })
                    if ((p.Scout = Ocr.Read(f, area, scale, prep).FirstOrDefault(l => Parse.Key(l.Text).StartsWith("SCOUT"))) != null) break;
            }

            if (p.Scout == null)
            {
                if (p.Attack < 0) p.Attack = ZoomedNumber(f, attack, right, scale);
                if (p.Defense < 0) p.Defense = ZoomedNumber(f, defense, right, scale);
            }
            return p;
        }

        static int ZoomedNumber(Frame f, OcrLine lab, int right, int scale)
        {
            if (lab == null) return -1;
            int lh = Math.Max(8, lab.Box.Height);
            if (right <= lab.Box.Right + lh) return -1;
            var strip = Rectangle.FromLTRB(lab.Box.Right + lh / 2, lab.Box.Y - lh / 2, right, lab.Box.Bottom + lh / 2);
            var seen = new List<int>();
            int empty = 0;
            foreach (var t in new[] { Tuple.Create(Math.Max(3, scale + 1), Prep.None), Tuple.Create(scale, Prep.WhiteBright), Tuple.Create(scale + 1, Prep.WhiteText),
                                      Tuple.Create(scale + 2, Prep.None), Tuple.Create(scale + 1, Prep.WhiteBright) })
            {
                var again = Ocr.Read(f, strip, t.Item1, t.Item2);
                if (again.Count == 0) { if (++empty == 2) break; continue; }
                empty = 0;
                int v = ProfileNumber(again.OrderBy(l => l.Box.Right).Last().Text);
                if (v < 0) continue;
                if (seen.Contains(v)) return v;
                seen.Add(v);
            }
            return -1;
        }

        internal static int ProfileNumber(string text)
        {
            var m = Regex.Match((text ?? "").Trim(), @"^([0-9]{1,3}(?:,[0-9]{3})*|[0-9]+)(?:\.([0-9]+))?\s*([KMB])?$", RegexOptions.IgnoreCase);
            if (!m.Success) return -1;
            double v = double.Parse(m.Groups[1].Value.Replace(",", "") + (m.Groups[2].Success ? "." + m.Groups[2].Value : ""), System.Globalization.CultureInfo.InvariantCulture);
            if (m.Groups[2].Success && !m.Groups[3].Success) return -1;
            switch (m.Groups[3].Success ? char.ToUpperInvariant(m.Groups[3].Value[0]) : ' ')
            {
                case 'K': v *= 1e3; break;
                case 'M': v *= 1e6; break;
                case 'B': v *= 1e9; break;
            }
            return v > int.MaxValue ? int.MaxValue : (int)v;
        }

        internal static bool ProfileNames(string profile, string meant)
        {
            string a = Parse.Key(profile), b = Parse.Key(meant);
            if (a.Length < 3 || b.Length < 3) return false;
            if (Math.Min(a.Length, b.Length) >= 5 && (a.StartsWith(b) || b.StartsWith(a))) return true;
            return a == b || View.Distance(a, b) <= Math.Max(1, Math.Min(a.Length, b.Length) / 4);
        }

        PlayerProfile LookAtProfile(int looks, Func<PlayerProfile, bool> done)
        {
            PlayerProfile p = null;
            for (int look = 0; look < looks; look++)
            {
                if (look > 0) Wait(300);
                using (var f = Capture())
                {
                    CheckForPurchasePrompt(Ocr.Read(f, PopupArea(f), PopupScale(f), Prep.None));
                    p = ReadPlayerProfile(f);
                }
                if (p != null && done(p)) return p;
            }
            return p;
        }

        public const string NoStaminaToScout = "no stamina for a scout";

        public const string NoProfile = "no profile - No Family, likely one of the game's computer players";

        public bool ProfileShowing()
        {
            using (var f = Capture()) return ReadPlayerProfile(f) != null;
        }

        public bool CloseProfile()
        {
            for (int t = 0; t < 3; t++)
            {
                PlayerProfile p;
                using (var f = Capture()) p = ReadPlayerProfile(f);
                if (p == null) return true;
                if (t == 2 || p.Close.IsEmpty) return false;
                Press("x", p.Close.X, p.Close.Y, 500);
            }
            return false;
        }

        public PlayerProfile ScoutPlayer(FightRow r, bool mayScout, out bool paid, out string why)
        {
            paid = false;
            why = null;
            if (r.Name.Length < 3 || r.NameAt.IsEmpty) { why = "its name didn't read"; return null; }
            if (NeverPress.Why(r.Name) != null) { why = "its name has a word the bot never presses"; return null; }
            Press(r.Name, r.NameAt.X, r.NameAt.Y, 600);

            var p = LookAtProfile(12, x => true);

            if (p == null && r.NoFamily) p = LookAtProfile(20, x => true);
            if (p == null)
            {

                if (r.NoFamily) { why = NoProfile; Snapshot("fight no profile no family", 0, true); }
                else { why = "no profile opened"; Snapshot("fight profile not opened", 60); }
                CloseProfile();
                return null;
            }
            try
            {
                if (p.Family) { why = "a family's profile opened"; Snapshot("fight family profile opened", 60); return null; }

                if (!ProfileNames(p.Name, r.Name)) p = LookAtProfile(20, x => x.Family || ProfileNames(x.Name, r.Name)) ?? p;
                if (p.Family) { why = "a family's profile opened"; Snapshot("fight family profile opened", 60); return null; }
                if (!ProfileNames(p.Name, r.Name)) { why = "the profile opened was someone else's"; Snapshot("fight profile not the player", 60); return null; }
                if (!p.Read && p.Scout == null) p = LookAtProfile(15, x => x.Read || x.Scout != null) ?? p;
                if (p.Read) return p;
                if (p.Scout == null) { why = "its numbers didn't load"; Snapshot("fight profile not loaded", 60); return null; }
                if (!mayScout) { why = NoStaminaToScout; return null; }
                Press(p.Scout, 700);
                paid = true;

                var after = LookAtProfile(15, x => x.Read);
                if (after == null || !after.Read) { why = "the numbers didn't read after SCOUT"; Snapshot("fight scout unreadable", 60); return null; }
                Snapshot("fight scouted", 0, true, false);
                return after;
            }
            finally
            {
                if (!CloseProfile()) { Snapshot("fight profile not closed", 60); if (why == null) why = "the profile didn't close"; }
            }
        }

        public PlayerProfile ReadOwnProfile()
        {
            Header h;
            using (var f = Capture()) h = ReadHeader(f);
            if (h == null || string.IsNullOrEmpty(h.Name) || h.NameBox.IsEmpty || NeverPress.Why(h.Name) != null) return null;
            Press(h.Name, h.NameBox.X + h.NameBox.Width / 2, h.NameBox.Y + h.NameBox.Height / 2, 600);
            var p = LookAtProfile(8, x => x.Read);
            if (p == null) { Snapshot("own profile not opened", 60); return null; }
            bool ok = p.Read && ProfileNames(p.Name, h.Name);
            if (!CloseProfile()) Snapshot("fight profile not closed", 60);
            return ok ? p : null;
        }

        public static bool Refused(string toast)
        {

            return toast != null && Regex.IsMatch(toast.ToUpperInvariant().Replace('0', 'O'),
                @"\b(NOT|CANNOT|CAN\W?T|ENOUGH|INSUFFICIENT|NO\s+(MORE\s+)?(FAVORS?|ENERGY|STAMINA|HEALTH)|OUT\s+OF\s+(FAVORS?|ENERGY|STAMINA|HEALTH)|COOL\s*DOWN|HOSPITALI[SZ]ED|MUST\s+RECOVER)\b");
        }

        public string LastFightRefusal;

        public string LastFightToast;

        public string FinishFight(int timeoutMs)
        {
            int end = Environment.TickCount + timeoutMs;
            bool skipped = false, sawWindow = false;
            LastFightRefusal = null;
            LastFightToast = null;
            while (end - Environment.TickCount > 0)
            {
                using (var f = Capture())
                {
                    var lines = Ocr.Read(f, PopupArea(f), PopupScale(f), Prep.None);
                    CheckForPurchasePrompt(lines);
                    var done = lines.FirstOrDefault(l => Parse.Key(l.Text) == "DONE");
                    if (done != null)
                    {
                        string all = string.Join(" ", lines.Select(l => l.Text)).ToUpperInvariant();
                        var rewards = lines.Where(l => l.Box.Bottom < done.Box.Y && l.Box.Y > done.Box.Y - 4 * done.Box.Height
                                                       && (l.Text.Contains("+") || l.Text.Contains("-"))).Select(l => l.Text);
                        Press(done, 700);
                        string outcome = all.Contains("VICTORY") ? "VICTORY" : all.Contains("DEFEAT") ? "DEFEAT" : "DONE";
                        string spoils = string.Join(" ", rewards).Trim();
                        return spoils.Length > 0 ? outcome + " (" + spoils + ")" : outcome;
                    }
                    var skip = lines.FirstOrDefault(l => Parse.Has(l.Text, "TAPTOSKIP"));
                    if (!skipped && skip != null)
                    {
                        sawWindow = true;
                        skipped = true;
                        Press(skip.Text, skip.CenterX, skip.CenterY - 3 * skip.Box.Height, 300);
                        continue;
                    }
                    string toast = sawWindow ? null : ToastIn(f);
                    if (toast != null) LastFightToast = toast;
                    if (Refused(toast)) { LastFightRefusal = toast; return null; }
                }
                Wait(250);
            }
            return null;
        }

        public OcrLine FindDepositAll(Frame f) { OcrLine w; return FindDepositAll(f, out w); }

        public OcrLine FindDepositAll(Frame f, out OcrLine withdraw)
        {
            withdraw = null;
            var area = PageArea(f);
            int s = PageScale(f);
            OcrLine card = null;
            foreach (var prep in new[] { Prep.None, Prep.WhiteText, Prep.DarkText })
            {
                var lines = Ocr.Read(f, area, s, prep);
                card = card ?? BankCard(lines);
                if (card == null) continue;
                foreach (var b in lines.Where(l => Parse.Key(l.Text) == "DEPOSITAII"))
                {
                    int h = Math.Max(8, b.Box.Height);
                    bool inCard = b.Box.Y > card.Box.Bottom && b.Box.X >= card.Box.X - 4 * h && b.Box.X < card.Box.Right;
                    var beside = lines.FirstOrDefault(w => Parse.Key(w.Text) == "WITHDRAWAII" && Math.Abs(w.CenterY - b.CenterY) <= h
                                                           && w.Box.X > b.Box.Right && w.Box.X - b.Box.Right < 2 * b.Box.Width);
                    if (inCard && beside != null) { withdraw = beside; return b; }
                }
            }
            return null;
        }

        static OcrLine BankCard(List<OcrLine> lines)
        {
            var title = lines.FirstOrDefault(l => { string k = Parse.Key(l.Text); return k.Contains("NATIONAIBANK") || View.Distance(k, "FIRSTNATIONAIBANK") <= 3; });
            if (title != null) return title;
            var onHand = lines.FirstOrDefault(l => Parse.Key(l.Text) == "ONHAND");
            if (onHand == null) return null;
            int h = Math.Max(8, onHand.Box.Height);
            return lines.Any(l => Parse.Key(l.Text) == "BANKED" && l.Box.Y > onHand.Box.Bottom && l.Box.Y < onHand.Box.Bottom + 4 * h
                                  && Math.Abs(l.Box.X - onHand.Box.X) < 2 * h) ? onHand : null;
        }

        public double DepositCashBefore = -1;

        public string DepositAll(out int fee)
        {
            fee = -1;
            DepositCashBefore = -1;
            if (!OpenTab(Tab.Bank)) return null;
            OcrLine button;
            string feeLine;
            Header before;

            int look = 0;
            while (true)
            {
                using (var f = Capture()) { button = FindDepositAll(f); fee = ReadBankFee(f, out feeLine); before = ReadHeader(f); }
                if (button != null || ++look >= 4) break;
                Wait(600);
            }
            DepositCashBefore = before.Cash;
            if (button == null) { log("Bank screen not recognised"); Snapshot("bank not recognised", 60); return null; }

            CheckForPurchaseWindow();
            Press(button, 200);
            return BankToast() ?? BankMoved(before, false);
        }

        string BankToast() { return ReadToast(2000, IsBankToast); }

        public static bool IsBankToast(string t)
        {
            return Regex.IsMatch(t ?? "", @"ba\s?n\s?k|d\s?e\s?p\s?[o0]\s?s|withdr|fee\s*\)", RegexOptions.IgnoreCase);
        }

        public const string BankUnchanged = "(the top bar shows no change)", BankUnread = "(no notification, and the top bar didn't read)";

        public const string BankHidden = "(Roblox's own title bar hides the top bar, so it can't be checked)";

        double BankCardOnHand(Frame f)
        {
            var lines = Ocr.Read(f, PageArea(f), PageScale(f), Prep.None);
            var title = lines.FirstOrDefault(l => Parse.Key(l.Text).Contains("NATIONAIBANK"));
            var label = lines.FirstOrDefault(l => Parse.Key(l.Text) == "ONHAND");
            if (title == null || label == null) return -1;
            int h = Math.Max(8, label.Box.Height);
            var row = Rectangle.FromLTRB(label.Box.Right + 2 * h, label.Box.Y - 2 * h, Math.Min(f.Width - 1, title.Box.X + title.Box.Width * 3 / 2 + 2 * h), label.Box.Bottom + 2 * h);
            foreach (int s in new[] { 2, 1, 3 })
                foreach (var prep in new[] { Prep.None, Prep.Contrast, Prep.DarkText })
                {
                    double v;
                    if (Parse.Money(string.Join(" ", Ocr.Read(f, row, s, prep).Select(l => l.Text)), out v)) return v;
                }
            return -1;
        }

        string BankMoved(Header before, bool withdraw)
        {
            if (!withdraw && RobloxBar)
            {

                double onHand;
                using (var f = Capture()) onHand = BankCardOnHand(f);
                return onHand >= 0 && onHand < 1000 ? "the notification didn't show, the bank card says " + SafehouseInfo.Money(onHand) + " on hand" : BankHidden;
            }
            if (before == null || before.Cash < 0 || before.Banked < 0) return BankUnread;
            if (!withdraw && before.Cash < 1) return null;
            bool read = false;
            for (int look = 0; look < 4; look++)
            {
                if (look > 0) Wait(400);
                Header h;
                using (var f = Capture()) h = ReadHeader(f);
                if (h.Cash < 0 || h.Banked < 0) continue;
                read = true;

                bool moved = withdraw ? h.Banked < before.Banked * 0.01 + 1 && h.Cash > before.Cash
                                      : h.Banked > before.Banked && (h.Cash < before.Cash * 0.05 + 1 || h.Banked - before.Banked >= before.Cash * 0.85);
                if (moved) return (withdraw ? "no notification, " : "the notification didn't show, ") + SafehouseInfo.Money(h.Cash) + " on hand and " + SafehouseInfo.Money(h.Banked) + " banked now";
            }
            return read ? BankUnchanged : BankUnread;
        }

        public string WithdrawAll()
        {
            if (!OpenTab(Tab.Bank)) return null;
            OcrLine deposit, withdraw;
            int fee;
            string feeLine;
            Header before;
            using (var f = Capture()) { deposit = FindDepositAll(f, out withdraw); fee = ReadBankFee(f, out feeLine); before = ReadHeader(f); }
            if (deposit == null || withdraw == null || fee < 0 || Parse.Key(withdraw.Text) != "WITHDRAWAII") return null;
            CheckForPurchaseWindow();
            NotDisconnected();
            if (OnlyNavigate) RefuseInCheck(withdraw.Text);
            Click(withdraw.CenterX, withdraw.CenterY, 200);
            return BankToast() ?? BankMoved(before, true);
        }

        static bool IsStartButton(string t) { string k = Parse.Key(t); return k == "START" || k == "IOCKED"; }

        List<KeyValuePair<OcrLine, OcrLine>> OpListRows(List<OcrLine> lines)
        {
            var rows = new List<KeyValuePair<OcrLine, OcrLine>>();
            foreach (var b in lines.Where(l => IsStartButton(l.Text)))
            {
                if (!lines.Any(u => u.CenterY > b.Box.Bottom && u.CenterY < b.Box.Bottom + 2.5 * b.Box.Height && Math.Abs(u.CenterX - b.CenterX) < 4 * b.Box.Height
                                    && (Parse.Has(u.Text, "UP FRONT") || Parse.Has(u.Text, "REQUIRES")))) continue;
                var name = lines.Where(l => l.Box.Right < b.Box.X - 4 * b.Box.Height && Math.Abs(l.CenterY - b.CenterY) < 1.6 * b.Box.Height
                                            && !l.Text.Contains(":") && !l.Text.Contains("$") && Regex.Matches(l.Text, "[A-Za-z]").Count >= 6)
                                .OrderBy(l => l.Box.X).FirstOrDefault();
                if (name != null) rows.Add(new KeyValuePair<OcrLine, OcrLine>(name, b));
            }
            return rows;
        }

        public List<OpSlot> ReadOpSlots(Frame f)
        {
            var p = PageArea(f);
            var lines = Ocr.Read(f, p, PageScale(f), Prep.None);
            var list = OpListRows(lines);
            int listTop = list.Count > 0 ? list.Min(r => r.Key.Box.Y) - 4 : p.Bottom;

            var head = lines.Where(l => Parse.Key(l.Text) == "OPERATIONS" || Parse.Key(l.Text) == "PERATIONS").OrderBy(l => l.Box.Y).FirstOrDefault();
            var top = SplitSlotTitles(f, lines.Where(l => l.Box.Bottom < listTop && (head == null || l.CenterY > head.Box.Bottom)).ToList(), p);

            var titles = top.Where(l =>
            {
                string k = Parse.Key(l.Text);
                if (k == "OPENSIOT" || k == "IOCKED") return true;
                if (k.Contains("PERATION") || k.Contains("OFFIINE") || k == "READY" || k.Contains("FINISH") || k.Contains("CANCEI") || k.Contains("COIIECT") || k.Contains("GAMEPASS")) return false;
                return KnownOp(l.Text) != null || (Regex.Matches(l.Text, "[A-Za-z]").Count >= 8 && !l.Text.Contains("$") && !Parse.Has(l.Text, "XP") && !l.Text.Contains(":"));
            }).OrderBy(l => l.Box.Y).ToList();

            var grid = new List<List<OcrLine>>();
            foreach (var t in titles)
            {
                var row = grid.FirstOrDefault(r => Math.Abs(r[0].CenterY - t.CenterY) < t.Box.Height);
                if (row == null) grid.Add(row = new List<OcrLine>());
                row.Add(t);
            }
            double pitch = grid.Where(r => r.Count > 1).Select(r => { var xs = r.Select(t => t.CenterX).OrderBy(x => x).ToList(); return (double)(xs.Last() - xs.First()) / (xs.Count - 1); })
                               .DefaultIfEmpty(p.Width / 4.1).First();
            var slots = new List<OpSlot>();
            for (int ri = 0; ri < grid.Count; ri++)
            {
                int rowBottom = ri + 1 < grid.Count ? grid[ri + 1].Min(t => t.Box.Y) : listTop;
                foreach (var t in grid[ri].OrderBy(x => x.CenterX))
                {
                    var s = new OpSlot { Index = slots.Count };
                    string tk = Parse.Key(t.Text);
                    if (tk == "IOCKED") s.Locked = true;
                    else if (tk != "OPENSIOT") s.Name = t.Text;
                    var inside = top.Where(l => l != t && Math.Abs(l.CenterX - t.CenterX) < pitch * 0.45 && l.CenterY > t.Box.Bottom && l.CenterY < rowBottom);
                    foreach (var l in inside)
                    {
                        string k = Parse.Key(l.Text);
                        if (k.Contains("IOCKED") || k.Contains("GAMEPASS")) s.Locked = true;
                        if (k.Contains("GAMEPASS")) s.Pass = true;
                        if (k.Contains("FINISHNOW") || k.Contains("CANCEI") || k.Contains("IEFT")) s.Running = true;
                        if (k == "READY" || k.StartsWith("READ") && k.Length <= 6 || k.Contains("COMPIETE") || k.Contains("FINISHED")) s.Done = true;
                        if (k.StartsWith("COIIECT") || k.StartsWith("CIAIM")) s.Collect = l;
                    }
                    if (s.Running && s.Collect == null)
                    {

                        int total = KnownOpSeconds(s.Name);
                        int y = t.CenterY + (int)(1.55 * t.Box.Height), half = (int)(pitch * 0.43);
                        if (total > 0) s.SecondsLeft = (int)((1 - f.FillRatio(t.CenterX - half, t.CenterX + half, y, BLUE, 30)) * total);
                    }
                    if (s.Collect != null) s.Running = false;
                    slots.Add(s);
                }
            }
            return slots;
        }

        static List<OcrLine> SplitSlotTitles(Frame f, List<OcrLine> lines, Rectangle page)
        {
            double slot = page.Width / 4.1;
            var all = new List<OcrLine>();
            foreach (var l in lines)
            {
                string k = Parse.Key(l.Text);
                bool twice = Regex.Matches(k, "OPENSIOT").Count >= 2;
                if (!twice && (l.Box.Width < 0.9 * slot || k.Contains("OFFIINE") || k.Contains("PERATION"))) { all.Add(l); continue; }
                int h = Math.Max(8, l.Box.Height);
                var words = Ocr.ReadWords(f, Rectangle.Intersect(Rectangle.Inflate(l.Box, h, h / 2), new Rectangle(0, 0, f.Width, f.Height)), View.ScaleFor(h), Prep.None, true)
                               .Where(w => w.CenterY >= l.Box.Y && w.CenterY <= l.Box.Bottom).OrderBy(w => w.Box.X).ToList();
                var groups = new List<List<OcrLine>>();
                foreach (var w in words)
                {
                    var last = groups.Count > 0 ? groups[groups.Count - 1] : null;
                    if (last == null || w.Box.X - last[last.Count - 1].Box.Right > h) groups.Add(last = new List<OcrLine>());
                    last.Add(w);
                }
                if (groups.Count < 2) { all.Add(l); continue; }
                foreach (var g in groups)
                    all.Add(new OcrLine { Text = string.Join(" ", g.Select(w => w.Text)), Box = g.Skip(1).Aggregate(g[0].Box, (r, w) => Rectangle.Union(r, w.Box)) });
            }
            return all;
        }

        public void CollectOp(OpSlot s) { Press(s.Collect, 1200); }

        static readonly string[] KnownOps =
        {
            "Fence a Hot Shipment|0.1667|20", "Warehouse Job|0.5|70", "Armored Truck Route|1|180", "The Long Con|2|450",
            "Skim the Counting House|3|800", "Run the Smuggler's Coast|4|1200", "The Diamond District Job|6|2100",
            "Own the Front Page|10|8000", "Empty the City Mint|12|12000", "Dissolve the Charter|16|18000",
            "Tap the Courier Exchange|10|12000", "Take the Gold Train Whole|12|16000", "Empty the Sovereign Vault|16|24000",
            "Open the Old Pilgrim Roads|10|16000", "Empty the Elders' Treasury|12|22000", "Overthrow the First Table|16|32000",
            "Hold the Salt Road|10|22000", "Seize the Great Cistern|12|30000", "Rewrite the Oath|16|45000",
        };

        static string[] KnownOp(string name)
        {
            string k = Parse.Key(name);
            if (k.Length < 5) return null;
            foreach (var row in KnownOps)
            {
                var p = row.Split('|');
                if (Parse.Key(p[0]) == k || Levenshtein(Parse.Key(p[0]), k) <= 2) return p;
            }
            return null;
        }

        public static int KnownOpSeconds(string name)
        {
            var p = KnownOp(name);
            return p == null ? -1 : (int)(double.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture) * 3600);
        }

        static int KnownOpXp(string name)
        {
            var p = KnownOp(name);
            return p == null ? -1 : int.Parse(p[2]);
        }

        public List<OpRow> ReadOpRows(Frame f, int page)
        {
            var lines = Ocr.Read(f, PageArea(f), PageScale(f), Prep.None);
            var rows = new List<OpRow>();
            foreach (var pair in OpListRows(lines))
            {
                OcrLine l = pair.Key, b = pair.Value;
                int h = Math.Max(8, b.Box.Height);
                var r = new OpRow
                {
                    Name = l.Text.Trim(), Y = l.Box.Y, Page = page, StartAt = new Point(b.CenterX, b.CenterY), StartText = b.Text,
                    CanStart = Parse.Key(b.Text) == "START" && GoldIsh(f, b.Box.X - Math.Max(4, h / 2), b.CenterY),
                };

                r.Seconds = KnownOpSeconds(r.Name);
                r.Xp = KnownOpXp(r.Name);
                if (r.Seconds < 0)
                    foreach (var s in lines)
                    {
                        if (s == l || s.Box.Y <= l.Box.Y + l.Box.Height / 2 || s.Box.Y >= l.Box.Bottom + 1.8 * l.Box.Height || s.Box.Right > b.Box.X) continue;
                        if (r.Seconds < 0) r.Seconds = Parse.Duration(s.Text);
                        var m = Regex.Match(s.Text, @"([0-9][0-9,]*)\s*XP", RegexOptions.IgnoreCase);
                        if (m.Success) r.Xp = int.Parse(m.Groups[1].Value.Replace(",", ""));
                    }
                rows.Add(r);
            }
            return rows;
        }

        public void StartOp(OpRow r) { Press(r.StartText, r.StartAt.X, r.StartAt.Y, 1300); }

        public void OpsTop() { ScrollPage(0.71, 40); }

        public void OpsDown(int notches) { ScrollPage(0.71, -notches); }

        public static int OpsStep(List<OpRow> rows)
        {
            return rows.Count < 2 ? 1 : Math.Max(1, Math.Min(5, (rows.Max(r => r.Y) - rows.Min(r => r.Y)) / 100));
        }

        bool fightUnread;

        public List<BossCard> ReadBosses(Frame f)
        {
            var p = PageArea(f);
            var lines = Ocr.Read(f, p, PageScale(f), Prep.None);

            var found = new List<BossBox>();
            foreach (var lab in lines.Where(l => { string k = Parse.Key(l.Text); return k.StartsWith("BOSSHE") || k == "BOSS" || k.StartsWith("HEAI") && k.Length <= 7; })
                                     .OrderBy(l => l.Box.Y))
            {
                var b = BossBoxUnder(f, lab, p);
                if (b != null && !found.Any(o => o.Box.IntersectsWith(b.Box))) found.Add(b);
            }
            found.AddRange(BossBoxesInColumns(f, p, found));

            var cards = new List<BossCard>();
            if (found.Count == 0) return cards;

            int span = found.Max(b => b.Box.X) - found.Min(b => b.Box.X);
            if (span < 2 * found.Max(b => b.Box.Width)) span = p.Width / 2;
            foreach (var b in found.OrderBy(o => o.Box.Y).ThenBy(o => o.Box.X))
            {
                int seconds;
                var c = new BossCard
                {
                    ButtonText = ReadBossButton(f, b.Box, lines, out seconds),
                    ButtonX = b.Box.X + b.Box.Width / 2, ButtonY = b.Box.Y + b.Box.Height / 2,

                    Order = found.Where(o => Math.Abs(o.Box.Y - b.Box.Y) < b.Bar.Height).Min(o => o.Box.Y),
                };
                if (seconds >= 0 || Parse.Has(c.ButtonText, "RESPAWN")) c.RespawnSeconds = seconds >= 0 ? seconds : 30 * 60;
                else if (c.ButtonText.Length == 0)
                {

                    if (LooksRespawning(f, b.Box)) c.RespawnSeconds = 30 * 60;
                    else
                    {

                        if (!fightUnread && b.Box.Height > 3.5 * b.Bar.Height && GoldIsh(f, b.Box.X + 3, b.Box.Y + 3))
                        {
                            fightUnread = true;
                            log("A boss button looks like FIGHT (gold) but its text can't be read at this window size - not pressing it");
                            Snapshot("boss button unreadable", 0, true);
                        }
                        continue;
                    }
                }

                var left = lines.Where(o => o.Box.X >= b.Box.X - 0.66 * span && o.Box.X < b.Box.X - 2).OrderBy(o => o.Box.Y).ThenBy(o => o.Box.X).ToList();
                var name = left.FirstOrDefault(o => o.Box.Y >= b.Top - b.Bar.Height && o.Box.Y < b.Bar.Y && o.Box.Height >= 6
                                                    && Regex.Matches(o.Text, "[A-Za-z]").Count >= 3 && !Regex.IsMatch(o.Text, @"\d"));
                if (name != null) c.Name = (name.Box.Right > b.Box.X ? Regex.Replace(name.Text, @"(\s+(BOSS|HEAL\S*))+\s*$", "", RegexOptions.IgnoreCase) : name.Text).Trim();
                c.Level = LevelIn(c.ButtonText);
                if (c.Level < 0) c.Level = ChipLevel(left.Where(o => o.CenterY > b.Bar.Y && o.CenterY < b.Box.Bottom).ToList());
                cards.Add(c);
            }
            return cards;
        }

        static int LevelIn(string t)
        {
            var m = Regex.Match(t ?? "", @"LEVEL\s*([0-9OIlS]+)", RegexOptions.IgnoreCase);
            int level;
            return m.Success && int.TryParse(Parse.Digits(m.Groups[1].Value), out level) && level > 0 && level < 1000 ? level : -1;
        }

        static int ChipLevel(List<OcrLine> lines)
        {
            foreach (var o in lines)
            {
                int level = LevelIn(o.Text);
                if (level > 0) return level;
                if (Parse.Key(o.Text) != "IEVEI") continue;
                var under = lines.FirstOrDefault(u => Math.Abs(u.Box.X - o.Box.X) <= o.Box.Height && u.Box.Y >= o.Box.Bottom - 2 && u.Box.Y <= o.Box.Bottom + o.Box.Height
                                                      && Regex.IsMatch(u.Text, @"^\s*[0-9OIlS]+\s*$"));
                if (under != null && (level = LevelIn("LEVEL " + under.Text)) > 0) return level;
            }
            return -1;
        }

        static readonly Tuple<int, Prep>[] BossButtonReads =
        {
            Tuple.Create(2, Prep.Contrast), Tuple.Create(2, Prep.None), Tuple.Create(3, Prep.Contrast), Tuple.Create(4, Prep.Contrast),
            Tuple.Create(3, Prep.None), Tuple.Create(5, Prep.Contrast), Tuple.Create(2, Prep.GreyText), Tuple.Create(4, Prep.None),
        };

        static string ReadBossButton(Frame f, Rectangle box, List<OcrLine> page, out int seconds)
        {
            seconds = -1;
            var inner = box;
            inner.Inflate(-2, -2);
            string pageText = BossText(page.Where(l => box.Contains(l.CenterX, l.CenterY))), first = null, level = null;
            var times = new List<Tuple<int, bool, string>>();
            for (int i = -1; i < BossButtonReads.Length; i++)
            {
                string t = i < 0 ? pageText : BossText(Ocr.Read(f, inner, BossButtonReads[i].Item1, BossButtonReads[i].Item2));
                if (t.Length == 0) continue;
                if (first == null) first = t;
                string k = Parse.Key(t);
                if (k.Contains("FIGHT") || k.Contains("ATTACK")) return t;
                if (LevelIn(t) > 0) { if (i >= 0) return t; level = t; continue; }
                int s = Parse.Duration(t);
                if (s < 0 || !(k.Contains("SPAWN") || k.Contains("MIN") || k.Contains("HR"))) continue;
                bool whole = Regex.Matches(t, @"\d+").Count <= TimeParts(s);
                times.Add(Tuple.Create(s, whole, t));
                if (whole && TimeParts(s) == times.Max(x => TimeParts(x.Item1)) && times.Count(x => x.Item2 && x.Item1 == s) >= 2) { seconds = s; return t; }
            }
            if (times.Count == 0) return level ?? first ?? "";

            var pool = times.Any(x => x.Item2) ? times.Where(x => x.Item2).ToList() : times;
            int most = pool.Max(x => TimeParts(x.Item1));
            var best = pool.Where(x => TimeParts(x.Item1) == most).GroupBy(x => x.Item1).OrderByDescending(g => g.Count()).First().First();
            seconds = best.Item1;
            return best.Item3;
        }

        static int TimeParts(int s) { return (s >= 3600 ? 1 : 0) + (s % 3600 > 0 ? 1 : 0); }

        static string BossText(IEnumerable<OcrLine> lines)
        {
            return string.Join(" ", lines.Where(l => l.Box.Height >= 6).OrderBy(l => l.Box.Y).ThenBy(l => l.Box.X).Select(l => l.Text)).Trim();
        }

        static bool LooksRespawning(Frame f, Rectangle box)
        {
            Color edge = f.Pixel(box.X + box.Width / 2, box.Y), bottom = f.Pixel(box.X + box.Width / 2, box.Bottom - 1), face = f.Pixel(box.X + 3, box.Y + 3);
            int spread = Math.Max(face.R, Math.Max(face.G, face.B)) - Math.Min(face.R, Math.Min(face.G, face.B));
            return edge.R >= 80 && BossEdge(bottom) && !BossEdge(face) && spread <= 12 && face.R >= 35;
        }

        internal sealed class BossBox { public Rectangle Box, Bar; public OcrLine Label; public int Top; }

        static List<BossBox> BossBoxesInColumns(Frame f, Rectangle page, List<BossBox> known)
        {
            var more = new List<BossBox>();
            foreach (var k in known.GroupBy(b => b.Box.X).Select(g => g.First()))
            {
                int x = k.Box.X + k.Box.Width / 2;
                for (int y = page.Top + 1; y < page.Bottom; y++)
                {
                    if (!BossEdge(f.Pixel(x, y)) || BossEdge(f.Pixel(x, y - 1)) || known.Concat(more).Any(o => o.Box.Contains(x, y))) continue;
                    var box = BossBoxAt(f, x, y, page);
                    if (box.IsEmpty || Math.Abs(box.X - k.Box.X) > 2 || Math.Abs(box.Right - k.Box.Right) > 2 || box.Height < 3 * k.Bar.Height) continue;
                    more.Add(new BossBox { Box = box, Bar = new Rectangle(k.Bar.X, box.Y - (k.Box.Y - k.Bar.Y), 1, k.Bar.Height), Top = box.Y - (k.Box.Y - k.Top) });
                    y = box.Bottom;
                }
            }
            return more;
        }

        internal static bool BossEdge(Color c) { return c.R >= 30 && c.R >= c.G && c.G * 10 >= c.R * 6 && c.B * 2 <= c.R && c.R - c.B >= 18; }

        static bool BossBarPixel(Color c)
        {
            bool red = c.R >= 60 && c.R - c.G >= 40 && c.R - c.B >= 40 && c.G - c.B <= 30;
            return red || Math.Abs(c.R - 49) <= 8 && Math.Abs(c.G - 49) <= 8 && Math.Abs(c.B - 52) <= 8;
        }

        internal static BossBox BossBoxUnder(Frame f, OcrLine label, Rectangle page)
        {
            int x = label.Box.X + 2, y = label.Box.Bottom, end = Math.Min(page.Bottom, label.Box.Bottom + 3 * label.Box.Height + 12);
            int barTop = -1, barH = 0;
            while (y < end && barTop < 0)
            {
                if (!BossBarPixel(f.Pixel(x, y))) { y++; continue; }
                int from = y;
                while (y < page.Bottom && BossBarPixel(f.Pixel(x, y))) y++;
                if (y - from >= 5) { barTop = from; barH = y - from; }
            }
            if (barTop < 0 || barH > 4 * Math.Max(8, label.Box.Height) + 20) return null;
            var bar = new Rectangle(x, barTop, 1, barH);
            for (end = Math.Min(page.Bottom, y + 4 * barH); y < end; y++)
                if (BossEdge(f.Pixel(x, y))) break;
            if (y >= end) return null;
            var box = BossBoxAt(f, x, y, page);
            if (box.IsEmpty || box.Width < 1.5 * barH) return null;
            return new BossBox { Box = box, Bar = bar, Label = label, Top = label.Box.Y };
        }

        static Rectangle BossBoxAt(Frame f, int x, int top, Rectangle page)
        {
            int l = x, r = x;
            while (l > page.Left && BossEdge(f.Pixel(l - 1, top))) l--;
            while (r < page.Right - 1 && BossEdge(f.Pixel(r + 1, top))) r++;
            int y = top;
            while (y + 1 < page.Bottom && (BossEdge(f.Pixel(l, y + 1)) || BossEdge(f.Pixel(l + 1, y + 1)) || BossEdge(f.Pixel(l + 2, y + 1)))) y++;
            return r - l < 8 ? Rectangle.Empty : new Rectangle(l, top, r - l + 1, y - top + 1);
        }

        public void OpenBoss(BossCard c) { Press(c.ButtonText, c.ButtonX, c.ButtonY, 500); }

        OcrLine bossAttack, bossLeave;

        public bool BossAttackReady;

        public string ReadBossWindow(Frame f)
        {
            var lines = Ocr.Read(f, PopupArea(f), PopupScale(f), Prep.None);
            CheckForPurchasePrompt(lines);
            if (!lines.Any(l => Parse.Has(l.Text, "BOSS FIGHT"))) { BossAttackReady = false; return null; }
            bossAttack = lines.FirstOrDefault(l => Parse.Key(l.Text).StartsWith("ATTACK"));
            bossLeave = lines.FirstOrDefault(l => Parse.Key(l.Text) == "IEAVE");
            string text = string.Join(" ", lines.Select(l => l.Text));
            BossAttackReady = bossAttack != null && !Parse.Has(text, "COOLDOWN");
            return text;
        }

        static readonly Regex BossHealthRx = new Regex(@"([0-9OIl][0-9OIl,]*)\s*/\s*([0-9OIl][0-9OIl,]*)\s*HEAL", RegexOptions.IgnoreCase);

        internal static bool BossWindowHealth(string text, out int cur, out int max)
        {
            cur = max = -1;
            var m = BossHealthRx.Match(text ?? "");
            if (!m.Success || !int.TryParse(Parse.Digits(m.Groups[1].Value), out cur) || !int.TryParse(Parse.Digits(m.Groups[2].Value), out max) || max <= 0)
            {
                cur = max = -1;
                return false;
            }
            return true;
        }

        public void BossAttack() { if (bossAttack != null) Press(bossAttack, 300); }

        OcrLine FindBossLeave(Frame f)
        {
            if (bossLeave != null) return bossLeave;
            var area = PopupArea(f);
            int s = PopupScale(f);
            if (bossAttack != null)
            {
                int h = Math.Max(8, bossAttack.Box.Height);
                var strip = new Rectangle(bossAttack.Box.Right + h, bossAttack.Box.Y - h, area.Right - bossAttack.Box.Right - h, 3 * h);
                var l = Ocr.Read(f, strip, s, Prep.None).FirstOrDefault(o => Parse.Key(o.Text) == "IEAVE");
                if (l != null) return l;
            }
            return Ocr.Read(f, area, s + 1, Prep.None).FirstOrDefault(o => Parse.Key(o.Text) == "IEAVE");
        }

        public bool LeaveBossWindow()
        {
            for (int i = 0; i < 3; i++)
            {
                OcrLine leave;
                using (var f = Win.Capture())
                {
                    if (ReadBossWindow(f) == null) return true;
                    leave = FindBossLeave(f);
                }
                if (leave == null) { Thread.Sleep(500); continue; }
                Check();
                Guard(leave.Text);
                Win.Click(leave.CenterX, leave.CenterY);
                Thread.Sleep(700);
            }

            for (int i = 0; i < 4; i++)
            {
                using (var f = Win.Capture()) if (ReadBossWindow(f) == null) return true;
                Thread.Sleep(500);
            }
            return false;
        }

        public void BossesTop() { ScrollPage(0.54, 60); }
        public void BossesDown() { ScrollPage(0.54, -ListNotches(5)); }

        public void BossesNudge() { ScrollPage(0.54, -2); }

        public int PlaytimeNextSeconds = -1;

        static int NextPlaytime(List<OcrLine> rewards)
        {
            if (rewards.Any(l => Parse.Key(l.Text) == "CIAIM")) return 0;
            int best = int.MaxValue;
            foreach (var l in rewards)
            {
                var m = Regex.Match(l.Text.Trim(), @"^(?:(\d{1,2}):)?(\d{1,2}):(\d{2})$");
                if (!m.Success) continue;
                int s = (m.Groups[1].Success ? int.Parse(m.Groups[1].Value) * 3600 : 0) + int.Parse(m.Groups[2].Value) * 60 + int.Parse(m.Groups[3].Value);
                best = Math.Min(best, s);
            }
            if (best != int.MaxValue) return best;
            return rewards.Count(l => Parse.Key(l.Text) == "CIAIMED") >= 5 ? -2 : -1;
        }

        public int ClaimPlaytimeRewards()
        {
            PlaytimeNextSeconds = -1;
            if (!OpenTab(Tab.Safehouse)) return 0;
            ScrollPage(0.54, 15);
            for (int look = 0; look < 4; look++)
            {
                List<OcrLine> lines;
                using (var f = Capture()) lines = Ocr.Read(f, PageArea(f), PageScale(f), Prep.None);
                var title = lines.FirstOrDefault(l => Parse.Has(l.Text, "PLAYTIME REWARDS"));
                var claimable = lines.Any(l => Parse.Key(l.Text) == "CIAIM" || Parse.Key(l.Text) == "CIAIMED");
                if (title != null && claimable)
                {
                    var stats = lines.FirstOrDefault(l => Parse.Has(l.Text, "YOUR STATS") && l.Box.Y > title.Box.Bottom);
                    int bottom = stats != null ? stats.Box.Y : int.MaxValue;
                    PlaytimeNextSeconds = NextPlaytime(lines.Where(l => l.Box.Y > title.Box.Bottom && l.Box.Y < bottom).ToList());
                    return ClaimButtons(l => l.Box.Y > title.Box.Bottom && l.Box.Y < bottom);
                }
                ScrollPage(0.54, -4);
            }
            return 0;
        }

        public int ClaimContracts()
        {
            if (!OpenTab(Tab.Contracts)) return -1;
            OcrLine tasks;
            using (var f = Capture())
            {
                var p = PageArea(f);
                tasks = Ocr.Read(f, new Rectangle(p.Left, p.Top, p.Width, Math.Max(60, p.Height / 4)), PageScale(f), Prep.None)
                           .FirstOrDefault(l => Parse.Key(l.Text) == "TASKS");
            }
            if (tasks != null) Press(tasks, 700);

            ScrollPage(0.65, 30);
            int n = 0;
            string lastSig = null;
            for (int page = 0; page < 12; page++)
            {
                n += ClaimButtons(l => true);
                string sig;
                using (var f = Capture()) sig = TaskListSignature(f);
                if (sig == lastSig) break;
                lastSig = sig;
                ScrollPage(0.65, -ListNotches(4));
            }

            return n;
        }

        public string TaskListSignature(Frame f)
        {
            var p = PageArea(f);
            return string.Join("|", Ocr.Read(f, p, PageScale(f), Prep.None)
                .Where(l => l.CenterX < p.Left + p.Width / 2 && Regex.Matches(l.Text, "[A-Za-z]").Count >= 4 && !Regex.IsMatch(l.Text, @"\d"))
                .OrderBy(l => l.Box.Y).Select(l => Parse.Key(l.Text)));
        }

        bool claimSeen;

        int ClaimButtons(Func<OcrLine, bool> where)
        {
            int claimed = 0;
            for (int round = 0; round < 6; round++)
            {
                OcrLine target;
                using (var f = Capture()) target = Ocr.Read(f, PageArea(f), PageScale(f), Prep.None).FirstOrDefault(l => Parse.Key(l.Text) == "CIAIM" && where(l));
                if (target == null) break;
                if (!claimSeen) { claimSeen = true; Snapshot("claim button", 0, true); }
                Press(target, 1000);
                claimed++;
                using (var f = Capture()) ClearPopupIfAny(f);
            }
            return claimed;
        }

        public int ClaimTrophies()
        {
            if (!OpenTab(Tab.Inventory)) return 0;
            OcrLine tab;
            using (var f = Capture())
            {
                var strip = TabStrip(PageArea(f));
                int s = PageScale(f);
                tab = Ocr.Read(f, strip, s, Prep.None).FirstOrDefault(l => Parse.Key(l.Text).StartsWith("TROPHIES"))
                   ?? Ocr.ReadWords(f, strip, s, Prep.DarkText).FirstOrDefault(l => Parse.Key(l.Text) == "TROPHIES");
            }
            if (tab == null) return 0;
            Press(tab, 800);
            int claimed = 0;
            var said = new HashSet<string>();
            for (int round = 0; round < 10; round++)
            {
                List<TrophyCard> cards;
                using (var f = Capture()) cards = TrophyCards(f, Ocr.Read(f, PageArea(f), PageScale(f), Prep.None));

                foreach (var t in cards.Where(c => c.Ready && c.TradeIn && said.Add(Parse.Key(c.Name))))
                {
                    log("Trophies: \"" + t.Name + "\" is ready, but claiming it hands items in (" + t.Text + ") - left for you");
                    Snapshot("trade-in trophy ready", 0, true);
                }
                var pick = cards.FirstOrDefault(c => c.Ready && !c.TradeIn);
                if (pick == null) break;
                var ready = pick.Tag;
                Press(ready.Text, ready.CenterX, ready.CenterY - (int)(2.4 * ready.Box.Height), 900);

                string toast = ReadToast(1500, t => Parse.Has(t, "TROPHY") && !Parse.Has(t, "READY"));
                using (var f = Capture()) ClearPopupIfAny(f);

                if (toast == null)
                {
                    Wait(800);
                    List<TrophyCard> after;
                    using (var f = Capture()) after = TrophyCards(f, Ocr.Read(f, PageArea(f), PageScale(f), Prep.None));
                    bool gone = after.Count > 0 && !after.Any(c => c.Ready && Parse.Key(c.Name) == Parse.Key(pick.Name));
                    if (!gone) { log("Trophies: a click on a ready one did nothing"); Snapshot("trophy claim did nothing", 60); break; }
                    toast = "claimed " + (pick.Name.Length > 0 ? pick.Name : "a trophy") + " (its card no longer says ready)";
                }
                log("Trophies: " + toast.Trim());
                claimed++;
            }
            return claimed;
        }

        internal sealed class TrophyCard { public OcrLine Tag; public bool Ready, TradeIn; public string Name = "", Text = ""; }

        internal static List<TrophyCard> TrophyCards(Frame f, List<OcrLine> lines)
        {
            var pics = ItemPictures(f, new Rectangle(0, 0, f.Width, f.Height));
            var cards = new List<TrophyCard>();
            foreach (var tag in lines.Where(l => { string k = Parse.Key(l.Text); return k == "READYTOCIAIM" || k == "IOCKED"; }))
            {
                var pic = pics.FirstOrDefault(r => r.Contains(tag.CenterX, tag.CenterY));
                int h = Math.Max(8, tag.Box.Height);
                Rectangle under = pic.IsEmpty ? Rectangle.FromLTRB(tag.CenterX - tag.Box.Width, tag.Box.Bottom, tag.CenterX + tag.Box.Width, tag.Box.Bottom + 9 * h)
                                              : Rectangle.FromLTRB(pic.Left, pic.Bottom - 2, pic.Right, pic.Bottom + pic.Height * 11 / 10);
                var below = lines.Where(l => l != tag && under.Contains(l.CenterX, l.CenterY)).OrderBy(l => l.Box.Y).ToList();
                string text = string.Join(" ", below.Select(l => l.Text.Trim()));
                string key = Parse.Key(text);
                cards.Add(new TrophyCard
                {
                    Tag = tag, Ready = Parse.Key(tag.Text) == "READYTOCIAIM",
                    Name = below.Count > 0 ? below[0].Text.Trim() : "",
                    Text = text, TradeIn = key.Contains("TRADEIN") || key.Contains("COIIECTED"),
                });
            }
            return cards;
        }

        static Rectangle TabStrip(Rectangle p) { return new Rectangle(p.Left, p.Top, p.Width, Math.Max(60, p.Height * 2 / 5)); }

        public OcrLine FindCratesTab(Frame f)
        {
            var strip = TabStrip(PageArea(f));
            int s = PageScale(f);

            return Ocr.Read(f, strip, s, Prep.None).FirstOrDefault(l => Parse.Key(l.Text) == "CRATES")
                ?? Ocr.Read(f, strip, s, Prep.DarkText).FirstOrDefault(l => Parse.Key(l.Text) == "CRATES")
                ?? Ocr.ReadWords(f, strip, s, Prep.DarkText).FirstOrDefault(l => Parse.Key(l.Text) == "CRATES")
                ?? Ocr.ReadWords(f, strip, s, Prep.None).FirstOrDefault(l => Parse.Key(l.Text) == "CRATES");
        }

        public int OpenCrates(out bool done)
        {
            done = false;
            if (!OpenTab(Tab.Inventory)) return 0;
            OcrLine tab;
            bool cratesShow;
            using (var f = Capture())
            {
                var p = PageArea(f);
                int s = PageScale(f);
                tab = FindCratesTab(f);

                cratesShow = tab == null && Ocr.Read(f, p, s, Prep.None).Any(l => Parse.Key(l.Text).EndsWith("CRATE"));
            }
            if (tab == null && !cratesShow) { log("Couldn't find the CRATES tab in the inventory"); Snapshot("inventory without crates tab", 60); return 0; }
            if (tab != null) Press(tab, 800);
            int tabBottom = tab != null ? tab.Box.Bottom + tab.Box.Height : 0;
            int opened = 0, menuTries = 0;
            string chosen = "", chosenTag = "";
            for (int round = 0; round < 60; round++)
            {
                List<OcrLine> lines;
                Rectangle area;
                List<Rectangle> cards;
                bool onlyCrates;
                using (var f = Capture())
                {
                    area = PageArea(f);
                    lines = Ocr.Read(f, area, PageScale(f), Prep.None);

                    onlyCrates = tab != null && ChosenTab(f, tab.Box);
                    int top = Math.Max(area.Top, tabBottom);
                    cards = ItemPictures(f, new Rectangle(area.Left, top, area.Width, area.Bottom - top));
                }
                if (lines.Any(l => Parse.Has(l.Text, "NO CRATES"))) { done = true; break; }
                var open = lines.FirstOrDefault(l => Parse.Key(l.Text) == "OPENCRATE");

                int many;
                var openAll = OpenAllIn(lines, open, out many);
                if (open == null)
                {

                    var at = NextCrate(lines, cards, onlyCrates, Math.Max(tabBottom, area.Top + area.Height / 4), out chosen, out chosenTag);

                    if (at == null) { done = onlyCrates || opened > 0; break; }
                    if (++menuTries > 3)
                    {

                        if (LeaveCrate(chosen)) { log("Crates: " + chosen + " shows no OPEN CRATE - leaving it alone for 6 hours"); Snapshot("crate without open crate", 60); }
                        break;
                    }
                    try { Press(chosen, at.Value.X, at.Value.Y, 900); }
                    catch (NeverPressException) { LeaveCrate(chosen); }
                    continue;
                }
                menuTries = 0;
                Press(openAll ?? open, 800);
                var got = new List<string>();
                int quiet = 0;
                for (int w = 0; w < (many > 0 ? 90 : 30); w++)
                {
                    List<OcrLine> reward;
                    using (var f = Capture()) reward = Ocr.Read(f, PopupArea(f), PopupScale(f), Prep.None);
                    var collect = reward.FirstOrDefault(l => { string k = Parse.Key(l.Text); return k == "COIIECT" || k == "COIIECTAII"; });
                    var youGot = reward.FirstOrDefault(l => Parse.Has(l.Text, "YOU GOT"));
                    if (collect != null)
                    {

                        var item = reward.Where(l => l.Box.Y > (youGot != null ? youGot.Box.Bottom : collect.Box.Y - 8 * collect.Box.Height) && l.Box.Bottom < collect.Box.Y - 4
                                                     && Math.Abs(l.CenterX - collect.CenterX) < 2 * collect.Box.Width && !Parse.Has(l.Text, "YOU GOT")).Select(l => l.Text).ToList();
                        got.Add(item.Count > 0 ? string.Join(" ", item) : "an item");
                        if (many > 0 && got.Count == 1) Snapshot("crates opened at once", 24 * 60, true);

                        if (got.Count == 1 && chosenTag.Length > 0) Snapshot(chosenTag + " opened", 24 * 60, true);
                        Press(collect, 900);
                        if (many == 0) break;
                        quiet = 0;
                        continue;
                    }
                    if (got.Count > 0 && ++quiet > 6) break;
                    Wait(300);
                }
                if (got.Count == 0)
                {
                    CheckForPurchaseWindow();

                    bool named = LeaveCrate(chosen);
                    log(named ? "Crates: " + chosen + " opened but no COLLECT button showed up - stopping crates, and leaving that one alone for 6 hours"
                              : "A crate opened but no COLLECT button showed up - stopping crates");

                    Snapshot("crate without collect", 60);
                    break;
                }
                if (many > 0)
                {
                    opened += many;
                    log("Crates: opened " + many + " at once" + (chosen.Length > 0 ? " (" + chosen + ")" : "") + ", got " + string.Join(", ", got));
                }
                else
                {
                    opened++;
                    log("Crate: got " + got[0] + (chosen.Length > 0 ? " from " + chosen : ""));
                }
            }
            return opened;
        }

        readonly Dictionary<string, DateTime> cratesLeft = new Dictionary<string, DateTime>();

        bool LeftAlone(string name)
        {
            DateTime until;
            string k = Parse.Key(name);
            return k.Length > 0 && cratesLeft.TryGetValue(k, out until) && DateTime.UtcNow < until;
        }

        internal bool LeaveCrate(string name)
        {
            string k = Parse.Key(name);
            if (k.Length == 0) return false;
            cratesLeft[k] = DateTime.UtcNow.AddHours(6);
            return true;
        }

        internal Point? NextCrate(List<OcrLine> lines, List<Rectangle> cards, bool onlyCrates, int below, out string name, out string tag)
        {
            foreach (var c in cards)
            {
                var pic = c;
                var tags = lines.Where(l => pic.Contains(l.CenterX, l.CenterY) && l.CenterY < pic.Y + pic.Height / 3 && Parse.Key(l.Text).EndsWith("CRATE")).ToList();
                name = CardName(lines, pic);
                if ((!onlyCrates && tags.Count == 0 && !CrateName(name)) || LeftAlone(name)) continue;
                tag = tags.Count > 0 && Parse.Key(tags[0].Text) != "CRATE" ? tags[0].Text.Trim().ToLowerInvariant() : "";
                return new Point(pic.X + pic.Width / 2, pic.Y + pic.Height / 2);
            }
            tag = "";
            var first = lines.Where(l => l.Box.Y > below && CrateName(l.Text) && !Parse.Has(l.Text, "LEGENDARY") && !Parse.Has(l.Text, "EPIC") && !Parse.Has(l.Text, "RARE")
                                         && Parse.Key(l.Text) != "CRATE" && !LeftAlone(l.Text))
                             .OrderBy(l => l.Box.Y / Math.Max(1, 3 * l.Box.Height)).ThenBy(l => l.Box.X).FirstOrDefault();
            name = first != null ? first.Text.Trim() : "";
            if (first == null) return null;
            return new Point(first.CenterX, first.CenterY - (int)(2.2 * first.Box.Height));
        }

        static bool CrateName(string text)
        {
            return Parse.Has(text, "CRATE") || Parse.Has(text, "STASH") || Parse.Has(text, "STRONGBOX") || Parse.Has(text, "DUFFEL") || Parse.Has(text, "LOCKBOX");
        }

        static string CardName(List<OcrLine> lines, Rectangle pic)
        {
            var name = lines.Where(l => l.CenterX > pic.X && l.CenterX < pic.Right && l.Box.Y >= pic.Bottom - 2 && l.CenterY < pic.Bottom + pic.Height * 55 / 100
                                        && !l.Text.TrimStart().StartsWith("+") && !RarityKeys.Contains(Parse.Key(l.Text)))
                            .OrderBy(l => l.Box.Y).Select(l => l.Text.Trim());
            return string.Join(" ", name);
        }

        internal static bool ChosenTab(Frame f, Rectangle text)
        {
            int gold = 0, n = 0;
            for (int y = text.Top; y < text.Bottom; y++)
                for (int x = text.Left; x < text.Right; x++) { n++; if (GoldIsh(f, x, y)) gold++; }
            return n > 0 && gold * 4 >= n;
        }

        internal static List<Rectangle> ItemPictures(Frame f, Rectangle area)
        {
            const int Edge = 0x3A3A3E, Inside = 0x18181A;
            area.Intersect(new Rectangle(0, 1, f.Width, f.Height - 5));
            var found = new List<Rectangle>();
            int minW = Math.Max(50, area.Width / 20);
            for (int y = area.Top; y < area.Bottom; y++)
                for (int x = area.Left; x < area.Right; x += 8)
                {
                    if (!f.Near(x, y, Edge, 6)) continue;
                    int s = x, e = x;
                    while (s > area.Left && f.Near(s - 1, y, Edge, 6)) s--;
                    while (e + 1 < area.Right && f.Near(e + 1, y, Edge, 6)) e++;
                    x = e;
                    int m = (s + e) / 2, w = e - s + 1;
                    if (w < minW || w * 4 > area.Width || f.Near(m, y - 1, Edge, 6)) continue;
                    int t = y + 1;
                    while (t < y + 5 && f.Near(m, t, Edge, 6)) t++;
                    if (!f.Near(s + 3, t, Inside, 8) && !f.Near(e - 3, t, Inside, 8)) continue;
                    int b = t;
                    while (b < f.Height && f.Near(s, b, Edge, 6)) b++;
                    b--;
                    int h = b - y + 1;
                    if (h * 100 < w * 65 || h * 100 > w * 87 || !f.Near(m, b, Edge, 6)) continue;
                    if (found.Any(r => Math.Abs(r.X - s) < 4 && Math.Abs(r.Y - y) < 4)) continue;
                    found.Add(new Rectangle(s, y, w, h));
                }
            return found.OrderBy(r => r.Y / Math.Max(1, r.Height / 2)).ThenBy(r => r.X).ToList();
        }

        static readonly Regex OpenAllRx = new Regex(@"^\s*OPEN\s*([0-9OoIlS]{1,3})\s*$", RegexOptions.IgnoreCase);

        public static OcrLine OpenAllIn(List<OcrLine> lines, OcrLine open, out int many)
        {
            many = 0;
            if (open == null) return null;
            foreach (var l in lines)
            {
                var m = OpenAllRx.Match(l.Text);
                int n;
                if (m.Success && int.TryParse(Parse.Digits(m.Groups[1].Value), out n) && n >= 2 && Math.Abs(l.CenterX - open.CenterX) < open.Box.Width
                    && l.Box.Y > open.Box.Y && l.Box.Y - open.Box.Bottom < 3 * Math.Max(8, open.Box.Height)) { many = n; return l; }
            }
            return null;
        }

        bool ClearPopupIfAny(Frame f)
        {
            var lines = Ocr.Read(f, PopupArea(f), PopupScale(f), Prep.None);
            CheckForPurchasePrompt(lines);
            foreach (var l in lines)
            {
                string k = Parse.Key(l.Text);
                if (k == "DONE" || k == "OK" || k == "OKAY" || k == "CONTINUE" || k == "CIOSE" || k == "NICE" || k == "AWESOME" || k == "COOI" || k == "GOTIT")
                {
                    Press(l, 800);
                    return true;
                }
            }

            if (lines.Any(l => Parse.Has(l.Text, "BOSS FIGHT"))) { log("Closing a BOSS FIGHT window that was left open"); return LeaveBossWindow(); }
            return false;
        }

        public sealed class Disconnect
        {
            public string Message = "";
            public int Code = -1;
            public OcrLine Leave, Reconnect;

            public bool Teleport;
            public OcrLine Ok;

            public bool Foreign;

            string Words { get { return " " + Regex.Replace((Message ?? "").ToUpperInvariant(), "[^A-Z0-9]+", " ").Trim() + " "; } }

            public bool OtherDevice { get { return Code == 264 || Code == 273 || Regex.IsMatch(Words, " (ANOTHER|OTHER|DIFFERENT) DEVICE "); } }

            public bool Kicked
            {
                get
                {
                    string w = Words;
                    if (Code == 268 || Regex.IsMatch(w, @" (BAN|BANS|BANNED|BANNING|EXPLOIT\w*|CHEAT\w*|HACK\w*|MACRO\w*|AUTO ?CLICK\w*) ")
                        || Regex.IsMatch(w, @" (NOT|CANNOT|CAN T|CANT|DON T|DONT|NEVER) REJOIN")) return true;
                    if (Code == 278 || w.Contains(" DISCONNECTED FOR BEING IDLE ")) return false;
                    if (Regex.IsMatch(w, @" SERVERS? (IS |ARE )?(BEING )?(RESTART\w*|REBOOT\w*|SHUT\w*|UPDAT\w*) ") || Regex.IsMatch(w, @" FOR (AN |A )?UPDATES? ")) return false;
                    if (Code == 267 || Regex.IsMatch(w, @" KICK\w* ")) return true;
                    return Reconnect == null && Leave != null && Code < 0;
                }
            }

            public override string ToString() { return (Message + (Code >= 0 ? " (Error Code: " + Code + ")" : "")).Trim(); }
        }

        public const string Disconnected = "Roblox disconnected the game";

        static readonly Regex ErrorCodeText = new Regex(@"\bErr?[oa]r\s*C[oca0][a-z0-9]{1,3}\W{0,3}\s*([0-9OoIlSB|]{1,4})", RegexOptions.IgnoreCase);

        public Disconnect ReadDisconnect(Frame f)
        {
            var area = new Rectangle(f.Width * 18 / 100, f.Height * 12 / 100, f.Width * 64 / 100, f.Height * 80 / 100);
            Disconnect best = null;
            List<OcrLine> first = null;
            foreach (int s in new[] { 2, 3 })
            {
                var lines = Ocr.Read(f, area, s, Prep.None);
                if (first == null) first = lines;
                var d = DisconnectIn(f, lines);
                if (d == null)
                {
                    if (best != null || !lines.Any(l => IsDialogTitle(l.Text) || ErrorCodeText.IsMatch(l.Text))) break;
                    continue;
                }
                if (best == null) best = d;
                else
                {

                    if (best.Leave == null && best.Reconnect == null && best.Ok == null) { best.Leave = d.Leave; best.Reconnect = d.Reconnect; best.Ok = d.Ok; }
                    if (best.Code < 0) best.Code = d.Code;
                    best.Teleport |= d.Teleport;
                    if (d.Message.Length > best.Message.Length) best.Message = d.Message;
                }
                if (best.Code >= 0 && (best.Leave != null || best.Reconnect != null || best.Ok != null)) break;
            }

            if (best != null && best.Reconnect != null && best.Leave == null)
            {
                var r = best.Reconnect;
                int h = Math.Max(8, r.Box.Height), w = r.Box.Width;
                best.Leave = Ocr.Read(f, new Rectangle(r.Box.X - 3 * w, r.Box.Y - h, 3 * w - h / 2, 3 * h), View.ScaleFor(h), Prep.None, true)
                                .FirstOrDefault(l => Parse.Key(l.Text) == "IEAVE" && Math.Abs(l.CenterY - r.CenterY) <= r.Box.Height);
            }

            return best ?? (first != null && DialogGrey(f) >= 0.4 ? ForeignWindow(f, first) : null);
        }

        static readonly Regex AnyCodeRx = new Regex(@"\([^()]{0,40}?[:：]\s*([0-9]{3})\s*\)");

        static double DialogGrey(Frame f)
        {
            int cx = f.Width / 2, cy = f.Height / 2, n = 0, grey = 0;
            for (int y = cy - 40; y <= cy + 40; y += 8)
                for (int x = cx - 100; x <= cx + 100; x += 10)
                {
                    var c = f.Pixel(x, y);
                    n++;
                    if (Math.Abs(c.R - 0x39) <= 8 && Math.Abs(c.G - 0x3B) <= 8 && Math.Abs(c.B - 0x3D) <= 8) grey++;
                }
            return n == 0 ? 0 : (double)grey / n;
        }

        internal static Disconnect ForeignWindow(Frame f, List<OcrLine> lines)
        {
            if (DialogGrey(f) < 0.4) return null;
            var code = lines.Select(l => AnyCodeRx.Match(l.Text)).FirstOrDefault(m => m.Success);
            bool buttons = false;
            for (int y = f.Height / 2; y < Math.Min(f.Height, f.Height / 2 + 260) && !buttons; y += 3)
            {
                int run = 0;
                for (int x = f.Width / 2 - 260; x < f.Width / 2 + 260; x++)
                {
                    var c = f.Pixel(x, y);
                    int mn = Math.Min(c.R, Math.Min(c.G, c.B)), mx = Math.Max(c.R, Math.Max(c.G, c.B));
                    run = mn >= 200 && mx - mn <= 30 ? run + 1 : 0;
                    if (run >= 50) { buttons = true; break; }
                }
            }
            if (code == null && !buttons) return null;
            var d = new Disconnect { Foreign = true, Message = string.Join(" ", lines.Where(l => Math.Abs(l.CenterX - f.Width / 2) < f.Width / 5).Select(l => l.Text.Trim())) };
            if (code != null) d.Code = int.Parse(code.Groups[1].Value);
            return d;
        }

        internal static Disconnect DisconnectIn(Frame f, List<OcrLine> lines)
        {
            var title = lines.FirstOrDefault(l => IsDialogTitle(l.Text));
            var code = lines.FirstOrDefault(l => ErrorCodeText.IsMatch(l.Text));
            if (title == null && code == null) return null;
            int top = code != null ? code.CenterY : title.Box.Bottom + 2 * title.Box.Height;

            var words = new List<OcrLine>();
            foreach (var l in lines.Where(l => l.CenterY > top))
            {
                string k = Parse.Key(l.Text);
                if (k.Length > 5 && k.StartsWith("IEAVE") && k.EndsWith("RECONNECT"))
                {
                    int mid = l.Box.X + l.Box.Width * 5 / 14;
                    words.Add(new OcrLine { Text = "Leave", Box = new Rectangle(l.Box.X, l.Box.Y, mid - l.Box.X - l.Box.Height / 2, l.Box.Height) });
                    words.Add(new OcrLine { Text = "Reconnect", Box = new Rectangle(mid + l.Box.Height / 2, l.Box.Y, l.Box.Right - mid - l.Box.Height / 2, l.Box.Height) });
                }
                else words.Add(l);
            }
            var reconnect = words.Where(l => (Parse.Key(l.Text) == "RECONNECT" || Parse.Key(l.Text) == "RETRY") && (f == null || WhiteFace(f, l))).OrderByDescending(l => l.Box.Y).FirstOrDefault();
            var leave = words.Where(l => Parse.Key(l.Text) == "IEAVE" && (f == null || WhiteFace(f, l)
                                          || (reconnect != null && Math.Abs(l.CenterY - reconnect.CenterY) <= reconnect.Box.Height && l.Box.Right < reconnect.Box.X)))
                             .OrderByDescending(l => l.Box.Y).FirstOrDefault();

            bool teleport = (title != null && IsTeleportTitle(title.Text)) || lines.Any(l => Regex.IsMatch(l.Text, @"\bTELEPORT", RegexOptions.IgnoreCase));
            var ok = !teleport ? null : words.Where(l => (Parse.Key(l.Text) == "OK" || Parse.Key(l.Text) == "OKAY") && (f == null || WhiteFace(f, l)))
                                              .OrderByDescending(l => l.Box.Y).FirstOrDefault();
            int evidence = (title != null ? 1 : 0) + (code != null ? 1 : 0) + (reconnect != null || leave != null || ok != null ? 1 : 0);
            if (evidence < 2) return null;

            var d = new Disconnect { Leave = leave, Reconnect = reconnect, Teleport = teleport, Ok = ok };
            if (code != null)
            {
                var m = ErrorCodeText.Match(code.Text);
                int n;
                if (int.TryParse(Parse.Digits(m.Groups[1].Value), out n)) d.Code = n;
            }

            int from = title != null ? title.Box.Bottom : (code != null ? code.Box.Y - 6 * code.Box.Height : 0);
            int to = code != null ? code.Box.Bottom : top;
            int cx = (title ?? code).CenterX, half = Math.Max(120, (title ?? code).Box.Width * 2);
            var text = lines.Where(l => l != title && l.CenterY > from && l.CenterY <= to && Math.Abs(l.CenterX - cx) < half)
                            .OrderBy(l => l.Box.Y).Select(l => l.Text.Trim());
            d.Message = Regex.Replace(Regex.Replace(string.Join(" ", text), @"\(?\s*\bErr?[oa]r\s*C[oca0][a-z0-9]{1,3}\W.*$", "", RegexOptions.IgnoreCase), @"\s{2,}", " ").Trim();
            return d;
        }

        static bool IsDialogTitle(string text)
        {
            string k = Parse.Key(text);
            return View.Distance(k, "DISCONNECTED") <= 2 || View.Distance(k, "CONNECTIONFAIIED") <= 2 || IsTeleportTitle(text);
        }

        static bool IsTeleportTitle(string text) { return View.Distance(Parse.Key(text), "TEIEPORTFAIIED") <= 2; }

        static bool WhiteFace(Frame f, OcrLine l)
        {
            int h = Math.Max(6, l.Box.Height), bright = 0;
            foreach (int x in new[] { l.Box.X - h * 6 / 10, l.Box.X - h, l.Box.Right + h * 6 / 10, l.Box.Right + h })
            {
                var c = f.Pixel(x, l.CenterY);
                int min = Math.Min(c.R, Math.Min(c.G, c.B)), max = Math.Max(c.R, Math.Max(c.G, c.B));
                if (min >= 200 && max - min <= 30) bright++;
            }
            return bright >= 3;
        }

        public bool PressDisconnect(bool reconnect)
        {
            return PressDisconnect(() => { using (var f = Capture()) return ReadDisconnect(f); }, (x, y) => { Check(); Win.Click(x, y); }, reconnect, GuardText);
        }

        internal static bool PressDisconnect(Func<Disconnect> read, Action<int, int> click, bool reconnect, Action<string> guard = null)
        {
            var d = read();
            if (d == null || d.OtherDevice || d.Kicked) return false;
            var b = reconnect ? d.Reconnect : d.Leave;
            if (b == null) return false;
            if (guard != null) guard(b.Text);
            click(b.CenterX, b.CenterY);
            return true;
        }

        public bool PressTeleportOk()
        {
            return PressTeleportOk(() => { using (var f = Capture()) return ReadDisconnect(f); }, (x, y) => { Check(); Win.Click(x, y); }, GuardText);
        }

        internal static bool PressTeleportOk(Func<Disconnect> read, Action<int, int> click, Action<string> guard = null)
        {
            var d = read();
            if (d == null || !d.Teleport || d.Ok == null || d.OtherDevice || d.Kicked) return false;
            if (guard != null) guard(d.Ok.Text);
            click(d.Ok.CenterX, d.Ok.CenterY);
            return true;
        }

        public bool KeepAlive()
        {
            Tab? open;
            using (var f = Capture()) { var v = ViewOf(f); open = v.Problem == null ? v.Selected(f) : null; }

            foreach (var t in new[] { Tab.Safehouse, Tab.Bank, Tab.Jobs })
                if (t != open && !NotInMenu(t) && OpenTab(t)) return true;
            return false;
        }

        public static readonly string[] StatNames = { "Max Energy", "Max Stamina", "Max Health", "Attack", "Defense", "Property Capacity" };

        public int PointsCost = 1;

        static readonly Regex CostChipRx = new Regex(@"^\s*([0-9OoIlS]{1,3})?\s*P\s?[O0]\s?[I1l]\s?N\s?[T7](S?)\s*$", RegexOptions.IgnoreCase);

        internal static int ChipCost(string text)
        {
            var m = CostChipRx.Match(text ?? "");
            int n;
            if (!m.Success) return -1;
            if (m.Groups[1].Success) return int.TryParse(Parse.Digits(m.Groups[1].Value), out n) ? n : -1;
            return m.Groups[2].Value.Length == 0 ? 1 : -1;
        }
        static readonly Regex AvailableRx = new Regex(@"([0-9OoIlS]{1,4})\s*AVA", RegexOptions.IgnoreCase);

        internal static bool ReadSkillRow(Frame f, List<OcrLine> lines, Rectangle popup, int stat, out int available, out int cost, out Point plus)
        {
            available = cost = -1; plus = Point.Empty;
            var have = lines.Select(l => AvailableRx.Match(l.Text)).FirstOrDefault(m => m.Success);
            int n;
            if (have != null && int.TryParse(Parse.Digits(have.Groups[1].Value), out n)) available = n;

            string name = Parse.Key(StatNames[stat]);

            var names = new HashSet<string>(StatNames.Select(Parse.Key));
            var column = lines.Where(l => names.Contains(Parse.Key(l.Text))).Select(l => l.Box.X).OrderBy(x => x).ToList();
            int colX = column.Count > 0 ? column[column.Count / 2] : 0;
            var row = lines.Where(l => Parse.Key(l.Text) == name).OrderBy(l => Math.Abs(l.Box.X - colX)).FirstOrDefault();
            if (row == null) return false;
            int h = Math.Max(8, row.Box.Height);
            Func<List<OcrLine>, OcrLine> chipIn = ls => ls.Where(l => ChipCost(l.Text) >= 0 && l.Box.X > row.Box.Right && Math.Abs(l.CenterY - row.CenterY) < 2 * h)
                                                          .OrderBy(l => Math.Abs(l.CenterY - row.CenterY)).FirstOrDefault();
            var chip = chipIn(lines);

            var strip = Rectangle.Intersect(Rectangle.FromLTRB(row.Box.Right + h, row.CenterY - 2 * h, popup.Right, row.CenterY + 2 * h), new Rectangle(0, 0, f.Width, f.Height));
            int zoom = PopupScale(f);
            foreach (var t in new[] { Tuple.Create(zoom + 1, Prep.None), Tuple.Create(zoom + 2, Prep.None), Tuple.Create(zoom + 1, Prep.Contrast) })
            {
                if (chip != null || strip.Width < 8 || strip.Height < 8) break;
                chip = chipIn(Ocr.Read(f, strip, Math.Min(6, t.Item1), t.Item2));
            }
            if (chip == null) return false;
            cost = ChipCost(chip.Text);

            var runs = new List<int[]>();
            int runRight = -1;
            for (int x = Math.Min(popup.Right, f.Width - 1); x > chip.Box.Right; x--)
            {
                bool gold = GoldIsh(f, x, chip.CenterY);
                if (gold && runRight < 0) runRight = x;
                if (!gold && runRight >= 0) { runs.Add(new[] { x + 1, runRight }); runRight = -1; }
            }

            int gap = Math.Max(6, (int)(1.2 * chip.Box.Height));
            var merged = new List<int[]>();
            foreach (var r in runs.OrderBy(r => r[0]))
            {
                var last = merged.Count > 0 ? merged[merged.Count - 1] : null;
                if (last != null && r[0] - last[1] <= Math.Max(gap, (int)(0.8 * Math.Min(r[1] - r[0], last[1] - last[0])))) last[1] = r[1];
                else merged.Add(new[] { r[0], r[1] });
            }
            var best = merged.OrderByDescending(r => r[1] - r[0]).FirstOrDefault();
            if (best != null && best[1] - best[0] >= 4) plus = new Point((best[0] + best[1]) / 2, chip.CenterY);
            return true;
        }

        internal static Point SkillWindowX(Frame f, OcrLine title, Rectangle popup)
        {

            int th = Math.Max(Math.Max(8, title.Box.Height), (int)(21 * Math.Min(f.Height / 1009.0, f.Width / 1790.0)));

            int right = Math.Min(popup.Right, f.Width - 1);
            for (int x = title.Box.Right + th; x < right; x++)
            {
                var c = f.Pixel(x, title.CenterY);
                if (c.R >= 150 && c.G >= 120 && c.R - c.B >= 50) { right = x; break; }
            }
            var r = Rectangle.Intersect(Rectangle.FromLTRB(title.Box.Right + th, title.CenterY - th * 6 / 5, right, title.CenterY + th * 6 / 5), new Rectangle(0, 0, f.Width, f.Height));
            Func<int, int, bool> grey = (x, y) =>
            {
                var c = f.Pixel(x, y);
                int mx = Math.Max(c.R, Math.Max(c.G, c.B)), mn = Math.Min(c.R, Math.Min(c.G, c.B));
                return mx >= 110 && mx - mn <= 25;
            };
            int x0 = int.MaxValue, y0 = int.MaxValue, x1 = -1, y1 = -1;
            for (int y = r.Top; y < r.Bottom; y++)
                for (int x = r.Left; x < r.Right; x++)
                    if (grey(x, y)) { x0 = Math.Min(x0, x); x1 = Math.Max(x1, x); y0 = Math.Min(y0, y); y1 = Math.Max(y1, y); }
            if (x1 < 0) return Point.Empty;
            int w = x1 - x0 + 1, h = y1 - y0 + 1;
            if (w < th / 3 || h < th / 3 || w > 2 * th || h > 2 * th || w > 1.5 * h || h > 1.5 * w) return Point.Empty;

            int cx = (x0 + x1) / 2, cy = (y0 + y1) / 2;
            if (!grey(cx, cy) || grey(cx, y0 + 1) || grey(cx, y1 - 1) || grey(x0 + 1, cy) || grey(x1 - 1, cy)) return Point.Empty;
            return new Point(cx, cy);
        }

        public int SpentOn = -1;
        public string SavingFor;

        public bool PointsFailed;

        public int SpendPoints(int stat, int available, int fallback = -1, int firstMaxCost = int.MaxValue)
        {
            SpentOn = stat; SavingFor = null; PointsFailed = false;
            if (available < 1) return 0;
            OcrLine button;
            using (var f = Capture())
            {
                var v = ViewOf(f);
                int right = v.Problem == null && v.Cash != null ? v.Cash.Box.X : f.Width / 2;
                button = Ocr.Read(f, new Rectangle(168, 0, Math.Max(40, right - 168), v.Problem == null ? v.Header.Bottom : 120), 3, Prep.None)
                            .FirstOrDefault(l => Parse.Has(l.Text, "POINTS"));
            }
            if (button == null) { log("Couldn't find the skill points button"); PointsFailed = true; return 0; }
            Press(button, 1000);
            List<OcrLine> lines;
            using (var f = Capture()) lines = Ocr.Read(f, PopupArea(f), PopupScale(f), Prep.None);
            if (!lines.Any(l => Parse.Has(l.Text, "SKILL POINTS")))
            {
                log("The skill point window didn't open");
                Snapshot("skill window missing", 60);
                PointsFailed = true;
                return 0;
            }
            int pending = 0;
            bool interrupted = false;
            try
            {
                for (int i = 0; i < 40; i++)
                {
                    int points, cost;
                    Point plus;
                    using (var f = Capture())
                    {
                        lines = Ocr.Read(f, PopupArea(f), PopupScale(f), Prep.None);
                        if (!ReadSkillRow(f, lines, PopupArea(f), stat, out points, out cost, out plus))
                        {
                            log("Skill points: " + StatNames[stat] + "'s row or cost didn't read");
                            Snapshot("skill row " + StatNames[stat], 60);
                            PointsFailed = true;
                            break;
                        }
                        if (i == 0 && fallback >= 0 && fallback != stat && cost > firstMaxCost)
                        {
                            stat = SpentOn = fallback;
                            if (!ReadSkillRow(f, lines, PopupArea(f), stat, out points, out cost, out plus))
                            {
                                log("Skill points: " + StatNames[stat] + "'s row or cost didn't read");
                                Snapshot("skill row " + StatNames[stat], 60);
                                PointsFailed = true;
                                break;
                            }
                        }
                        else if (i > 0 && fallback >= 0 && fallback != stat && cost > firstMaxCost)
                        {

                            int fp, fc;
                            Point fplus;
                            PointsCost = ReadSkillRow(f, lines, PopupArea(f), fallback, out fp, out fc, out fplus) && fc > 0 ? fc : 1;
                            break;
                        }
                    }
                    if (cost > 0) PointsCost = cost;
                    if (pending == 0 && points >= 0 && cost > points) SavingFor = string.Format("{0} ({1} points, you have {2})", StatNames[stat], cost, points);
                    if (points < 0 || cost <= 0 || points < cost || plus == Point.Empty) break;
                    Click(plus.X, plus.Y, 450);
                    pending++;
                }
                if (pending > 0)
                {

                    var apply = lines.FirstOrDefault(l => Parse.Key(l.Text) == "APPIY");
                    using (var f = Capture()) apply = Ocr.Read(f, PopupArea(f), PopupScale(f), Prep.None).FirstOrDefault(l => Parse.Key(l.Text) == "APPIY") ?? apply;
                    if (apply == null) { log("Skill points: APPLY didn't read - the choice isn't kept"); Snapshot("skill apply missing", 60); pending = 0; PointsFailed = true; }
                    else { Press(apply, 900); using (var f = Capture()) lines = Ocr.Read(f, PopupArea(f), PopupScale(f), Prep.None); }
                }
            }
            catch (Exception) { interrupted = true; throw; }
            finally
            {

                var title = lines.FirstOrDefault(l => Parse.Has(l.Text, "SKILL POINTS"));
                var close = lines.FirstOrDefault(l => Parse.Key(l.Text) == "DONE")
                            ?? (title == null ? null : lines.FirstOrDefault(l => (l.Text.Trim() == "x" || l.Text.Trim() == "X") && l.Box.X > title.Box.Right && Math.Abs(l.CenterY - title.CenterY) < 2 * title.Box.Height));
                if (close != null && !interrupted) { Check(); Guard(close.Text); Win.Click(close.CenterX, close.CenterY); Thread.Sleep(700); }
                else if (title != null && !interrupted)
                {

                    Point x;
                    using (var f = Capture()) x = SkillWindowX(f, title, PopupArea(f));
                    if (!x.IsEmpty) { Check(); Guard("X"); Win.Click(x.X, x.Y); Thread.Sleep(700); }
                }
            }
            return pending;
        }

        readonly HashSet<string> refused = new HashSet<string>();

        void Guard(string text)
        {
            GuardText(text);
            NotDisconnected();
        }

        void GuardText(string text)
        {
            string why = NeverPress.Why(text);
            if (why == null) return;
            if (refused.Add(Parse.Key(text) + "|" + why))
            {
                log("Refused to press \"" + (text ?? "").Trim() + "\" (" + why + ": on the never-press list) - skipping that for now");
                Snapshot("never press " + why);
            }
            throw new NeverPressException(text, why);
        }

        void NotDisconnected()
        {
            using (var f = Win.Capture())
                if (DisconnectShowing(f)) throw new WindowChangedException(Disconnected);
        }

        internal static bool DisconnectShowing(Frame f)
        {
            if (DialogGrey(f) < 0.4) return false;
            var lines = Ocr.Read(f, new Rectangle(f.Width * 18 / 100, f.Height * 12 / 100, f.Width * 64 / 100, f.Height * 80 / 100), 2, Prep.None);

            return DisconnectIn(null, lines) != null || ForeignWindow(f, lines) != null;
        }

        void Press(OcrLine button, int waitMs) { Press(button.Text, button.CenterX, button.CenterY, waitMs); }

        void Press(string text, int x, int y, int waitMs)
        {
            Guard(text);
            if (OnlyNavigate && !NavigationPress(text, x, y)) RefuseInCheck(text);
            Click(x, y, waitMs);
        }

        public sealed class CrewTotals
        {
            public int Attack = -1, Defense = -1, Henchmen = -1, Slots = -1;
            public bool Read { get { return Attack >= 0 && Defense >= 0; } }
            public override string ToString() { return Read ? "+" + Attack + " attack, +" + Defense + " defense" : "totals not read"; }
        }

        static readonly Regex CrewTotal = new Regex(@"\+\s*([0-9][0-9,]{0,6})\s*(ATTACK|DEFENSE)\b", RegexOptions.IgnoreCase);

        public CrewTotals ReadCrewTotals(Frame f)
        {
            var t = new CrewTotals();
            var area = PageArea(f);
            foreach (var prep in new[] { Prep.None, Prep.DarkText, Prep.WhiteText })
            {
                var lines = Ocr.Read(f, area, PageScale(f), prep);
                if (t.Henchmen < 0)
                    foreach (var l in CountWrapped(lines))
                    {
                        int hn, hm;
                        if (HenchmenCount(l.Text, out hn, out hm)) { t.Henchmen = hn; t.Slots = hm; break; }
                    }

                var found = lines.SelectMany(l => CrewTotal.Matches(l.Text).Cast<Match>().Select(m => new { L = l, M = m })).ToList();
                foreach (var a in found.Where(x => !t.Read && x.M.Groups[2].Value.ToUpperInvariant() == "ATTACK").OrderBy(x => x.L.Box.Y))
                {
                    var d = found.FirstOrDefault(x => x.M.Groups[2].Value.ToUpperInvariant() == "DEFENSE"
                                                      && (x.L == a.L ? x.M.Index > a.M.Index
                                                          : x.L.Box.X > a.L.Box.Right && Math.Abs(x.L.CenterY - a.L.CenterY) <= Math.Max(a.L.Box.Height, x.L.Box.Height) / 2 + 2));
                    if (d == null) continue;
                    t.Attack = int.Parse(a.M.Groups[1].Value.Replace(",", ""));
                    t.Defense = int.Parse(d.M.Groups[1].Value.Replace(",", ""));
                    break;
                }

                if (t.Read && t.Henchmen >= 0) return t;
            }
            return t;
        }

        internal OcrLine FindAutoEquip(Frame f, out Rectangle box, out string why)
        {
            box = Rectangle.Empty;
            var lines = SplitHeaderLine(f, Ocr.Read(f, PageArea(f), PageScale(f), Prep.None), "AUTOEQUIPBEST");
            var button = lines.FirstOrDefault(l => Parse.Key(l.Text) == "AUTOEQUIPBEST");
            if (button == null)
            {
                why = lines.Any(l => Parse.Key(l.Text).Contains("AUTOEQUIP")) ? "its text ran together with other words" : "it wasn't found on the Crew page";
                return null;
            }
            var b = ButtonBox(f, button.Box);
            int h = Math.Max(8, button.Box.Height);
            if (b.IsEmpty || !b.Contains(button.Box) || b.Width > 2 * button.Box.Width + 4 * h || b.Height > 5 * h)
            {
                why = "its button's frame wasn't found around it";
                return null;
            }
            var other = lines.FirstOrDefault(l => l != button && b.Contains(l.CenterX, l.CenterY));
            if (other != null) { why = "\"" + other.Text + "\" is inside its frame too"; return null; }
            foreach (var u in lines.Where(l => Parse.Key(l.Text).Contains("UNEQUIP")))
            {
                var ub = ButtonBox(f, u.Box);
                if (u.Box.IntersectsWith(b) || (!ub.IsEmpty && ub.IntersectsWith(b))) { why = "UNEQUIP ALL's box touches its box"; return null; }
            }
            box = b;
            why = null;
            return button;
        }

        static List<OcrLine> SplitHeaderLine(Frame f, List<OcrLine> lines, string key)
        {
            lines = HeaderWrapped(lines);
            if (lines.Any(l => Parse.Key(l.Text) == key)) return lines;
            var joined = lines.FirstOrDefault(l => Parse.Key(l.Text).Contains(key));
            if (joined == null) return lines;
            int h = Math.Max(8, joined.Box.Height);
            var words = Ocr.ReadWords(f, Rectangle.Inflate(joined.Box, h, h / 2), View.ScaleFor(h), Prep.None, true)
                           .Where(w => w.CenterY >= joined.Box.Y && w.CenterY <= joined.Box.Bottom).OrderBy(w => w.Box.X).ToList();
            for (int i = 0; i < words.Count; i++)
            {
                string acc = "";
                for (int j = i; j < words.Count && key.StartsWith(acc + Parse.Key(words[j].Text)); j++)
                {
                    acc += Parse.Key(words[j].Text);
                    if (acc != key) continue;
                    var own = words.GetRange(i, j - i + 1);
                    var button = new OcrLine
                    {
                        Text = string.Join(" ", own.Select(w => w.Text)),
                        Box = own.Skip(1).Aggregate(own[0].Box, (r, w) => Rectangle.Union(r, w.Box)),
                    };
                    return lines.Where(l => l != joined).Concat(new[] { button }).Concat(words.Where(w => !own.Contains(w))).ToList();
                }
            }
            return lines;
        }

        internal static List<OcrLine> HeaderWrapped(List<OcrLine> lines)
        {
            return JoinWrapped(JoinWrapped(lines, t => Parse.Key(t) == "AUTOSORT", t => Parse.Key(t) == "CREW"),
                               t => Parse.Key(t) == "AUTOEQUIP", t => Parse.Key(t) == "BEST");
        }

        static readonly string UnequipKey = Parse.Key("UNEQUIP ALL");

        internal OcrLine FindUnequipAll(Frame f, out Rectangle box, out string why)
        {
            box = Rectangle.Empty;
            var lines = SplitHeaderLine(f, Ocr.Read(f, PageArea(f), PageScale(f), Prep.None), UnequipKey);
            var button = lines.FirstOrDefault(l => Parse.Key(l.Text) == UnequipKey);
            if (button == null)
            {
                why = lines.Any(l => Parse.Key(l.Text).Contains("UNEQUIP")) ? "its text ran together with other words" : "it wasn't found on the Crew page";
                return null;
            }
            var b = ButtonBox(f, button.Box);
            int h = Math.Max(8, button.Box.Height);
            if (b.IsEmpty || !b.Contains(button.Box) || b.Width > 2 * button.Box.Width + 4 * h || b.Height > 5 * h)
            {
                why = "its button's frame wasn't found around it";
                return null;
            }
            var other = lines.FirstOrDefault(l => l != button && b.Contains(l.CenterX, l.CenterY));
            if (other != null) { why = "\"" + other.Text + "\" is inside its frame too"; return null; }
            foreach (var u in lines.Where(l => Parse.Key(l.Text).Contains("AUTOEQUIP")))
            {
                var ub = ButtonBox(f, u.Box);
                if (u.Box.IntersectsWith(b) || (!ub.IsEmpty && ub.IntersectsWith(b))) { why = "AUTO EQUIP BEST's box touches its box"; return null; }
            }
            box = b;
            why = null;
            return button;
        }

        public bool UnequipAll(out bool pressed, out string why)
        {
            pressed = false;
            if (!OpenTab(Tab.Crew)) { why = "the Crew tab didn't open"; return false; }
            ScrollPage(0.62, 40);
            Wait(400);
            OcrLine button;
            Rectangle box;
            CrewTotals before;
            using (var f = Capture()) { before = ReadCrewTotals(f); button = FindUnequipAll(f, out box, out why); }
            for (int again = 0; again < 2 && button == null; again++)
            {
                Wait(1500);
                using (var f = Capture()) { before = ReadCrewTotals(f); button = FindUnequipAll(f, out box, out why); }
            }
            if (button == null) { Snapshot("unequip all not pressed", 60); return false; }

            using (var f = Capture())
                if (!ReadCrew(f).Page || ReadDismissWindow(f) != null) { why = "a window is open over the Crew page"; return false; }
            if (Parse.Key(button.Text) != UnequipKey || box.IsEmpty || !box.Contains(button.Box)) throw new NeverPressException(button.Text, "not the crew's UNEQUIP ALL");
            NotDisconnected();
            if (OnlyNavigate) RefuseInCheck(button.Text);
            Click(box.X + box.Width / 2, box.Y + box.Height / 2, 900);
            pressed = true;
            Snapshot("crew unequip all", 0, true);
            for (int look = 0; look < 3; look++)
            {
                using (var f = Capture())
                {
                    var popup = Ocr.Read(f, PopupArea(f), PopupScale(f), Prep.None);
                    CheckForPurchasePrompt(popup);
                    if (popup.Any(l => { string k = Parse.Key(l.Text); return k.Contains("CONFIRM") || k.Contains("AREYOUSURE") || k == "YES" || k == "NO" || k == "CANCEI"; }))
                    {
                        why = "a window came up: \"" + string.Join(" ", popup.Select(l => l.Text)) + "\"";
                        Snapshot("crew unequip all window", 0, true);
                        return false;
                    }
                    var after = ReadCrewTotals(f);
                    string toast = ToastIn(f);

                    if ((before.Read && after.Read && after.Attack < before.Attack && after.Defense < before.Defense)
                        || (toast != null && (Parse.Has(toast, "UNEQUIP") || Parse.Has(toast, "NOTHING EQUIPPED"))) || NothingEquipped(f))
                    {
                        why = null;
                        return true;
                    }
                }
                Wait(700);
            }
            why = "the totals didn't go down";
            Snapshot("crew unequip all no change", 60);
            return false;
        }

        internal static bool IsEquipToast(string t)
        {
            return t != null && Parse.Has(t, "EQUIPPED") && Parse.Has(t, "BEST") && !Parse.Has(t, "NOTHING");
        }

        static readonly Regex GearEmpty = new Regex(@"\b(WEAPON|ARMOR|VEHICLE)\s*[:;.,]?\s*EMPTY\b", RegexOptions.IgnoreCase);

        static readonly Regex GearKind = new Regex(@"\b(COMMON|UNCOMMON|RARE|EPIC|LEGENDARY|MYTHIC|SECRET|FORBIDDEN)\s*(WEAPON|ARMOR|VEHICLE)\b", RegexOptions.IgnoreCase);

        internal bool NothingEquipped(Frame f)
        {
            int empty = 0;
            foreach (var l in Ocr.Read(f, PageArea(f), PageScale(f), Prep.None))
            {
                if (GearKind.IsMatch(l.Text)) return false;
                empty += GearEmpty.Matches(l.Text).Count;
            }
            return empty >= 6;
        }

        internal static Rectangle ButtonBox(Frame f, Rectangle text)
        {
            int y = text.Y + text.Height / 2, gap = Math.Max(3, text.Height / 2), x = text.X - gap;
            var face = f.Pixel(x, y);
            if (Math.Abs(Shade(f.Pixel(x - 2, y)) - Shade(face)) > 4 || Math.Abs(Shade(f.Pixel(text.Right + gap, y)) - Shade(face)) > 4) return Rectangle.Empty;
            int maxW = 2 * text.Width + 40, maxH = 5 * Math.Max(8, text.Height);
            int l = FrameEdge(f, x, y, -1, 0, face, maxW), r = FrameEdge(f, text.Right + gap, y, 1, 0, face, maxW);
            int t = FrameEdge(f, x, y, 0, -1, face, maxH), b = FrameEdge(f, x, y, 0, 1, face, maxH);
            if (l < 0 || r < 0 || t < 0 || b < 0) return Rectangle.Empty;
            return Rectangle.FromLTRB(l, t, r + 1, b + 1);
        }

        static int Shade(Color c) { return (c.R + c.G + c.B) / 3; }

        static int FrameEdge(Frame f, int x, int y, int dx, int dy, Color face, int max)
        {
            int shade = Shade(face);
            for (int i = 0; i < max; i++, x += dx, y += dy)
            {
                if (x < 3 || y < 3 || x >= f.Width - 3 || y >= f.Height - 3) return -1;
                if (Shade(f.Pixel(x, y)) >= shade - 8) continue;
                bool frame = false;
                for (int k = 1; k <= 3 && !frame; k++) frame = Shade(f.Pixel(x - k * dx, y - k * dy)) > shade + 6;
                if (!frame) return -1;
                return dx != 0 ? x - dx : y - dy;
            }
            return -1;
        }

        public sealed class EquipResult
        {
            public CrewTotals Before, After;
            public string Window;
            public bool Confirmed, Unchanged, Bare;
            public int Presses;

            public bool GearOn { get { return Window == null && (Confirmed || Unchanged); } }
        }

        public EquipResult EquipBest()
        {
            if (!OpenTab(Tab.Crew)) return null;
            var r = new EquipResult();
            for (int press = 0; press < 2 && !r.Confirmed && r.Window == null; press++)
            {
                OcrLine button;
                Rectangle box;
                string why;
                CrewTotals before;
                using (var f = Capture()) { before = ReadCrewTotals(f); button = FindAutoEquip(f, out box, out why); }

                for (int again = 0; again < 2 && button == null; again++)
                {
                    Wait(1500);
                    using (var f = Capture()) { before = ReadCrewTotals(f); button = FindAutoEquip(f, out box, out why); }
                }
                if (button == null)
                {
                    log("Didn't press AUTO EQUIP BEST: " + why);
                    Snapshot("auto equip best not pressed", 60);
                    return press == 0 ? null : r;
                }
                if (press == 0) r.Before = before;

                Press(button.Text, box.X + box.Width / 2, box.Y + box.Height / 2, 900);
                r.Presses++;
                for (int look = 0; look < 3; look++)
                {
                    if (look > 0) Wait(700);
                    using (var f = Capture())
                    {
                        var popup = Ocr.Read(f, PopupArea(f), PopupScale(f), Prep.None);
                        CheckForPurchasePrompt(popup);

                        if (popup.Any(l => { string k = Parse.Key(l.Text); return k.Contains("CONFIRM") || k.Contains("AREYOUSURE") || k == "YES" || k == "NO"; }))
                            r.Window = string.Join(" ", popup.Select(l => l.Text));
                        r.After = ReadCrewTotals(f);
                        string toast = ToastIn(f);
                        r.Confirmed = IsEquipToast(toast)
                                      || (r.Before.Read && r.After.Read && (r.After.Attack != r.Before.Attack || r.After.Defense != r.Before.Defense));
                        r.Bare = NothingEquipped(f);
                    }
                    if (r.Confirmed || r.Window != null) break;
                }
            }

            r.Unchanged = !r.Confirmed && r.Window == null && r.Presses >= 2 && !r.Bare && r.Before.Read && r.After != null && r.After.Read;
            return r;
        }

        public sealed class ShopItem
        {
            public string Name, PriceText;
            public double Price = -1;
            public bool Gold;
            public int Owned = -1;
            public int Rarity = -1;
            public OcrLine Buy;
            public bool BuyGold;
            public override string ToString() { return (Name ?? "an item") + " (" + (Gold ? Price + " gold" : SafehouseInfo.Money(Price)) + ")"; }
        }

        public sealed class ShopPage
        {
            public Rectangle Cash;
            public int StockSeconds = -1;
            public List<ShopItem> Items = new List<ShopItem>();
            public string Signature = "";
        }

        static readonly Regex OwnedRx = new Regex(@"Owned\s*[:;.]?\s*([0-9OoIlS,]{1,7})", RegexOptions.IgnoreCase);

        public static readonly string[] Rarities = { "COMMON", "UNCOMMON", "RARE", "EPIC", "LEGENDARY", "MYTHIC", "SECRET", "FORBIDDEN", "EXOTIC" };

        public ShopPage ReadShop(Frame f)
        {
            var area = PageArea(f);
            var lines = Ocr.Read(f, area, PageScale(f), Prep.None);
            var equipment = lines.FirstOrDefault(l => Parse.Key(l.Text).Contains("EQUIPMENT"));
            var robux = lines.FirstOrDefault(l => Parse.Key(l.Text).Contains("ROBUXSHOP"));

            if (robux == null && equipment != null)
                robux = lines.FirstOrDefault(l => Parse.Key(l.Text).StartsWith("ROBUX") && l.Box.X > equipment.Box.Right
                                                  && Math.Abs(l.CenterY - equipment.CenterY) < Math.Max(8, equipment.Box.Height));
            if (equipment == null || robux == null || robux.Box.X <= equipment.Box.X) return null;
            int h = Math.Max(8, equipment.Box.Height);
            var page = new ShopPage();
            int right = robux.Box.X - Math.Max(4, h / 3);
            page.Cash = Rectangle.FromLTRB(area.Left, equipment.Box.Bottom, right, area.Bottom);
            var stock = lines.FirstOrDefault(l => Parse.Key(l.Text).Contains("NEWSTOCK"));
            if (stock != null)
            {
                var m = Regex.Match(stock.Text, @"([0-9OoIl]{1,2})\s*[:.]\s*([0-9OoIlS]{2})\s*$");
                int mm, ss;
                if (m.Success && int.TryParse(Parse.Digits(m.Groups[1].Value), out mm) && int.TryParse(Parse.Digits(m.Groups[2].Value), out ss)) page.StockSeconds = mm * 60 + ss;
            }
            var inCash = lines.Where(l => l.Box.Right <= right && l.Box.Y > equipment.Box.Bottom).ToList();
            page.Signature = string.Join("|", inCash.Select(l => Parse.Key(l.Text)));

            var buys = inCash.Where(l => Parse.Key(l.Text) == "BUY").ToList();
            foreach (var d in Ocr.Read(f, Rectangle.FromLTRB(area.Left, equipment.Box.Bottom, right, area.Bottom), PageScale(f), Prep.DarkText))
                if (Parse.Key(d.Text) == "BUY" && d.Box.Right <= right && !buys.Any(b => b.Box.IntersectsWith(d.Box))) buys.Add(d);
            foreach (var b in buys.OrderBy(l => l.Box.Y))
            {
                int bh = Math.Max(8, b.Box.Height);

                var above = inCash.Where(l => l != b && l.Box.Bottom <= b.Box.Y + bh / 3 && b.Box.Y - l.Box.Bottom < 2 * bh
                                              && l.Box.Right > b.Box.X && l.Box.X < b.Box.Right && Regex.IsMatch(l.Text, "[0-9]")).ToList();

                var price = above.Where(l => l.Text.Contains("$") && GreenInk(f, l.Box)).OrderByDescending(l => l.Box.Right).FirstOrDefault()
                            ?? above.OrderByDescending(l => l.Box.Bottom).FirstOrDefault();

                var it = new ShopItem { Buy = b, BuyGold = GoldFace(f, b.Box) };
                if (price != null)
                {
                    it.PriceText = price.Text.Trim();
                    double v;
                    if (Regex.IsMatch(it.PriceText, @"R\s?\$")) continue;

                    if (it.PriceText.Contains("$")) { v = LastMoney(it.PriceText); if (v >= 0 && GreenInk(f, price.Box)) it.Price = v; }
                    else
                    {

                        string rest = Regex.Replace(it.PriceText, "GOLD|BARS?", "", RegexOptions.IgnoreCase), d = Parse.Digits(rest);
                        if (d.Length > 0 && d.Length <= 7 && !Regex.IsMatch(rest, "[A-Za-z]{2}"))
                        { it.Gold = true; it.Price = double.Parse(d); }
                    }
                }

                if (it.Price < 0 && it.BuyGold && !Regex.IsMatch(it.PriceText ?? "", @"R\s?\$"))
                {
                    string again;
                    double v2 = RereadShopPrice(f, b, page.Cash, out again);
                    if (v2 >= 0) { it.Price = v2; it.PriceText = again; }
                }

                else if (it.Price >= 0 && !it.BuyGold && !it.Gold && (it.PriceText ?? "").Contains("."))
                {
                    string again;
                    double v2 = RereadShopPrice(f, b, page.Cash, out again);
                    if (v2 > it.Price) { it.Price = v2; it.PriceText = again; }
                }

                int top = (price != null ? price.Box.Y : b.Box.Y - 2 * bh) - bh / 2, bottom = b.Box.Bottom + bh / 2;
                int leftOf = Math.Min(b.Box.X, price != null ? price.Box.X : b.Box.X) - bh / 2;
                var card = inCash.Where(l => l.Box.Right < leftOf && l.CenterY >= top && l.CenterY <= bottom).ToList();
                var owned = card.Select(l => OwnedRx.Match(l.Text)).FirstOrDefault(m => m.Success);
                int o;
                if (owned != null && int.TryParse(Parse.Digits(owned.Groups[1].Value), out o)) it.Owned = o;
                var name = card.Where(l => !OwnedRx.IsMatch(l.Text) && !Rarities.Any(r => Parse.Key(l.Text).StartsWith(Parse.Key(r)))
                                           && !l.Text.TrimStart().StartsWith("+") && !l.Text.Contains("%"))
                               .OrderByDescending(l => l.Box.Height).ThenBy(l => l.Box.Y).FirstOrDefault();
                if (name != null) it.Name = name.Text.Trim();

                var over = card.Where(l => l != name && (name == null || l.CenterY < name.Box.Y + name.Box.Height / 3) && !l.Text.Contains("%"))
                               .OrderBy(l => l.Box.Y).ToList();
                foreach (var l in over)
                {
                    int r = ShopRarityOf(l.Text);
                    if (r >= 0) { it.Rarity = r; break; }
                }
                if (it.Rarity < 0 && over.Count > 0)
                {
                    var top0 = over[0];
                    var box = Rectangle.Intersect(Rectangle.Inflate(top0.Box, Math.Max(4, top0.Box.Height / 2), Math.Max(4, top0.Box.Height / 2)), area);
                    foreach (var t in new[] { Tuple.Create(PageScale(f) + 1, Prep.None), Tuple.Create(PageScale(f) + 1, Prep.Contrast) })
                    {
                        if (box.Width < 10 || box.Height < 5) break;
                        it.Rarity = Ocr.Read(f, box, t.Item1, t.Item2).Select(l => ShopRarityOf(l.Text)).Where(r => r >= 0).DefaultIfEmpty(-1).First();
                        if (it.Rarity >= 0) break;
                    }
                }
                page.Items.Add(it);
            }
            return page;
        }

        double RereadShopPrice(Frame f, OcrLine buy, Rectangle cash, out string text)
        {
            text = null;
            int bh = Math.Max(8, buy.Box.Height);
            var band = Rectangle.Intersect(Rectangle.FromLTRB(buy.Box.X - 3 * bh, buy.Box.Y - 3 * bh, buy.Box.Right + 3 * bh, buy.Box.Y - bh / 4), cash);
            if (band.Width < 10 || band.Height < 5) return -1;
            int s = PageScale(f);
            foreach (var t in new[] { Tuple.Create(s + 1, Prep.None), Tuple.Create(s + 1, Prep.Contrast), Tuple.Create(s + 2, Prep.None) })
                foreach (var l in Ocr.Read(f, band, t.Item1, t.Item2).OrderByDescending(l => l.Box.Right))
                {
                    double v;
                    if (!l.Text.Contains("$") || Regex.IsMatch(l.Text, @"R\s?\$") || (v = LastMoney(l.Text)) < 0 || !GreenInk(f, l.Box)) continue;
                    text = l.Text.Trim();
                    return v;
                }
            return -1;
        }

        internal static int ShopRarityOf(string text)
        {
            string k = Parse.Key(text ?? "");
            int r = Array.FindIndex(Rarities, x => k.StartsWith(Parse.Key(x)));
            if (r >= 0) return r;
            string w = Parse.Key(Regex.Match((text ?? "").Trim(), @"^[A-Za-z0-9]+").Value);
            if (w.Length < 4) return -1;
            for (int i = 0; i < Rarities.Length; i++)
            {
                string x = Parse.Key(Rarities[i]);
                if (x.Length >= 6 && w.StartsWith(x.Substring(0, 4))) return i;
                if (x.Length >= 5 && w.Length >= x.Length - 1 && View.Distance(w.Substring(0, Math.Min(w.Length, x.Length)), x) <= 1) return i;
            }
            return -1;
        }

        internal static double LastMoney(string text)
        {
            for (int i = (text ?? "").LastIndexOf('$'); i >= 0; i = i > 0 ? text.LastIndexOf('$', i - 1) : -1)
            {
                double v;
                if (Parse.Money(text.Substring(i), out v)) return v;
            }
            return -1;
        }

        internal static bool GreenInk(Frame f, Rectangle box)
        {
            box.Intersect(new Rectangle(0, 0, f.Width, f.Height));
            int green = 0;
            for (int y = box.Top; y < box.Bottom; y++)
                for (int x = box.Left; x < box.Right; x++)
                {
                    var c = f.Pixel(x, y);
                    if (c.G >= 100 && c.G - c.R >= 30 && c.G - c.B >= 30) green++;
                }
            return green * 100 >= 3 * Math.Max(1, box.Width * box.Height);
        }

        void PressShopEntry(OcrLine label, Tab t)
        {
            if ((t != Tab.Shop && t != Tab.Properties && t != Tab.BlackMarket) || View.Label(new List<OcrLine> { label }, View.TabLabels[t]) == null)
                throw new NeverPressException(label.Text, "not the " + View.TabLabels[t] + " entry");
            NotDisconnected();
            if (OnlyNavigate) RefuseInCheck(label.Text);
            Click(label.CenterX, label.CenterY, 650);
        }

        void PressShopBuy(OcrLine buy, ShopPage page)
        {
            if (Parse.Key(buy.Text) != "BUY" || !page.Cash.Contains(buy.Box)) throw new NeverPressException(buy.Text, "a BUY outside the shop's cash list");
            NotDisconnected();
            if (OnlyNavigate) RefuseInCheck(buy.Text);
            Click(buy.CenterX, buy.CenterY, 450);
        }

        public List<ShopItem> BuyFromShop(out int stockSeconds, double keep = 0, string prefer = null,
                                          List<ShopItem> tooDear = null, double minPrice = 0, Func<ShopItem, bool> want = null)
        {
            stockSeconds = -1;
            var bought = new List<ShopItem>();
            if (!OpenTab(Tab.Shop, true)) return bought;
            ShopPage page = null;

            for (int look = 0; look < 3 && page == null; look++)
            {
                if (look > 0) Wait(700);
                using (var f = Capture()) page = ReadShop(f);
            }
            if (page == null) { log("Shop: couldn't read the page - not buying"); Snapshot("shop not read", 60); return bought; }
            stockSeconds = page.StockSeconds;
            int x = page.Cash.X + page.Cash.Width / 2, y = page.Cash.Y + page.Cash.Height / 2;
            Scroll(x, y, 15);
            Wait(400);
            var tried = new HashSet<string>();
            string beforeScroll = null;
            int scrolls = 0;
            ShopPage carried = null;
            double carriedCash = -1;

            bool rechecked = false;
            var doubt = new Dictionary<string, string>();
            var seenFine = new HashSet<string>();
            Func<ShopItem, double, string> doubtOf = (i, cashNow) =>
            {
                if (i.Gold || (want != null && !want(i))) return null;
                if (i.Price < 0) return i.BuyGold ? "its price didn't read" : null;
                if (i.Price < minPrice) return null;
                if (cashNow < 0) return i.BuyGold ? "the cash on hand didn't read" : null;
                if (!i.BuyGold && i.Price <= cashNow - Math.Max(0, keep)) return "its BUY looked grey though the cash covers it";
                return null;
            };
            for (int look = 0; look < 60; look++)
            {
                double cash = -1;
                bool fromCarried = carried != null;
                if (carried != null) { page = carried; cash = carriedCash; carried = null; }
                else
                {

                    for (int again = 0; again < 3; again++)
                    {
                        if (again > 0) Wait(500);
                        using (var f = Capture()) { page = ReadShop(f); cash = ReadCash(f); }
                        if (page != null) break;
                    }
                }
                if (page == null) break;
                if (beforeScroll != null && page.Signature == beforeScroll) break;
                beforeScroll = null;
                foreach (var i in page.Items)
                    if (i.Name != null && i.Price >= 0 && doubtOf(i, cash) == null) { seenFine.Add(i.Name); doubt.Remove(i.Name); }
                if (tooDear != null)
                    foreach (var i in TooDear(page, cash, keep, want))
                    {
                        if (tried.Contains(ItemKey(i))) continue;
                        var had = tooDear.FirstOrDefault(d => ItemKey(d) == ItemKey(i));
                        if (had == null) tooDear.Add(i);
                        else if (had.Rarity < 0 && i.Rarity >= 0) had.Rarity = i.Rarity;
                    }
                var can = page.Items.Where(i => !tried.Contains(ItemKey(i)) && i.Price >= 0 && i.BuyGold && (want == null || want(i))
                                                && !i.Gold && i.Price >= minPrice && cash >= 0 && i.Price <= cash - Math.Max(0, keep)).ToList();

                var next = (prefer != null ? can.FirstOrDefault(i => i.Name != null && Parse.Key(i.Name) == Parse.Key(prefer)) : null) ?? can.FirstOrDefault();
                if (next == null)
                {
                    var unsure = page.Items.Where(i => !tried.Contains(ItemKey(i)) && doubtOf(i, cash) != null).ToList();
                    if (!rechecked && (fromCarried || unsure.Count > 0 || cash < 0)) { rechecked = true; Wait(400); continue; }
                    rechecked = false;
                    if (unsure.Count > 0)
                    {
                        foreach (var i in unsure.Where(i => i.Name == null || !seenFine.Contains(i.Name))) doubt[i.Name ?? "an item"] = doubtOf(i, cash);
                        if (doubt.Count > 0) Snapshot("shop card not acted on", 0, true);
                    }
                    foreach (var i in page.Items) tried.Add(ItemKey(i));
                    if (scrolls++ >= 12) break;
                    beforeScroll = page.Signature;
                    Scroll(x, y, -Math.Max(1, Math.Min(4, (page.Cash.Height - 150) / 98)));
                    Wait(500);
                    continue;
                }
                tried.Add(ItemKey(next));
                rechecked = false;
                PressShopBuy(next.Buy, page);

                string toast = null;
                ShopPage after = null;
                List<OcrLine> popup = null;
                double afterCash = -1;
                bool took = false;
                for (int settle = 0; settle < 2 && !took; settle++)
                {
                    if (settle > 0) Wait(500);
                    after = null;

                    for (int again = 0; again < 3 && after == null; again++)
                    {
                        if (again > 0) Wait(500);
                        using (var f = Capture())
                        {
                            popup = Ocr.Read(f, PopupArea(f), PopupScale(f), Prep.None);
                            after = ReadShop(f);
                            CheckForPurchasePrompt(popup, after != null);
                            if (after != null) afterCash = ReadCash(f);

                            if (toast == null) { string t = ToastIn(f); if (t != null && (Parse.Has(t, "BOUGHT") || Parse.Has(t, "PURCHASED"))) toast = t; }
                        }
                    }
                    if (after == null) break;
                    var same = after.Items.FirstOrDefault(i => i.Name != null && i.Name == next.Name);
                    took = same == null || (same != null && next.Owned >= 0 && same.Owned > next.Owned)
                           || (toast != null && (Parse.Has(toast, "BOUGHT") || Parse.Has(toast, "PURCHASED")));
                }

                if (afterCash >= 0)
                {
                    carried = after;
                    carriedCash = took && cash >= 0 && next.Price > 0 ? Math.Min(afterCash, cash - next.Price) : afterCash;
                }
                if (after == null)
                {
                    log("Shop: a window came up after BUY on " + next + " - stopping the shop for now: \"" + string.Join(" ", popup.Select(l => l.Text)) + "\"");
                    Snapshot("shop window after buy", 0, true);
                    break;
                }
                if (took) bought.Add(next);
                else log("Shop: BUY on " + next + " did nothing" + (toast != null ? " (\"" + toast + "\")" : ""));
            }
            if (tooDear != null) tooDear.RemoveAll(d => bought.Any(b => ItemKey(b) == ItemKey(d)));
            foreach (var b in bought) if (b.Name != null) doubt.Remove(b.Name);
            if (doubt.Count > 0) log("Shop: couldn't buy " + string.Join(", ", doubt.Select(d => d.Key + " (" + d.Value + ")")) + " - left in the shop");
            return bought;
        }

        static string ItemKey(ShopItem i) { return Parse.Key(i.Name ?? "") + "|" + Parse.Key(i.PriceText ?? ""); }

        public static List<ShopItem> TooDear(ShopPage page, double cash, double keep = 0, Func<ShopItem, bool> want = null)
        {
            if (page == null || cash < 0) return new List<ShopItem>();
            return page.Items.Where(i => !i.Gold && i.Price > 0 && i.Price > cash - Math.Max(0, keep) && (want == null || want(i))).ToList();
        }

        public sealed class PropertyCard
        {
            public string Name = "";
            public double Income = -1;
            public double Price = -1;
            public int Level = -1;
            public OcrLine Build;

            public bool BlockFull;

            public int Gold = -1;
            public override string ToString() { return (Name.Length > 0 ? Name : "a property") + " (" + SafehouseInfo.Money(Price) + ", " + (Gold > 0 ? "+" + Gold + " gold bar" + (Gold == 1 ? "" : "s") + "/day" : "+" + SafehouseInfo.Money(Income) + "/hr") + ")"; }
        }

        public sealed class PropertiesPage
        {
            public bool Build;
            public OcrLine BuildTab, BlockTab;
            public Rectangle Area;
            public List<PropertyCard> Cards = new List<PropertyCard>();
            public int LotsUsed = -1, LotsMax = -1, LotsOpen = -1;
            public bool Full;
        }

        static readonly Regex PerLotRx = new Regex(@"\$\s?([0-9OoIlS]{1,3}(?:[,.][0-9OoIlS]{1,3})*\s?[KMBT]?)\s*/\s*h", RegexOptions.IgnoreCase);

        internal static readonly Regex GoldPerDayRx = new Regex(@"\+?\s*([0-9OoIlS]{1,2})\s*G[o0]\s?[lI1]d\s*Bars?\s*/\s*d\s?a\s?y", RegexOptions.IgnoreCase);

        static readonly Regex BlockFullRx = new Regex(@"^\s*B\s?[LI1]\s?O\s?C\s?K\s*F\s?U\s?[LI1]\s?[LI1].*\$", RegexOptions.IgnoreCase);
        static readonly Regex BuildPriceRx = new Regex(@"^\s*BUI[LI1]D\s*\$\s?([0-9OoIlS]{1,3}(?:[,.][0-9OoIlS]{1,3})*\s?[KMBT]?)\s*$", RegexOptions.IgnoreCase);

        static double ShortPrice(Frame f, OcrLine line, double first)
        {
            int h = Math.Max(8, line.Box.Height);
            var r = Rectangle.Intersect(Rectangle.Inflate(line.Box, h / 2, h / 2), new Rectangle(0, 0, f.Width, f.Height));
            var reads = new List<Tuple<double, bool>>();
            foreach (var t in new[] { Tuple.Create(3, Prep.None), Tuple.Create(2, Prep.None), Tuple.Create(3, Prep.Contrast), Tuple.Create(4, Prep.None) })
            {
                string text = Ocr.ReadText(f, r, t.Item1, t.Item2);
                var m = Regex.Match(text, @"\$\s?([0-9OoIlS]{1,3}(?:[,.][0-9OoIlS]{1,3})*)\s?[KMBT](?![A-Za-z])", RegexOptions.IgnoreCase);
                double v;
                if (m.Success && Parse.Money(m.Value, out v) && v > 0) { reads.Add(Tuple.Create(v, Regex.IsMatch(m.Groups[1].Value, "[,.]"))); continue; }

                var b = Regex.Match(text, @"\$\s?([0-9OoIlS]{1,3}\.[0-9OoIlS]{1,2})8(?![0-9A-Za-z])");
                if (b.Success && Parse.Money("$" + b.Groups[1].Value + "B", out v) && v > 0) reads.Add(Tuple.Create(v, true));
            }
            var pointed = reads.Where(x => x.Item2).ToList();
            var pool = pointed.Count > 0 ? pointed : reads;
            return pool.Count == 0 ? first : pool.GroupBy(x => x.Item1).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).First().Key;
        }

        public PropertiesPage ReadProperties(Frame f)
        {
            var area = PageArea(f);
            int s = PageScale(f);
            var lines = Ocr.Read(f, area, s, Prep.None);
            var block = lines.FirstOrDefault(l => Parse.Key(l.Text) == "YOURBIOCK" && l.Box.Y < area.Top + area.Height / 4);
            if (block == null) return null;
            int h = Math.Max(8, block.Box.Height);
            var tab = lines.FirstOrDefault(l => Parse.Key(l.Text) == "BUIID" && Math.Abs(l.CenterY - block.CenterY) < h && l.Box.X > block.Box.Right);

            var tabSpot = Rectangle.Intersect(new Rectangle(block.Box.Right + h / 2, block.Box.Y - h, block.Box.Width * 2, 3 * h), area);
            foreach (var t in new[] { Tuple.Create(s, Prep.None), Tuple.Create(s + 1, Prep.GreyText), Tuple.Create(s + 1, Prep.Contrast) })
            {
                if (tab != null || tabSpot.Width < 10 || tabSpot.Height < 5) break;
                tab = Ocr.Read(f, tabSpot, t.Item1, t.Item2).FirstOrDefault(l => Parse.Key(l.Text) == "BUIID" && Math.Abs(l.CenterY - block.CenterY) < h);
            }
            var page = new PropertiesPage { BuildTab = tab, BlockTab = block, Area = area };

            var all = new List<OcrLine>(lines);
            foreach (var d in Ocr.Read(f, area, s, Prep.DarkText))
                if (BuildPriceRx.IsMatch(d.Text) && !all.Any(l => l.Box.IntersectsWith(d.Box) && BuildPriceRx.IsMatch(l.Text))) all.Add(d);
            var incomes = all.Where(l => Parse.Has(l.Text, "PER LOT") && (PerLotRx.IsMatch(l.Text) || GoldPerDayRx.IsMatch(l.Text))).ToList();
            page.Build = incomes.Count > 0;
            foreach (var inc in incomes)
            {
                var card = new PropertyCard();
                double v;
                var gm = GoldPerDayRx.Match(inc.Text);
                int gold;
                if (gm.Success && !PerLotRx.IsMatch(inc.Text)) { if (int.TryParse(Parse.Digits(gm.Groups[1].Value), out gold) && gold > 0) card.Gold = gold; }
                else if (Parse.Money(PerLotRx.Match(inc.Text).Value.Replace("/", " ").Trim(), out v)) card.Income = v;
                int ih = Math.Max(8, inc.Box.Height), half = Math.Max(inc.Box.Width / 2, 3 * ih);
                Func<OcrLine, bool> column = l => Math.Abs(l.CenterX - inc.CenterX) < half;
                var name = all.Where(l => l != inc && column(l) && l.Box.Bottom <= inc.Box.Y + ih / 2 && inc.Box.Y - l.Box.Bottom < 2 * ih
                                          && Regex.Matches(l.Text, "[A-Za-z]").Count >= 3 && !l.Text.Contains("$"))
                              .OrderByDescending(l => l.Box.Bottom).FirstOrDefault();
                if (name != null) card.Name = name.Text.Trim();
                var below = all.Where(l => l != inc && column(l) && l.Box.Y >= inc.Box.Bottom - ih / 2 && l.Box.Y - inc.Box.Bottom < 3 * ih)
                               .OrderBy(l => l.Box.Y).ToList();
                foreach (var b in below)
                {
                    var bm = BuildPriceRx.Match(b.Text);
                    if (bm.Success)
                    {
                        if (Parse.Money("$" + bm.Groups[1].Value, out v)) { card.Price = v; card.Build = b; }
                        break;
                    }
                    var lm = Regex.Match(b.Text, @"^\s*LEVEL\s*([0-9OoIlS]{1,3})\s*$", RegexOptions.IgnoreCase);
                    int lv;
                    if (lm.Success && int.TryParse(Parse.Digits(lm.Groups[1].Value), out lv)) { card.Level = lv; break; }
                    if (Parse.Has(b.Text, "YOU HAVE") && Parse.Money(b.Text, out v)) { card.Price = ShortPrice(f, b, v); break; }
                    if (BlockFullRx.IsMatch(b.Text) && Parse.Money(b.Text.Substring(b.Text.IndexOf('$')), out v) && v > 0) { card.Price = ShortPrice(f, b, v); card.BlockFull = true; break; }
                }
                page.Cards.Add(card);
            }
            if (!page.Build)
            {

                var lots = lines.FirstOrDefault(l => Parse.Key(l.Text) == "IOTS");
                if (lots != null)
                {
                    int lh = Math.Max(6, lots.Box.Height);
                    var frac = lines.Where(l => l.Box.Y > lots.Box.Y && l.Box.Y - lots.Box.Bottom < 3 * lh && Math.Abs(l.CenterX - lots.CenterX) < 4 * lh).ToList();
                    var said = frac.Select(l => l.Text).ToList();
                    foreach (var t in said) if (LotsCount(t, page)) break;

                    var spot = Rectangle.Intersect(new Rectangle(lots.Box.X - lh, lots.Box.Bottom, 14 * lh, 3 * lh), new Rectangle(0, 0, f.Width, f.Height));
                    if (page.LotsMax < 0 && spot.Width > 10 && spot.Height > 5)
                        foreach (var t in new[] { Tuple.Create(4, Prep.Contrast), Tuple.Create(3, Prep.Contrast), Tuple.Create(2, Prep.WhiteText), Tuple.Create(6, Prep.Contrast), Tuple.Create(5, Prep.DarkText) })
                        {
                            var more = Ocr.Read(f, spot, t.Item1, t.Item2).Select(l => l.Text).ToList();
                            said.AddRange(more);
                            if (more.Any(x => LotsCount(x, page))) break;
                        }

                    page.Full = LotsInColour(f, Rectangle.Intersect(new Rectangle(spot.X, spot.Y, spot.Width, (int)(2.6 * lh)), spot));
                    if (page.Full && page.LotsMax < 0)
                    {

                        foreach (var t in said)
                        {
                            var m = Regex.Match(t, @"^\s*(?:/\s*)?([0-9OoIlS]{1,3})\s*$|^\s*([0-9OoIlS]{1,2})\s*[\s1lI|\[\]]\s*([0-9OoIlS]{1,2})\s*$");
                            int a, b;
                            if (!m.Success) continue;
                            if (m.Groups[1].Success && int.TryParse(Parse.Digits(m.Groups[1].Value), out a) && a > 0) { page.LotsUsed = page.LotsMax = a; break; }
                            if (m.Groups[2].Success && int.TryParse(Parse.Digits(m.Groups[2].Value), out a) && int.TryParse(Parse.Digits(m.Groups[3].Value), out b) && a == b && a > 0) { page.LotsUsed = page.LotsMax = a; break; }
                        }
                    }

                    var open = lines.Select(l => new { l, m = Regex.Match(l.Text, @"^\s*([0-9OoIlS]{1,3})\s*[O0][PO0R]EN\s*$", RegexOptions.IgnoreCase) })
                                    .FirstOrDefault(x => Math.Abs(x.l.CenterY - lots.CenterY) < 2 * lh && x.m.Success);
                    int o;
                    if (open != null && int.TryParse(Parse.Digits(open.m.Groups[1].Value), out o)) page.LotsOpen = o;
                    else if (page.LotsMax > 0 && page.LotsUsed >= 0) page.LotsOpen = page.LotsMax - page.LotsUsed;
                    else if (page.Full) page.LotsOpen = 0;

                    if (page.Full) page.LotsOpen = 0;
                }
            }
            return page;
        }

        static bool LotsCount(string text, PropertiesPage page)
        {
            var m = Regex.Match(text ?? "", @"([0-9OoIlS]{1,3})\s*[/|\\]\s*([0-9OoIlS]{1,3})");
            int a, b;
            if (!m.Success || !int.TryParse(Parse.Digits(m.Groups[1].Value), out a) || !int.TryParse(Parse.Digits(m.Groups[2].Value), out b) || a > b || b <= 0) return false;
            page.LotsUsed = a; page.LotsMax = b;
            return true;
        }

        static bool LotsInColour(Frame f, Rectangle spot)
        {
            int colour = 0, white = 0;
            for (int y = spot.Top; y < spot.Bottom; y++)
                for (int x = spot.Left; x < spot.Right; x++)
                {
                    var p = f.Pixel(x, y);
                    int hi = Math.Max(p.R, Math.Max(p.G, p.B)), lo = Math.Min(p.R, Math.Min(p.G, p.B));
                    if (hi > 150 && hi - lo > 90) colour++;
                    else if (lo > 200) white++;
                }
            return colour >= 20 && colour > 3 * white;
        }

        public sealed class TutorialBox { public string Hint = ""; public OcrLine Skip, Done; }

        public TutorialBox ReadTutorial(Frame f)
        {
            var p = PageArea(f);
            var area = new Rectangle(p.Left, p.Top + p.Height * 3 / 4, p.Width, p.Height - p.Height * 3 / 4);
            int s = PageScale(f);
            var lines = Ocr.Read(f, area, s, Prep.None);
            var button = lines.FirstOrDefault(l => Parse.Key(l.Text) == "SKIP") ?? lines.FirstOrDefault(l => Parse.Key(l.Text) == "DONE");

            List<OcrLine> plainWords = null;
            foreach (var prep in new[] { Prep.DarkText, Prep.None })
            {
                if (button != null) break;
                var words = Ocr.ReadWords(f, area, s, prep);
                if (prep == Prep.None) plainWords = words;
                button = words.FirstOrDefault(w => Parse.Key(w.Text) == "SKIP") ?? words.FirstOrDefault(w => Parse.Key(w.Text) == "DONE");
            }
            if (button == null && plainWords != null)
            {
                var said = plainWords.Where(w => w.Box.X > p.Left + p.Width / 6 && Regex.IsMatch(w.Text, "[A-Za-z]{2}")).ToList();
                if (said.Count >= 5)
                {
                    int mh = said.Select(w => w.Box.Height).OrderBy(x => x).ElementAt(said.Count / 2);
                    var block = said.Where(w => w.Box.Height >= 0.7 * mh).ToList();
                    int right = block.Max(w => w.Box.Right), top = block.Min(w => w.Box.Y), bottom = block.Max(w => w.Box.Bottom);
                    var spot = Rectangle.Intersect(new Rectangle(right + mh / 3, top - mh, 12 * mh, bottom - top + 2 * mh), area);
                    foreach (var t in new[] { Tuple.Create(3, Prep.DarkText), Tuple.Create(3, Prep.Contrast), Tuple.Create(4, Prep.None), Tuple.Create(2, Prep.DarkText) })
                    {
                        if (spot.Width < 10 || spot.Height < 10) break;
                        button = Ocr.Read(f, spot, t.Item1, t.Item2).FirstOrDefault(l => Parse.Key(l.Text) == "DONE" || Parse.Key(l.Text) == "SKIP");
                        if (button != null) break;
                    }
                }
            }
            if (button == null) return null;
            int h = Math.Max(8, button.Box.Height);
            var hint = lines.Where(l => l != button && l.Box.X < button.Box.X - h && Math.Abs(l.CenterY - button.CenterY) < 3 * h && l.Box.X > p.Left + p.Width / 6
                                        && Parse.Key(l.Text) != "DONE" && Parse.Key(l.Text) != "SKIP")
                            .OrderBy(l => l.Box.Y / Math.Max(1, h)).ThenBy(l => l.Box.X).ToList();
            string text = string.Join(" ", hint.Select(l => Regex.Replace(l.Text.Trim(), @"\s+D\s?o(ne)?\s*$", "")));
            if (Regex.Matches(text, "[A-Za-z]").Count < 12) return null;
            var box = new TutorialBox { Hint = text };
            if (Parse.Key(button.Text) == "SKIP") box.Skip = button; else box.Done = button;
            return box;
        }

        public void PressTutorial(OcrLine button)
        {
            string k = Parse.Key(button.Text);
            if (k != "DONE" && k != "SKIP") throw new NeverPressException(button.Text, "not the tutorial's DONE or SKIP");
            Press(button, 800);
        }

        void PressPropertyBuild(PropertyCard c, PropertiesPage page, double cash)
        {
            var b = c.Build;
            if (b == null || !page.Build || !BuildPriceRx.IsMatch(b.Text) || (c.Income <= 0 && c.Gold <= 0) || c.Price <= 0 || cash < c.Price || !page.Area.Contains(b.Box))
                throw new NeverPressException(b != null ? b.Text : "BUILD", "not a property's BUILD within the cash on hand");
            NotDisconnected();
            if (OnlyNavigate) RefuseInCheck(b.Text);
            Click(b.CenterX, b.CenterY, 900);
        }

        bool BuildSeen(PropertyCard c, double cashBefore, int lotsBefore)
        {
            for (int look = 0; look < 3; look++)
            {
                if (look > 0) Wait(600);
                PropertiesPage p;
                double cash;
                using (var f = Capture()) { p = ReadProperties(f); cash = ReadHeader(f).Cash; }
                double down = cashBefore - cash;
                if (cashBefore > 0 && cash >= 0 && c.Price > 0 && down >= c.Price * 0.9 && down <= c.Price * 1.1) return true;
                if (p != null && !p.Build && lotsBefore > 0 && p.LotsOpen >= 0 && p.LotsOpen < lotsBefore) return true;
            }
            return false;
        }

        public sealed class BlockLot
        {
            public string Name = "";
            public double Income = -1;
            public int Gold = -1;
            public Point Picture;
            public override string ToString() { return (Name.Length > 0 ? Name : "a property") + " (" + (Gold > 0 ? Gold + " gold bar" + (Gold == 1 ? "" : "s") + "/day" : SafehouseInfo.Money(Income) + "/hr") + ")"; }
        }

        static readonly Regex LotGoldRx = new Regex(@"^\s*(?:\S{1,2}\s+)?\+?\s*([0-9OoIlS]{1,2})\s*G[o0]\s?[lI1]d\s*Bars?\s*/\s*d\s?a\s?y\s*$", RegexOptions.IgnoreCase);
        static readonly Regex LotIncomeRx = new Regex(@"^\s*(?:\S{1,2}\s+)?\$\s?[0-9OoIlS]{1,3}(?:[,.][0-9OoIlS]{1,3})*\s?[KMBT]?\s*/\s*hr?\s*$", RegexOptions.IgnoreCase);

        public List<BlockLot> ReadBlockLots(Frame f) { return ReadBlockLots(f, null); }

        public List<BlockLot> ReadBlockLots(Frame f, List<PropertyCard> goldCards)
        {
            var area = PageArea(f);
            int s = PageScale(f);

            var lines = new List<OcrLine>();
            for (int i = 0; i < 5; i++)
            {
                int top = area.Top + area.Height * 15 * i / 100;
                var band = Rectangle.FromLTRB(area.Left, top, area.Right, Math.Min(area.Bottom, top + area.Height * 40 / 100));
                foreach (var l in Ocr.Read(f, band, s, Prep.None))
                {

                    if ((band.Top > area.Top && l.Box.Y < band.Top + 4) || (band.Bottom < area.Bottom && l.Box.Bottom > band.Bottom - 4)) continue;
                    if (!lines.Any(x => x.Text == l.Text && Math.Abs(x.CenterX - l.CenterX) < 6 && Math.Abs(x.CenterY - l.CenterY) < 6)) lines.Add(l);
                }
            }

            var income = lines.FirstOrDefault(l => Parse.Key(l.Text) == "INCOME");
            var notNames = new[] { "PROPERTIES", "YOURBLOCK", "BUILD", "INCOME", "LOTS", "GOLDBARS" }.Select(Parse.Key).ToArray();
            var lots = new List<BlockLot>();
            foreach (var inc in lines.Where(l => LotIncomeRx.IsMatch(l.Text) || LotGoldRx.IsMatch(l.Text)))
            {
                int h = Math.Max(8, inc.Box.Height);
                if (income != null && inc.Box.Y >= income.Box.Y && inc.Box.Y - income.Box.Bottom < 3 * h && Math.Abs(inc.Box.X - income.Box.X) < 4 * h) continue;

                var name = lines.Where(l => l != inc && l.Box.Bottom < inc.Box.Y && inc.Box.Y - l.Box.Bottom > 3 * h && inc.Box.Y - l.Box.Bottom < 18 * h
                                            && Math.Abs(l.CenterX - inc.CenterX) < Math.Max(3 * h, l.Box.Width / 2)
                                            && Regex.Matches(l.Text, "[A-Za-z]").Count >= 4 && !l.Text.Contains("$") && !l.Text.Contains("+") && !LotGoldRx.IsMatch(l.Text))
                                .OrderByDescending(l => l.Box.Bottom).FirstOrDefault();
                if (name == null || notNames.Contains(Parse.Key(name.Text)) || name.Box.Y < area.Top + 2 || inc.Box.Bottom > area.Bottom - 2) continue;
                double v = -1;
                int gold = -1;
                var gm = LotGoldRx.Match(inc.Text);
                if (gm.Success) { if (!int.TryParse(Parse.Digits(gm.Groups[1].Value), out gold) || gold <= 0) continue; }
                else if (!Parse.Money(inc.Text.Substring(inc.Text.IndexOf('$')).Replace("/", " "), out v) || v <= 0) continue;
                var at = new Point(name.CenterX, (name.CenterY + inc.CenterY) / 2);
                if (lots.Any(x => Math.Abs(x.Picture.X - at.X) < 3 * h && Math.Abs(x.Picture.Y - at.Y) < 3 * h)) continue;
                lots.Add(new BlockLot { Name = name.Text.Trim(), Income = v, Gold = gold, Picture = at });
            }

            if (goldCards != null)
                foreach (var l in lines)
                {
                    var card = goldCards.FirstOrDefault(c => c.Gold > 0 && SameName(l.Text, c.Name));
                    if (card == null || l.Box.Y < area.Top + 2) continue;
                    int h = Math.Max(8, l.Box.Height);
                    if (lots.Any(x => Math.Abs(x.Picture.X - l.CenterX) < 3 * h && x.Picture.Y > l.CenterY && x.Picture.Y - l.CenterY < 12 * h)) continue;
                    var at = new Point(l.CenterX, l.CenterY + 4 * h);
                    if (lots.Any(x => Math.Abs(x.Picture.X - at.X) < 3 * h && Math.Abs(x.Picture.Y - at.Y) < 6 * h)) continue;
                    lots.Add(new BlockLot { Name = l.Text.Trim(), Gold = card.Gold, Picture = at });
                }
            return lots.OrderBy(l => l.Picture.Y).ThenBy(l => l.Picture.X).ToList();
        }

        bool OpenYourBlock()
        {
            if (!OpenTab(Tab.Properties, true)) return false;
            for (int look = 0; look < 3; look++)
            {
                PropertiesPage page;
                using (var f = Capture()) page = ReadProperties(f);
                if (page == null) { Wait(700); continue; }
                if (!page.Build) return true;
                if (page.BlockTab == null) return false;
                Press(page.BlockTab, 1000);
            }
            return false;
        }

        public bool BuildReady(PropertyCard target)
        {
            PropertiesPage page;
            var card = FindCard(target, out page);
            if (card != null && card.Build != null && card.Price > 0) return true;
            if (card != null && card.BlockFull && card.Price > 0)
            {
                double cash;
                using (var f = Capture()) cash = ReadHeader(f).Cash;
                if (cash >= card.Price) return true;
            }
            Snapshot(card == null ? "property card not found" : "property build not gold", 60);
            return false;
        }

        PropertyCard FindCard(PropertyCard target, out PropertiesPage page)
        {
            page = null;
            if (target == null || !OpenTab(Tab.Properties, true)) return null;
            bool top = false;
            string last = null;
            for (int look = 0; look < 12; look++)
            {
                using (var f = Capture()) page = ReadProperties(f);
                if (page == null) { Wait(700); continue; }
                if (!page.Build)
                {
                    if (page.BuildTab == null) return null;
                    Press(page.BuildTab, 1000);
                    continue;
                }
                if (!top) { top = true; ScrollPage(0.62, 15); Wait(500); continue; }
                var card = page.Cards.FirstOrDefault(c => SameCard(c, target));
                if (card != null) return card;
                string sig = string.Join("|", page.Cards.Select(c => c.Name + "@" + (c.Build != null ? c.Build.Box.Y : 0)));
                if (sig == last) break;
                last = sig;
                ScrollPage(0.62, -ListNotches(3));
                Wait(500);
            }
            page = null;
            return null;
        }

        public List<PropertyCard> ReadBuildCards()
        {
            if (!OpenTab(Tab.Properties, true)) return null;
            var all = new List<PropertyCard>();
            bool top = false, read = false;
            string last = null;
            for (int look = 0; look < 14; look++)
            {
                PropertiesPage page;
                using (var f = Capture()) page = ReadProperties(f);
                if (page == null) { Wait(700); continue; }
                if (!page.Build)
                {
                    if (page.BuildTab == null) return null;
                    Press(page.BuildTab, 1000);
                    continue;
                }
                if (!top) { top = true; ScrollPage(0.62, 15); Wait(500); continue; }
                read = true;
                foreach (var c in page.Cards.Where(c => c.Level < 0 && c.Price > 0 && (c.Income > 0 || c.Gold > 0)))
                {
                    all.RemoveAll(x => SameCard(x, c));
                    c.Build = null;
                    all.Add(c);
                }
                string sig = string.Join("|", page.Cards.Select(c => c.Name + c.Price));
                if (sig == last) break;
                last = sig;
                ScrollPage(0.62, -ListNotches(3));
                Wait(500);
            }
            return read ? all : null;
        }

        public bool BuildCard(PropertyCard target, out string why)
        {
            why = null;
            PropertiesPage page;
            var card = FindCard(target, out page);
            if (card == null) { why = target.Name + "'s card wasn't found on the BUILD page"; Snapshot("property card not found", 60); return false; }
            double cash;
            using (var f = Capture()) cash = ReadHeader(f).Cash;
            if (card.Build == null || card.Price <= 0 || cash < 0 || card.Price > cash) { why = target.Name + "'s BUILD isn't showing gold"; return false; }
            PressPropertyBuild(card, page, cash);
            string toast = ReadToast(1500, t => Parse.Has(t, "BOUGHT"));
            using (var f = Capture()) CheckForPurchasePrompt(Ocr.Read(f, PopupArea(f), PopupScale(f), Prep.None));
            if (toast != null || BuildSeen(card, cash, -1)) return true;
            why = "BUILD on " + card + " did nothing";
            Snapshot("property build did nothing", 60);
            return false;
        }

        public List<BlockLot> ReadBlock(List<PropertyCard> goldCards, out int lotsOpen, int notches = 3)
        {
            lotsOpen = -1;
            if (!OpenYourBlock()) return null;
            ScrollPage(0.62, 15);
            Wait(400);
            PropertiesPage top;
            using (var f = Capture()) top = ReadProperties(f);
            if (top != null && !top.Build) lotsOpen = top.LotsOpen;
            var all = new List<BlockLot>();
            string last = null;
            for (int page = 0; page < 24 / notches; page++)
            {
                List<BlockLot> seen;
                using (var f = Capture()) seen = ReadBlockLots(f, goldCards);
                string sig = string.Join("|", seen.Select(l => l.Name + l.Income + l.Gold + "@" + l.Picture.Y));
                if (sig == last) break;
                last = sig;
                all.AddRange(seen);
                ScrollPage(0.62, -ListNotches(notches));
            }
            return all;
        }

        public int GoldLotsOwned(List<BlockLot> lots)
        {
            var kinds = new List<BlockLot>();
            foreach (var l in lots.Where(l => l.Gold > 0)) if (!kinds.Any(k => SameLot(k, l))) kinds.Add(l);
            if (kinds.Count == 0) return 0;
            int total = 0;
            foreach (var kind in kinds)
            {
                var lot = LotInSight(kind);
                if (lot == null) return -1;
                NotDisconnected();
                if (OnlyNavigate) RefuseInCheck(lot.Name);
                Click(lot.Picture.X, lot.Picture.Y, 900);
                LotWindow w = null;
                for (int look = 0; look < 3 && w == null; look++)
                {
                    if (look > 0) Wait(500);
                    using (var f = Capture()) w = ReadLotWindow(f);
                }
                if (w == null) { Snapshot("gold lot window not read", 60, true); return -1; }
                bool ok = !w.Question && w.Names(kind) && w.Owned > 0;
                int owned = w.Owned;
                CloseLotWindow(w);
                if (!ok) { Snapshot("gold lot window not read", 60, true); return -1; }
                total += owned;
            }
            return total;
        }

        BlockLot LotInSight(BlockLot want)
        {
            if (!OpenYourBlock()) return null;
            ScrollPage(0.62, 15);
            var gold = want.Gold > 0 ? new List<PropertyCard> { new PropertyCard { Name = want.Name, Gold = want.Gold } } : null;
            string last = null;
            for (int page = 0; page < 8; page++)
            {
                List<BlockLot> seen;
                using (var f = Capture()) seen = ReadBlockLots(f, gold);
                var lot = seen.FirstOrDefault(l => SameLot(l, want));
                if (lot != null) return lot;
                string sig = string.Join("|", seen.Select(l => l.Name + l.Income + l.Gold + "@" + l.Picture.Y));
                if (sig == last) break;
                last = sig;
                ScrollPage(0.62, -ListNotches(3));
            }
            return null;
        }

        public static BlockLot WorstLot(params List<BlockLot>[] reads)
        {
            return reads.Where(r => r != null).SelectMany(r => r).Where(l => l.Income > 0 && l.Gold <= 0).OrderBy(l => l.Income).FirstOrDefault();
        }

        public static BlockLot WorstGoldLot(params List<BlockLot>[] reads)
        {
            return reads.Where(r => r != null).SelectMany(r => r).Where(l => l.Gold > 0).OrderBy(l => l.Gold).FirstOrDefault();
        }

        void PressDemolish(OcrLine b, bool ok, string why)
        {
            if (b == null || !ok || Parse.Key(b.Text) != "DEMOIISH") throw new NeverPressException(b != null ? b.Text : "DEMOLISH", why);
            NotDisconnected();
            if (OnlyNavigate) RefuseInCheck(b.Text);
            Click(b.CenterX, b.CenterY, 900);
        }

        static bool SameName(string a, string b)
        {
            string x = Parse.Key(a ?? ""), y = Parse.Key(b ?? "");
            return x.Length >= 4 && y.Length >= 4 && (x == y || x.Contains(y) || y.Contains(x) || View.Distance(x, y) <= Math.Max(1, Math.Min(x.Length, y.Length) / 8));
        }

        static bool SameIncome(double a, double b) { return a > 0 && b > 0 && Math.Abs(a - b) <= 0.02 * Math.Max(a, b); }

        static bool SameLot(BlockLot a, BlockLot b) { return SameName(a.Name, b.Name) && (b.Gold > 0 ? a.Gold == b.Gold : a.Gold <= 0 && SameIncome(a.Income, b.Income)); }

        internal static bool SameCard(PropertyCard a, PropertyCard b) { return SameName(a.Name, b.Name) && (b.Gold > 0 ? a.Gold == b.Gold : a.Gold <= 0 && SameIncome(a.Income, b.Income)); }

        public sealed class LotWindow
        {
            public string Name = ""; public double Income = -1; public OcrLine Demolish, Close, Cancel; public bool Question;

            public OcrLine Move;
            public OcrLine X;
            public int Gold = -1;
            public int Owned = -1;

            public bool Names(BlockLot lot) { return SameName(Name, lot.Name) && (lot.Gold > 0 ? Gold == lot.Gold : SameIncome(Income, lot.Income)); }
            public string Says { get { return Name + " (" + (Gold > 0 ? Gold + " gold bars/day" : SafehouseInfo.Money(Income) + "/hr") + ")"; } }
        }

        static readonly Regex GoldBarsRx = new Regex(@"([0-9OoIlS]{1,2})\s*G[o0]\s?[lI1]d\s*Bars?", RegexOptions.IgnoreCase);

        static int GoldIn(string text)
        {
            var m = GoldBarsRx.Match(text ?? "");
            int n;
            return m.Success && int.TryParse(Parse.Digits(m.Groups[1].Value), out n) && n > 0 ? n : -1;
        }

        public LotWindow ReadLotWindow(Frame f)
        {
            var lines = Ocr.Read(f, PopupArea(f), PopupScale(f), Prep.None);
            CheckForPurchasePrompt(lines);
            var demolish = lines.Where(l => Parse.Key(l.Text) == "DEMOIISH").ToList();
            foreach (var d in demolish)
            {
                int h = Math.Max(8, d.Box.Height);
                Func<OcrLine, bool> level = l => Math.Abs(l.CenterY - d.CenterY) < h;
                var close = lines.FirstOrDefault(l => Parse.Key(l.Text) == "CIOSE" && level(l) && l.Box.X > d.Box.Right);
                var cancel = lines.FirstOrDefault(l => Parse.Key(l.Text) == "CANCEI" && level(l) && l.Box.Right < d.Box.X);

                var move = close != null ? null : lines.FirstOrDefault(l => (Parse.Key(l.Text) == "MOVE" || Parse.Key(l.Text) == "BUIIDANOTHER") && level(l) && l.Box.X > d.Box.Right);
                var w = new LotWindow { Demolish = d, Close = close, Cancel = cancel, Move = move };
                double v;
                if (close != null || move != null)
                {
                    if (move != null)
                        w.X = lines.Where(l => l.Box.Bottom < d.Box.Y && l.Box.X > move.Box.X && Regex.IsMatch(l.Text.Trim(), "^[xX×]$")).OrderBy(l => l.Box.Y).FirstOrDefault();

                    var title = lines.Where(l => l.Box.Bottom < d.Box.Y && l.Box.Height >= 2 * h && Regex.Matches(l.Text, "[A-Za-z]").Count >= 4 && l.Box.X >= d.Box.X - 6 * h)
                                     .OrderByDescending(l => l.Box.Height).FirstOrDefault();
                    var each = lines.FirstOrDefault(l => Parse.Has(l.Text, "EACH EARNS") && l.Box.Bottom < d.Box.Y);
                    var money = each == null ? null : lines.Where(l => l != each && Math.Abs(l.CenterY - each.CenterY) < Math.Max(8, each.Box.Height) && l.Box.X > each.Box.Right)
                                                           .FirstOrDefault(l => l.Text.Contains("$") || GoldIn(l.Text) > 0);

                    int gold = money != null ? GoldIn(money.Text) : each != null ? GoldIn(each.Text) : -1;
                    var owned = lines.Select(l => Regex.Match(l.Text, @"TOTAL\s*OWNED\s*:?\s*([0-9OoIlS]{1,3})", RegexOptions.IgnoreCase)).FirstOrDefault(m => m.Success);
                    int n;
                    if (owned != null && int.TryParse(Parse.Digits(owned.Groups[1].Value), out n)) w.Owned = n;
                    else
                    {

                        var label = lines.FirstOrDefault(l => Parse.Has(l.Text, "TOTAL OWNED") && l.Box.Bottom < d.Box.Y);
                        if (label != null && money != null && money.Box.Right > label.Box.Right)
                        {
                            int lh = Math.Max(8, label.Box.Height);
                            var ink = InkBox(f, Rectangle.FromLTRB(label.Box.Right + 2 * lh, label.Box.Y - lh / 2, money.Box.Right + lh, label.Box.Bottom + lh / 2));
                            int o = ink.Width > 0 && ink.Height >= lh / 2 && ink.Height <= 2 * lh ? ReadLoneNumber(f, Rectangle.Inflate(ink, Math.Max(4, lh / 4), Math.Max(4, lh / 4)), new[] { 2, 3, 4 }) : -1;
                            if (o > 0) w.Owned = o;
                        }
                    }
                    if (title == null) continue;
                    if (gold > 0) { w.Name = title.Text.Trim(); w.Gold = gold; return w; }
                    if (money == null || !Parse.Money(money.Text.Replace("/", " "), out v)) continue;
                    w.Name = title.Text.Trim(); w.Income = v;
                    return w;
                }
                if (cancel != null)
                {

                    var title = lines.FirstOrDefault(l => l.Box.Bottom < d.Box.Y && Regex.IsMatch(l.Text, @"^\s*Demolish\s+.+\?\s*$", RegexOptions.IgnoreCase));
                    var stops = lines.FirstOrDefault(l => Parse.Has(l.Text, "STOPS EARNING") && l.Box.Bottom < d.Box.Y);
                    if (title == null || stops == null) continue;
                    int gold = GoldIn(stops.Text);
                    v = -1;
                    var m = Regex.Match(stops.Text, @"\$\s?[0-9OoIlS]{1,3}(?:[,.][0-9OoIlS]{1,3})*\s?[KMBT]?");
                    if (gold <= 0 && (!m.Success || !Parse.Money(m.Value, out v))) continue;
                    w.Question = true;
                    w.Name = Regex.Replace(title.Text.Trim(), @"^\s*Demolish\s+|\?\s*$", "", RegexOptions.IgnoreCase).Trim();
                    if (gold > 0) w.Gold = gold; else w.Income = v;
                    return w;
                }
            }
            return null;
        }

        public bool DemolishLot(BlockLot worst, out string why)
        {
            why = null;
            if (!OpenYourBlock()) { why = "YOUR BLOCK didn't open"; return false; }
            ScrollPage(0.62, 15);

            PropertiesPage first;
            using (var f = Capture()) first = ReadProperties(f);
            if (first != null && first.LotsOpen > 0) { why = "a lot is free already (" + first.LotsOpen + " open)"; return false; }
            BlockLot lot = null;
            string last = null;
            for (int page = 0; page < 8 && lot == null; page++)
            {
                List<BlockLot> seen;
                using (var f = Capture()) seen = ReadBlockLots(f);
                lot = seen.FirstOrDefault(l => SameLot(l, worst));
                if (lot != null) break;
                string sig = string.Join("|", seen.Select(l => l.Name + l.Income + "@" + l.Picture.Y));
                if (sig == last) break;
                last = sig;
                ScrollPage(0.62, -ListNotches(3));
            }
            if (lot == null) { why = "no " + worst + " in sight on YOUR BLOCK"; return false; }
            NotDisconnected();
            if (OnlyNavigate) RefuseInCheck(lot.Name);
            Click(lot.Picture.X, lot.Picture.Y, 900);
            LotWindow w = null;
            for (int look = 0; look < 3 && w == null; look++)
            {
                if (look > 0) Wait(500);
                using (var f = Capture()) w = ReadLotWindow(f);
            }
            if (w == null) { why = "its window didn't show"; Snapshot("property lot window not read", 60); return false; }
            if (w.Question || !w.Names(worst))
            {
                why = "the window showed " + w.Says;
                CloseLotWindow(w);
                return false;
            }
            PressDemolish(w.Demolish, w.Close != null || w.Move != null, "not the DEMOLISH of the lot's own window");
            LotWindow q = null;
            for (int look = 0; look < 3 && q == null; look++)
            {
                if (look > 0) Wait(500);
                using (var f = Capture()) q = ReadLotWindow(f);
            }
            if (q == null || !q.Question || !q.Names(worst))
            {
                why = q == null ? "no \"Demolish ...?\" question after DEMOLISH" : "the question named " + q.Says;
                Snapshot("property demolish question", 0, true);
                if (q != null) CloseLotWindow(q);
                return false;
            }
            PressDemolish(q.Demolish, q.Cancel != null, "not the DEMOLISH beside CANCEL of the question");
            string toast = ReadToast(1500, t => Parse.Has(t, "DELETED"));
            PropertiesPage after = null;
            for (int look = 0; look < 3 && after == null; look++)
            {
                if (look > 0) Wait(600);
                using (var f = Capture()) { CheckForPurchasePrompt(Ocr.Read(f, PopupArea(f), PopupScale(f), Prep.None)); after = ReadProperties(f); }
            }
            if (toast != null || (after != null && after.LotsOpen > 0)) return true;
            why = "the game didn't say it went";
            Snapshot("property demolish not seen", 60);
            return false;
        }

        void CloseLotWindow(LotWindow w)
        {
            if (w.Close != null) { Press(w.Close, 700); return; }
            if (w.X != null) { Press(w.X, 700); return; }
            if (w.Cancel == null || Parse.Key(w.Cancel.Text) != "CANCEI") return;
            NotDisconnected();
            Click(w.Cancel.CenterX, w.Cancel.CenterY, 700);
        }

        public sealed class CollectionState
        {
            public int Deposited = -1, Total = -1, Attack = -1, Defense = -1;
            public override string ToString() { return (Deposited >= 0 ? Deposited + " of " + Total : "? of ?") + (Attack >= 0 ? ", attack +" + Attack : "") + (Defense >= 0 ? ", defense +" + Defense : ""); }
        }

        static readonly Regex DepositedRx = new Regex(@"(?:sited\s*|^\s*)([0-9OoIlS,]{1,5})\s*(?:[o0O]f)\b\s*([0-9OoIlS,]{1,5})?", RegexOptions.IgnoreCase);
        static readonly Regex BonusRx = new Regex(@"(Attack|Defense)\s*\+\s*([0-9OoIlS,]{1,7})", RegexOptions.IgnoreCase);
        static readonly Regex CollectedRx = new Regex(@"([0-9OoIlS,]{1,5})\s*[/|]\s*([0-9OoIlS,]{1,5})", RegexOptions.IgnoreCase);

        string collectionSeen;

        public OcrLine FindCollectionDeposit(Frame f, out CollectionState state)
        {
            state = new CollectionState();
            var area = PageArea(f);
            int s = PageScale(f);
            OcrLine button = null;
            bool sawTitle = false, sawCounter = false, sawDeposit = false, sawWithdraw = false;
            int cardLines = 0;
            foreach (var prep in new[] { Prep.None, Prep.DarkText, Prep.WhiteText })
            {
                var lines = Ocr.Read(f, area, s, prep);
                if (BankCard(lines) != null) { collectionSeen = "the Bank's page"; return null; }
                var top = lines.Where(l => l.Box.Y < area.Top + area.Height / 4).ToList();
                ReadCollectionCounts(top, state);
                cardLines = Math.Max(cardLines, lines.Count(l => Parse.Has(l.Text, "DEPOSIT FOR") || Parse.Has(l.Text, "NOT DISCOVERED")));
                sawTitle |= top.Any(l => Parse.Key(l.Text) == "COIIECTION");
                sawCounter |= top.Any(l => Parse.Key(l.Text).Contains("COIIECTED"));
                sawDeposit |= top.Any(l => Parse.Key(l.Text) == "DEPOSITAII");
                sawWithdraw |= top.Any(l => Parse.Key(l.Text).Contains("DRAWAII"));
                if (button != null) continue;
                var title = top.FirstOrDefault(l => Parse.Key(l.Text) == "COIIECTION");
                var hint = top.FirstOrDefault(l => Parse.Has(l.Text, "PERMANENT") || Parse.Has(l.Text, "EARN PERM") || Parse.Has(l.Text, "DEPOSIT ITEMS"));
                foreach (var b in top.Where(l => Parse.Key(l.Text) == "DEPOSITAII"))
                {
                    int h = Math.Max(8, b.Box.Height);
                    Func<OcrLine, bool> level = l => Math.Abs(l.CenterY - b.CenterY) < 2 * h;
                    bool titleLeft = title != null && title.Box.Right < b.Box.X && level(title);
                    bool hintLeft = hint != null && hint.Box.Right <= b.Box.X + h && b.Box.X - hint.Box.Right < 8 * h && level(hint);
                    bool countRight = top.Any(l => DepositedRx.IsMatch(l.Text) && l.Box.X >= b.Box.Right - h && l.Box.X - b.Box.Right < 8 * h && level(l));
                    bool withdraw = top.Any(l => Parse.Key(l.Text).Contains("WITHDRAW"));

                    bool collectedAbove = title != null && title.Box.Right < b.Box.X && title.Box.Bottom <= b.Box.Y
                        && b.Box.Y - title.Box.Bottom < 5 * h
                        && top.Any(l => Parse.Key(l.Text).Contains("COIIECTED") && l.Box.X > title.Box.Right
                            && l.Box.Y < b.Box.Y && b.Box.Y - l.Box.Bottom < 5 * h);

                    bool withdrawRight = top.Any(l => Parse.Key(l.Text).Contains("DRAWAII") && l.Box.X >= b.Box.Right && l.Box.X - b.Box.Right < 12 * h && level(l));
                    bool redesigned = withdrawRight && cardLines >= 2;

                    bool counterAbove = withdrawRight && state.Deposited >= 0
                        && top.Any(l => Parse.Key(l.Text).Contains("COIIECTED") && l.Box.Bottom <= b.Box.Y && b.Box.Y - l.Box.Bottom < 5 * h
                                        && l.Box.Right > b.Box.X - 2 * h && l.Box.X < b.Box.Right);
                    if (((titleLeft || hintLeft || countRight) && !withdraw) || collectedAbove || redesigned || counterAbove) { button = b; break; }
                }
            }
            collectionSeen = (button != null ? "found" : "not found") + "; read: page title " + (sawTitle ? "yes" : "no") + ", COLLECTED counter " + (sawCounter ? "yes" : "no")
                             + ", DEPOSIT ALL " + (sawDeposit ? "yes" : "no") + ", WITHDRAW ALL " + (sawWithdraw ? "yes" : "no") + ", " + cardLines + " card lines";
            if (button != null && (state.Attack < 0 || state.Defense < 0))
            {

                var header = new Rectangle(area.X, area.Y, area.Width, button.Box.Bottom - area.Y + 10);
                var labels = Ocr.Read(f, header, s, Prep.None);
                ReadCollectionCounts(labels, state);
                foreach (var label in labels)
                {
                    string key = Parse.Key(label.Text);
                    if (!((key == "ATTACK" && state.Attack < 0) || (key == "DEFENSE" && state.Defense < 0))) continue;
                    int h = Math.Max(8, label.Box.Height);
                    var valueArea = new Rectangle(label.Box.Right, label.Box.Y - h / 2,
                        Math.Min(14 * h, area.Right - label.Box.Right), 2 * h);
                    var value = Ocr.Read(f, valueArea, Math.Max(3, s), Prep.None)
                        .FirstOrDefault(l => Regex.IsMatch(l.Text.Trim(), @"^\+\s*[0-9OoIlS,]+$"));
                    int bonus;
                    if (value == null || !int.TryParse(Parse.Digits(value.Text), out bonus)) continue;
                    if (key == "ATTACK") state.Attack = bonus; else state.Defense = bonus;
                }
            }
            return button;
        }

        static void ReadCollectionCounts(List<OcrLine> top, CollectionState state)
        {
            var collected = top.FirstOrDefault(l => Parse.Key(l.Text).Contains("COIIECTED"));
            if (collected != null && state.Deposited < 0)
            {
                int h = Math.Max(8, collected.Box.Height);
                foreach (var l in top.Where(l => l == collected || (l.Box.X >= collected.Box.Right - h
                    && l.Box.X - collected.Box.Right < 12 * h && Math.Abs(l.CenterY - collected.CenterY) < h)))
                {
                    var m = CollectedRx.Match(l.Text);
                    int count, total;
                    if (m.Success && int.TryParse(Parse.Digits(m.Groups[1].Value), out count)
                        && int.TryParse(Parse.Digits(m.Groups[2].Value), out total) && count <= total)
                    { state.Deposited = count; state.Total = total; break; }
                }
            }
            foreach (var l in top)
            {
                var d = DepositedRx.Match(l.Text);
                int a, b;
                if (d.Success && state.Deposited < 0 && int.TryParse(Parse.Digits(d.Groups[1].Value), out a))
                {
                    state.Deposited = a;
                    if (d.Groups[2].Success && int.TryParse(Parse.Digits(d.Groups[2].Value), out b)) state.Total = b;
                    else
                    {
                        int h = Math.Max(8, l.Box.Height);
                        var below = top.FirstOrDefault(n => n != l && n.Box.Y > l.Box.Y && n.Box.Y - l.Box.Bottom < h && n.Box.X < l.Box.Right && n.Box.Right > l.Box.X
                                                             && Regex.IsMatch(n.Text.Trim(), "^[0-9OoIlS,]{1,5}$"));
                        if (below != null && int.TryParse(Parse.Digits(below.Text), out b)) state.Total = b;
                    }
                }
                foreach (Match m in BonusRx.Matches(l.Text))
                {
                    int v;
                    if (!int.TryParse(Parse.Digits(m.Groups[2].Value), out v)) continue;
                    if (m.Groups[1].Value.ToUpperInvariant() == "ATTACK" && state.Attack < 0) state.Attack = v;
                    if (m.Groups[1].Value.ToUpperInvariant() == "DEFENSE" && state.Defense < 0) state.Defense = v;
                }

                string key = Parse.Key(l.Text);
                if (key == "ATTACK" || key == "DEFENSE")
                {
                    int h = Math.Max(8, l.Box.Height);
                    var value = top.Where(n => n.Box.X >= l.Box.Right && n.Box.X - l.Box.Right < 12 * h
                        && Math.Abs(n.CenterY - l.CenterY) < h / 2 + 1 && Regex.IsMatch(n.Text.Trim(), @"^\+\s*[0-9OoIlS,]+$"))
                        .OrderBy(n => n.Box.X).FirstOrDefault();
                    int v;
                    if (value != null && int.TryParse(Parse.Digits(value.Text), out v))
                    {
                        if (key == "ATTACK" && state.Attack < 0) state.Attack = v;
                        if (key == "DEFENSE" && state.Defense < 0) state.Defense = v;
                    }
                }
            }
        }

        public sealed class DepositResult { public CollectionState Before, After; public string Toast, Window; }

        bool depositPictured;

        public DepositResult DepositCollection()
        {
            if (!OpenTab(Tab.Collection)) return null;
            var r = new DepositResult();
            OcrLine button;
            using (var f = Capture()) button = FindCollectionDeposit(f, out r.Before);

            if (button == null) { Wait(700); using (var f = Capture()) button = FindCollectionDeposit(f, out r.Before); }

            if (button == null) { Wait(2500); using (var f = Capture()) button = FindCollectionDeposit(f, out r.Before); }
            if (button == null)
            {
                log("Collection: DEPOSIT ALL not found - nothing pressed (" + collectionSeen + ")");
                Snapshot("collection deposit not found", 60);
                return null;
            }
            CheckForPurchaseWindow();
            Press(button, 300);

            if (!depositPictured) { depositPictured = true; Snapshot("collection deposit pressed", 0, true); }
            r.Toast = ReadToast(1500, t => (Parse.Has(t, "DEPOSIT") || Parse.Has(t, "COLLECTION")) && !Parse.Has(t, "BANKED"));
            for (int look = 0; look < 2; look++)
            {
                using (var f = Capture())
                {
                    var popup = Ocr.Read(f, PopupArea(f), PopupScale(f), Prep.None);
                    CheckForPurchasePrompt(popup);
                    if (popup.Any(l => { string k = Parse.Key(l.Text); return k.Contains("CONFIRM") || k.Contains("AREYOUSURE") || k == "YES" || k == "NO"; }))
                        r.Window = string.Join(" ", popup.Select(l => l.Text));
                    FindCollectionDeposit(f, out r.After);
                }

                if (r.Window != null || r.Toast != null || r.After.Deposited != r.Before.Deposited || r.After.Attack != r.Before.Attack) break;
                Wait(700);
            }
            return r;
        }

        static readonly string[] StatsRows = { "RESPECT", "ATTACK POWER", "DEFENSE POWER", "PROPERTY INCOME" };
        static readonly string[] CrewRows = { "HENCHMEN", "EQUIPMENT" };
        static readonly string[] UpgradeRows = { "SKILL POINTS", "ENERGY", "STAMINA", "HEALTH", "BASE ATTACK", "BASE DEFENSE", "PROPERTY CAPACITY" };
        static readonly string[] FamilyRows = { "RANK", "MEMBERS" };
        static readonly string[] PanelTitles = { "YOUR STATS", "YOUR CREW", "YOUR FAMILY", "YOUR UPGRADES", "YOUR RESIDENCE" };

        public SafehouseInfo ReadSafehouse()
        {
            if (!OpenTab(Tab.Safehouse)) return null;
            ScrollPage(0.54, 15);
            var s = new SafehouseInfo();
            string last = null;
            for (int page = 0; page < 8 && !s.Complete; page++)
            {
                string sig;
                using (var f = Capture()) sig = ReadSafehouseFrame(f, s);
                if (sig == last) break;
                last = sig;
                ScrollPage(0.54, -ListNotches(5));
            }
            return s;
        }

        static readonly Regex UpgradePriceRx = new Regex(@"^\s*UPGRADE\s*(?:\$|S(?=\s?[0-9]))\s?([0-9OoIlS]{1,3}(?:[,.][0-9OoIlS]{1,3})*\s*[KMBT]?)\s*$", RegexOptions.IgnoreCase);

        public sealed class SafehouseUpgrade
        {
            public int Level = -1, MaxLevel = -1;
            public string Next;
            public double Price = -1;
            public bool Gold;
            public bool Upgraded, Confirmed;
            public SafehouseUpgrade After;
            public string Toast, Window;
            public string Why;
            public int NeedLevel = -1;
            public string Seen;
        }

        static readonly Regex RequiresLevelRx = new Regex(@"REQUIRES\s*LEVE[LI1]\s*([0-9OoIlS]{1,4})", RegexOptions.IgnoreCase);

        OcrLine ReadUpgradeButton(Frame f, SafehouseUpgrade r)
        {
            var area = PageArea(f);
            int scale = PageScale(f);
            var lines = Ocr.Read(f, area, scale, Prep.None);
            var s = new SafehouseInfo();
            SafehouseTop(f, scale, lines, s);

            if (s.Level <= 0)
                foreach (var t in new[] { Tuple.Create(scale, Prep.WhiteText), Tuple.Create(scale, Prep.WhiteSoft), Tuple.Create(scale + 1, Prep.None) })
                {
                    var more = Ocr.Read(f, area, t.Item1, t.Item2);
                    SafehouseTop(f, t.Item1, more, s);
                    lines.AddRange(more);
                    if (s.Level > 0) break;
                }
            r.Level = s.Level; r.MaxLevel = s.MaxLevel; r.Next = s.Next;

            lines = JoinWrapped(lines, t => Parse.Key(t) == "REQUIRES", t => Regex.IsMatch(t, @"^\s*LEVE[LI1]\s*[0-9OoIlS]{1,4}\s*$", RegexOptions.IgnoreCase));
            lines = JoinWrapped(lines, t => Parse.Key(t) == "UPGRADE", t => Regex.IsMatch(t, @"^\s*\$\s?[0-9OoIlS]"));

            var needs = lines.FirstOrDefault(l => RequiresLevelRx.IsMatch(l.Text) && l.CenterX > area.X + area.Width / 2);
            int lv;
            if (needs != null && (s.Level > 0 || s.Next != null) && int.TryParse(Parse.Digits(RequiresLevelRx.Match(needs.Text).Groups[1].Value), out lv) && lv > 0) { r.NeedLevel = lv; return null; }
            if (s.Level <= 0) return null;

            Func<OcrLine, double> priceOf = l =>
            {
                double v;
                return l.CenterX >= area.X + area.Width / 2 && UpgradePriceRx.IsMatch(l.Text)
                       && Parse.Money("$" + UpgradePriceRx.Match(l.Text).Groups[1].Value, out v) && v >= 1000 ? v : -1;
            };
            var up = lines.FirstOrDefault(l => priceOf(l) > 0)
                     ?? JoinWrapped(Ocr.Read(f, area, scale, Prep.DarkText), t => Parse.Key(t) == "UPGRADE", t => Regex.IsMatch(t, @"^\s*\$\s?[0-9OoIlS]")).FirstOrDefault(l => priceOf(l) > 0)
                     ?? RereadUpgrade(f, lines, area, scale, priceOf);
            if (up == null)
            {
                r.Seen = string.Join(" / ", lines.Where(l => Parse.Key(l.Text).Contains("UPGRADE") && l.CenterX >= area.X + area.Width / 2).Select(l => l.Text.Trim()));
                return null;
            }
            r.Price = priceOf(up);
            r.Gold = GoldFace(f, up.Box);
            return up;
        }

        static OcrLine RereadUpgrade(Frame f, List<OcrLine> lines, Rectangle area, int scale, Func<OcrLine, double> priceOf)
        {
            var word = lines.Where(l => Parse.Key(l.Text).StartsWith("UPGRADE") && l.CenterX >= area.X + area.Width / 2).OrderBy(l => l.Box.Y).FirstOrDefault();
            if (word == null) return null;
            int h = Math.Max(8, word.Box.Height);

            var box = Rectangle.Intersect(Rectangle.FromLTRB(word.Box.X - h, word.Box.Y - h / 2, word.Box.Right + 4 * h, word.Box.Bottom + h / 2), area);
            var read = new List<OcrLine>();
            foreach (var t in new[] { Tuple.Create(scale, Prep.WhiteSoft), Tuple.Create(scale + 1, Prep.WhiteSoft), Tuple.Create(scale + 1, Prep.None),
                                      Tuple.Create(scale, Prep.GreyText), Tuple.Create(scale + 1, Prep.Contrast), Tuple.Create(scale + 1, Prep.DarkText) })
            {
                var l = Ocr.Read(f, box, t.Item1, t.Item2).FirstOrDefault(x => priceOf(x) > 0);
                if (l == null) continue;
                var same = read.FirstOrDefault(x => priceOf(x) == priceOf(l));
                if (same != null) return same;
                read.Add(l);
            }
            return null;
        }

        static bool GoldFace(Frame f, Rectangle text)
        {
            int h = Math.Max(6, text.Height), gold = 0, n = 0;
            foreach (int side in new[] { -1, 1 })
            {
                int x0 = side < 0 ? text.X - h * 7 / 10 : text.Right + h / 5, x1 = x0 + h / 2;
                for (int x = x0; x <= x1; x++)
                    for (int y = text.Y + h / 4; y <= text.Bottom - h / 4; y++)
                    {
                        if (x < 0 || y < 0 || x >= f.Width || y >= f.Height) continue;
                        n++;
                        if (GoldIsh(f, x, y)) gold++;
                    }
            }
            return n > 0 && gold >= n * 6 / 10;
        }

        public SafehouseUpgrade UpgradeSafehouse(double keep)
        {
            var r = new SafehouseUpgrade();
            if (!OpenTab(Tab.Safehouse)) { r.Why = "the Safehouse didn't open"; return r; }
            ScrollPage(0.54, 15);
            OcrLine button;
            double cash;
            using (var f = Capture()) { button = ReadUpgradeButton(f, r); cash = ReadHeader(f).Cash; }

            for (int again = 0; again < 2 && (button == null || r.Next == null) && r.NeedLevel <= 0 && !(r.Level > 0 && r.Level >= r.MaxLevel); again++)
            {
                Wait(800);
                r = new SafehouseUpgrade();
                using (var f = Capture()) { button = ReadUpgradeButton(f, r); cash = ReadHeader(f).Cash; }
            }
            if (r.Level > 0 && r.MaxLevel > 0 && r.Level >= r.MaxLevel) { r.Why = "top level"; return r; }
            if (button == null && r.NeedLevel > 0) { r.Why = "needs level " + r.NeedLevel; return r; }
            if (button == null || r.Next == null)
            {

                r.Why = "its UPGRADE didn't read (" + (r.Level > 0 ? "level " + r.Level : "level not read") + ", " + (r.Next != null ? "next " + r.Next : "NEXT not read")
                      + (button != null ? ", \"" + button.Text.Trim() + "\"" : !string.IsNullOrEmpty(r.Seen) ? ", read \"" + r.Seen + "\"" : ", no UPGRADE read") + ")";
                Snapshot("safehouse upgrade unread", 60);
                return r;
            }
            if (cash < 0) { r.Why = "the cash on hand didn't read"; return r; }
            if (!r.Gold || r.Price > cash - Math.Max(0, keep)) { r.Why = "saving up"; return r; }
            PressSafehouseUpgrade(button, r, cash, keep);

            r.Toast = ReadToast(1500, t => Parse.Has(t, "UPGRADED") || Parse.Has(t, r.Next));
            if (r.Toast != null) r.Upgraded = true;
            for (int look = 0; look < 4; look++)
            {
                if (look > 0) Wait(800);
                using (var f = Capture())
                {
                    var popup = Ocr.Read(f, PopupArea(f), PopupScale(f), Prep.None);
                    CheckForPurchasePrompt(popup);
                    var after = new SafehouseUpgrade();
                    ReadUpgradeButton(f, after);
                    if (after.Level > r.Level) { r.Upgraded = true; r.After = after; break; }
                    if (r.Upgraded) continue;
                    if (look >= 1 && ConfirmUpgrade(popup, r)) continue;
                    if (look == 3) r.Window = string.Join(" ", popup.Select(l => l.Text.Trim()));
                }
            }
            return r;
        }

        void PressSafehouseUpgrade(OcrLine b, SafehouseUpgrade r, double cash, double keep)
        {
            if (b == null || !UpgradePriceRx.IsMatch(b.Text) || r.Level <= 0 || r.Next == null || !r.Gold || r.Price <= 0 || r.Price > cash - Math.Max(0, keep))
                throw new NeverPressException(b != null ? b.Text : "UPGRADE", "not the Safehouse's UPGRADE within the cash on hand");
            NotDisconnected();
            if (OnlyNavigate) RefuseInCheck(b.Text);
            Click(b.CenterX, b.CenterY, 900);
        }

        bool ConfirmUpgrade(List<OcrLine> popup, SafehouseUpgrade r)
        {
            if (r.Confirmed || popup.Any(l => Parse.Has(l.Text, "ROBUX") || l.Text.Contains("R$"))) return false;
            bool samePrice = popup.Any(l => { double v; return l.Text.Contains("$") && !UpgradePriceRx.IsMatch(l.Text) && Parse.Money(l.Text, out v) && Math.Abs(v - r.Price) <= r.Price * 0.01; });
            var yes = popup.FirstOrDefault(l => { string k = Parse.Key(l.Text); return k == "CONFIRM" || k == "YES"; });
            if (!samePrice || yes == null) return false;
            r.Confirmed = true;
            log("Safehouse: a window asked to confirm the upgrade (\"" + string.Join(" ", popup.Select(l => l.Text.Trim())) + "\") - " + yes.Text.Trim() + " pressed");
            Snapshot("safehouse upgrade confirm", 0, false, false);
            Press(yes, 900);
            return true;
        }

        public string ReadSafehouseFrame(Frame f, SafehouseInfo s)
        {
            var area = PageArea(f);
            int scale = PageScale(f);
            var lines = Ocr.Read(f, area, scale, Prep.None);
            SafehouseTop(f, scale, lines, s);

            var titles = new Dictionary<string, OcrLine>();
            foreach (var t in PanelTitles)
            {
                var l = View.Label(lines, t);
                if (l != null) titles[t] = l;
            }
            OcrLine rt;
            int split = titles.TryGetValue("YOUR UPGRADES", out rt) || titles.TryGetValue("YOUR RESIDENCE", out rt)
                ? rt.Box.X - Math.Max(8, rt.Box.Height) : area.X + area.Width / 2;
            var left = Rectangle.FromLTRB(area.Left, area.Top, split, area.Bottom);
            var right = Rectangle.FromLTRB(split, area.Top, area.Right, area.Bottom);

            rowPitch = 0;
            var stats = SafehousePanel(f, scale, lines, titles, "YOUR STATS", left, StatsRows);
            if (stats != null)
            {
                SetInt(stats, 0, ref s.Respect);
                SetInt(stats, 1, ref s.AttackPower);
                SetInt(stats, 2, ref s.DefensePower);
                double money;
                if (s.Income < 0 && stats[3] != null && Parse.Money(stats[3], out money)) s.Income = money;
            }
            var up = SafehousePanel(f, scale, lines, titles, "YOUR UPGRADES", right, UpgradeRows);

            var crew = SafehousePanel(f, scale, lines, titles, "YOUR CREW", left, CrewRows);
            if (crew != null)
            {
                SetOf(crew[0], ref s.Hired, ref s.HireMax);
                SetOf(crew[1], ref s.Gear, ref s.GearMax);
            }
            if (up != null)
            {
                SetInt(up, 0, ref s.SkillPoints);
                SetInt(up, 1, ref s.Energy);
                SetInt(up, 2, ref s.Stamina);
                SetInt(up, 3, ref s.Health);
                SetInt(up, 4, ref s.BaseAttack);
                SetInt(up, 5, ref s.BaseDefense);
                SetInt(up, 6, ref s.Capacity);
            }
            OcrLine famTitle;
            if (titles.TryGetValue("YOUR FAMILY", out famTitle))
            {
                s.FamilySeen = true;
                var fam = SafehousePanel(f, scale, lines, titles, "YOUR FAMILY", left, FamilyRows);
                if (fam != null)
                {
                    if (s.Rank == null && fam[0] != null && Regex.IsMatch(fam[0], "[A-Za-z]{3}")) s.Rank = fam[0].Trim();
                    SetOf(fam[1], ref s.Members, ref s.MembersMax);
                }
                if (s.Family == null)
                {

                    var rank = View.Label(lines.Where(l => l.Box.X < split && l.Box.Y > famTitle.Box.Bottom).ToList(), "RANK");
                    int bottom = rank != null ? rank.Box.Y : famTitle.Box.Bottom + 5 * famTitle.Box.Height;
                    var name = lines.Where(l => l.Box.X < split && l.Box.Y > famTitle.Box.Bottom && l.Box.Bottom <= bottom && Regex.Matches(l.Text, "[A-Za-z]").Count >= 3
                                                && Parse.Key(l.Text) != "OPEN").OrderByDescending(l => l.Box.Height).FirstOrDefault();
                    if (name != null)
                    {

                        int h = Math.Max(6, name.Box.Height), z = View.ScaleFor(h);
                        var r = Rectangle.Inflate(name.Box, h / 2, h / 2);
                        string text = new[] { Tuple.Create(z, Prep.WhiteSoft), Tuple.Create(z, Prep.WhiteBright), Tuple.Create(z + 1, Prep.WhiteText) }
                                          .Select(t => Ocr.ReadText(f, r, t.Item1, t.Item2, true)).FirstOrDefault(t => Regex.Matches(t, "[A-Za-z]").Count >= 3);
                        s.Family = FamilyName(text ?? name.Text);
                    }
                }
            }

            return string.Join(",", lines.Where(l => !Regex.IsMatch(l.Text, @"\d")).Select(l => Parse.Key(l.Text) + "@" + l.Box.Y));
        }

        static void SetInt(string[] values, int i, ref int field)
        {
            if (field >= 0 || values[i] == null) return;
            var m = Regex.Match(values[i].Trim(), @"([0-9OoIlSB][0-9OoIlSB,]*)$");
            int n;
            if (m.Success && int.TryParse(Parse.Digits(m.Groups[1].Value), out n)) field = n;
        }

        static void SetOf(string text, ref int a, ref int b)
        {
            if (a >= 0 || text == null) return;
            var m = Regex.Match(text, @"([0-9OoIlS]{1,4})\s*of\s*([0-9OoIlS]{1,4})", RegexOptions.IgnoreCase);
            int x, y;
            if (m.Success && int.TryParse(Parse.Digits(m.Groups[1].Value), out x) && int.TryParse(Parse.Digits(m.Groups[2].Value), out y) && x <= y) { a = x; b = y; }
        }

        static string FamilyName(string text)
        {
            string t = Regex.Replace(text.Trim(), @"\s{2,}", " ");
            if (t.Contains("[") && !t.Contains("]")) t = ("Il1|".IndexOf(t[t.Length - 1]) >= 0 ? t.Substring(0, t.Length - 1) : t) + "]";
            return t;
        }

        void SafehouseTop(Frame f, int scale, List<OcrLine> lines, SafehouseInfo s)
        {
            var chip = lines.FirstOrDefault(l => LevelChip.IsMatch(l.Text.ToUpperInvariant()))
                    ?? lines.FirstOrDefault(l => { string k = Parse.Key(l.Text); return k.StartsWith("SAFEHOUSE") && k.Length > 11; });
            if (chip != null && s.Level < 0)
            {
                var m = LevelChip.Match(ReadAgain(f, chip, scale, t => LevelChip.IsMatch(t.ToUpperInvariant())).ToUpperInvariant());
                int a, b;
                if (m.Success && int.TryParse(Parse.Digits(m.Groups[1].Value), out a) && int.TryParse(Parse.Digits(m.Groups[2].Value), out b) && a > 0 && a <= b)
                { s.Level = a; s.MaxLevel = b; }
            }
            var bonus = lines.FirstOrDefault(l => Parse.Has(l.Text, "INCOME") && Parse.Has(l.Text, "DEFENSE"));
            if (bonus != null && s.Bonus < 0)
            {
                var m = Percent.Match(ReadAgain(f, bonus, scale, t => Percent.IsMatch(t)));
                if (m.Success) s.Bonus = int.Parse(Parse.Digits(m.Groups[1].Value));
            }
            if (chip != null && s.Estate == null)
            {

                var name = lines.Where(l => l.Box.Bottom <= chip.Box.Y + 2 && l.Box.Bottom >= chip.Box.Y - 3 * Math.Max(l.Box.Height, chip.Box.Height)
                                            && l.Box.X < chip.CenterX && !Regex.IsMatch(l.Text, @"\d") && Regex.Matches(l.Text, "[A-Za-z]").Count >= 4)
                                .OrderByDescending(l => l.Box.Bottom).FirstOrDefault();
                if (name != null) s.Estate = name.Text.Trim();
            }
            var upgrade = lines.FirstOrDefault(l => Parse.Key(l.Text).StartsWith("UPGRADE"));
            if (upgrade != null && s.UpgradeCost < 0)
            {
                double cost;
                if (Parse.Money(ReadAgain(f, upgrade, scale, t => { double c; return Parse.Money(t, out c); }), out cost)) s.UpgradeCost = cost;
            }

            if (s.Next == null)
            {
                OcrLine next = upgrade == null ? lines.FirstOrDefault(l => Parse.Key(l.Text).StartsWith("NEXT"))
                    : lines.Where(l => l != upgrade && l.Box.Bottom <= upgrade.Box.Y + 2 && l.Box.Bottom >= upgrade.Box.Y - 3 * upgrade.Box.Height
                                       && l.Box.Right > upgrade.Box.X - upgrade.Box.Width && l.Box.X < upgrade.Box.Right && Regex.Matches(l.Text, "[A-Za-z]").Count >= 6)
                           .OrderBy(l => l.Box.Y).FirstOrDefault();
                if (next != null)
                {

                    string t = next.Text.Trim();
                    int colon = t.IndexOf(':');
                    if (colon >= 0 && colon < 8) t = t.Substring(colon + 1);
                    else t = Regex.Replace(t, @"^(NEXT|N\S{1,3}[.:])\s+", "", RegexOptions.IgnoreCase);
                    string name = t.Split(',')[0].Trim();
                    if (name.Length >= 3) s.Next = name;
                    var near = lines.Where(l => l == next || (Math.Abs(l.Box.X - next.Box.Right) < 3 * next.Box.Height && Math.Abs(l.CenterY - next.CenterY) <= next.Box.Height)
                                                || (l.Box.Y > next.Box.Y && l.Box.Y < next.Box.Bottom + next.Box.Height && l.Box.Right > next.Box.X && l.Box.X < next.Box.Right));

                    var pm = near.Select(l => PercentExact.Match(l.Text)).FirstOrDefault(x => x.Success);
                    int h = Math.Max(6, next.Box.Height);
                    var r = Rectangle.FromLTRB(next.Box.X - h, next.Box.Y - h, PageArea(f).Right - 4, next.Box.Bottom + 2 * h);
                    var hows = new[] { Tuple.Create(scale, Prep.WhiteSoft), Tuple.Create(scale + 1, Prep.WhiteSoft), Tuple.Create(scale, Prep.WhiteText), Tuple.Create(scale + 1, Prep.None) };
                    if (pm == null)
                    {

                        foreach (var how in hows)
                            if ((pm = PercentExact.Match(Ocr.ReadText(f, r, how.Item1, how.Item2))).Success) break;
                        if (!pm.Success) pm = null;
                    }
                    if (pm == null) pm = near.Select(l => Percent.Match(l.Text)).FirstOrDefault(x => x.Success);
                    if (pm == null)
                        foreach (var how in hows)
                            if ((pm = Percent.Match(Ocr.ReadText(f, r, how.Item1, how.Item2))).Success) break;
                    if (pm != null && pm.Success) s.NextBonus = int.Parse(Parse.Digits(pm.Groups[1].Value));
                }
            }
        }

        static readonly Regex LevelChip = new Regex(@"LEVE\s?[LI1|]\s*([0-9OIlSB|]{1,3})\s*OF\s*([0-9OIlSB|]{1,3})");

        static readonly Regex Percent = new Regex(@"\+\s*([0-9OIlS]{1,3}?)\s*(?:[0Oo]/[0Oo]|%|/)");
        static readonly Regex PercentExact = new Regex(@"\+\s*([0-9]{1,3})\s*%");

        static string ReadAgain(Frame f, OcrLine l, int scale, Func<string, bool> ok)
        {
            if (ok(l.Text)) return l.Text;
            int h = Math.Max(6, l.Box.Height);
            var r = new Rectangle(l.Box.X - h, l.Box.Y - h, l.Box.Width + 2 * h, l.Box.Height + 2 * h);
            foreach (var t in new[] { Tuple.Create(scale + 1, Prep.None), Tuple.Create(Math.Max(2, scale - 1), Prep.None), Tuple.Create(scale + 2, Prep.None),
                                      Tuple.Create(Math.Max(2, scale - 1), Prep.Contrast), Tuple.Create(scale + 3, Prep.WhiteSoft), Tuple.Create(scale, Prep.WhiteText) })
            {
                string text = Ocr.ReadText(f, r, t.Item1, t.Item2);
                if (ok(text)) return text;
            }
            return l.Text;
        }

        double rowPitch;

        string[] SafehousePanel(Frame f, int scale, List<OcrLine> lines, Dictionary<string, OcrLine> titles, string title, Rectangle column, string[] rows)
        {
            OcrLine t;
            if (!titles.TryGetValue(title, out t)) return null;
            int bottom = titles.Values.Where(o => o != t && o.Box.Y > t.Box.Bottom && o.CenterX >= column.Left && o.CenterX < column.Right)
                               .Select(o => o.Box.Y).DefaultIfEmpty(column.Bottom).Min();
            var inPanel = lines.Where(l => l != t && l.CenterY > t.Box.Bottom && l.CenterY < bottom && l.CenterX >= column.Left && l.CenterX < column.Right).ToList();
            int valueLeft = column.Left + column.Width * 6 / 10;

            var y = new int[rows.Length];
            var found = new bool[rows.Length];
            var heights = new List<int>();
            for (int i = 0; i < rows.Length; i++)
            {
                var label = View.Label(inPanel.Where(l => l.Box.Right < valueLeft || Regex.IsMatch(l.Text, @"\d")).ToList(), rows[i]);
                if (label == null) continue;
                y[i] = label.CenterY; found[i] = true; heights.Add(label.Box.Height);
            }
            var steps = new List<double>();
            for (int i = 0; i < rows.Length; i++)
                for (int j = i + 1; j < rows.Length; j++)
                    if (found[i] && found[j] && y[j] > y[i]) steps.Add((double)(y[j] - y[i]) / (j - i));
            double pitch = steps.Count > 0 ? steps.OrderBy(x => x).ElementAt(steps.Count / 2) : 0;

            if (pitch > 0 && rowPitch <= 0) rowPitch = pitch;
            if (pitch <= 0) pitch = rowPitch;
            int rh = heights.Count > 0 ? heights.OrderBy(x => x).ElementAt(heights.Count / 2) : Math.Max(8, t.Box.Height * 2 / 3);

            var values = new string[rows.Length];
            for (int i = 0; i < rows.Length; i++)
            {
                if (!found[i])
                {
                    if (pitch <= 0 || !found.Any(x => x)) continue;
                    int k = Enumerable.Range(0, rows.Length).Where(j => found[j]).OrderBy(j => Math.Abs(j - i)).First();
                    y[i] = y[k] + (int)Math.Round((i - k) * pitch);
                    if (y[i] <= t.Box.Bottom || y[i] >= bottom - rh / 2 || y[i] > column.Bottom - rh) continue;
                }
                int band = Math.Max(rh * 8 / 10, (int)(pitch * 0.35));
                var value = inPanel.Where(l => l.Box.Right >= valueLeft && Math.Abs(l.CenterY - y[i]) <= band).OrderByDescending(l => l.Box.Right).FirstOrDefault();
                if (value != null && RowValueOk(rows[i], value.Text)) { values[i] = value.Text; continue; }
                if (NumberRow(rows[i]))
                {

                    int reach = pitch > 0 ? (int)Math.Min(pitch * 0.7, 1.6 * rh) : rh * 6 / 5;
                    var ink = InkBox(f, Rectangle.FromLTRB(valueLeft, y[i] - reach, column.Right - 2, y[i] + reach));
                    int n = ink.Height >= rh / 2 && ink.Height <= 2 * rh ? ReadLoneNumber(f, ink) : -1;
                    if (n >= 0) { values[i] = n.ToString(); continue; }
                }
                values[i] = ReadRowValue(f, scale, rows[i], Rectangle.FromLTRB(column.Left + 2, y[i] - rh, column.Right - 2, y[i] + rh), valueLeft);
            }
            return values;
        }

        static bool NumberRow(string row) { return row != "PROPERTY INCOME" && row != "HENCHMEN" && row != "EQUIPMENT" && row != "MEMBERS" && row != "RANK"; }

        static Rectangle InkBox(Frame f, Rectangle r)
        {
            int x0 = int.MaxValue, y0 = int.MaxValue, x1 = -1, y1 = -1;
            r.Intersect(new Rectangle(0, 0, f.Width, f.Height));
            for (int y = r.Top; y < r.Bottom; y++)
            {
                int first = -1, last = -1, n = 0;
                for (int x = r.Left; x < r.Right; x++)
                {
                    var c = f.Pixel(x, y);

                    if ((c.R + c.G + c.B) / 3 < 170 && !(c.R >= 180 && c.G >= 140 && c.B < 120)) continue;
                    if (first < 0) first = x;
                    last = x; n++;
                }
                if (first < 0 || n > r.Width / 2) continue;
                x0 = Math.Min(x0, first); x1 = Math.Max(x1, last); y0 = Math.Min(y0, y); y1 = Math.Max(y1, y);
            }
            return x1 < 0 ? Rectangle.Empty : Rectangle.FromLTRB(x0, y0, x1 + 1, y1 + 1);
        }

        internal static int ReadLoneNumber(Frame f, Rectangle ink) { return ReadLoneNumber(f, ink, new[] { 2, 3 }); }

        internal static int ReadLoneNumber(Frame f, Rectangle ink, int[] tries)
        {
            var crop = Rectangle.Inflate(ink, 2, 2);
            var back = f.Pixel(crop.X, crop.Y);
            int m = Math.Max(4, ink.Height);
            foreach (int copies in tries)
            {
                var bmp = new Bitmap(2 * m + copies * crop.Width + (copies - 1) * m, crop.Height + 2 * m, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(back);
                    for (int k = 0; k < copies; k++)
                        g.DrawImage(f.Bitmap, new Rectangle(m + k * (crop.Width + m), m, crop.Width, crop.Height), crop, GraphicsUnit.Pixel);
                }
                using (var copy = new Frame(bmp))
                    foreach (var t in new[] { Tuple.Create(2, Prep.None), Tuple.Create(3, Prep.None), Tuple.Create(2, Prep.WhiteText), Tuple.Create(2, Prep.Contrast), Tuple.Create(3, Prep.WhiteBright) })
                    {
                        string text = Ocr.ReadText(copy, new Rectangle(0, 0, copy.Width, copy.Height), t.Item1, t.Item2);
                        if (Regex.IsMatch(text, @"[A-Za-z]{2}") && !Regex.IsMatch(text, @"^[\sOo0]+$")) continue;
                        string d = Parse.Digits(text);
                        if (d.Length == 0 || d.Length % copies != 0) continue;
                        string one = d.Substring(0, d.Length / copies);
                        int n;
                        if (one.Length <= 7 && Enumerable.Range(1, copies - 1).All(k => d.Substring(k * one.Length, one.Length) == one) && int.TryParse(one, out n)) return n;
                    }
            }
            return -1;
        }

        static bool RowValueOk(string row, string text)
        {
            double money;
            if (row == "PROPERTY INCOME") return Parse.Money(text, out money);
            if (row == "HENCHMEN" || row == "EQUIPMENT" || row == "MEMBERS") return Regex.IsMatch(text, @"[0-9OoIlS]{1,4}\s*of\s*[0-9OoIlS]{1,4}", RegexOptions.IgnoreCase);
            if (row == "RANK") return Regex.IsMatch(text.Trim(), @"^[A-Za-z]{3,}$");
            return Regex.IsMatch(text.Trim(), @"(^|\s)[0-9OoIlSB][0-9OoIlSB,]*$") && Regex.IsMatch(text, @"[0-9]|^\s*[Oo]\s*$");
        }

        static string ReadRowValue(Frame f, int scale, string row, Rectangle r, int valueLeft)
        {
            var strip = Rectangle.FromLTRB(valueLeft, r.Top, r.Right, r.Bottom);
            foreach (var t in new[]
            {
                Tuple.Create(r, scale, Prep.None), Tuple.Create(r, Math.Max(2, scale - 1), Prep.None), Tuple.Create(r, scale + 1, Prep.Contrast),
                Tuple.Create(strip, 2, Prep.WhiteText), Tuple.Create(strip, 2, Prep.WhiteSoft), Tuple.Create(strip, 3, Prep.WhiteText), Tuple.Create(r, scale + 1, Prep.None),
            })
            {
                var value = Ocr.Read(f, t.Item1, t.Item2, t.Item3).Where(l => l.Box.Right >= valueLeft).OrderByDescending(l => l.Box.Right).FirstOrDefault();
                if (value != null && RowValueOk(row, value.Text)) return value.Text;
            }
            return null;
        }
    }

    sealed class NeverPressException : Exception
    {
        public NeverPressException(string text, string why) : base("refused to press \"" + (text ?? "").Trim() + "\" (" + why + ")") { }
    }

    static class NeverPress
    {

        static readonly string[] Anywhere =
        {
            "ROBUX", "WITHDRAW", "SABOTAGE", "FINISH NOW", "CANCEL", "REPLACE", "DELETE", "START A HEIST", "BLACK MARKET",
            "PROPERTIES", "MAKE BOSS", "LEAVE FAMILY", "UNEQUIP", "DISMISS", "AUTO ROLL", "UNLOCK", "RECALL", "DECLARE WAR",
            "DONATE", "INVITE", "UPGRADE", "TRIBUTE", "GARRISON", "TICKET", "PURCHASE", "REFILL", "GOLD BARS", "GAME PASS",
            "GAMEPASS", "USE DEFAULT", "RANDOM COLOR", "NOT ELIGIBLE", "PICK A DISTRICT", "RESET ALL", "DEMOLISH",

            "INCREASE YOUR LUCK", "OFFLINE OPERATION", "RECENT ATTACKS", "LEADERBOARD", "WAR TARGETS ONLY",
        };

        static readonly string[] WordStart =
        {
            "BUY", "SELL", "TRADE", "SHOP", "GIFT", "HIRE", "PAY", "SPEND", "SAVE", "EDIT", "JOIN", "GIVE", "ROLL", "RANKS",
            "ACCEPT", "SEND", "SETTINGS", "PRODUCTS", "HOURS", "TRAIN",
        };

        static readonly string[] Whole = { "OPEN", "DEPOSIT", "STOP", "REFRESH", "SEARCH" };

        static readonly string[] Allowed = { "GIVE 5" };
        static readonly string[] AllowedKeys = Allowed.Select(K).ToArray();

        static readonly string[] AnywhereKeys = Anywhere.Select(K).ToArray();
        static readonly string[] WordStartKeys = WordStart.Select(K).ToArray();
        static readonly string[] WholeKeys = Whole.Select(K).ToArray();

        static string K(string s) { return Parse.Key(s).Replace('Q', 'R'); }

        public static string Why(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            string raw = text.Trim();
            if (Regex.IsMatch(raw, @"R\s?\$")) return "R$ (Robux)";
            if (Regex.IsMatch(raw, @"\$\s?[0-9OoIlS]")) return "a $ price";
            if (Regex.IsMatch(raw, @"^[+\-−–—]$")) return "a + or - button";
            if (Regex.IsMatch(raw, @"(^|\s)[0-9OoIl]{1,4}\s*KEYS?\b", RegexOptions.IgnoreCase)) return "N KEYS";
            string key = K(raw), words = " " + Fold(raw) + " ";
            if (AllowedKeys.Contains(key)) return null;
            for (int i = 0; i < AnywhereKeys.Length; i++) if (key.Contains(AnywhereKeys[i]) || AlmostIn(key, AnywhereKeys[i])) return Anywhere[i];
            for (int i = 0; i < WordStartKeys.Length; i++) if (words.Contains(" " + WordStartKeys[i])) return WordStart[i];
            for (int i = 0; i < WholeKeys.Length; i++) if (key == WholeKeys[i]) return Whole[i];

            var one = new List<OcrLine> { new OcrLine { Text = raw } };
            foreach (var t in NeverTabs) if (View.Label(one, View.TabLabels[t]) != null) return View.TabLabels[t];
            return null;
        }

        static readonly Tab[] NeverTabs = { Tab.Trade, Tab.BlackMarket, Tab.Shop, Tab.Properties };

        static bool AlmostIn(string key, string entry)
        {
            int off = entry.Length >= 10 ? 2 : 1;
            if (entry.Length < 7 || key.Length < entry.Length - off) return false;
            for (int start = 0; start + entry.Length - off <= key.Length; start++)
                for (int len = entry.Length - off; len <= entry.Length + off && start + len <= key.Length; len++)
                    if (View.Distance(key.Substring(start, len), entry) <= off) return true;
            return false;
        }

        static string Fold(string s)
        {
            var words = Regex.Split((s ?? "").ToUpperInvariant(), @"[^\p{L}\p{N}]+").Where(w => w.Length > 0).Select(K);
            return string.Join(" ", words);
        }
    }

    sealed class SafehouseInfo
    {
        public string Estate, Next, Family, Rank;
        public int Level = -1, MaxLevel = -1, Bonus = -1, NextBonus = -1;
        public double UpgradeCost = -1, Income = -1;
        public int Respect = -1, AttackPower = -1, DefensePower = -1;
        public int Hired = -1, HireMax = -1, Gear = -1, GearMax = -1;
        public int SkillPoints = -1, Energy = -1, Stamina = -1, Health = -1, BaseAttack = -1, BaseDefense = -1, Capacity = -1;
        public int Members = -1, MembersMax = -1;
        public bool FamilySeen;

        public bool NoFamily { get { return Family != null && Parse.Key(Family) == "NONEYET"; } }

        public bool Complete
        {
            get
            {
                return Level > 0 && Respect >= 0 && AttackPower >= 0 && DefensePower >= 0 && Income >= 0 && Hired >= 0 && Gear >= 0
                    && SkillPoints >= 0 && Energy >= 0 && Stamina >= 0 && Health >= 0 && BaseAttack >= 0 && BaseDefense >= 0 && Capacity >= 0
                    && FamilySeen && (Members >= 0 || Family == null || NoFamily);
            }
        }

        public int Found
        {
            get
            {
                return new[] { Level, Respect, AttackPower, DefensePower, Hired, Gear, SkillPoints, Energy, Stamina, Health, BaseAttack, BaseDefense, Capacity, Members }.Count(v => v >= 0)
                    + (Income >= 0 ? 1 : 0) + (UpgradeCost >= 0 ? 1 : 0);
            }
        }

        static string N(int v) { return v >= 0 ? v.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) : "?"; }
        static string Of(int a, int b) { return a >= 0 ? a + " of " + b : "?"; }

        public static string Money(double v)
        {
            if (v < 0) return "?";
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            if (v >= 1e12) return "$" + (v / 1e12).ToString("0.##", ci) + "T";
            if (v >= 1e9) return "$" + (v / 1e9).ToString("0.##", ci) + "B";
            if (v >= 1e6) return "$" + (v / 1e6).ToString("0.##", ci) + "M";
            return "$" + v.ToString("N0", ci);
        }

        public string[] Lines()
        {
            return new[]
            {
                "Safehouse: " + (Estate ?? "?") + ", level " + Of(Level, MaxLevel) + (Bonus >= 0 ? " (+" + Bonus + "% income, attack and defense)" : "")

                    + (Next != null || UpgradeCost >= 0 ? ". Next: " + (Next ?? "?") + (NextBonus >= 0 ? " (+" + NextBonus + "%)" : "") + (UpgradeCost >= 0 ? " for " + Money(UpgradeCost) : "") : ""),
                "Your stats: respect " + N(Respect) + ", attack power " + N(AttackPower) + ", defense power " + N(DefensePower) + ", property income " + Money(Income) + " an hour",
                "Your crew: " + Of(Hired, HireMax) + " henchmen hired, " + Of(Gear, GearMax) + " equipment slots filled",
                "Your upgrades: " + N(SkillPoints) + " skill points, energy " + N(Energy) + ", stamina " + N(Stamina) + ", health " + N(Health)
                    + ", base attack " + N(BaseAttack) + ", base defense " + N(BaseDefense) + ", property capacity " + N(Capacity),
                "Your family: " + (Family ?? (FamilySeen ? "none read" : "?")) + (Rank != null ? ", rank " + Rank : "") + (Members >= 0 ? ", " + Of(Members, MembersMax) + " members" : ""),
            };
        }
    }
}
