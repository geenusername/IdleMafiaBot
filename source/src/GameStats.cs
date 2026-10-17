using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text.RegularExpressions;

namespace IdleMafiaBot
{
    sealed class PlayerStats
    {
        public int Level = -1;
        public int PlaytimeSecs = -1;
        public string PlaytimeText = "";
        public long Respect = -1;
        public long FightWins = -1, Defenses = -1, BossesBeaten = -1;
        public long JobsDone = -1, JobsMastered = -1, OpsDone = -1, Properties = -1;
        public double IncomePerHour = -1, CashBanked = -1;
        public long HeistsDone = -1, HeistAssists = -1, HeistSabotages = -1, CleanRuns = -1;
        public long UpgradeTries = -1, UpgradeWins = -1;
        public int Found;
        public DateTime At;

        public List<string> Lines()
        {
            var lines = new List<string>();
            if (Level > 0 || Respect >= 0 || PlaytimeSecs >= 0)
                lines.Add("Player: level " + (Level > 0 ? Level.ToString() : "?")
                    + (Respect >= 0 ? ", respect " + Respect.ToString("N0") : "")
                    + (PlaytimeSecs >= 0 ? ", playtime " + PlaytimeText : ""));
            if (FightWins >= 0 || Defenses >= 0 || BossesBeaten >= 0)
                lines.Add("Combat: " + FightWins + " fight wins, " + Defenses + " defenses, " + BossesBeaten + " bosses beaten");
            if (JobsDone >= 0 || OpsDone >= 0 || Properties >= 0)
                lines.Add("Empire: " + JobsDone + " jobs (" + JobsMastered + " mastered), " + OpsDone + " operations, " + Properties + " properties"
                    + (IncomePerHour >= 0 ? ", " + SafehouseInfo.Money(IncomePerHour) + "/hr" : "")
                    + (CashBanked >= 0 ? ", " + SafehouseInfo.Money(CashBanked) + " banked" : ""));
            if (HeistsDone >= 0 || HeistAssists >= 0)
                lines.Add("Heists: " + HeistsDone + " done, " + HeistAssists + " assists, " + HeistSabotages + " sabotages, " + CleanRuns + " clean runs");
            if (UpgradeTries >= 0)
                lines.Add("Upgrades: " + UpgradeWins + " of " + UpgradeTries + " tries worked");
            return lines;
        }
    }

    sealed partial class Game
    {
        public PlayerStats ReadPlayerStats()
        {
            var s = new PlayerStats();
            for (int attempt = 0; attempt < 3; attempt++)
            {
                Check();
                using (var f = Capture())
                {
                    if (StatsTitle(f) != null) break;
                    if (DropdownLines(f) != null) { CloseMenu(f); continue; }
                    var at = FindMenuButton(f);
                    if (at.IsEmpty) { Wait(1500); continue; }
                    Click(at.X, at.Y, 900);
                }
                using (var f = Capture())
                {
                    var stats = DropdownStats(f);
                    if (stats == null) continue;
                    Press(stats, 1200);
                }
            }
            OcrLine title;
            using (var f = Capture()) title = StatsTitle(f);
            if (title == null)
            {
                NoteFailure("the STATS panel didn't open");
                Snapshot("stats not opened", 60);
                return s;
            }
            var seen = new HashSet<string>();
            for (int pass = 0; pass < 5; pass++)
            {
                Check();
                int before = s.Found;
                using (var f = Capture()) ReadStatsRows(f, s, seen);
                if (s.Found == before) break;
                using (var f = Capture())
                {
                    var v = ViewOf(f);
                    var p = v != null && v.Problem == null ? v.PagePoint(0.5, 0.5) : new Point(f.Width / 2, f.Height / 2);
                    Scroll(p.X, p.Y, -4);
                }
                Wait(600);
            }
            for (int attempt = 0; attempt < 3; attempt++)
            {
                Check();
                using (var f = Capture())
                {
                    title = StatsTitle(f);
                    if (title == null) break;
                    var close = StatsClose(f, title);
                    if (close.IsEmpty) { Snapshot("stats no close mark", 60); break; }
                    Click(close.X, close.Y, 900);
                }
            }
            using (var f = Capture())
                if (StatsTitle(f) != null) { NoteFailure("the STATS panel didn't close"); Snapshot("stats not closed", 60); }
            if (s.Found > 0) s.At = DateTime.UtcNow;
            return s;
        }

        void CloseMenu(Frame f)
        {
            var v = ViewOf(f);
            double hs = v != null && v.Problem == null ? v.HeaderScale : Math.Max(0.6, f.Height / 1009.0);
            var at = FindMenuButton(f);
            if (!at.IsEmpty) Click(at.X, at.Y, 700);
            else if (v != null && v.Problem == null) Click(f.Width - (int)(30 * hs), v.Header.Bottom / 2, 700);
        }

        static List<OcrLine> DropdownLines(Frame f)
        {
            var area = new Rectangle(f.Width / 2, 0, f.Width - f.Width / 2, f.Height);
            int scale = Math.Max(2, Math.Min(4, (int)Math.Ceiling(1100.0 / Math.Max(300, area.Height / 4))));
            var lines = Ocr.Read(f, area, scale, Prep.None);
            var want = new[] { "MAIL", "SETTINGS", "PLAYERS", "STATS", "UPDATES" };
            var found = lines.Where(l => want.Any(w => Parse.Key(l.Text) == Parse.Key(w))).ToList();
            return found.Count >= 3 ? found : null;
        }

        static OcrLine DropdownStats(Frame f)
        {
            var lines = DropdownLines(f);
            if (lines == null) return null;
            return lines.FirstOrDefault(l => Parse.Key(l.Text) == Parse.Key("STATS"))
                ?? lines.FirstOrDefault(l => Parse.Key(l.Text).Contains(Parse.Key("STAT")));
        }

        static OcrLine StatsTitle(Frame f)
        {
            int scale = Math.Max(2, Math.Min(4, (int)Math.Ceiling(1000.0 / Math.Max(300, f.Height / 3))));
            return Ocr.Read(f, new Rectangle(0, 0, f.Width, f.Height), scale, Prep.None)
                .FirstOrDefault(l => Parse.Has(l.Text, "YOUR STATS"));
        }

        Point FindMenuButton(Frame f)
        {
            var v = ViewOf(f);
            double hs = v != null && v.Problem == null ? v.HeaderScale : Math.Max(0.6, f.Height / 1009.0);
            int top = 0, bottom = v != null && v.Problem == null ? v.Header.Bottom : (int)(100 * hs);
            int x0 = Math.Max(0, f.Width - (int)(100 * hs)), x1 = f.Width - 2;
            if (bottom <= top + 4 || x1 <= x0 + 4) return Point.Empty;
            int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1, red = 0;
            for (int y = top; y < bottom; y += 2)
                for (int x = x0; x < x1; x += 2)
                {
                    var c = f.Pixel(x, y);
                    if (c.R < 140 || c.R - c.G < 50 || c.R - c.B < 50) continue;
                    red++;
                    minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                    minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                }
            if (red >= 12 && maxX - minX >= 6 && maxY - minY >= 4 && maxX - minX <= (int)(70 * hs))
                return new Point(Math.Max(2, minX - (int)(30 * hs)), (minY + maxY) / 2);
            var glyph = MenuGlyph(f, top, bottom, hs);
            if (!glyph.IsEmpty) return glyph;
            int midY = (top + bottom) / 2;
            return midY > 2 ? new Point(f.Width - (int)(32 * hs), midY) : Point.Empty;
        }

        static Point MenuGlyph(Frame f, int top, int bottom, double hs)
        {
            int step = Math.Max(2, (int)(6 * hs));
            for (int y = top + step; y < bottom - step; y += 2)
            {
                int rows = 0, cy = y;
                for (int r = 0; r < 3; r++)
                {
                    int ry = y + r * step;
                    if (ry >= bottom) break;
                    int runAt = 0;
                    for (int x = Math.Max(0, f.Width - (int)(70 * hs)); x < f.Width - 2; x++)
                    {
                        var c = f.Pixel(x, ry);
                        bool light = c.R >= 120 && c.G >= 120 && c.B >= 120
                            && Math.Max(c.R, Math.Max(c.G, c.B)) - Math.Min(c.R, Math.Min(c.G, c.B)) <= 40;
                        if (light) { if (++runAt >= (int)(22 * hs)) break; }
                        else if (runAt > 0 && x > f.Width - (int)(30 * hs)) break;
                    }
                    if (runAt >= (int)(22 * hs)) { rows++; cy = ry; }
                }
                if (rows >= 3) return new Point(f.Width - (int)(32 * hs), cy - step);
            }
            return Point.Empty;
        }

        static Point StatsClose(Frame f, OcrLine title)
        {
            int th = Math.Max(8, title.Box.Height);
            int x0 = title.Box.Right + 2 * th, y0 = Math.Max(0, title.Box.Y - th), y1 = Math.Min(f.Height, title.Box.Bottom + th);
            Func<Color, bool> light = c => c.R >= 150 && c.G >= 150 && c.B >= 150 && Math.Max(c.R, Math.Max(c.G, c.B)) - Math.Min(c.R, Math.Min(c.G, c.B)) <= 25;
            int minX = -1, maxX = -1;
            for (int x = Math.Max(x0, f.Width / 2); x < f.Width; x++)
            {
                bool any = false;
                for (int y = y0; y < y1 && !any; y++) any = light(f.Pixel(x, y));
                if (any) { if (minX < 0) minX = x; maxX = x; }
                else if (minX >= 0 && x - maxX > th / 2) break;
            }
            if (maxX < 0 || maxX - minX > 4 * th || maxX - minX < th / 3) return Point.Empty;
            int minY = int.MaxValue, maxY = -1;
            for (int y = y0; y < y1; y++)
                for (int x = minX; x <= maxX; x++)
                    if (light(f.Pixel(x, y))) { minY = Math.Min(minY, y); maxY = Math.Max(maxY, y); }
            if (maxY < 0) return Point.Empty;
            return new Point((minX + maxX) / 2, (minY + maxY) / 2);
        }

        static readonly Tuple<string, string>[] StatLabels =
        {
            Tuple.Create("Level", "int"), Tuple.Create("Total playtime", "time"), Tuple.Create("Current respect", "int"),
            Tuple.Create("Fight wins", "int"), Tuple.Create("Successful defenses", "int"), Tuple.Create("Bosses defeated", "int"),
            Tuple.Create("Jobs completed", "int"), Tuple.Create("Jobs fully mastered", "int"), Tuple.Create("Operations completed", "int"),
            Tuple.Create("Properties owned", "int"), Tuple.Create("Property income per hour", "money"), Tuple.Create("Total cash banked", "money"),
            Tuple.Create("Heists completed", "int"), Tuple.Create("Heist assists", "int"), Tuple.Create("Heist sabotages", "int"), Tuple.Create("Clean runs", "int"),
            Tuple.Create("Upgrade attempts", "int"), Tuple.Create("Successful upgrades", "int"),
        };

        void ReadStatsRows(Frame f, PlayerStats s, HashSet<string> seen)
        {
            var area = new Rectangle(0, 0, f.Width, f.Height);
            var lines = new List<OcrLine>();
            foreach (var t in new[] { Tuple.Create(2, Prep.None), Tuple.Create(3, Prep.None), Tuple.Create(2, Prep.WhiteText) })
                lines.AddRange(Ocr.Read(f, area, t.Item1, t.Item2));
            foreach (var kv in StatLabels)
            {
                string key = Parse.Key(kv.Item1);
                if (!seen.Add(key)) continue;
                var label = lines.Where(l => l.Box.X < f.Width * 62 / 100)
                    .OrderBy(l => l.Box.Y)
                    .FirstOrDefault(l => Parse.Key(l.Text) == key || Parse.Key(l.Text).StartsWith(key) || (key.Length > 8 && Parse.Key(l.Text).Contains(key)));
                if (label == null) { seen.Remove(key); continue; }
                int h = Math.Max(8, label.Box.Height);
                string row = label.Text;
                var right = lines.Where(l => l != label && Math.Abs(l.CenterY - label.CenterY) <= h && l.Box.X > label.Box.X)
                    .OrderByDescending(l => l.CenterX).FirstOrDefault();
                if (right != null) row += " " + right.Text;
                if (SetStat(s, kv.Item1, kv.Item2, row)) s.Found++;
                else seen.Remove(key);
            }
        }

        static bool SetStat(PlayerStats s, string label, string type, string row)
        {
            if (type == "money")
            {
                double v;
                if (!Parse.Money(row, out v)) return false;
                if (label == "Property income per hour") s.IncomePerHour = v;
                else s.CashBanked = v;
                return true;
            }
            if (type == "time")
            {
                int secs = PlaytimeSecs(row);
                if (secs < 0) return false;
                s.PlaytimeSecs = secs;
                s.PlaytimeText = ShortPlaytime(secs);
                return true;
            }
            var m = Regex.Match(row, @"([0-9][0-9,\.]*)\s*[^0-9]*$");
            if (!m.Success) m = Regex.Match(row, @"([0-9][0-9,\.]*)");
            if (!m.Success) return false;
            long n;
            if (!long.TryParse(m.Groups[1].Value.Replace(",", "").Replace(".", ""), out n)) return false;
            switch (label)
            {
                case "Level": if (n <= 0 || n > 10000) return false; s.Level = (int)n; return true;
                case "Current respect": s.Respect = n; return true;
                case "Fight wins": s.FightWins = n; return true;
                case "Successful defenses": s.Defenses = n; return true;
                case "Bosses defeated": s.BossesBeaten = n; return true;
                case "Jobs completed": s.JobsDone = n; return true;
                case "Jobs fully mastered": s.JobsMastered = n; return true;
                case "Operations completed": s.OpsDone = n; return true;
                case "Properties owned": s.Properties = n; return true;
                case "Heists completed": s.HeistsDone = n; return true;
                case "Heist assists": s.HeistAssists = n; return true;
                case "Heist sabotages": s.HeistSabotages = n; return true;
                case "Clean runs": s.CleanRuns = n; return true;
                case "Upgrade attempts": s.UpgradeTries = n; return true;
                case "Successful upgrades": s.UpgradeWins = n; return true;
                default: return false;
            }
        }

        static int PlaytimeSecs(string row)
        {
            int total = 0;
            bool any = false;
            foreach (Match m in Regex.Matches(row ?? "", @"([0-9OoIlS]{1,4})\s*([dhmsDHMS])\b"))
            {
                int n;
                if (!int.TryParse(Parse.Digits(m.Groups[1].Value), out n)) continue;
                switch (m.Groups[2].Value.ToLowerInvariant())
                {
                    case "d": total += n * 86400; break;
                    case "h": total += n * 3600; break;
                    case "m": total += n * 60; break;
                    default: total += n; break;
                }
                any = true;
            }
            return any ? total : -1;
        }

        static string ShortPlaytime(int secs)
        {
            int d = secs / 86400, h = secs % 86400 / 3600, m = secs % 3600 / 60;
            return (d > 0 ? d + "d " : "") + (h > 0 || d > 0 ? h + "h " : "") + m + "m";
        }
    }
}
