using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text.RegularExpressions;

namespace IdleMafiaBot
{

    sealed class MarketListing
    {
        public string Name = "";
        public int Left = -1, Total = -1, Price = -1;
        public bool ForSale;
        public OcrLine Remove, Each;
        public int Y;
        public override string ToString() { return Name + " (" + (Left >= 0 ? Left.ToString() : "?") + " at " + (Price > 0 ? Price.ToString() : "?") + " each)"; }
    }

    sealed class MyShopPage
    {
        public int Used = -1, Slots = -1;
        public readonly List<MarketListing> Rows = new List<MarketListing>();
        public bool ActivityInSight;
        public string Signature = "";
        public bool Seen { get { return Used >= 0 || Rows.Count > 0; } }
    }

    sealed class SellRow { public string Name = ""; public int Count = -1; public OcrLine Line, Sell; }

    sealed class SellPage
    {
        public OcrLine Title, NewListing;
        public Point SearchAt = Point.Empty;
        public string SearchText = "";
        public readonly List<SellRow> Items = new List<SellRow>();
        public string FormItem;
        public int FormCount = -1;
        public OcrLine All, Button;
        public Point QuantityAt = Point.Empty, PriceAt = Point.Empty;
        public int PayEach = -1, PayAll = -1, PayFor = -1;
        public int SlotsUsed = -1, Slots = -1;
        public string ButtonText = "";
    }

    sealed partial class Game
    {
        static readonly Regex ListingsRx = new Regex(@"LISTINGS\s*\(\s*([0-9OoIlS]+?)\s*[O0]F\s*([0-9OoIlS]+)\s*\)", RegexOptions.IgnoreCase);
        static readonly Regex MarketLeftRx = new Regex(@"([0-9][0-9,]*)\s*of\s*([0-9][0-9,]*)\s*l[eo]ft", RegexOptions.IgnoreCase);
        static readonly Regex ItemRx = new Regex(@"^(.*?[A-Za-z]{2}.*?)\s+[xX×]\s*([0-9][0-9,]*)\s*$");
        static readonly Regex PayRx = new Regex(@"receive\s+([0-9][0-9,]*)\s+Gold\s+Bars?\s+per\s+item\s+sold\W*([0-9][0-9,]*)\s+Gold\s+Bars?\s+for\s+all\s+([0-9][0-9,]*)", RegexOptions.IgnoreCase);
        static readonly Regex SlotsRx = new Regex(@"([0-9]+)\s*of\s*([0-9]+)\s*shop\s*slots", RegexOptions.IgnoreCase);

        internal static Action<string> Trace;

        static int Num(string s) { int n; return int.TryParse(Parse.Digits((s ?? "").Replace(",", "")), out n) ? n : -1; }

        internal static string TidyItem(string s)
        {
            var words = (s ?? "").Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).ToList();
            while (words.Count > 1 && words[0].Count(char.IsLetter) < 2) words.RemoveAt(0);
            return string.Join(" ", words).Trim();
        }

        internal static bool SameItem(string a, string b)
        {
            string ka = Regex.Replace(Parse.Key(a), "[^A-Z]", ""), kb = Regex.Replace(Parse.Key(b), "[^A-Z]", "");
            if (ka.Length < 3 || kb.Length < 3) return false;
            if (ka == kb) return true;
            return View.Distance(ka, kb) <= Math.Max(1, Math.Min(ka.Length, kb.Length) / 8);
        }

        internal static List<string> SearchWords(string name)
        {
            return Regex.Split(name ?? "", "[^A-Za-z]+").Where(w => w.Length >= 3).Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderByDescending(w => w.Length).Take(2).Select(w => w.ToLowerInvariant()).ToList();
        }

        internal List<OcrLine> MarketLines(Frame f) { return Ocr.Read(f, PageArea(f), PageScale(f), Prep.None); }

        internal OcrLine MarketTab(Frame f, List<OcrLine> lines, string name)
        {
            var area = PageArea(f);
            var browse = lines.FirstOrDefault(l => Parse.Key(l.Text) == "BROWSE" && l.CenterY < area.Top + area.Height * 0.3);
            if (browse == null) return null;
            return lines.FirstOrDefault(l => Parse.Key(l.Text) == Parse.Key(name) && Math.Abs(l.CenterY - browse.CenterY) <= browse.Box.Height && l.Box.Left > browse.Box.Right);
        }

        internal enum Prices { None, Top, Last, All }

        public MyShopPage ReadMyShop(Frame f, Prices prices = Prices.None) { return ReadMyShop(f, MarketLines(f), prices); }

        internal MyShopPage ReadMyShop(Frame f, List<OcrLine> lines, Prices prices = Prices.None)
        {
            var area = PageArea(f);
            var p = new MyShopPage { Signature = string.Join(",", lines.Select(l => l.Text + "@" + l.Box.Y)) };
            foreach (var l in lines)
            {
                var m = ListingsRx.Match(l.Text);
                if (m.Success && Parse.Key(l.Text).Contains("SHOPIISTINGS"))
                {
                    int a = Num(m.Groups[1].Value), b = Num(m.Groups[2].Value);
                    if (a >= 0 && b > 0 && a <= b) { p.Used = a; p.Slots = b; }
                }
                if (Parse.Key(l.Text).StartsWith("RECENTACTIVITY")) p.ActivityInSight = true;
            }
            var eaches = lines.Where(l => Parse.Key(l.Text) == "EACH").ToList();
            foreach (var r in lines.Where(l => Parse.Key(l.Text) == "REMOVE").OrderBy(l => l.CenterY))
            {
                int h = Math.Max(8, r.Box.Height);
                var each = eaches.Where(e => Math.Abs(e.CenterY - r.CenterY) <= h && e.Box.Right <= r.Box.Left + h && r.Box.Left - e.Box.Right < 6 * h)
                                 .OrderBy(e => r.Box.Left - e.Box.Right).FirstOrDefault();
                if (each == null) continue;
                var row = new MarketListing { Remove = r, Y = r.CenterY };
                var name = lines.Where(l => l.CenterX < area.Left + area.Width / 2 && l.CenterY < r.CenterY - h / 5 && l.CenterY > r.CenterY - 2.6 * h
                                            && TidyItem(l.Text).Count(char.IsLetter) >= 3)
                                .OrderBy(l => r.CenterY - l.CenterY).FirstOrDefault();
                if (name != null) row.Name = TidyItem(name.Text);
                var forSale = lines.FirstOrDefault(l => Parse.Key(l.Text) == "FORSAIE" && l.CenterY <= r.CenterY + h / 3 && l.CenterY > r.CenterY - 2.6 * h);
                row.ForSale = forSale != null;
                var left = lines.Where(l => l.CenterY > r.CenterY - h / 3 && l.CenterY < r.CenterY + 2.6 * h && l.Box.Right < each.Box.Left).Select(l => MarketLeftRx.Match(l.Text)).FirstOrDefault(m => m.Success);
                if (left == null)
                {

                    int fh = forSale != null ? Math.Max(6, forSale.Box.Height) : h;
                    var corner = forSale != null ? Rectangle.FromLTRB(forSale.Box.Right - 14 * fh, forSale.Box.Bottom, forSale.Box.Right + fh, forSale.Box.Bottom + 3 * fh)
                                                 : Rectangle.FromLTRB(each.Box.Left - 20 * h, r.CenterY + h / 3, each.Box.Left - 9 * h, r.CenterY + 2 * h);
                    foreach (var t in new[] { Tuple.Create(3, Prep.None), Tuple.Create(2, Prep.WhiteSoft), Tuple.Create(4, Prep.None) })
                    {
                        var m = MarketLeftRx.Match(Ocr.ReadText(f, corner, t.Item1, t.Item2));
                        if (m.Success) { left = m; break; }
                    }
                }
                if (left != null)
                {
                    row.Left = Num(left.Groups[1].Value); row.Total = Num(left.Groups[2].Value);
                    if (row.Left > row.Total) row.Left = row.Total = -1;
                }
                row.Each = each;
                p.Rows.Add(row);
            }
            for (int i = 0; i < p.Rows.Count; i++)
                if (prices == Prices.All || (prices == Prices.Top && i == 0) || (prices == Prices.Last && i == p.Rows.Count - 1))
                    p.Rows[i].Price = MarketPrice(f, p.Rows[i].Each, Math.Max(8, p.Rows[i].Remove.Box.Height));
            return p;
        }

        internal static int MarketPrice(Frame f, OcrLine each, int h)
        {
            var band = Rectangle.Intersect(Rectangle.FromLTRB(each.Box.Left - 12 * h, each.Box.Top - h / 2, each.Box.Left - 2, each.Box.Bottom + h / 2), new Rectangle(0, 0, f.Width, f.Height));
            if (band.Width <= 0 || band.Height <= 0) return -1;
            Func<int, int, bool> gold = (x, y) =>
            {
                var c = f.Pixel(x, y);
                return Math.Abs(c.R - 212) <= 18 && Math.Abs(c.G - 175) <= 18 && Math.Abs(c.B - 55) <= 22;
            };
            var cols = new int[band.Width];
            for (int x = band.Left; x < band.Right; x++)
                for (int y = band.Top; y < band.Bottom; y++)
                    if (gold(x, y)) cols[x - band.Left]++;

            int end = Array.FindLastIndex(cols, n => n > 0);
            if (end < 0) return -1;
            int start = end, gap = 0, maxGap = Math.Max(3, h * 6 / 10);
            for (int i = end - 1; i >= 0; i--)
            {
                if (cols[i] > 0) { start = i; gap = 0; }
                else if (++gap > maxGap) break;
            }
            int x0 = band.Left + start, x1 = band.Left + end, y0 = int.MaxValue, y1 = -1;
            for (int y = band.Top; y < band.Bottom; y++)
                for (int x = x0; x <= x1; x++)
                    if (gold(x, y)) { y0 = Math.Min(y0, y); y1 = Math.Max(y1, y); break; }
            if (y1 - y0 < h / 2 || (x1 - x0) > 6 * h) return -1;
            var ink = Rectangle.FromLTRB(x0, y0, x1 + 1, y1 + 1);
            var reads = new List<int>();
            var around = Rectangle.Inflate(ink, h, h / 2);

            Func<int, bool> add = n =>
            {
                if (n <= 0) return false;
                reads.Add(n);
                return reads.Count(x => x == n) >= 2 && reads.All(x => x == n);
            };
            bool sure = false;
            foreach (var t in new[] { Tuple.Create(3, Prep.None), Tuple.Create(4, Prep.None) })
            {
                var m = Regex.Match(Ocr.ReadText(f, around, t.Item1, t.Item2), @"[0-9][0-9,]*");
                if (add(m.Success ? Num(m.Value) : -1)) { sure = true; break; }
            }
            if (!sure)
                foreach (var crop in new[] { ink, Rectangle.Inflate(ink, 1, 1), Rectangle.Inflate(ink, 2, 1) })
                {
                    foreach (int copies in new[] { 2, 3, 4 })
                        if (add(ReadLoneNumber(f, crop, new[] { copies }))) { sure = true; break; }
                    if (sure) break;
                }
            if (Trace != null) Trace("price ink " + ink + " reads " + string.Join(",", reads));
            var groups = reads.GroupBy(n => n).OrderByDescending(g => g.Count()).ToList();
            if (groups.Count == 0 || groups[0].Count() < 2 || (groups.Count > 1 && groups[1].Count() * 2 >= groups[0].Count())) return -1;
            return groups[0].Key <= 10000000 ? groups[0].Key : -1;
        }

        internal sealed class RemoveWindow { public string Name; public OcrLine Remove, Cancel; }

        internal RemoveWindow ReadRemoveWindow(Frame f)
        {
            var lines = MarketLines(f);
            var w = ReadRemoveWindow(lines);
            if (w == null || (w.Remove != null && w.Cancel != null)) return w;

            var title = lines.First(l => Parse.Key(l.Text).Contains("REMOVEIISTING"));
            int th = Math.Max(8, title.Box.Height);
            var below = Rectangle.Intersect(Rectangle.FromLTRB(title.Box.Left - 3 * th, title.Box.Bottom + 3 * th, title.Box.Left + 30 * th, title.Box.Bottom + 16 * th), new Rectangle(0, 0, f.Width, f.Height));
            foreach (var t in new[] { Tuple.Create(1, Prep.None), Tuple.Create(1, Prep.Contrast), Tuple.Create(2, Prep.Contrast) })
            {
                var more = lines.Concat(Ocr.Read(f, below, t.Item1, t.Item2)).ToList();
                var again = ReadRemoveWindow(more);
                if (again != null && again.Remove != null && again.Cancel != null) return again;
            }
            return w;
        }

        internal static RemoveWindow ReadRemoveWindow(List<OcrLine> lines)
        {
            var title = lines.FirstOrDefault(l => Parse.Key(l.Text).Contains("REMOVEIISTING"));
            if (title == null) return null;
            var w = new RemoveWindow();
            w.Cancel = lines.FirstOrDefault(l => Parse.Key(l.Text) == "CANCEI" && l.CenterY > title.CenterY);
            if (w.Cancel != null)
                w.Remove = lines.FirstOrDefault(l => Parse.Key(l.Text) == "REMOVE" && Math.Abs(l.CenterY - w.Cancel.CenterY) <= w.Cancel.Box.Height
                                                     && l.CenterX < w.Cancel.CenterX && w.Cancel.Box.Left - l.Box.Right < 12 * w.Cancel.Box.Height);
            int th = Math.Max(8, title.Box.Height);
            int bottom = w.Remove != null ? w.Remove.Box.Top : title.CenterY + 12 * th;
            int right = title.Box.Left + 30 * th;
            string q = string.Join(" ", lines.Where(l => l.CenterY > title.Box.Bottom && l.CenterY < bottom && l.Box.Left >= title.Box.Left - 2 * th && l.Box.Right <= right)
                                             .OrderBy(l => l.CenterY).ThenBy(l => l.CenterX).Select(l => l.Text));
            var m = Regex.Match(q, @"Take\s+(.+?)\s+out\s+of\s+your", RegexOptions.IgnoreCase);
            if (m.Success) w.Name = m.Groups[1].Value.Trim();
            return w;
        }

        internal SellPage ReadSellPage(Frame f) { return ReadSellPage(MarketLines(f)); }

        internal static SellPage ReadSellPage(List<OcrLine> lines)
        {
            var p = new SellPage();
            p.Title = lines.FirstOrDefault(l => Parse.Key(l.Text).StartsWith("YOURTRADABIE"));
            p.NewListing = lines.FirstOrDefault(l => Parse.Key(l.Text).StartsWith("NEWIISTING"));
            if (p.Title == null || p.NewListing == null || p.NewListing.Box.Left <= p.Title.Box.Right) return null;
            int h = Math.Max(8, p.Title.Box.Height);
            int split = p.NewListing.Box.Left - 2 * h;
            var left = lines.Where(l => l.CenterX < split && l.Box.Left >= p.Title.Box.Left - 4 * h && l.CenterY > p.Title.Box.Bottom).OrderBy(l => l.CenterY).ToList();
            var right = lines.Where(l => l.Box.Left >= split && l.CenterY > p.NewListing.Box.Bottom).OrderBy(l => l.CenterY).ToList();

            foreach (var sell in left.Where(l => Parse.Key(l.Text) == "SEII"))
            {
                var name = left.Where(l => l.Box.Right < sell.Box.Left && l.CenterY <= sell.CenterY + h / 3 && l.CenterY > sell.CenterY - 2 * h
                                           && TidyItem(l.Text).Count(char.IsLetter) >= 3)
                               .OrderBy(l => l.CenterY).FirstOrDefault();
                if (name == null) continue;
                var row = new SellRow { Line = name, Sell = sell, Name = TidyItem(name.Text) };
                var m = ItemRx.Match(name.Text);
                if (m.Success) { row.Name = TidyItem(m.Groups[1].Value); row.Count = Num(m.Groups[2].Value); }
                else
                {
                    var count = left.FirstOrDefault(l => l.Box.Left > name.Box.Right && l.Box.Right < sell.Box.Left && Math.Abs(l.CenterY - name.CenterY) <= h / 2
                                                         && Regex.IsMatch(l.Text.Trim(), @"^[xX×]\s*[0-9][0-9,]*$"));
                    if (count != null) row.Count = Num(count.Text);
                }
                p.Items.Add(row);
            }

            int firstItem = p.Items.Count > 0 ? p.Items.Min(i => i.Line.Box.Top) : int.MaxValue;
            var search = left.FirstOrDefault(l => Parse.Key(l.Text).Contains("SEARCHYOURITEMS"));
            if (search == null)
                search = left.FirstOrDefault(l => l.CenterY < p.Title.CenterY + 5 * h && l.Box.Bottom < firstItem && !ItemRx.IsMatch(l.Text) && Parse.Key(l.Text) != "SEII");
            else p.SearchText = "";
            if (search != null)
            {
                if (!Parse.Key(search.Text).Contains("SEARCHYOURITEMS")) p.SearchText = search.Text.Trim();
                p.SearchAt = new Point(p.Title.Box.Left + (split - p.Title.Box.Left) * 85 / 100, search.CenterY);
            }

            var item = right.FirstOrDefault(l => ItemRx.IsMatch(l.Text));
            if (item != null && !right.Any(l => l.CenterY < item.CenterY && Parse.Key(l.Text).StartsWith("PICKANITEM")))
            {
                var m = ItemRx.Match(item.Text);
                p.FormItem = TidyItem(m.Groups[1].Value);
                p.FormCount = Num(m.Groups[2].Value);
            }
            var qty = right.FirstOrDefault(l => Parse.Key(l.Text).StartsWith("QUANTIT"));
            var price = right.FirstOrDefault(l => Parse.Key(l.Text).StartsWith("PRICEPERITEM"));
            if (qty != null) p.All = right.FirstOrDefault(l => Parse.Key(l.Text) == "AII" && l.CenterY > qty.CenterY && l.CenterY < qty.CenterY + 6 * h);
            if (qty != null && p.All != null)
            {
                p.QuantityAt = new Point((p.NewListing.Box.Left + p.All.Box.Left) / 2, p.All.CenterY);
                if (price != null && price.CenterY > p.All.CenterY) p.PriceAt = new Point(p.QuantityAt.X, price.CenterY + (p.All.CenterY - qty.CenterY));
            }
            var view = right.FirstOrDefault(l => Parse.Key(l.Text).StartsWith("VIEWACTIVE"));
            int payTop = price != null ? price.Box.Bottom : p.NewListing.Box.Bottom, payBottom = view != null ? view.Box.Top : int.MaxValue;
            string pay = string.Join(" ", right.Where(l => l.CenterY > payTop && l.CenterY < payBottom).Select(l => l.Text));
            var pm = PayRx.Match(pay);
            if (pm.Success) { p.PayEach = Num(pm.Groups[1].Value); p.PayAll = Num(pm.Groups[2].Value); p.PayFor = Num(pm.Groups[3].Value); }
            if (view != null)
            {
                p.Button = right.FirstOrDefault(l => l.Box.Top > view.Box.Bottom && l.CenterY < view.CenterY + 6 * h);
                if (p.Button != null) p.ButtonText = p.Button.Text.Trim();
            }
            var slots = right.Select(l => SlotsRx.Match(l.Text)).FirstOrDefault(m => m.Success);
            if (slots != null) { p.SlotsUsed = Num(slots.Groups[1].Value); p.Slots = Num(slots.Groups[2].Value); }
            return p;
        }

        void PressMarket(OcrLine b, string want, int waitMs)
        {
            if (b == null || Parse.Key(b.Text) != Parse.Key(want)) throw new NeverPressException(b == null ? want : b.Text, "not the Black Market's " + want);
            NotDisconnected();
            if (OnlyNavigate) RefuseInCheck(b.Text);
            Click(b.CenterX, b.CenterY, waitMs);
        }

        void Type(string keys)
        {
            Check();
            Win.TypeKeys(keys);
        }

        public MyShopPage OpenMyShop(Prices prices = Prices.None)
        {
            if (!OpenTab(Tab.BlackMarket, true)) return null;
            for (int look = 0; look < 5; look++)
            {
                if (look > 0) Wait(800);
                using (var f = Capture())
                {
                    var lines = MarketLines(f);
                    var shop = ReadMyShop(f, lines, prices);
                    if (shop.Seen) return shop;
                    var tab = MarketTab(f, lines, "MY SHOP");
                    if (tab != null) { PressMarket(tab, "MY SHOP", 1200); continue; }
                }
            }
            log("Black Market: MY SHOP didn't open");
            Snapshot("market my shop not open", 60);
            return null;
        }

        public MyShopPage MyShopToEnd(MyShopPage top)
        {
            var shop = top;
            for (int roll = 0; roll < 12 && !shop.ActivityInSight; roll++)
            {
                ScrollPage(0.6, -3);
                Wait(400);
                MyShopPage next;
                using (var f = Capture()) next = ReadMyShop(f);
                if (next.Signature == shop.Signature) break;
                if (next.Used < 0) { next.Used = top.Used; next.Slots = top.Slots; }
                shop = next;
            }
            return shop;
        }

        public bool? RemoveListing(MarketListing l, int usedBefore)
        {
            PressMarket(l.Remove, "REMOVE", 1200);
            RemoveWindow w = null;
            for (int look = 0; look < 4 && (w == null || w.Remove == null); look++)
            {
                if (look > 0) Wait(600);
                using (var f = Capture()) w = ReadRemoveWindow(f);
            }
            if (w == null || w.Remove == null)
            {
                log("Black Market: REMOVE on " + l.Name + " didn't bring up its question - nothing taken out");
                Snapshot("market remove question unread", 60);
                if (w != null && w.Cancel != null) PressMarket(w.Cancel, "CANCEL", 800);
                return false;
            }
            if (w.Name == null || !SameItem(w.Name, l.Name))
            {
                log("Black Market: the question asked about \"" + (w.Name ?? "?") + "\", not " + l.Name + " - cancelled");
                Snapshot("market remove question other item", 60);
                if (w.Cancel != null) PressMarket(w.Cancel, "CANCEL", 800);
                return false;
            }
            PressMarket(w.Remove, "REMOVE", 1500);
            for (int look = 0; look < 5; look++)
            {
                if (look > 0) Wait(700);
                using (var f = Capture())
                {
                    var lines = MarketLines(f);
                    if (ReadRemoveWindow(lines) != null) continue;
                    var shop = ReadMyShop(f, lines);
                    if (usedBefore > 0 && shop.Used == usedBefore - 1) return true;
                }
            }

            var again = OpenMyShop();
            if (again == null) return null;
            if (usedBefore > 0 && again.Used == usedBefore - 1) return true;
            again = MyShopToEnd(again);
            return again.Seen && !again.Rows.Any(r => SameItem(r.Name, l.Name) && r.Left == l.Left);
        }

        SellPage OpenSell()
        {
            for (int look = 0; look < 5; look++)
            {
                if (look > 0) Wait(800);
                using (var f = Capture())
                {
                    var lines = MarketLines(f);
                    var p = ReadSellPage(lines);
                    if (p != null) return p;
                    var tab = MarketTab(f, lines, "SELL");
                    if (tab != null) { PressMarket(tab, "SELL", 1200); continue; }
                    if (!OnTab(f, Tab.BlackMarket) && !OpenTab(Tab.BlackMarket, true)) return null;
                }
            }
            return null;
        }

        public const string NotTradable = "it isn't among your tradable items";

        public string ListItem(string name, int qty, int price, out int listed)
        {
            listed = 0;
            var p = OpenSell();
            if (p == null) return "the SELL tab didn't open";
            SellRow row = null;
            foreach (var word in SearchWords(name))
            {
                if (p.SearchAt.IsEmpty) return "its search box wasn't found";
                Click(p.SearchAt.X, p.SearchAt.Y, 300);
                Type(new string('\b', (p.SearchText ?? "").Length + 8) + word);
                for (int look = 0; look < 3 && row == null; look++)
                {
                    Wait(look == 0 ? 900 : 700);
                    using (var f = Capture()) p = ReadSellPage(f) ?? p;
                    row = p.Items.FirstOrDefault(i => SameItem(i.Name, name));
                }
                if (row != null) break;
            }
            if (row == null) return NotTradable;
            if (row.Sell == null) return "its SELL didn't read";
            PressMarket(row.Sell, "SELL", 900);
            int want = -1;
            for (int go = 0; go < 2; go++)
            {
                using (var f = Capture()) p = ReadSellPage(f);
                if (p == null || p.FormItem == null || !SameItem(p.FormItem, name)) return "its listing form didn't show it";
                if (p.QuantityAt.IsEmpty || p.PriceAt.IsEmpty) return "its listing form didn't read";

                if (want < 0)
                {
                    int have = p.FormCount > 0 ? p.FormCount : row.Count;
                    if (have <= 0) return "how many you have didn't read";
                    want = Math.Min(qty, have);
                }

                if (want == p.FormCount && p.All != null) Press(p.All, 500);
                else { Click(p.QuantityAt.X, p.QuantityAt.Y, 300); Type(new string('\b', 8) + want); }
                Click(p.PriceAt.X, p.PriceAt.Y, 300);
                Type(new string('\b', 8) + price + "\r");
                Wait(700);
                bool ready;
                using (var f = Capture())
                {
                    p = ReadSellPage(f);

                    ready = p != null && FormSays(p, name, want, price) && GoldIsh(f, p.Button.Box.Left - p.Button.Box.Height, p.Button.CenterY);
                }
                if (ready) break;
                if (go == 1)
                {
                    log("Black Market: the listing form for " + name + " didn't read back right (" + (p == null ? "no form" : "\"" + p.ButtonText + "\", "
                        + (p.PayFor >= 0 ? p.PayEach + " per item, for all " + p.PayFor : "no payout line")) + ") - not put up");
                    Snapshot("market form unread", 60);
                    return "its listing form didn't read back right";
                }
            }
            PressMarket(p.Button, "PUT IN YOUR SHOP", 1500);

            for (int look = 0; look < 8; look++)
            {
                Wait(800);
                using (var f = Capture())
                {
                    var shop = ReadMyShop(f, Prices.Top);
                    var top = shop.Rows.FirstOrDefault();
                    if (top != null && SameItem(top.Name, name) && top.Left == want && top.Price == price) { listed = want; return null; }
                }
            }

            var again = OpenMyShop(Prices.Top);
            if (again != null)
            {
                var top = again.Rows.FirstOrDefault();
                if (top != null && SameItem(top.Name, name) && top.Left == want && top.Price == price) { listed = want; return null; }
            }
            Snapshot("market listing not seen", 60);
            return "it didn't show in MY SHOP after PUT IN YOUR SHOP";
        }

        internal static bool FormSays(SellPage p, string name, int qty, int price)
        {
            if (p.FormItem == null || !SameItem(p.FormItem, name) || p.Button == null || Parse.Key(p.ButtonText) != "PUTINYOURSHOP") return false;
            if (p.PayFor != qty || p.PayEach <= 0 || p.PayEach >= price || p.PayEach < price - Math.Max(1, (int)Math.Ceiling(price * 0.07)) - 1) return false;
            return p.PayAll < 0 || (long)p.PayEach * qty == p.PayAll;
        }
    }
}
