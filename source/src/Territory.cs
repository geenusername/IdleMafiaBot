using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text.RegularExpressions;

namespace IdleMafiaBot
{

    sealed class DistrictWindow
    {
        public string Title = "", TitleAgain = "", HeldBy = "";

        public bool Is(string name)
        {
            string k = Parse.Key(name);
            foreach (var t in new[] { Parse.Key(Title), Parse.Key(TitleAgain) })
            {
                if (t.Length == 0) continue;
                if (View.Distance(t, k) <= 2) return true;
                if (t.Length >= 7 && t.Length * 10 >= k.Length * 7 && k.Contains(t)) return true;
            }
            return false;
        }
        public int Garrison = -1, GarrisonMax = -1, Available = -1;
        public OcrLine StationCrew, StationAll;
        public Point Close = Point.Empty;
    }

    sealed partial class Game
    {

        static readonly Regex ToastWhat = new Regex(@"^(.*?)\s*[—–\-:]*\s*(defen[cs]es\s*fa[lI1|]{2}en|attacked|garrison\s*[lI1|]ost)", RegexOptions.IgnoreCase);

        static readonly Regex DistrictRow = new Regex(@"^\s*([0-9OoDIl|]{3})\s+(.+?)\s*/\s*(.+?)\s*$");

        internal static bool ListedDistrict(string text, string city, out int number, out string name)
        {
            number = -1; name = null;
            var m = Regex.Match(text ?? "", @"^\s*(\S{2,4})\s+(.+?)\s*/\s*(.+?)\s*$");
            if (!m.Success || View.Distance(Parse.Key(m.Groups[3].Value), Parse.Key(city)) > 2) return false;
            string c2;
            if (MatchDistrict(m.Groups[2].Value, out c2, out number, out name) && c2 == city) return true;
            string digits = Regex.Replace(m.Groups[1].Value, "[OoD]", "0");
            digits = Regex.Replace(digits, "[Il|]", "1");
            if (!Regex.IsMatch(digits, @"^\d{3}$")) return false;
            number = int.Parse(digits);
            name = m.Groups[2].Value.Trim();
            return true;
        }

        internal static int DistrictRowNumber(string text)
        {
            var m = DistrictRow.Match(text ?? "");
            return m.Success ? int.Parse(Parse.Digits(m.Groups[1].Value)) : -1;
        }
        static readonly Regex GarrisonRx =new Regex(@"GARRISON\s*([0-9OoIl]{1,3})\s*/\s*([0-9OoIl]{1,3})", RegexOptions.IgnoreCase);
        static readonly Regex GarrisonFreeRx = new Regex(@"([0-9]{1,3})\s*available", RegexOptions.IgnoreCase);

        internal string TerritoryToastDistrict(Frame f, out bool garrisonLost, out Point x) { string by; return TerritoryToastDistrict(f, out garrisonLost, out x, out by); }

        internal string TerritoryToastDistrict(Frame f, out bool garrisonLost, out Point x, out string by)
        {
            garrisonLost = false;
            by = "";
            string text;
            x = TerritoryToastX(f, out text);
            if (x.IsEmpty || text == null) return null;
            var m = ToastWhat.Match(text.Trim());
            if (!m.Success) return null;
            garrisonLost = Parse.Key(m.Groups[2].Value).StartsWith("GARRISON") || Parse.Key(m.Groups[2].Value).StartsWith("DEFEN");
            var from = Regex.Match(text, @"\bfrom\s+(.+?)\s*$", RegexOptions.IgnoreCase);

            if (from.Success) by = Regex.Replace(from.Groups[1].Value.Trim(), @"\s+[xX×]$", "");
            return m.Groups[1].Value.Trim();
        }

        internal static bool MatchDistrict(string read, out string city, out int number, out string name)
        {
            city = name = null; number = -1;

            read = Regex.Replace(read ?? "", @"^\W*\d{1,2}[.):]?\s+(?=[A-Za-z])", "");
            string k = Parse.Key((read ?? "").Replace("æ", "as").Replace("Æ", "As"));
            if (k.Length < 4) return false;
            var hits = new List<Tuple<int, string, int, string>>();
            foreach (var kv in Districts.ByCity)
                for (int i = 0; i < kv.Value.Length; i++)
                {
                    int d = View.Distance(Parse.Key(kv.Value[i]), k);
                    if (d <= Math.Min(2, k.Length / 6)) hits.Add(Tuple.Create(d, kv.Key, i + 1, kv.Value[i]));
                }
            if (hits.Count == 0) return false;
            int best = hits.Min(h => h.Item1);
            var top = hits.Where(h => h.Item1 == best).ToList();
            if (top.Count != 1) return false;
            city = top[0].Item2; number = top[0].Item3; name = top[0].Item4;
            return true;
        }

        internal static string CleanHolder(string read)
        {
            var words = (read ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).ToList();
            if (words.Count > 1 && Regex.IsMatch(words[words.Count - 1], @"^(TURF|FORT|STRONGHOLD|CAPITAL|HQ)$", RegexOptions.IgnoreCase)) words.RemoveAt(words.Count - 1);
            return string.Join(" ", words).Trim();
        }

        internal static bool HeldByUs(string heldBy, string ours)
        {
            string h = Parse.Key(heldBy), o = Parse.Key(ours);
            if (o.Length == 0 || h.Length <= 2) return true;
            if (View.Distance(h, o) <= 2 || h.Contains(o)) return true;
            if (h.Length >= o.Length && o.Length >= 3 && View.Distance(h.Substring(0, o.Length), o) <= 1) return true;

            return o.Length >= 3 && h.Length > o.Length && Enumerable.Range(0, h.Length - o.Length + 1).Any(i => View.Distance(h.Substring(i, o.Length), o) <= 1) && h.Length <= o.Length + 4;
        }

        internal bool TerritoryBar(Frame f, out OcrLine cityBox, out OcrLine districtBox)
        {
            cityBox = districtBox = null;
            var area = PageArea(f);
            var lines = Ocr.Read(f, area, PageScale(f), Prep.None);
            var search = lines.FirstOrDefault(l => Parse.Has(l.Text, "Search districts"));
            if (search == null) return TerritoryBarTyped(lines, area, out cityBox, out districtBox);
            int h = Math.Max(8, search.Box.Height);
            var row = lines.Where(l => Math.Abs(l.CenterY - search.CenterY) < 2 * h).ToList();
            cityBox = row.Where(l => l.Box.Right < search.Box.X && Districts.Cities.Any(c => Parse.Key(l.Text).StartsWith(Parse.Key(c))))
                         .OrderByDescending(l => l.Box.X).FirstOrDefault();

            string[] buttons = { "MYFAMIIY", "AIIBONUSES", "SETTINGS" };
            districtBox = row.Where(l => l.Box.X > search.Box.Right && l.Box.X - search.Box.Right < Math.Max(12 * h, area.Width / 7) && !buttons.Contains(Parse.Key(l.Text)))
                             .OrderBy(l => l.Box.X).FirstOrDefault();

            if (districtBox != null) districtBox = Stacked(row, districtBox, h);
            if (cityBox != null) cityBox = Stacked(row, cityBox, h);
            return cityBox != null && districtBox != null;
        }

        static bool TerritoryBarTyped(List<OcrLine> lines, Rectangle area, out OcrLine cityBox, out OcrLine districtBox)
        {
            cityBox = districtBox = null;
            var mine = lines.FirstOrDefault(l => Parse.Key(l.Text) == "MYFAMIIY");
            if (mine == null) return false;
            int h = Math.Max(8, mine.Box.Height);
            var row = lines.Where(l => Math.Abs(l.CenterY - mine.CenterY) < 2 * h).ToList();
            cityBox = row.Where(l => l.Box.Right < mine.Box.X && Districts.Cities.Any(c => Parse.Key(l.Text).StartsWith(Parse.Key(c))))
                         .OrderBy(l => l.Box.X).FirstOrDefault();
            if (cityBox == null) return false;
            var city = cityBox;
            var last = row.Where(l => l.Box.X > city.Box.Right + 4 * h && l.Box.Right < mine.Box.X).OrderByDescending(l => l.Box.X).FirstOrDefault();
            if (last == null) return false;
            districtBox = Stacked(row, last, h);
            cityBox = Stacked(row, cityBox, h);
            return true;
        }

        static OcrLine Stacked(List<OcrLine> row, OcrLine first, int h)
        {
            var stack = row.Where(l => Math.Abs(l.Box.X - first.Box.X) < 2 * h && !(l.Box.Right < first.Box.X || l.Box.X > first.Box.Right)).OrderBy(l => l.Box.Y).ToList();
            if (stack.Count < 2) return first;
            var box = stack[0].Box;
            foreach (var l in stack) box = Rectangle.Union(box, l.Box);
            return new OcrLine { Text = string.Join(" ", stack.Select(l => l.Text.Trim())), Box = box };
        }

        bool SelectCity(string city, out OcrLine cityBox, out OcrLine districtBox)
        {
            using (var f = Capture()) if (!TerritoryBar(f, out cityBox, out districtBox)) { log("Territory: the page's city and district drop-downs didn't read"); return false; }
            if (Parse.Key(cityBox.Text).StartsWith(Parse.Key(city))) return true;
            int h = Math.Max(8, cityBox.Box.Height);
            var box = cityBox;
            Click(box.CenterX, box.CenterY, 900);
            OcrLine row;
            using (var f = Capture())
                row = Ocr.Read(f, PageArea(f), PageScale(f), Prep.None)
                         .FirstOrDefault(l => l.Box.Y > box.Box.Bottom && Math.Abs(l.Box.X - box.Box.X) < 6 * h && Parse.Key(l.Text).StartsWith(Parse.Key(city)) && Parse.Has(l.Text, "TIER"));
            if (row == null) { log("Territory: " + city + " not in the city list"); Click(box.CenterX, box.CenterY, 600); return false; }
            Click(row.CenterX, row.CenterY, 2000);
            using (var f = Capture()) return TerritoryBar(f, out cityBox, out districtBox) && Parse.Key(cityBox.Text).StartsWith(Parse.Key(city));
        }

        bool MyFamilyOn()
        {
            for (int tries = 0; tries < 2; tries++)
            {
                List<OcrLine> lines;
                using (var f = Capture()) lines = Ocr.Read(f, PageArea(f), PageScale(f), Prep.None);
                if (lines.Any(l => Parse.Has(l.Text, "MATCHING"))) return true;
                var mine = lines.FirstOrDefault(l => Parse.Key(l.Text) == "MYFAMIIY");
                if (mine == null) break;
                Click(mine.CenterX, mine.CenterY, 1500);
            }
            log("Territory: the MY FAMILY filter didn't turn on - no district opened");
            return false;
        }

        internal List<string> FlaggedCities(Frame f)
        {
            var got = new List<string>();
            var lines = Ocr.Read(f, PageArea(f), PageScale(f), Prep.None);
            var search = lines.FirstOrDefault(l => Parse.Has(l.Text, "Search districts"));
            foreach (var row in lines.Where(l => Parse.Has(l.Text, "TIER")))
            {
                string city = Districts.Cities.FirstOrDefault(c => Parse.Key(row.Text).StartsWith(Parse.Key(c)));
                if (city == null || got.Contains(city)) continue;
                if (RowFlagged(f, row.Box, search != null ? search.Box.X : row.Box.X + 30 * Math.Max(8, row.Box.Height))) got.Add(city);
            }
            return got;
        }

        static bool FlagGold(Color c) { return c.R > 170 && c.G > 130 && c.G < 200 && c.B < 90 && c.R - c.B > 110; }

        internal static bool RowFlagged(Frame f, Rectangle row, int limit)
        {
            int h = Math.Max(8, row.Height);
            int y0 = Math.Max(0, row.Y + row.Height / 2 - h), y1 = Math.Min(f.Height - 1, row.Y + row.Height / 2 + h);
            int gold = 0;
            for (int x = row.Right + h; x < Math.Min(f.Width, limit); x++)
            {
                int col = 0;
                for (int y = y0; y <= y1; y++) if (FlagGold(f.Pixel(x, y))) col++;
                if (col * 10 >= (y1 - y0 + 1) * 9) break;
                gold += col;
            }
            return gold * 3 >= h * h;
        }

        List<string> FlaggedCities(OcrLine cityBox)
        {
            Click(cityBox.CenterX, cityBox.CenterY, 900);
            List<string> got;
            using (var f = Capture()) got = FlaggedCities(f);
            Click(cityBox.CenterX, cityBox.CenterY, 600);

            bool open;
            using (var f = Capture()) open = Ocr.Read(f, PageArea(f), PageScale(f), Prep.None).Count(l => l.Box.Y > cityBox.Box.Bottom && Parse.Has(l.Text, "TIER")) >= 3;
            if (open) Click(cityBox.CenterX, cityBox.CenterY, 600);
            return got;
        }

        static readonly Regex HoldsRx = new Regex(@"HOLDS\s*([0-9OoIl]{1,3})\s*OF\s*([0-9OoIl]{1,3})", RegexOptions.IgnoreCase);

        public List<Tuple<string, int, string>> ReadFamilyDistricts()
        {
            if (!OpenTab(Tab.Territory)) return null;
            var held = new List<Tuple<string, int, string>>();
            List<OcrLine> lines;
            using (var f = Capture()) lines = Ocr.Read(f, PageArea(f), PageScale(f), Prep.None);
            if (!MyFamilyOn()) return null;

            var cities = Districts.Cities.ToList();
            OcrLine bar, barDistrict;
            bool barRead;
            using (var f = Capture()) barRead = TerritoryBar(f, out bar, out barDistrict);
            if (barRead)
            {
                var flagged = FlaggedCities(bar);
                log("Territory: the city list's flag " + (flagged.Count > 0 ? "on " + string.Join(", ", flagged) : "not seen - every city looked at"));
                if (flagged.Count > 0) cities = flagged;
            }

            for (int pass = 0; pass < 2; pass++)
            {
                if (pass == 1)
                {
                    if (held.Count > 0 || cities.Count == Districts.Cities.Count()) break;
                    log("Territory: no district read in " + string.Join(", ", cities) + " - every city looked at");
                    cities = Districts.Cities.ToList();
                }
            foreach (var city in cities)
            {
                OcrLine cityBox, districtBox;
                if (!SelectCity(city, out cityBox, out districtBox)) continue;

                if (!MyFamilyOn()) continue;
                int holds = -1;
                using (var f = Capture())
                    foreach (var l in Ocr.Read(f, PageArea(f), PageScale(f), Prep.None))
                    {
                        var m = HoldsRx.Match(l.Text);
                        int n;
                        if (m.Success && int.TryParse(Parse.Digits(m.Groups[1].Value), out n)) holds = n;
                    }
                if (holds == 0) continue;
                Click(districtBox.CenterX, districtBox.CenterY, 900);

                Scroll(districtBox.CenterX, districtBox.Box.Bottom + 6 * Math.Max(8, districtBox.Box.Height), 10);
                Wait(300);

                var seen = new Dictionary<int, string>();
                int still = 0;
                for (int look = 0; look < 15; look++)
                {
                    int before = seen.Count;
                    OcrLine any = null;
                    using (var f = Capture())
                        foreach (var scale in new[] { PageScale(f) + 1, PageScale(f) + 2 })
                            foreach (var l in Ocr.Read(f, PageArea(f), scale, Prep.None).Where(l => l.Box.Y > districtBox.Box.Bottom))
                            {
                                int no;
                                string nm;
                                if (!ListedDistrict(l.Text, city, out no, out nm)) continue;
                                any = l;
                                seen[no] = nm;
                            }
                    if (any == null || (holds > 0 && seen.Count >= holds)) break;

                    if (look > 0 && seen.Count == before && ++still >= 2) break;
                    if (seen.Count > before) still = 0;
                    Scroll(any.CenterX, any.CenterY, -3);
                    Wait(500);
                }
                if (holds > 0 && seen.Count > 0 && seen.Count < holds)
                {
                    log("Territory: " + city + "'s district list gave " + seen.Count + " of the " + holds + " the page counts");
                    Snapshot("district list short", 180);
                }
                Click(districtBox.CenterX, districtBox.CenterY, 700);
                if (seen.Count == 0) log("Territory: " + city + "'s district list read nothing (the page says " + (holds < 0 ? "no count" : "it holds " + holds) + ")");
                foreach (var kv in seen.OrderBy(kv => kv.Key)) held.Add(Tuple.Create(city, kv.Key, kv.Value));
            }
            }
            return held;
        }

        public bool OpenDistrict(string city, int number, string name)
        {
            LastDistrictGone = false;
            if (!OpenTab(Tab.Territory)) return false;
            OcrLine cityBox, districtBox;
            if (!SelectCity(city, out cityBox, out districtBox)) return false;
            if (!MyFamilyOn()) return false;
            int h = Math.Max(8, cityBox.Box.Height);

            Click(districtBox.CenterX, districtBox.CenterY, 900);
            OcrLine pick = null;
            for (int look = 0; look < 25 && pick == null; look++)
            {
                List<Tuple<int, OcrLine>> rows;
                using (var f = Capture())
                    rows = Ocr.Read(f, PageArea(f), PageScale(f) + 1, Prep.None).Where(l => l.Box.Y > districtBox.Box.Bottom)
                              .Select(l => { var m = DistrictRow.Match(l.Text); return m.Success ? Tuple.Create(int.Parse(Parse.Digits(m.Groups[1].Value)), l) : null; })
                              .Where(t => t != null).ToList();

                Func<Tuple<int, OcrLine>, int> off = t => View.Distance(Parse.Key(DistrictRow.Match(t.Item2.Text).Groups[2].Value), Parse.Key(name));
                pick = rows.Where(t => off(t) <= 2 || (t.Item1 == number && off(t) <= 4)).OrderBy(off).Select(t => t.Item2).FirstOrDefault();
                if (pick != null || rows.Count == 0) break;
                int notches = number < rows.Min(t => t.Item1) ? 3 : -3;
                Scroll(rows[0].Item2.CenterX, rows[rows.Count / 2].Item2.CenterY, notches);
                Wait(400);
            }
            if (pick == null)
            {

                bool filtered;
                using (var f = Capture()) filtered = Ocr.Read(f, PageArea(f), PageScale(f), Prep.None).Any(l => Parse.Has(l.Text, "MATCHING"));
                LastDistrictGone = filtered;
                log("Territory: " + name + " not found in " + city + "'s district list" + (filtered ? " of your family's districts" : ""));
                Click(districtBox.CenterX, districtBox.CenterY, 600);
                return false;
            }
            Click(pick.CenterX, pick.CenterY, 2500);

            OcrLine nowCity, nowDistrict;
            bool took;
            using (var f = Capture()) took = TerritoryBar(f, out nowCity, out nowDistrict) && View.Distance(Parse.Key(Regex.Replace(nowDistrict.Text, @"\s*[Vv]$", "")), Parse.Key(name)) <= 2;
            if (!took)
            {
                log("Territory: picking " + name + " in the district list didn't take");
                CloseStrayDistrict();
                return false;
            }

            OcrLine label = null;
            for (int look = 0; look < 3 && label == null; look++)
            {
                if (look > 0) Wait(700);
                using (var f = Capture())
                    label = Ocr.Read(f, PageArea(f), PageScale(f), Prep.None)
                               .Where(l => l.Box.Y > districtBox.Box.Bottom + 2 * h && View.Distance(Parse.Key(l.Text), Parse.Key(name)) <= 2)
                               .OrderBy(l => View.Distance(Parse.Key(l.Text), Parse.Key(name))).FirstOrDefault();
            }
            if (label == null) { log("Territory: " + name + " not seen on the map"); CloseStrayDistrict(); return false; }
            Click(label.CenterX, label.CenterY + 2 * label.Box.Height, 2000);
            DistrictWindow w;
            using (var f = Capture()) w = ReadDistrictWindow(f);
            if (w == null || !w.Is(name))
            {
                log("Territory: " + name + "'s window didn't open" + (w != null ? " (" + w.Title + " did)" : ""));
                if (w != null) CloseDistrict(w);
                return false;
            }
            return true;
        }

        public bool LastDistrictGone;

        void CloseStrayDistrict()
        {
            DistrictWindow w;
            using (var f = Capture()) w = ReadDistrictWindow(f);
            if (w != null) CloseDistrict(w);
        }

        public DistrictWindow ReadDistrictWindow(Frame f)
        {
            var lines = Ocr.Read(f, PopupArea(f), PopupScale(f), Prep.None);
            var held = lines.FirstOrDefault(l => Parse.Has(l.Text, "HELD BY"));
            var gar = lines.Select(l => Tuple.Create(l, GarrisonRx.Match(l.Text))).FirstOrDefault(t => t.Item2.Success);
            if (held == null && gar == null) return null;
            var w = new DistrictWindow();
            int h = Math.Max(8, (held ?? gar.Item1).Box.Height);
            if (held != null)
            {

                int reach = Math.Max(6 * h, (int)(60 * Math.Min(f.Height / 1009.0, f.Width / 1790.0)));
                var title = lines.Where(l => l.Box.Bottom < held.Box.Y && held.Box.Y - l.Box.Bottom < reach && Regex.IsMatch(l.Text, "[A-Za-z]{3}")).OrderByDescending(l => l.Box.Height).FirstOrDefault();
                if (title != null)
                {

                    var row = lines.Where(l => Math.Abs(l.CenterY - title.CenterY) < title.Box.Height / 2 && l.Box.Height * 3 >= title.Box.Height * 2
                                               && Regex.IsMatch(l.Text, "[A-Za-z]{2}")).OrderBy(l => l.Box.X).ToList();
                    if (row.Count > 1)
                    {
                        var u = row.Aggregate(title.Box, (r, l) => Rectangle.Union(r, l.Box));
                        title = new OcrLine { Text = string.Join(" ", row.Select(l => l.Text.Trim())), Box = u };
                    }
                    w.Title = title.Text.Trim();
                    w.Close = CloseMark(f, title);

                    int th = title.Box.Height, more = title.Box.X <= PopupArea(f).X + 2 ? 2 * th : th / 2;
                    var box = Rectangle.FromLTRB(title.Box.X - more, title.Box.Y - th / 4 - 2, title.Box.Right + th / 2, title.Box.Bottom + th / 4 + 2);
                    w.TitleAgain = Ocr.ReadText(f, Rectangle.Intersect(box, new Rectangle(0, 0, f.Width, f.Height)), PopupScale(f) + 1, Prep.Contrast).Trim();

                    w.TitleAgain = Regex.Replace(w.TitleAgain, @"^\S\s+(?=\S{2})", "");
                }
                string after = Regex.Replace(held.Text, @"^.*HELD\s*BY\s*", "", RegexOptions.IgnoreCase).Trim();
                if (after.Length < 2) after = string.Join(" ", lines.Where(l => Math.Abs(l.CenterY - held.CenterY) < h && l.Box.X > held.Box.Right).OrderBy(l => l.Box.X).Select(l => l.Text.Trim()));
                w.HeldBy = CleanHolder(after);
            }
            if (gar != null)
            {
                int a, b;
                if (int.TryParse(Parse.Digits(gar.Item2.Groups[1].Value), out a)) w.Garrison = a;
                if (int.TryParse(Parse.Digits(gar.Item2.Groups[2].Value), out b)) w.GarrisonMax = b;
            }
            w.StationCrew = lines.FirstOrDefault(l => Parse.Key(l.Text) == "STATIONCREW");
            w.StationAll = lines.FirstOrDefault(l => Parse.Key(l.Text).StartsWith("STATIONAII"));
            var av = lines.Select(l => GarrisonFreeRx.Match(l.Text)).FirstOrDefault(m => m.Success);
            if (av != null) w.Available = int.Parse(av.Groups[1].Value);
            return w;
        }

        static Point CloseMark(Frame f, OcrLine title)
        {
            int th = Math.Max(8, title.Box.Height), x0 = title.Box.Right + 4 * th, y0 = Math.Max(0, title.Box.Y - th), y1 = Math.Min(f.Height, title.Box.Bottom + th);
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
            return new Point((minX + maxX) / 2, (minY + maxY) / 2);
        }

        public int StationAllHere()
        {
            DistrictWindow w;
            using (var f = Capture()) w = ReadDistrictWindow(f);
            if (w == null) return -1;
            if (w.StationAll == null)
            {
                if (w.StationCrew == null) return -1;
                Click(w.StationCrew.CenterX, w.StationCrew.CenterY, 1200);
                using (var f = Capture()) w = ReadDistrictWindow(f);
                if (w == null) return -1;
            }
            if (w.Available == 0) return 0;
            if (w.StationAll == null) return w.Available > 0 ? -1 : 0;
            int before = w.Garrison;
            NotDisconnected();
            if (OnlyNavigate) RefuseInCheck(w.StationAll.Text);
            Click(w.StationAll.CenterX, w.StationAll.CenterY, 1500);
            using (var f = Capture()) w = ReadDistrictWindow(f);
            if (w != null && before >= 0 && w.Garrison > before) return w.Garrison - before;
            return w != null && w.Available == 0 ? Math.Max(1, w.Garrison - Math.Max(0, before)) : 0;
        }

        public void CloseDistrict(DistrictWindow w = null)
        {
            if (w == null) using (var f = Capture()) w = ReadDistrictWindow(f);
            if (w == null || w.Close.IsEmpty) return;
            Click(w.Close.X, w.Close.Y, 900);
        }
    }
}
