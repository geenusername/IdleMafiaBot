using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Globalization;
using System.Text;

namespace IdleMafiaBot
{

    sealed class View
    {
        public int Width, Height;
        public OcrLine Energy, Stamina, Health, Cash;
        public Rectangle Header, Menu, Page;
        public double MenuScale = 1, HeaderScale = 1;
        public readonly Dictionary<Tab, OcrLine> Tabs = new Dictionary<Tab, OcrLine>();
        public string Problem;
        public int ProbeX;
        public bool EnergyPlaced;

        public string Language;

        static readonly string[][] ForeignWords =
        {
            new[] { "Spanish", "ENERGÍA", "ESTAMINA", "SALUD", "EFECTIVO EN MANO", "REFUGIO", "TRABAJOS", "PROPIEDADES", "INVENTARIO", "COLECCIÓN", "TIENDA", "LUCHAR", "BANCO", "OPERACIONES", "CONTRATOS", "TRIPULACIÓN", "JEFES", "FAMILIA", "TERRITORIO", "CLASIFICACIONES", "INTERCAMBIO", "MERCADO NEGRO", "ROBOS" },
            new[] { "Portuguese", "SAÚDE", "DINHEIRO EM MÃOS", "TRABALHOS", "PROPRIEDADES", "COLEÇÃO", "LOJA", "LUTAR", "OPERAÇÕES", "CONTRATOS", "TRIPULAÇÃO", "CHEFES", "FAMÍLIA", "TERRITÓRIO", "MERCADO NEGRO", "ASSALTOS" },
            new[] { "French", "ÉNERGIE", "ENDURANCE", "SANTÉ", "PROPRIÉTÉS", "INVENTAIRE", "BOUTIQUE", "COMBATTRE", "BANQUE", "OPÉRATIONS", "ÉQUIPAGE", "PATRONS", "FAMILLE", "TERRITOIRE", "CLASSEMENTS", "MARCHÉ NOIR", "BRAQUAGES" },
            new[] { "German", "AUSDAUER", "GESUNDHEIT", "BARGELD", "IMMOBILIEN", "SAMMLUNG", "LADEN", "KÄMPFEN", "OPERATIONEN", "VERTRÄGE", "BOSSE", "FAMILIE", "TERRITORIUM", "RANGLISTE", "HANDEL", "SCHWARZMARKT", "RAUBZÜGE" },
        };

        public static string GameLanguage(IEnumerable<OcrLine> lines)
        {

            var keys = lines.Select(l => Plain(l.Text)).Where(k => k.Length >= 4).ToList();
            Func<string, bool> seen = w => { string p = Plain(w); return keys.Any(k => k == p || (k.EndsWith(p) && k.Length <= p.Length + 2)); };
            int english = TabLabels.Values.Concat(new[] { "ENERGY", "STAMINA", "HEALTH", "CASH ON HAND" }).Count(seen);
            string best = null;
            int most = 1;
            foreach (var lang in ForeignWords)
            {
                int n = lang.Skip(1).Count(seen);
                if (n > most) { most = n; best = lang[0]; }
            }
            return best != null && most > english ? best : null;
        }

        static string Plain(string s)
        {
            var sb = new StringBuilder();
            foreach (char ch in Parse.Key(s).Normalize(NormalizationForm.FormD))
                if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) sb.Append(ch);
            return sb.ToString();
        }

        public static string InLanguage(string language)
        {
            return "The game shows in " + language + ", and the bot only reads it in English. Set Roblox to English (roblox.com, Settings, "
                 + "Account Info, Language: English), rejoin the game, then press Start again.";
        }

        public static readonly Dictionary<Tab, string> TabLabels = new Dictionary<Tab, string>
        {
            { Tab.Safehouse, "SAFEHOUSE" }, { Tab.Jobs, "JOBS" }, { Tab.Properties, "PROPERTIES" }, { Tab.Inventory, "INVENTORY" },
            { Tab.Collection, "COLLECTION" }, { Tab.Shop, "SHOP" }, { Tab.Fight, "FIGHT" }, { Tab.Bank, "BANK" },
            { Tab.Operations, "OPERATIONS" }, { Tab.Contracts, "CONTRACTS" }, { Tab.Crew, "CREW" }, { Tab.Bosses, "BOSSES" },
            { Tab.Family, "FAMILY" }, { Tab.Territory, "TERRITORY" }, { Tab.Rankings, "RANKINGS" }, { Tab.Trade, "TRADE" },
            { Tab.BlackMarket, "BLACK MARKET" }, { Tab.Heists, "HEISTS" },
        };

        public static int ScaleFor(double px) { return Math.Max(2, Math.Min(5, (int)Math.Ceiling(28 / Math.Max(4, px)))); }

        public static int ScaleFor(Frame f, double fullHd, double here) { return ScaleFor(Ocr.Big(f) ? fullHd : here); }

        public static View Read(Frame f)
        {
            var v = new View { Width = f.Width, Height = f.Height };

            int scale = ScaleFor(f, 11, 11.0 * f.Height / 1009);

            int topH = Math.Max(70, f.Height * 14 / 100);
            var strip = new Rectangle(f.Width / 3, 0, f.Width - f.Width / 3, topH);
            var top = Ocr.Read(f, strip, scale, Prep.None);
            v.Cash = Label(top, "CASH ON HAND");
            v.Energy = Label(top, "ENERGY");
            v.Stamina = Label(top, "STAMINA");
            v.Health = Label(top, "HEALTH");
            if (v.Energy == null && v.Stamina == null && v.Health == null)
            {

                int w45 = f.Width * 45 / 100;
                foreach (var t in new[]
                {
                    new { A = strip, S = Math.Max(1, scale - 1), P = Prep.None }, new { A = strip, S = scale, P = Prep.WhiteText },
                    new { A = strip, S = 1, P = Prep.WhiteText }, new { A = strip, S = scale + 1, P = Prep.None },
                    new { A = new Rectangle(f.Width - w45, 0, w45, topH), S = scale, P = Prep.WhiteSoft },
                })
                {
                    top = Ocr.Read(f, t.A, t.S, t.P);
                    v.Energy = Label(top, "ENERGY");
                    v.Stamina = Label(top, "STAMINA");
                    v.Health = Label(top, "HEALTH");
                    if (v.Energy != null || v.Stamina != null || v.Health != null) { v.Cash = v.Cash ?? Label(top, "CASH ON HAND"); break; }
                }
            }
            var any = v.Energy ?? v.Stamina ?? v.Health;
            if (any != null && (v.Energy == null || v.Stamina == null || v.Health == null))
            {
                int pad = any.Box.Height * 2;
                var column = Ocr.Read(f, new Rectangle(any.Box.X - pad, 0, any.Box.Width + 2 * pad, topH), scale + 1, Prep.None);
                v.Energy = v.Energy ?? Label(column, "ENERGY");
                v.Stamina = v.Stamina ?? Label(column, "STAMINA");
                v.Health = v.Health ?? Label(column, "HEALTH");
            }
            if (v.Cash == null && any != null)
            {

                int x0 = f.Width / 4;
                var mid = Ocr.Read(f, new Rectangle(x0, 0, Math.Max(40, any.Box.X - x0), topH), scale + 1, Prep.None);
                v.Cash = Label(mid, "CASH ON HAND");
            }

            if (v.Energy != null && v.Health != null) v.HeaderScale = (v.Health.CenterY - v.Energy.CenterY) / 69.0;
            else if (v.Stamina != null && (v.Energy ?? v.Health) != null)
                v.HeaderScale = Math.Abs(v.Stamina.CenterY - (v.Energy ?? v.Health).CenterY) / 34.5;
            else
            {
                v.Problem = "Can't find the game's top bar (ENERGY, STAMINA, HEALTH) - is Idle Mafia Game open and the window big enough?";

                v.Language = GameLanguage(top.Concat(Ocr.Read(f, new Rectangle(0, topH, Math.Max(170, f.Width / 6), f.Height - topH), scale, Prep.None)));
                if (v.Language != null) v.Problem = InLanguage(v.Language);
                return v;
            }
            v.EnergyPlaced = v.Energy == null;
            v.Energy = v.Energy ?? Beside(v.Stamina, -34.5 * v.HeaderScale);
            v.Stamina = v.Stamina ?? Beside(v.Energy, 34.5 * v.HeaderScale);
            v.Health = v.Health ?? Beside(v.Stamina, 34.5 * v.HeaderScale);
            v.ProbeX = ProbeAt(f, v);
            int headerBottom = v.Health.Box.Bottom + (int)Math.Round(20 * v.HeaderScale);
            v.Header = new Rectangle(0, 0, f.Width, headerBottom);
            ReadMenu(f, v, scale);
            return v;
        }

        public bool ReadMenu(Frame f) { return ReadMenu(f, this, ScaleFor(f, 11, 11.0 * f.Height / 1009)); }

        static bool ReadMenu(Frame f, View v, int scale)
        {
            int headerBottom = v.Header.Bottom;
            v.Tabs.Clear();
            v.menuZoom = scale;

            int stripW = Math.Max(170, f.Width / 6);
            v.stripW = stripW;
            var left = Ocr.Read(f, new Rectangle(0, headerBottom, stripW, f.Height - headerBottom), scale, Prep.None);
            foreach (var kv in TabLabels)
            {
                var l = Label(left, kv.Value);
                if (l != null && l.Box.X < stripW * 2 / 3) v.Tabs[kv.Key] = l;
            }
            KeepInOrder(v.Tabs);
            v.others.Clear();
            v.NoteOthers(left);
            if (v.Tabs.Count < 3)
            {
                v.Problem = "Can't find the game's side menu - is Idle Mafia Game open, with no window covering it?";
                return false;
            }

            var ys = v.Tabs.OrderBy(t => (int)t.Key).Select(t => new { T = (int)t.Key, Y = t.Value.CenterY }).ToList();
            var steps = new List<double>();
            for (int i = 1; i < ys.Count; i++)
                if (ys[i].T - ys[i - 1].T == 1 && ys[i].T != (int)Tab.Heists) steps.Add(ys[i].Y - ys[i - 1].Y);
            if (steps.Count > 0) v.MenuScale = steps.OrderBy(s => s).ElementAt(steps.Count / 2) / 50.0;
            else
            {

                var sorted = ys.Select(p => p.Y).OrderBy(y => y).ToList();
                var gaps = sorted.Skip(1).Select((y, i) => y - sorted[i]).Where(g => g > 8).ToList();
                if (gaps.Count > 0) v.MenuScale = gaps.Min() / 50.0;
            }
            int menuRight = (int)Math.Round(242 * v.MenuScale) + 4;
            v.Menu = new Rectangle(0, headerBottom, menuRight, f.Height - headerBottom);
            int pageLeft = menuRight + (int)Math.Round(14 * v.MenuScale);
            v.Page = new Rectangle(pageLeft, headerBottom, f.Width - pageLeft, f.Height - headerBottom);
            v.menuStep = 50 * v.MenuScale;
            v.menuX = v.Tabs.Values.Select(l => l.Box.X).OrderBy(x => x).ElementAt(v.Tabs.Count / 2);

            v.Problem = null;
            return true;
        }

        static void KeepInOrder(Dictionary<Tab, OcrLine> tabs)
        {
            var byY = tabs.OrderBy(kv => kv.Value.CenterY).ToList();
            int n = byY.Count;
            if (n < 2) return;
            var len = new int[n];
            var prev = new int[n];
            int best = 0;
            for (int i = 0; i < n; i++)
            {
                len[i] = 1; prev[i] = -1;
                for (int j = 0; j < i; j++)
                    if ((int)byY[j].Key < (int)byY[i].Key && len[j] + 1 > len[i]) { len[i] = len[j] + 1; prev[i] = j; }
                if (len[i] > len[best]) best = i;
            }
            if (len[best] == n) return;
            var keep = new HashSet<Tab>();
            for (int i = best; i >= 0; i = prev[i]) keep.Add(byY[i].Key);
            foreach (var kv in byY) if (!keep.Contains(kv.Key)) tabs.Remove(kv.Key);
        }

        double menuStep;
        int menuX, stripW, menuZoom;
        readonly List<int> others = new List<int>();

        void NoteOthers(List<OcrLine> lines)
        {
            foreach (var l in lines)
                if (l.Box.X < stripW * 2 / 3 && Parse.Key(l.Text).Contains("BRIEFCASE") && !others.Contains(l.CenterY)) others.Add(l.CenterY);
        }

        public Point? TabPoint(Frame f, Tab t)
        {
            OcrLine l;
            if (Tabs.TryGetValue(t, out l)) return new Point(l.CenterX, l.CenterY);
            var p = Where(f, t);
            if (p.Spot != Spot.Hidden || p.Y < 0) return null;
            return new Point(menuX + (int)Math.Round(30 * MenuScale), p.Y);
        }

        public enum Spot { Read, Up, Down, Hidden, Absent }

        public sealed class Place
        {
            public Spot Spot;
            public OcrLine Label;
            public Rectangle Area;
            public int Y = -1;
            public string Why = "";
            public override string ToString()
            {
                return Spot == Spot.Read ? "read@" + Label.CenterY : Spot == Spot.Hidden ? "hidden" + (Y >= 0 ? "@" + Y : "") : Spot.ToString().ToLowerInvariant();
            }
        }

        public Place Where(Frame f, Tab t)
        {
            OcrLine own;
            if (Tabs.TryGetValue(t, out own)) return new Place { Spot = Spot.Read, Label = own };
            int i = (int)t, half = (int)(menuStep / 2);
            var before = Tabs.Where(kv => (int)kv.Key < i).OrderByDescending(kv => (int)kv.Key).FirstOrDefault();
            var after = Tabs.Where(kv => (int)kv.Key > i).OrderBy(kv => (int)kv.Key).FirstOrDefault();
            if (before.Value != null && after.Value != null)
            {

                int slots = (int)Math.Round((after.Value.CenterY - before.Value.CenterY) / menuStep) - 1;
                if (slots <= 0) return new Place { Spot = Spot.Absent, Why = TabLabels[before.Key] + " and " + TabLabels[after.Key] + " are next to each other" };
                int known = TabLabels.Keys.Count(k => (int)k > (int)before.Key && (int)k < (int)after.Key);
                return new Place
                {
                    Spot = Spot.Hidden, Area = Rectangle.FromLTRB(0, before.Value.CenterY + half, stripW, after.Value.CenterY - half),
                    Y = slots == known ? (int)Math.Round(before.Value.CenterY + (i - (int)before.Key) * menuStep) : -1,
                    Why = slots + " unread between " + TabLabels[before.Key] + " and " + TabLabels[after.Key],
                };
            }
            if (before.Value == null)
            {

                var first = after.Value;
                int room = (int)Math.Floor((first.CenterY - (Menu.Top + 0.45 * menuStep)) / menuStep);
                if (room <= 0) return new Place { Spot = Spot.Up, Why = "above " + TabLabels[after.Key] };

                return new Place { Spot = Spot.Hidden, Area = Rectangle.FromLTRB(0, Menu.Top, stripW, first.CenterY - half), Why = room + " unread above " + TabLabels[after.Key] };
            }

            var last = before.Value;
            int below = (int)Math.Floor(((Height - 0.45 * menuStep) - last.CenterY) / menuStep);
            if (below <= 0) return new Place { Spot = Spot.Down, Why = "below " + TabLabels[before.Key] };
            int drawn = 0;
            for (int k = 1; k <= below; k++) if (ButtonAt(f, (int)Math.Round(last.CenterY + k * menuStep))) drawn++; else break;
            if (drawn == 0) return new Place { Spot = Spot.Absent, Why = "the list ends after " + TabLabels[before.Key] };
            return new Place
            {
                Spot = Spot.Hidden, Area = Rectangle.FromLTRB(0, last.CenterY + half, stripW, Math.Min(Height, (int)Math.Round(last.CenterY + (drawn + 0.5) * menuStep))),
                Why = drawn + " unread below " + TabLabels[before.Key],
            };
        }

        public Place Find(Frame f, Tab t)
        {
            var p = Where(f, t);
            if (p.Spot == Spot.Hidden && ReadAgain(f, p.Area)) p = Where(f, t);
            return p;
        }

        public Place AtEnd(Frame f, Tab t, int dir)
        {

            for (int pass = 0; pass <= TabLabels.Count; pass++)
            {
                var p = Where(f, t);
                if (p.Spot != Spot.Up && p.Spot != Spot.Down) return p;
                var edge = dir > 0 ? Tabs.OrderBy(kv => (int)kv.Key).Last() : Tabs.OrderBy(kv => (int)kv.Key).First();
                int y = ButtonBeyond(f, edge.Value, dir);
                if (y < 0) return new Place { Spot = Spot.Absent, Why = dir > 0 ? "the list ends after " + TabLabels[edge.Key] : "the list starts with " + TabLabels[edge.Key] };
                int half = (int)(menuStep / 2);
                var area = dir > 0 ? Rectangle.FromLTRB(0, edge.Value.CenterY + half, stripW, Math.Min(Height, (int)Math.Round(edge.Value.CenterY + 1.5 * menuStep)))
                                   : Rectangle.FromLTRB(0, Math.Max(Menu.Top, (int)Math.Round(edge.Value.CenterY - 1.5 * menuStep)), stripW, edge.Value.CenterY - half);
                if (!ReadAgain(f, area))
                    return new Place { Spot = Spot.Hidden, Area = area, Why = "a button " + (dir > 0 ? "after " : "before ") + TabLabels[edge.Key] + " whose label doesn't read" };

            }
            return new Place { Spot = Spot.Hidden, Why = "labels at the menu's " + (dir > 0 ? "end" : "start") + " that don't read" };
        }

        int ButtonBeyond(Frame f, OcrLine edge, int dir)
        {
            foreach (double k in new[] { 0.6, 0.75, 0.9, 1.0 })
            {
                int y = (int)Math.Round(edge.CenterY + dir * k * menuStep);
                if (y <= Menu.Top || y >= Height) continue;
                if (ButtonAt(f, y)) return y;
            }
            return -1;
        }

        public bool ReadAgain(Frame f, Rectangle area)
        {
            area.Intersect(new Rectangle(0, Menu.Top, stripW, Height - Menu.Top));
            if (area.Height < 6) return false;

            var r = Rectangle.FromLTRB(0, Math.Max(Menu.Top, area.Top - 3), stripW, Math.Min(Height, area.Bottom + 3));
            bool found = false;
            foreach (var t in new[] { Tuple.Create(menuZoom + 1, Prep.None), Tuple.Create(menuZoom + 2, Prep.None), Tuple.Create(menuZoom + 1, Prep.Contrast), Tuple.Create(Math.Max(1, menuZoom - 1), Prep.None) })
            {
                var lines = Ocr.Read(f, r, t.Item1, t.Item2);
                NoteOthers(lines);
                foreach (var kv in TabLabels)
                {
                    if (Tabs.ContainsKey(kv.Key)) continue;
                    var l = Label(lines, kv.Value);
                    if (l == null || l.Box.X >= stripW * 2 / 3 || !area.Contains(l.CenterX, l.CenterY)) continue;

                    if (Tabs.Any(o => ((int)o.Key < (int)kv.Key) != (o.Value.CenterY < l.CenterY))) continue;
                    Tabs[kv.Key] = l;
                    found = true;
                }
                if (found) return true;
            }
            return false;
        }

        public Rectangle Strip { get { return new Rectangle(0, Menu.Top, stripW, Height - Menu.Top); } }

        public int UnreadButtons(Frame f)
        {
            if (Tabs.Count == 0) return 0;
            double y = Tabs.Values.Min(l => l.CenterY);
            while (y - menuStep >= Menu.Top + 0.15 * menuStep) y -= menuStep;
            int n = 0;
            for (; y <= Height - 0.15 * menuStep; y += menuStep)
            {
                double at = y;
                if (ButtonAt(f, (int)Math.Round(at)) && !Tabs.Values.Any(l => Math.Abs(l.CenterY - at) < menuStep / 2)
                    && !others.Any(o => Math.Abs(o - at) < menuStep / 2)) n++;
            }
            return n;
        }

        public bool ButtonAt(Frame f, int y)
        {
            int x1 = Math.Max(4, (int)Math.Round(20 * MenuScale)), x2 = (int)Math.Round(215 * MenuScale);
            return (ButtonPixel(f.Pixel(x1, y)) || Game.GoldIsh(f, x1, y)) && (ButtonPixel(f.Pixel(x2, y)) || Game.GoldIsh(f, x2, y));
        }

        public bool? Badge(Frame f, Tab t)
        {
            OcrLine l;
            if (!Tabs.TryGetValue(t, out l) || !ButtonAt(f, l.CenterY)) return null;
            int x0 = Math.Max(l.Box.Right + 2, (int)Math.Round(200 * MenuScale)), x1 = Math.Min(f.Width - 1, (int)Math.Round(244 * MenuScale));
            int h = Math.Max(4, (int)Math.Round(12 * MenuScale)), red = 0;
            for (int y = Math.Max(0, l.CenterY - h); y <= Math.Min(f.Height - 1, l.CenterY + h); y++)
                for (int x = x0; x <= x1; x++)
                {
                    var c = f.Pixel(x, y);
                    if (c.R >= 140 && c.R - c.G >= 50 && c.R - c.B >= 50) red++;
                }
            return red >= Math.Max(10, (int)(70 * MenuScale * MenuScale));
        }

        static bool ButtonPixel(Color c)
        {
            int max = Math.Max(c.R, Math.Max(c.G, c.B)), min = Math.Min(c.R, Math.Min(c.G, c.B));
            return max - min <= 14 && (c.R + c.G + c.B) / 3 >= 32;
        }

        public Dictionary<Tab, int> MenuSignature() { return Tabs.ToDictionary(kv => kv.Key, kv => kv.Value.CenterY); }

        public static bool SameMenu(Dictionary<Tab, int> a, Dictionary<Tab, int> b)
        {
            return a != null && b != null && a.Count == b.Count && a.All(kv => { int y; return b.TryGetValue(kv.Key, out y) && Math.Abs(y - kv.Value) <= 2; });
        }

        public Point ParkPoint
        {
            get { return new Point(Page.Left + (int)(Page.Width * 0.70), Page.Top + (int)(Height * 0.012)); }
        }

        public Point PagePoint(double fx, double fy)
        {
            return new Point(Page.Left + (int)(Page.Width * fx), Page.Top + (int)(Page.Height * fy));
        }

        static int ProbeAt(Frame f, View v)
        {
            int edge = int.MaxValue;
            foreach (var l in new[] { v.Stamina, v.Health })
            {
                if (l.Text == "") continue;

                int x = Math.Max(l.Box.X, l.Box.Right - (int)Math.Round(100 * v.HeaderScale));
                int stop = x - (int)Math.Round(60 * v.HeaderScale), run = 0;
                for (; x > stop && x > 0; x--)
                    if (!f.Near(x, l.CenterY, Game.HEADER_BG, 4)) run = 0;
                    else if (++run == 3) { edge = Math.Min(edge, x + 2); break; }
            }
            if (edge == int.MaxValue) return v.Energy.Box.X - (int)Math.Round(22 * v.HeaderScale);
            return edge - (int)Math.Round(8 * v.HeaderScale);
        }

        static OcrLine Beside(OcrLine l, double dy)
        {
            var b = l.Box;
            b.Offset(0, (int)Math.Round(dy));
            return new OcrLine { Text = "", Box = b };
        }

        public static OcrLine Label(List<OcrLine> lines, string label)
        {
            string want = Parse.Key(label).Replace('Q', 'R');
            OcrLine best = null;
            int bestD = int.MaxValue;
            foreach (var l in lines)
            {
                string k = Parse.Key(l.Text).Replace('Q', 'R');
                if (k.Length < want.Length - 2 || k.Length > want.Length + 4) continue;
                int d = k == want ? 0 : k.EndsWith(want) ? 1 : Distance(k, want);

                if (d > 1 && k.Length > want.Length) d = Math.Min(d, 1 + Distance(k.Substring(k.Length - want.Length), want));
                if (d < bestD) { bestD = d; best = l; }
            }
            return bestD <= Math.Max(1, want.Length / 5) ? best : null;
        }

        public static int Distance(string a, string b)
        {
            var prev = new int[b.Length + 1];
            var cur = new int[b.Length + 1];
            for (int j = 0; j <= b.Length; j++) prev[j] = j;
            for (int i = 1; i <= a.Length; i++)
            {
                cur[0] = i;
                for (int j = 1; j <= b.Length; j++)
                    cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
                var t = prev; prev = cur; cur = t;
            }
            return prev[b.Length];
        }

        public bool IsSelected(Frame f, Tab t)
        {
            var p = TabPoint(f, t);
            if (p == null) return false;
            OcrLine l;
            int left = Math.Max(10, (int)Math.Round(20 * MenuScale));
            int right = Tabs.TryGetValue(t, out l) ? l.Box.Right + Math.Max(3, l.Box.Height / 2) : (int)Math.Round(200 * MenuScale);
            int y = p.Value.Y;
            return (Game.GoldIsh(f, left, y) && Game.GoldIsh(f, left + 3, y)) || (Game.GoldIsh(f, right, y) && Game.GoldIsh(f, right + 3, y));
        }

        public Tab? Selected(Frame f)
        {
            foreach (Tab t in TabLabels.Keys) if (IsSelected(f, t)) return t;
            return null;
        }

        public override string ToString()
        {
            if (Problem != null) return Width + "x" + Height + ": " + Problem;
            return string.Format("{0}x{1}: header 0-{2} (x{3:0.00}), menu {4} tabs, right edge {5} (x{6:0.00}), page from x {7}",
                Width, Height, Header.Bottom, HeaderScale, Tabs.Count, Menu.Right, MenuScale, Page.Left);
        }
    }
}
