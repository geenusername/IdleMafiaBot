using System;
using System.Drawing;

namespace IdleMafiaBot
{

    sealed partial class Game
    {

        public Func<bool> WantShots;

        public Bitmap LastFightShot;

        public Action<ShopItem, int> ShopSeen;

        bool ShotsWanted() { var w = WantShots; try { return w != null && w(); } catch (Exception) { return false; } }

        static bool Vivid(Frame f, int x, int y)
        {
            var c = f.Pixel(x, y);
            int max = Math.Max(c.R, Math.Max(c.G, c.B)), min = Math.Min(c.R, Math.Min(c.G, c.B));
            return max >= 100 && max - min >= 45;
        }

        static bool VividNear(Frame f, int x, int y) { return Vivid(f, x, y) || Vivid(f, x - 1, y) || Vivid(f, x + 1, y) || Vivid(f, x, y - 1) || Vivid(f, x, y + 1); }

        internal static Rectangle FrameAround(Frame f, Rectangle inside)
        {
            int y = inside.Y + inside.Height / 2, x = inside.Right + 2, dark = 0, right = -1;
            for (; x < f.Width; x++)
            {
                if (!Vivid(f, x, y)) { dark++; continue; }
                if (dark >= 3) { right = x; break; }
                dark = 0;
            }
            if (right < 0) return Rectangle.Empty;

            while (right + 1 < f.Width && Vivid(f, right + 1, y)) right++;
            int top = y, bottom = y;
            while (top > 0 && VividNear(f, right, top - 1)) top--;
            while (bottom + 1 < f.Height && VividNear(f, right, bottom + 1)) bottom++;
            int left = right;
            while (left > 0 && VividNear(f, left - 1, top + 1)) left--;
            var r = Rectangle.FromLTRB(left, top, right + 1, bottom + 1);
            if (r.Width < 3 * inside.Width || r.Height < 2 * inside.Height || r.Right <= inside.Right || r.Left >= inside.Left) return Rectangle.Empty;
            return r;
        }

        internal static Bitmap Crop(Frame f, Rectangle r)
        {
            r = Rectangle.Intersect(Rectangle.Inflate(r, 2, 2), new Rectangle(0, 0, f.Width, f.Height));
            if (r.Width < 20 || r.Height < 20) return null;
            var b = new Bitmap(r.Width, r.Height);
            using (var g = Graphics.FromImage(b)) g.DrawImage(f.Bitmap, new Rectangle(0, 0, r.Width, r.Height), r, GraphicsUnit.Pixel);
            return b;
        }
    }
}
