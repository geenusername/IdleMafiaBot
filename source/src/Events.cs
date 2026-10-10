using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text.RegularExpressions;

namespace IdleMafiaBot
{

    enum GameEvent { Cash, EnergyRegen, StaminaRegen, HealthRegen, OpsSpeed, BossDamage, Mastery, CrateLuck, RecruitLuck, ShopSale, Other }

    sealed partial class Game
    {

        public sealed class EventBadge
        {
            public Rectangle Box;
            public Color Colour;
            public override string ToString() { return Box.X + "," + Box.Y + " " + Box.Width + "x" + Box.Height + " (" + Colour.R + "," + Colour.G + "," + Colour.B + ")"; }
        }

        public sealed class ActiveEvent
        {
            public GameEvent Kind = GameEvent.Other;
            public string Title = "", Text = "";
            public int SecondsLeft = -1;
            public override string ToString() { return (Title.Length > 0 ? Title : Kind.ToString()) + (SecondsLeft >= 0 ? " (ends in " + Parse.Short(SecondsLeft) + ")" : ""); }
        }

        static bool Saturated(Color c)
        {
            int max = Math.Max(c.R, Math.Max(c.G, c.B)), min = Math.Min(c.R, Math.Min(c.G, c.B));
            return max >= 110 && max - min >= 50;
        }

        static bool SameColour(Color a, Color b, int tol) { return Math.Abs(a.R - b.R) <= tol && Math.Abs(a.G - b.G) <= tol && Math.Abs(a.B - b.B) <= tol; }

        public static List<EventBadge> FindBadges(Frame f)
        {
            var found = new List<EventBadge>();

            int x0 = Math.Max(f.Width * 12 / 100, 180), x1 = f.Width * 55 / 100, y1 = Math.Min(f.Height, f.Height * 6 / 100 + 4);
            int minRun = Math.Max(8, f.Width / 110), maxRun = f.Width * 5 / 100 + 10, minH = Math.Max(8, f.Height * 12 / 1000), maxH = f.Height * 45 / 1000 + 2;
            var runs = new List<Tuple<int, int, int, Color>>();
            for (int y = 2; y < y1; y++)
                for (int x = x0; x < x1; )
                {
                    var c = f.Pixel(x, y);
                    if (!Saturated(c)) { x++; continue; }
                    int s = x;
                    while (x < x1 && Saturated(f.Pixel(x, y)) && SameColour(f.Pixel(x, y), c, 16)) x++;
                    if (x - s >= minRun && x - s <= maxRun) runs.Add(Tuple.Create(y, s, x - 1, c));
                }
            foreach (var top in runs)
            {
                if (found.Any(b => Math.Abs(b.Box.X - top.Item2) <= 2 && top.Item1 >= b.Box.Y && top.Item1 <= b.Box.Bottom)) continue;
                var bottom = runs.Where(r => r.Item1 >= top.Item1 + minH && r.Item1 <= top.Item1 + maxH && Math.Abs(r.Item2 - top.Item2) <= 2
                                             && Math.Abs(r.Item3 - top.Item3) <= 2 && SameColour(r.Item4, top.Item4, 24))
                                 .OrderByDescending(r => r.Item1).FirstOrDefault();
                if (bottom == null) continue;
                int side = 0, n = 0;
                for (int y = top.Item1; y <= bottom.Item1; y++) { n++; if (SameColour(f.Pixel(top.Item2, y), top.Item4, 30)) side++; }
                if (side * 10 < n * 7) continue;
                found.Add(new EventBadge { Box = Rectangle.FromLTRB(top.Item2, top.Item1, top.Item3 + 1, bottom.Item1 + 1), Colour = top.Item4 });
            }
            return found.OrderBy(b => b.Box.X).ToList();
        }

        public static bool LuckBadge(List<EventBadge> badges)
        {
            return badges.Any(b => SameColour(b.Colour, Color.FromArgb(110, 155, 220), 16));
        }

        public static string BadgeSignature(List<EventBadge> badges)
        {
            return string.Join(";", badges.Select(b => (b.Colour.R / 32) + "." + (b.Colour.G / 32) + "." + (b.Colour.B / 32) + "@" + b.Box.Width / 4));
        }

        static readonly Regex EndsRx = new Regex(@"ENDS\s*[I1l]N\s*(.+)$", RegexOptions.IgnoreCase);

        public static ActiveEvent ReadTooltip(Frame f, EventBadge b)
        {
            var frame = TooltipFrame(f, b);
            if (!frame.IsEmpty)
            {
                var inside = Rectangle.FromLTRB(frame.Left + 3, frame.Top + 3, frame.Right - 3, frame.Bottom - 3);
                var all = Ocr.Read(f, inside, f.Height < 800 ? 3 : 2, Prep.None).OrderBy(l => l.Box.Y).ThenBy(l => l.Box.X).ToList();
                if (all.Count > 0)
                {
                    var t = new ActiveEvent { Title = all[0].Text.Trim(), Text = string.Join(" ", all.Select(l => l.Text.Trim())) };

                    var endsLine = all.FirstOrDefault(l => EndsRx.IsMatch(l.Text)) ?? all.FirstOrDefault(l => Parse.Key(l.Text).StartsWith("ENDSIN"));
                    if (endsLine != null && EndsRx.IsMatch(endsLine.Text)) t.SecondsLeft = Parse.Countdown(EndsRx.Match(endsLine.Text).Groups[1].Value);
                    if (t.SecondsLeft < 0) t.SecondsLeft = TooltipEnds(f, inside, endsLine);
                    t.Kind = EventKind(t.Text);
                    return t;
                }
            }
            var area = Rectangle.Intersect(new Rectangle(b.Box.X - f.Width / 100, b.Box.Bottom + 1, f.Width * 24 / 100, f.Height * 15 / 100), new Rectangle(0, 0, f.Width, f.Height));
            if (area.Width < 20 || area.Height < 20) return null;
            int scale = f.Height < 800 ? 3 : 2;
            var lines = Ocr.Read(f, area, scale, Prep.None).OrderBy(l => l.Box.Y).ThenBy(l => l.Box.X).ToList();
            var ends = lines.FirstOrDefault(l => EndsRx.IsMatch(l.Text));
            var mine = lines.Where(l => ends == null || l.Box.Y <= ends.Box.Y + 2).Where(l => l.Box.X >= b.Box.X - f.Width / 100 - 2).ToList();
            if (mine.Count == 0) return null;
            var e = new ActiveEvent { Title = mine[0].Text.Trim(), Text = string.Join(" ", mine.Select(l => l.Text.Trim())) };
            if (ends != null) e.SecondsLeft = Parse.Countdown(EndsRx.Match(ends.Text).Groups[1].Value);
            e.Kind = EventKind(e.Text);

            if (ends == null && e.Kind == GameEvent.Other) return null;
            return e;
        }

        static Rectangle TooltipFrame(Frame f, EventBadge b)
        {
            int x = b.Box.X + 2, maxY = Math.Min(f.Height - 2, b.Box.Bottom + 2 * b.Box.Height);
            for (int y = b.Box.Bottom + 1; y < maxY; y++)
            {
                if (!SameColour(f.Pixel(x, y), b.Colour, 30)) continue;
                int right = x;
                for (int xx = x, miss = 0; xx < f.Width && miss < 3; xx++)
                    if (SameColour(f.Pixel(xx, y), b.Colour, 30)) { right = xx; miss = 0; } else miss++;
                if (right - x < 3 * b.Box.Width) continue;
                int left = x, bottom = y;
                while (left > 0 && SameColour(f.Pixel(left - 1, y), b.Colour, 30)) left--;
                while (bottom < f.Height - 1 && SameColour(f.Pixel(left, bottom + 1), b.Colour, 30)) bottom++;
                if (bottom - y < 2 * b.Box.Height) continue;
                return Rectangle.FromLTRB(left, y, right + 1, bottom + 1);
            }
            return Rectangle.Empty;
        }

        static int TooltipEnds(Frame f, Rectangle inside, OcrLine endsLine)
        {
            Rectangle row;
            if (endsLine != null)
            {
                int h = Math.Max(6, endsLine.Box.Height);
                row = Rectangle.Intersect(Rectangle.FromLTRB(inside.Left, endsLine.Box.Y - h / 2, inside.Right, endsLine.Box.Bottom + h / 2), inside);
            }
            else row = Rectangle.FromLTRB(inside.Left, inside.Top + inside.Height * 2 / 3, inside.Right, inside.Bottom);

            var tries = new[] { Tuple.Create(2, Prep.None), Tuple.Create(3, Prep.None), Tuple.Create(4, Prep.None), Tuple.Create(3, Prep.Contrast), Tuple.Create(2, Prep.GreyText) };
            var words = Ocr.ReadWords(f, row, 3, Prep.None).OrderBy(w => w.Box.X).ToList();
            int ends = words.FindIndex(w => Parse.Key(w.Text).StartsWith("ENDS"));
            var inWord = ends >= 0 ? words.Skip(ends + 1).FirstOrDefault(w => Regex.IsMatch(w.Text, @"^[I1l]N$", RegexOptions.IgnoreCase)) : null;
            if (inWord != null)
            {
                var time = Rectangle.FromLTRB(inWord.Box.Right + 2, row.Top, row.Right, row.Bottom);
                foreach (var t in tries)
                {
                    int s = Parse.Countdown(Ocr.ReadText(f, time, t.Item1, t.Item2).Trim());
                    if (s >= 0) return s;
                }
            }
            foreach (var t in tries)
            {
                var m = EndsRx.Match(Ocr.ReadText(f, row, t.Item1, t.Item2));
                int s = m.Success ? Parse.Countdown(m.Groups[1].Value) : -1;
                if (s >= 0) return s;
            }
            return -1;
        }

        internal static GameEvent EventKind(string text)
        {
            string k = Parse.Key(text);
            if (k.Contains("RECRUIT") || k.Contains("RARERHENCHMEN")) return GameEvent.RecruitLuck;
            if (k.Contains("CRATE") && (k.Contains("IUCK") || k.Contains("RARER"))) return GameEvent.CrateLuck;
            if (k.Contains("BOSS") && k.Contains("DAMAGE")) return GameEvent.BossDamage;
            if (k.Contains("TAKEDOWN") && k.Contains("DAMAGE")) return GameEvent.BossDamage;
            if (k.Contains("OPERATION") && (k.Contains("SPEED") || k.Contains("FAST") || k.Contains("TIMER"))) return GameEvent.OpsSpeed;
            if (k.Contains("MASTERY")) return GameEvent.Mastery;
            if (k.Contains("SHOP") && (k.Contains("SAIE") || k.Contains("OFF"))) return GameEvent.ShopSale;
            if (k.Contains("ENERGY") && (k.Contains("REFIII") || k.Contains("REGEN") || k.Contains("FAST"))) return GameEvent.EnergyRegen;
            if (k.Contains("STAMINA") && (k.Contains("REFIII") || k.Contains("REGEN") || k.Contains("FAST"))) return GameEvent.StaminaRegen;
            if (k.Contains("HEAITH") && (k.Contains("REFIII") || k.Contains("REGEN") || k.Contains("FAST"))) return GameEvent.HealthRegen;
            if (k.Contains("CASH") || k.Contains("PAYDOUBIE")) return GameEvent.Cash;
            return GameEvent.Other;
        }

        public List<ActiveEvent> ReadEvents(List<EventBadge> badges)
        {
            var events = new List<ActiveEvent>();
            foreach (var b in badges)
            {
                Check();
                Win.Hover(b.Box.X + b.Box.Width / 2, b.Box.Y + b.Box.Height / 2);
                ActiveEvent e = null;
                for (int look = 0; look < 3 && (e == null || e.Kind == GameEvent.Other || e.SecondsLeft < 0); look++)
                {
                    Wait(look == 0 ? 350 : 300);
                    using (var f = Capture())
                    {
                        var again = ReadTooltip(f, b);
                        if (again != null && (e == null || again.Kind != GameEvent.Other || again.SecondsLeft >= 0)) e = again;
                    }
                }
                if (e != null) events.Add(e);
            }
            Win.Park();
            return events;
        }

        public sealed class BriefcasePage
        {
            public bool Page;
            public int Free = -1;
            public int ButtonFree = -1;
            public int NextSeconds = -1;
            public int Keys = -1, Left = -1;
            public string Paid;
            public List<Rectangle> Cases = new List<Rectangle>();
        }

        static readonly Regex FreePicksRx = new Regex(@"([0-9]{1,2})\s*FREE\s*PICKS?", RegexOptions.IgnoreCase);
        static readonly Regex ButtonPicksRx = new Regex(@"([0-9]{1,2})\s*FREE\s*PICKS?");
        static readonly Regex NoPickRx = new Regex(@"^\s*FREE\s*PICK\s*[I1l]N\s*(.+)$", RegexOptions.IgnoreCase);
        static readonly Regex NextPickRx = new Regex(@"\+\s*1\s*[I1l]N\s*([^)]+)", RegexOptions.IgnoreCase);
        static readonly Regex HaveRx = new Regex(@"have\s*([0-9]{1,2}|no)\s*free\s*pick", RegexOptions.IgnoreCase);
        static readonly Regex KeysRx = new Regex(@"^\s*([0-9O]{1,3})\s*KEYS?\s*$", RegexOptions.IgnoreCase);
        static readonly Regex LeftRx = new Regex(@"^\s*([0-9]{1,2})\s*LEFT\s*$", RegexOptions.IgnoreCase);
        static readonly Regex PaidRx = new Regex(@"Case\s*\S{1,3}\s*paid\s*(.+?)\.?\s*(Added|$)", RegexOptions.IgnoreCase);

        static bool IsPlate(Color c) { return Math.Abs(c.R - 245) <= 14 && Math.Abs(c.G - 236) <= 14 && Math.Abs(c.B - 215) <= 16; }

        public BriefcasePage ReadBriefcases(Frame f)
        {
            var page = new BriefcasePage();
            var area = PageArea(f);
            var lines = Ocr.Read(f, area, PageScale(f), Prep.None);
            bool title = lines.Any(l => Parse.Key(l.Text).Contains("BRIEFCASES") && l.Box.Y < area.Y + area.Height / 4);
            var board = lines.FirstOrDefault(l => Parse.Key(l.Text).Contains("ONTHISBOARD"));
            page.Page = title && (board != null || lines.Any(l => Parse.Key(l.Text).Contains("FREEPICK")));
            if (!page.Page) return page;

            var titleLine = lines.Where(l => Parse.Key(l.Text).Contains("BRIEFCASES")).OrderBy(l => l.Box.Y).First();
            int th = Math.Max(10, titleLine.Box.Height);
            var band = Rectangle.Intersect(Rectangle.FromLTRB(area.X + area.Width / 2, titleLine.Box.Y - th, area.Right, titleLine.Box.Bottom + th), area);
            Func<OcrLine, bool> timer = l => NextPickRx.IsMatch(l.Text) || NoPickRx.IsMatch(l.Text.Trim());

            if (!lines.Any(timer))
                foreach (var t in new[] { Tuple.Create(1, Prep.None), Tuple.Create(2, Prep.Contrast), Tuple.Create(3, Prep.None) })
                {
                    var more = Ocr.Read(f, band, t.Item1, t.Item2, true);
                    lines.AddRange(more);
                    if (more.Any(timer)) break;
                }
            foreach (var l in lines)
            {
                string t = l.Text.Trim();
                Match m;
                if ((m = FreePicksRx.Match(t)).Success && page.Free < 0) page.Free = int.Parse(m.Groups[1].Value);

                if ((m = ButtonPicksRx.Match(t)).Success && page.ButtonFree < 0 && !Regex.IsMatch(t, @"\bhave\b", RegexOptions.IgnoreCase)) page.ButtonFree = int.Parse(m.Groups[1].Value);
                if ((m = NoPickRx.Match(t)).Success) { page.Free = page.ButtonFree = 0; page.NextSeconds = Parse.Countdown(m.Groups[1].Value); }
                if ((m = NextPickRx.Match(t)).Success && page.NextSeconds < 0) page.NextSeconds = Parse.Countdown(m.Groups[1].Value);
                if ((m = HaveRx.Match(t)).Success && page.Free < 0) page.Free = m.Groups[1].Value.ToLowerInvariant() == "no" ? 0 : int.Parse(m.Groups[1].Value);
                if ((m = KeysRx.Match(t)).Success) page.Keys = int.Parse(m.Groups[1].Value.Replace('O', '0').Replace('o', '0'));
                if ((m = LeftRx.Match(t)).Success) page.Left = int.Parse(m.Groups[1].Value);
                if ((m = PaidRx.Match(t)).Success) page.Paid = m.Groups[1].Value.Trim();
            }

            int right = board != null ? board.Box.X - board.Box.Height : area.X + area.Width * 2 / 3;
            int minW = f.Width * 4 / 100, minH = f.Height * 25 / 1000;
            var seen = new List<Rectangle>();
            for (int y = area.Y + area.Height / 6; y < area.Bottom; y += 2)
                for (int x = area.X; x < right; )
                {
                    if (!IsPlate(f.Pixel(x, y))) { x += 2; continue; }
                    int s = x;
                    while (x < right && IsPlate(f.Pixel(x, y))) x++;
                    if (x - s < minW || seen.Any(r => r.Contains(s + (x - s) / 2, y))) continue;

                    int top = y, bottom = y, cx = s + Math.Max(2, (x - s) / 20);
                    while (top > area.Y && IsPlate(f.Pixel(cx, top - 1))) top--;
                    while (bottom < area.Bottom - 1 && IsPlate(f.Pixel(cx, bottom + 1))) bottom++;
                    var plate = Rectangle.FromLTRB(s, top, x, bottom + 1);
                    seen.Add(plate);
                    if (plate.Height >= minH && plate.Height <= plate.Width) page.Cases.Add(plate);
                }
            page.Cases = page.Cases.OrderBy(r => r.Y / Math.Max(1, minH * 2)).ThenBy(r => r.X).ToList();
            return page;
        }

        OcrLine BriefcasesEntry(Frame f, View v)
        {
            var lines = Ocr.Read(f, v.Menu, View.ScaleFor(f, 11, 11.0 * f.Height / 1009), Prep.None);
            return lines.FirstOrDefault(l => Parse.Key(l.Text).Contains("BRIEFCASES") || Parse.Key(l.Text).Contains("BRIEFCASE"));
        }

        public bool OpenBriefcases()
        {
            bool rolled = false;
            for (int attempt = 0; attempt < 4; attempt++)
            {
                OcrLine entry;
                View v;
                using (var f = Capture())
                {
                    v = ViewOf(f);
                    if (v.Problem != null) { bool cleared = ClearPopupIfAny(f); ForgetView(); if (!cleared) Wait(1000); continue; }
                    if (ReadBriefcases(f).Page) return true;
                    entry = BriefcasesEntry(f, v);
                }
                if (entry == null)
                {
                    if (rolled) return false;
                    rolled = true;
                    Scroll(v.Menu.X + v.Menu.Width / 2, v.Menu.Y + v.Menu.Height / 2, 10);
                    ForgetView();
                    continue;
                }
                if (entry.CenterY < ChatZoneBottom) using (var f = Capture()) if (HideChat(f)) { ForgetView(); continue; }
                Press(entry.Text, entry.CenterX, entry.CenterY, 900);
                for (int look = 0; look < 3; look++)
                {
                    using (var f = Capture()) if (ReadBriefcases(f).Page) return true;
                    Wait(600);
                }
                ForgetView();
            }
            return false;
        }

        public string PickBriefcase(BriefcasePage page, out BriefcasePage after)
        {
            after = null;
            if (page == null || page.Free <= 0 || page.Cases.Count == 0) return null;
            var plate = page.Cases[0];
            Press("briefcase", plate.X + plate.Width / 2, plate.Y + plate.Height / 2, 900);
            for (int look = 0; look < 4; look++)
            {
                using (var f = Capture())
                {
                    if (look == 0) CheckForPurchasePrompt(Ocr.Read(f, PopupArea(f), PopupScale(f), Prep.None));
                    after = ReadBriefcases(f);
                    if (Picked(page, after))
                    {
                        Snapshot("lucky briefcase picked", 0, true);
                        return after.Paid != null && after.Paid != page.Paid ? after.Paid : "";
                    }
                }
                Wait(500);
            }
            Snapshot("lucky briefcase pick not seen", 60);
            return null;
        }

        internal static bool Picked(BriefcasePage before, BriefcasePage after)
        {
            if (before == null || after == null || !after.Page) return false;
            return (after.Paid != null && after.Paid != before.Paid) || after.Cases.Count < before.Cases.Count
                   || (after.Free >= 0 && before.Free >= 0 && after.Free < before.Free) || (after.Left >= 0 && before.Left >= 0 && after.Left < before.Left);
        }

        public static int FreeFromButton(BriefcasePage page) { return page == null || !page.Page ? -1 : page.ButtonFree; }
    }
}
