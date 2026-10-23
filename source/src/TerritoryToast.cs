using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text.RegularExpressions;

namespace IdleMafiaBot
{
    sealed partial class Game
    {

        internal Point TerritoryToastX(Frame f) { string text; return TerritoryToastX(f, out text); }

        internal Point TerritoryToastX(Frame f, out string text) { Rectangle box; return TerritoryToastX(f, out text, out box); }

        internal Point TerritoryToastX(Frame f, out string text, out Rectangle box) { return TerritoryToastX(f, out text, out box, IsAttackWords); }

        static bool IsAttackWords(string key) { return key.Contains("TTACKED") || key.Contains("GARRISON"); }

        internal string ShipmentToast(Frame f, out Point x)
        {
            string text;
            Rectangle box;
            x = TerritoryToastX(f, out text, out box, IsShipmentWords);
            if (!x.IsEmpty && text != null) return text.Trim();
            x = Point.Empty;
            string toast = ToastIn(f);
            if (toast == null || !IsShipmentWords(Parse.Key(toast))) return null;

            var v = ViewOf(f);
            if (v.Problem != null) return toast.Trim();
            double row = 34.5 * v.HeaderScale;
            int left = ToastProbeX(v) - 8, top = Math.Max(0, (int)(v.Energy.CenterY - row / 2) - (int)row);
            var area = Rectangle.Intersect(new Rectangle(left, top, v.Width - left, (int)(3.5 * row)), new Rectangle(0, 0, f.Width, f.Height));
            if (area.Width < 20 || area.Height < 8) return toast.Trim();
            var lines = Ocr.Read(f, area, View.ScaleFor(20 * v.HeaderScale) + 1, Prep.None).OrderBy(l => l.Box.Y).ThenBy(l => l.Box.X).ToList();
            var title = lines.FirstOrDefault(l => IsShipmentWords(Parse.Key(l.Text)));
            if (title == null) return toast.Trim();

            string all = string.Join(" / ", lines.Where(l => l.Box.Y >= title.Box.Y - 2 && l.Box.X >= title.Box.X - 3 * title.Box.Height).Select(l => l.Text.Trim())
                                                 .Where(t => t.Length > 1));
            return all.Length > toast.Trim().Length ? all : toast.Trim();
        }

        internal static bool IsShipmentWords(string key) { return key.Contains("SHIPMENT") && !key.Contains("FENCE") && !key.Contains("HOTSHIPMENT"); }

        internal Point TerritoryToastX(Frame f, out string text, out Rectangle box, Func<string, bool> accept)
        {
            text = null; box = Rectangle.Empty;
            var v = ViewOf(f);
            if (v.Problem == null) return TerritoryToastAt(f, v.Energy.CenterY, 34.5 * v.HeaderScale, v.ProbeX, out text, out box, accept);

            double hs = Math.Max(0.6, f.Height / 1009.0), rowH = 34.5 * hs;
            var tried = new List<Rectangle>();
            for (int y = (int)(8 * hs); y < Math.Min(f.Height, (int)(230 * hs)); y += Math.Max(2, (int)(6 * hs)))
            {
                var x = TerritoryToastAt(f, y, rowH, f.Width / 2, out text, out box, accept, tried);
                if (!x.IsEmpty) return x;
            }
            text = null; box = Rectangle.Empty;
            return Point.Empty;
        }

        Point TerritoryToastAt(Frame f, int y, double row, int probeX, out string text, out Rectangle box, Func<string, bool> accept, List<Rectangle> tried = null)
        {
            text = null; box = Rectangle.Empty;
            int right = -1;
            for (int x = f.Width - 1; x > probeX; x--) if (IsToastGold(f.Pixel(x, y))) { right = x; break; }

            if (right < 0 || f.Width - right > 2 * row) return Point.Empty;

            int inX = right - (int)row, top = y, bottom = y;
            if (IsToastGold(f.Pixel(inX, y))) return FilledToastAt(f, y, row, inX, right, out text, out box, accept, tried);
            while (top > 0 && y - top < 4 * row && !IsToastGold(f.Pixel(inX, top))) top--;
            while (bottom < f.Height - 1 && bottom - y < 4 * row && !IsToastGold(f.Pixel(inX, bottom))) bottom++;
            if (bottom - top < 0.8 * row || bottom - top >= 4 * row) return Point.Empty;

            int left = right;
            for (int dy = 0; dy <= 2; dy++)
            {
                int x = inX, miss = 0, end = x;
                for (; x > 0 && miss <= 2; x--) if (IsToastGold(f.Pixel(x, top + dy))) { end = x; miss = 0; } else miss++;
                left = Math.Min(left, end);
            }
            if (right - left < 6 * row) return Point.Empty;
            var inside = Rectangle.FromLTRB(left + 3, top + 3, right - 2, bottom - 2);
            if (tried != null && tried.Contains(inside)) return Point.Empty;
            string words = Ocr.ReadText(f, inside, View.ScaleFor(20 * row / 34.5), Prep.None), k = Parse.Key(words);
            if (!accept(k)) { if (tried != null) tried.Add(inside); return Point.Empty; }

            int x0 = int.MaxValue, x1 = -1, y0 = int.MaxValue, y1 = -1;
            for (int yy = top + 3; yy < Math.Min(bottom - 2, top + (int)(1.2 * row)); yy++)
                for (int x = Math.Max(left, right - (int)(1.2 * row)); x < right - 2; x++)
                {
                    var c = f.Pixel(x, yy);
                    if (Math.Min(c.R, Math.Min(c.G, c.B)) < 180) continue;
                    x0 = Math.Min(x0, x); x1 = Math.Max(x1, x); y0 = Math.Min(y0, yy); y1 = Math.Max(y1, yy);
                }
            if (x1 < 0 || x1 - x0 > row || y1 - y0 > row) return Point.Empty;
            text = words;
            box = Rectangle.FromLTRB(left, top, right, bottom);
            return new Point((x0 + x1) / 2, (y0 + y1) / 2);
        }

        Point FilledToastAt(Frame f, int y, double row, int inX, int right, out string text, out Rectangle box, Func<string, bool> accept, List<Rectangle> tried)
        {
            text = null; box = Rectangle.Empty;
            int top = y, bottom = y;
            while (top > 0 && y - top < 3 * row && IsToastGold(f.Pixel(inX, top - 1))) top--;
            while (bottom < f.Height - 1 && bottom - y < 3 * row && IsToastGold(f.Pixel(inX, bottom + 1))) bottom++;
            if (bottom - top < 0.8 * row || bottom - top >= 3 * row) return Point.Empty;

            int ry = top + 2;
            while (right + 1 < f.Width && IsToastGold(f.Pixel(right + 1, ry))) right++;
            int left = inX, miss = 0;
            for (int x = inX; x > 0 && miss <= 2; x--) if (IsToastGold(f.Pixel(x, ry))) { left = x; miss = 0; } else miss++;
            if (right - left < 6 * row) return Point.Empty;
            var inside = Rectangle.FromLTRB(left + 2, top + 2, right - 2, bottom - 2);
            if (tried != null && tried.Contains(inside)) return Point.Empty;
            string words = Ocr.ReadText(f, inside, View.ScaleFor(20 * row / 34.5), Prep.None), k = Parse.Key(words);
            if (!accept(k)) { if (tried != null) tried.Add(inside); return Point.Empty; }

            int x0 = int.MaxValue, x1 = -1, y0 = int.MaxValue, y1 = -1;
            for (int yy = top + 2; yy < Math.Min(bottom - 1, top + (int)(1.2 * row)); yy++)
                for (int x = Math.Max(left, right - (int)(1.2 * row)); x < right - 1; x++)
                {
                    var c = f.Pixel(x, yy);
                    if (Math.Max(c.R, Math.Max(c.G, c.B)) > 80) continue;
                    x0 = Math.Min(x0, x); x1 = Math.Max(x1, x); y0 = Math.Min(y0, yy); y1 = Math.Max(y1, yy);
                }
            if (x1 < 0 || x1 - x0 > row || y1 - y0 > row) return Point.Empty;
            text = words;
            box = Rectangle.FromLTRB(left, top, right, bottom);
            return new Point((x0 + x1) / 2, (y0 + y1) / 2);
        }

        public bool OpenDistrictFromToast(string name)
        {
            string text;
            Rectangle box;
            using (var f = Capture()) if (TerritoryToastX(f, out text, out box).IsEmpty) return false;
            Click(box.Left + box.Width / 3, box.Top + box.Height / 2, 900);

            for (int look = 0; look < 8; look++)
            {
                using (var f = Capture())
                {
                    var w = ReadDistrictWindow(f);
                    if (w != null && w.Is(name)) return true;

                    if (w != null && look >= 1 && (w.Title.Length > 0 || w.TitleAgain.Length > 0)) { CloseDistrict(w); return false; }
                }
                Wait(500);
            }
            return false;
        }

        internal Point RulingFamilyX(Frame f, out string text) { return RulingFamilyX(f, IsRulingWords, out text); }

        static bool IsRulingWords(string key) { return key.Contains(Parse.Key("RULING FAMILY")) || key.Contains(Parse.Key("CHANGED HANDS")) || key.Contains(Parse.Key("RULES THE CITY")); }

        internal Point RulingFamilyX(Frame f, Func<string, bool> isTitle, out string text)
        {
            text = null;

            var v = ViewOf(f);
            double row = 34.5 * (v.Problem == null ? v.HeaderScale : f.Height / 989.0);

            var area = Rectangle.Intersect(new Rectangle(f.Width / 5, 0, f.Width * 3 / 5, f.Height / 3), new Rectangle(0, 0, f.Width, f.Height));
            var lines = Ocr.Read(f, area, PopupScale(f), Prep.None);
            var title = lines.FirstOrDefault(l => isTitle(Parse.Key(l.Text)));
            if (title == null) return Point.Empty;

            Func<Color, bool> gold = c => IsToastGold(c) || c.R >= 150 && c.G >= 120 && c.B <= 130 && c.R - c.B >= 60 && c.R - c.G <= 60;

            int y = title.CenterY, right = -1, half = (int)(row / 2);
            for (int x = title.Box.Right; x < f.Width - 1 && x - title.Box.Right < 20 * row; x++)
                if (y - half >= 0 && y + half < f.Height && gold(f.Pixel(x, y)) && gold(f.Pixel(x, y - half)) && gold(f.Pixel(x, y + half))) { right = x; break; }
            if (right < 0) return Point.Empty;

            int inX = right - (int)row, top = y;
            while (top > 0 && y - top < 3 * row && !gold(f.Pixel(inX, top))) top--;
            if (y - top >= 3 * row) return Point.Empty;
            int bottom = y;
            while (bottom < f.Height - 1 && bottom - y < 4 * row && !gold(f.Pixel(inX, bottom))) bottom++;

            int x0 = int.MaxValue, x1 = -1, y0 = int.MaxValue, y1 = -1;
            for (int yy = top + 3; yy < Math.Min(f.Height, top + (int)(1.2 * row)); yy++)
                for (int x = right - (int)(1.2 * row); x < right - 2; x++)
                {
                    var c = f.Pixel(x, yy);
                    if (Math.Min(c.R, Math.Min(c.G, c.B)) < 130 || Math.Max(c.R, Math.Max(c.G, c.B)) - Math.Min(c.R, Math.Min(c.G, c.B)) > 40) continue;
                    x0 = Math.Min(x0, x); x1 = Math.Max(x1, x); y0 = Math.Min(y0, yy); y1 = Math.Max(y1, yy);
                }
            if (x1 < 0 || x1 - x0 > row || y1 - y0 > row) return Point.Empty;
            text = string.Join(" ", lines.Where(l => l.Box.Y >= top && l.Box.Bottom <= bottom && l.Box.Right <= right && l.Box.X >= right - 20 * row).OrderBy(l => l.Box.Y).ThenBy(l => l.Box.X).Select(l => l.Text.Trim()));
            return new Point((x0 + x1) / 2, (y0 + y1) / 2);
        }

        public bool CloseRulingFamily(Frame f)
        {
            string text;
            var x = RulingFamilyX(f, out text);
            if (x.IsEmpty) return false;
            log("Closing the game's \"ruling family has changed hands\" window with its x (\"" + text + "\")");
            Click(x.X, x.Y, 500);
            return true;
        }

        public void CloseShipmentToast(Point x)
        {
            if (x.IsEmpty) return;
            log("Closing the game's shipment notification with its x - it covered the bars");
            Click(x.X, x.Y, 500);
        }

        internal Point AnyToastX(Frame f, out string text) { Rectangle box; return TerritoryToastX(f, out text, out box, k => k.Length > 0); }

        internal static Point GreenToastX(Frame f)
        {
            double hs = Math.Max(0.6, f.Height / 1009.0), row = 34.5 * hs;
            int xs = Math.Max(0, f.Width - (int)(2.5 * row)), yEnd = Math.Min(f.Height - 1, (int)(260 * hs));
            int left = (int)(0.6 * row), right = (int)(0.35 * row);
            for (int y = 1; y < yEnd; y++)
                for (int x = xs; x < f.Width - right - 1; x++)
                {
                    if (!IsToastWhite(f.Pixel(x, y)) || !IsToastGreen(f.Pixel(x - left, y)) || !IsToastGreen(f.Pixel(x + right, y))) continue;

                    int x0 = x, x1 = x, y0 = y, y1 = y;
                    for (int yy = y; yy < Math.Min(f.Height, y + (int)row); yy++)
                        for (int xx = Math.Max(0, x - (int)(row / 2)); xx < Math.Min(f.Width, x + (int)(row / 2)); xx++)
                            if (IsToastWhite(f.Pixel(xx, yy))) { x0 = Math.Min(x0, xx); x1 = Math.Max(x1, xx); y1 = Math.Max(y1, yy); }
                    if (x1 - x0 < 3 || y1 - y0 < 3 || x1 - x0 > row / 2 || y1 - y0 > row / 2) continue;
                    if (!IsToastGreen(f.Pixel(Math.Max(0, x0 - 3), (y0 + y1) / 2)) || !IsToastGreen(f.Pixel(Math.Min(f.Width - 1, x1 + 3), (y0 + y1) / 2))) continue;
                    return new Point((x0 + x1) / 2, (y0 + y1) / 2);
                }
            return Point.Empty;
        }

        static bool IsToastGreen(Color c) { return c.G >= 75 && c.G <= 140 && c.G - c.R >= 30 && c.G - c.B >= 30 && c.R < 80; }

        static bool IsToastWhite(Color c) { return Math.Min(c.R, Math.Min(c.G, c.B)) >= 200; }

        static string Short(string words)
        {
            words = Regex.Replace(Regex.Replace(words ?? "", @"\s+", " ").Trim(), @"^[xX×]\s+|\s+[xX×]$", "");
            return words.Length > 60 ? words.Substring(0, 60) + "..." : words;
        }

        public bool CloseTerritoryToast(Frame f)
        {
            string text;
            var x = AnyToastX(f, out text);

            if (x.IsEmpty) { x = GreenToastX(f); text = "a green one"; }
            if (x.IsEmpty) return false;
            log("Closing the game's notification (\"" + Short(text) + "\") with its x - it covered the bars");
            Click(x.X, x.Y, 500);
            return true;
        }

        public bool CloseTerritoryToasts()
        {
            bool any = false;
            for (int i = 0; i < 6; i++)
            {
                Point x;
                string text;
                using (var f = Capture())
                {
                    x = AnyToastX(f, out text);
                    if (x.IsEmpty) { x = GreenToastX(f); text = "a green one"; }
                }
                if (x.IsEmpty) break;
                log("Closing the game's notification (\"" + Short(text) + "\") with its x - it covered the page");
                Click(x.X, x.Y, 500);
                any = true;
            }
            return any;
        }
    }
}
