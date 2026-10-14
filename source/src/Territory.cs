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

        static readonly Regex ToastWhat = new Regex(@"^(.*?)\s*[—–\-:]*\s*(attacked|garrison\s*[lI1|]ost)", RegexOptions.IgnoreCase);
        static readonly Regex DistrictRow = new Regex(@"^\s*(\d{3})\s+(.+?)\s*/\s*(.+?)\s*$");
        static readonly Regex GarrisonRx = new Regex(@"GARRISON\s*([0-9OoIl]{1,3})\s*/\s*([0-9OoIl]{1,3})", RegexOptions.IgnoreCase);
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
            garrisonLost = Parse.Key(m.Groups[2].Value).StartsWith("GARRISON");
            var from = Regex.Match(text, @"\bfrom\s+(.+?)\s*$", RegexOptions.IgnoreCase);

            if (from.Success) by = Regex.Replace(from.Groups[1].Value.Trim(), @"\s+[xX×]$", "");
            return m.Groups[1].Value.Trim();
        }

        internal static bool MatchDistrict(string read, out string city, out int number, out string name)
        {
            city = name = null; number = -1;

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

        internal bool TerritoryBar(Frame f, out OcrLine cityBox, out OcrLine districtBox)
        {
            cityBox = districtBox = null;
            var area = PageArea(f);
            var lines = Ocr.Read(f, area, PageScale(f), Prep.None);
            var search = lines.FirstOrDefault(l => Parse.Has(l.Text, "Search districts"));
            if (search == null) return false;
            int h = Math.Max(8, search.Box.Height);
            cityBox = lines.Where(l => Math.Abs(l.CenterY - search.CenterY) < 2 * h && l.Box.Right < search.Box.X && Districts.Cities.Any(c => Parse.Key(l.Text).StartsWith(Parse.Key(c))))
                           .OrderByDescending(l => l.Box.X).FirstOrDefault();
            districtBox = lines.Where(l => Math.Abs(l.CenterY - search.CenterY) < 2 * h && l.Box.X > search.Box.Right && l.Box.X - search.Box.Right < 12 * h)
                               .OrderBy(l => l.Box.X).FirstOrDefault();
            return cityBox != null && districtBox != null;
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

        static readonly Regex HoldsRx = new Regex(@"HOLDS\s*([0-9OoIl]{1,3})\s*OF\s*([0-9OoIl]{1,3})", RegexOptions.IgnoreCase);

        public List<Tuple<string, int, string>> ReadFamilyDistricts()
        {
            if (!OpenTab(Tab.Territory)) return null;
            var held = new List<Tuple<string, int, string>>();
            List<OcrLine> lines;
            using (var f = Capture()) lines = Ocr.Read(f, PageArea(f), PageScale(f), Prep.None);
            if (!lines.Any(l => Parse.Has(l.Text, "MATCHING")))
            {
                var mine = lines.FirstOrDefault(l => Parse.Key(l.Text) == "MYFAMIIY");
                if (mine == null) return null;
                Click(mine.CenterX, mine.CenterY, 1500);
            }
            foreach (var city in Districts.Cities)
            {
                OcrLine cityBox, districtBox;
                if (!SelectCity(city, out cityBox, out districtBox)) continue;
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
                for (int look = 0; look < 12; look++)
                {
                    int before = seen.Count;
                    OcrLine any = null;
                    using (var f = Capture())
                        foreach (var l in Ocr.Read(f, PageArea(f), PageScale(f) + 1, Prep.None).Where(l => l.Box.Y > districtBox.Box.Bottom))
                        {
                            var m = DistrictRow.Match(l.Text);
                            if (!m.Success) continue;
                            any = l;
                            string c2, nm; int no;

                            seen[int.Parse(m.Groups[1].Value)] = MatchDistrict(m.Groups[2].Value, out c2, out no, out nm) && c2 == city ? nm : m.Groups[2].Value.Trim();
                        }
                    if (any == null || (look > 0 && seen.Count == before) || (holds > 0 && seen.Count >= holds)) break;
                    Scroll(any.CenterX, any.CenterY, -3);
                    Wait(400);
                }
                Click(districtBox.CenterX, districtBox.CenterY, 700);
                foreach (var kv in seen.OrderBy(kv => kv.Key)) held.Add(Tuple.Create(city, kv.Key, kv.Value));
            }
            return held;
        }

        public bool OpenDistrict(string city, int number, string name)
        {
            LastDistrictGone = false;
            if (!OpenTab(Tab.Territory)) return false;
            OcrLine cityBox, districtBox;
            if (!SelectCity(city, out cityBox, out districtBox)) return false;
            int h = Math.Max(8, cityBox.Box.Height);

            Click(districtBox.CenterX, districtBox.CenterY, 900);
            OcrLine pick = null;
            for (int look = 0; look < 25 && pick == null; look++)
            {
                List<Tuple<int, OcrLine>> rows;
                using (var f = Capture())
                    rows = Ocr.Read(f, PageArea(f), PageScale(f) + 1, Prep.None).Where(l => l.Box.Y > districtBox.Box.Bottom)
                              .Select(l => { var m = DistrictRow.Match(l.Text); return m.Success ? Tuple.Create(int.Parse(m.Groups[1].Value), l) : null; })
                              .Where(t => t != null).ToList();
                pick = rows.Where(t => t.Item1 == number || View.Distance(Parse.Key(DistrictRow.Match(t.Item2.Text).Groups[2].Value), Parse.Key(name)) <= 1)
                           .Select(t => t.Item2).FirstOrDefault();
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
                var title = lines.Where(l => l.Box.Bottom < held.Box.Y && held.Box.Y - l.Box.Bottom < 6 * h && Regex.IsMatch(l.Text, "[A-Za-z]{3}")).OrderByDescending(l => l.Box.Height).FirstOrDefault();
                if (title != null)
                {
                    w.Title = title.Text.Trim();
                    w.Close = CloseMark(f, title);

                    var box = Rectangle.Inflate(title.Box, title.Box.Height / 2, title.Box.Height / 4 + 2);
                    w.TitleAgain = Ocr.ReadText(f, Rectangle.Intersect(box, new Rectangle(0, 0, f.Width, f.Height)), PopupScale(f) + 1, Prep.Contrast).Trim();
                }
                string after = Regex.Replace(held.Text, @"^.*HELD\s*BY\s*", "", RegexOptions.IgnoreCase).Trim();
                if (after.Length < 2) after = string.Join(" ", lines.Where(l => Math.Abs(l.CenterY - held.CenterY) < h && l.Box.X > held.Box.Right).OrderBy(l => l.Box.X).Select(l => l.Text.Trim()));
                w.HeldBy = after;
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
            int minX = int.MaxValue, maxX = -1, minY = int.MaxValue, maxY = -1;
            for (int y = y0; y < y1; y++)
                for (int x = Math.Max(x0, f.Width / 2); x < f.Width; x++)
                {
                    var c = f.Pixel(x, y);
                    if (c.R < 150 || c.G < 150 || c.B < 150 || Math.Max(c.R, Math.Max(c.G, c.B)) - Math.Min(c.R, Math.Min(c.G, c.B)) > 25) continue;
                    minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                }
            if (maxX < 0 || maxX - minX > 4 * th || maxX - minX < th / 3) return Point.Empty;
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
