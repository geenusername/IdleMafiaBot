using System;
using System.Drawing;

namespace IdleMafiaBot
{
    sealed partial class Game
    {

        internal Point TerritoryToastX(Frame f) { string text; return TerritoryToastX(f, out text); }

        internal Point TerritoryToastX(Frame f, out string text) { Rectangle box; return TerritoryToastX(f, out text, out box); }

        internal Point TerritoryToastX(Frame f, out string text, out Rectangle box)
        {
            text = null; box = Rectangle.Empty;
            var v = ViewOf(f);
            if (v.Problem != null) return Point.Empty;
            double row = 34.5 * v.HeaderScale;
            int y = v.Energy.CenterY, right = -1;
            for (int x = f.Width - 1; x > v.ProbeX; x--) if (IsToastGold(f.Pixel(x, y))) { right = x; break; }

            if (right < 0 || f.Width - right > 2 * row) return Point.Empty;

            int inX = right - (int)row, top = y, bottom = y;
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
            string words = Ocr.ReadText(f, inside, View.ScaleFor(20 * v.HeaderScale), Prep.None), k = Parse.Key(words);
            if (!k.Contains("ATTACKED") && !k.Contains("GARRISON")) return Point.Empty;

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

        public bool CloseTerritoryToast(Frame f)
        {
            var x = TerritoryToastX(f);
            if (x.IsEmpty) return false;
            log("Closing the game's territory notification with its x - it covered the bars");
            Click(x.X, x.Y, 500);
            return true;
        }
    }
}
