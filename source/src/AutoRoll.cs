using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text.RegularExpressions;

namespace IdleMafiaBot
{

    sealed partial class Game
    {

        internal static bool AutoRollWindowWord(string k)
        {
            return k == "HIRETIER" || k.StartsWith("STOPATTHISRARITY") || k.StartsWith("THISSIOTONIY") || k.StartsWith("EVERYEMPTYSIOT") || k == "CHANCEPERHIRE";
        }

        internal static bool RollingWord(string k, bool left)
        {
            return k.StartsWith("ROIIINGFOR") || (left && k.StartsWith("AU") && k.EndsWith("ROIIING") && k.Length >= 9 && k.Length <= 13);
        }

        internal static bool LightInk(Frame f, Rectangle box)
        {
            box.Intersect(new Rectangle(0, 0, f.Width, f.Height));
            int n = 0;
            for (int y = box.Top; y < box.Bottom; y++)
                for (int x = box.Left; x < box.Right; x++)
                {
                    var c = f.Pixel(x, y);
                    int mx = Math.Max(c.R, Math.Max(c.G, c.B)), mn = Math.Min(c.R, Math.Min(c.G, c.B));
                    if (mx >= 150 && mx - mn <= 30) n++;
                }
            return n >= Math.Max(4, box.Height);
        }

        static bool BrightInk(Frame f, Rectangle box)
        {
            box.Intersect(new Rectangle(0, 0, f.Width, f.Height));
            int n = 0;
            for (int y = box.Top; y < box.Bottom; y++)
                for (int x = box.Left; x < box.Right; x++)
                {
                    var c = f.Pixel(x, y);
                    if (Math.Max(c.R, Math.Max(c.G, c.B)) >= 150) n++;
                }
            return n >= Math.Max(4, box.Height);
        }

        static OcrLine AutoIcon(Frame f, Rectangle area, OcrLine train, OcrLine trash, OcrLine anchor, int h, out bool active)
        {
            active = false;
            if (trash == null) return null;
            int tw = Math.Max(h, trash.Box.Width);
            int x1 = trash.Box.Left - 2;
            int x0 = train != null && train.Box.Right < x1 - tw ? Math.Max(train.Box.Right + 3, x1 - 6 * tw) : x1 - 9 * tw / 2;
            int y0 = Math.Max(area.Y, anchor.CenterY - 3 * h), y1 = Math.Min(area.Bottom - 1, anchor.CenterY + 3 * h);

            int[] l = { int.MaxValue, int.MaxValue }, t = { int.MaxValue, int.MaxValue }, r = { -1, -1 }, b = { -1, -1 }, n = { 0, 0 };
            for (int y = y0; y <= y1; y++)
                for (int x = Math.Max(0, x0); x <= x1 && x < f.Width; x++)
                {
                    var c = f.Pixel(x, y);
                    int mx = Math.Max(c.R, Math.Max(c.G, c.B)), mn = Math.Min(c.R, Math.Min(c.G, c.B));
                    if (mx - mn > 25 || mx < 80) continue;
                    int i = mx >= 150 ? 0 : 1;
                    n[i]++;
                    l[i] = Math.Min(l[i], x); r[i] = Math.Max(r[i], x); t[i] = Math.Min(t[i], y); b[i] = Math.Max(b[i], y);
                }
            active = n[0] >= h;
            int k = active ? 0 : 1;
            if (r[k] < 0 || n[k] < h) return null;
            int w = r[k] - l[k] + 1, hh = b[k] - t[k] + 1;

            if (w < h / 2 || w > 4 * tw || hh < h / 2 || hh > 7 * h) { active = false; return null; }
            return new OcrLine { Text = "AUTO", Box = Rectangle.FromLTRB(l[k], t[k], r[k] + 1, b[k] + 1) };
        }

        static readonly Regex RollForRx = new Regex(@"R\s*O\s*[LI1]\s*[LI1]\s*[IL1]\s*N\s*G\s*F\s*O\s*R\s*([A-Za-z]{3,12})\s*O\s*R\s*B\s*E", RegexOptions.IgnoreCase);
        static readonly Regex RollSpentRx = new Regex(@"(?:(STREET|PROFESSIONAL|ELITE)\b.*?)?([0-9OoIlS]{1,6})\s*h\s*[iIl1]\s*r\s*e\s*s?\b.*?(\$\s?[0-9OoIlS]{1,3}(?:[.,][0-9OoIlS]{1,3})*\s?[KMBT]?)\s*s\s*p\s*e\s*n\s*t", RegexOptions.IgnoreCase);

        static void ReadRoll(Frame f, Rectangle area, List<OcrLine> lines, CrewRow row, Func<OcrLine, bool> inBand)
        {
            row.AutoRolling = true;
            foreach (var l in lines.Where(inBand))
            {
                var m = RollForRx.Match(l.Text);
                if (m.Success) { row.RollLine = l; row.RollFor = RarityNear(Parse.Key(m.Groups[1].Value)); }
                var s = RollSpentRx.Match(l.Text);
                if (!s.Success) continue;
                int n;
                if (int.TryParse(Parse.Digits(s.Groups[2].Value), out n)) row.RollHires = n;
                double spent;
                if (Parse.Money(s.Groups[3].Value.Replace(" ", ""), out spent)) row.RollSpent = spent;
                if (s.Groups[1].Success)
                {
                    row.RollTier = (CrewRoll)Array.IndexOf(new[] { "STREET", "PROFESSIONAL", "ELITE" }, s.Groups[1].Value.ToUpperInvariant());
                    row.RollTierRead = true;
                }
            }
            var at = row.RollLine ?? row.Anchor;
            int h = Math.Max(8, at.Box.Height);
            row.RollStop = lines.FirstOrDefault(l => inBand(l) && Parse.Key(l.Text) == "STOP" && l.CenterX > area.X + area.Width * 0.6
                                                     && Math.Abs(l.CenterY - at.CenterY) <= 3 * h && (row.RollLine == null || l.Box.X > row.RollLine.Box.Right));
            row.RollStopGold = row.RollStop != null && GoldFace(f, row.RollStop.Box);
        }

        public sealed class AutoRollWindow
        {
            public Rectangle Box;
            public OcrLine Title;
            public string Name;
            public bool Empty;
            public OcrLine[] Tiers = new OcrLine[3];
            public bool[] TierLight = new bool[3];
            public int Tier = -1;
            public OcrLine[] Rarities = new OcrLine[8];
            public bool[] RarityLight = new bool[8];
            public int Rarity = -1;
            public OcrLine ThisSlot, EverySlot;
            public bool ThisSlotPicked, EverySlotPicked;
            public OcrLine Start;
            public bool StartGold;
            public Point X = Point.Empty;
            public bool Robux;
            public string Words = "";

            public bool Usable { get { return !Robux && Start != null && Tiers.Count(t => t != null) >= 2 && Rarities.Count(r => r != null) >= 6; } }
            public bool Names(string rowName) { return Name != null && rowName != null && WindowNames(Name, rowName); }
            public override string ToString()
            {
                return (Name ?? (Empty ? "an empty slot" : "no one named")) + ", " + (Tier >= 0 ? ((CrewRoll)Tier).ToString() : "no tier") + ", "
                       + (Rarity >= 0 ? RarityTitle(Rarity) : "no rarity") + ", " + (ThisSlotPicked ? "this slot only" : EverySlotPicked ? "every empty slot" : "no slots picked")
                       + (Start == null ? ", no START" : StartGold ? "" : ", START not gold") + (X.IsEmpty ? ", no X" : "") + (Robux ? ", ROBUX" : "");
            }
        }

        static readonly string[] TierKeys = { "STREET", "PROFESSIONAI", "EIITE" };

        internal static bool RobuxWords(string text)
        {
            string k = Parse.Key(text);
            return Regex.IsMatch(text ?? "", @"R\s?\$") || k.Contains("ROBUX") || k.Contains("GAMEPASS") || k.Contains("PURCHASE")
                   || Regex.IsMatch(" " + (text ?? "").ToUpperInvariant() + " ", @"[^A-Z](PASS|PASSES)[^A-Z]") || k == "BUY" || k == "BUYNOW" || k == "GET" || k == "UNIOCK";
        }

        public bool CrewClear()
        {
            using (var f = Capture())
            {
                if (Ocr.Read(f, PopupArea(f), PopupScale(f), Prep.None).Any(l => RobuxWords(l.Text))) return false;
                var p = ReadCrew(f);
                return p.Page && !p.AutoRollWindow;
            }
        }

        public AutoRollWindow ReadAutoRollWindow(Frame f)
        {
            var pop = Ocr.Read(f, PopupArea(f), PopupScale(f), Prep.None);
            var title = pop.Where(l => Parse.Key(l.Text) == "AUTOROII").OrderBy(l => l.Box.Y).FirstOrDefault();
            if (title == null) return null;

            var box = WindowFrame(f, title.Box);
            if (box.IsEmpty && !pop.Any(l => AutoRollWindowWord(Parse.Key(l.Text)))) return null;
            var w = new AutoRollWindow { Title = title, Box = box.IsEmpty ? PopupArea(f) : box };
            var lines = Ocr.Read(f, w.Box, PopupScale(f) + 1, Prep.None);
            if (lines.Count == 0) lines = pop;
            w.Words = string.Join(" | ", lines.OrderBy(l => l.Box.Y).ThenBy(l => l.Box.X).Select(l => l.Text.Trim()));
            int th = Math.Max(8, title.Box.Height);
            OcrLine hireTier = null, stopAt = null, slots = null;
            foreach (var l in lines)
            {
                string k = Parse.Key(l.Text);
                if (k == "HIRETIER") hireTier = l;
                else if (k.StartsWith("STOPATTHISRARITY")) stopAt = l;
                else if (k == "SIOTS") slots = l;

                if (RobuxWords(l.Text)) w.Robux = true;
            }
            int top = hireTier != null ? hireTier.Box.Bottom : title.Box.Bottom + 4 * th;
            int mid = stopAt != null ? stopAt.Box.Y : int.MaxValue;
            foreach (var l in lines)
            {
                string k = Parse.Key(l.Text);
                if (l.Box.Y >= top - 2 && l.Box.Bottom <= mid + 2)
                    for (int t = 0; t < 3; t++)
                        if (w.Tiers[t] == null && (k == TierKeys[t] || (k.Length >= 4 && View.Distance(k, TierKeys[t]) <= (t == 2 ? 2 : 1))))
                        {
                            w.Tiers[t] = l; w.TierLight[t] = BrightInk(f, l.Box);
                            if (Picked(f, l.Box)) w.Tier = t;
                        }
                if (stopAt != null && l.Box.Y > stopAt.Box.Bottom - 2 && (slots == null || l.Box.Bottom < slots.Box.Y + 2))
                {
                    int r = RarityNear(k);
                    if (r >= 0 && w.Rarities[r] == null)
                    {
                        w.Rarities[r] = l; w.RarityLight[r] = BrightInk(f, l.Box);
                        if (Picked(f, l.Box)) w.Rarity = r;
                    }
                }
                if (k.StartsWith("THISSIOT")) { w.ThisSlot = l; w.ThisSlotPicked = Picked(f, l.Box); }
                if (k.StartsWith("EVERYEMPTYSIOT")) { w.EverySlot = l; w.EverySlotPicked = Picked(f, l.Box); }
                if (k == "START" && (slots == null || l.Box.Y > slots.Box.Bottom)) { w.Start = l; w.StartGold = GoldFace(f, l.Box); }
            }

            var panel = lines.Where(l => l.Box.Y > title.Box.Bottom && l.Box.Bottom < top - (hireTier != null ? hireTier.Box.Height : 0) && Regex.IsMatch(l.Text, "[A-Za-z]{2}"))
                             .OrderBy(l => l.Box.Y).ToList();
            var named = panel.FirstOrDefault(l => !Regex.IsMatch(l.Text, @"(?i)(rep\S*ed|\bs\S{0,2}t\s*\d)"));
            if (named != null)
            {
                if (Parse.Key(named.Text).Contains("EMPTY")) w.Empty = true;
                else w.Name = TidyName(Regex.Replace(named.Text, @"\s+", " ").Trim());
            }
            else if (panel.Count == 0) w.Empty = true;
            w.X = WindowCross(f, w.Box, title);
            return w;
        }

        static Rectangle WindowFrame(Frame f, Rectangle title)
        {
            int th = Math.Max(8, title.Height), y = title.Y + title.Height / 2;
            Func<int, int, bool> gold = (x, yy) => x >= 0 && yy >= 0 && x < f.Width && yy < f.Height && GoldIsh(f, x, yy);
            int left = -1, right = -1;
            for (int x = title.Left - 3; x >= Math.Max(0, title.Left - 8 * th); x--) if (gold(x, y)) { left = x; break; }
            for (int x = title.Right + 3; x < f.Width; x++) if (gold(x, y)) { right = x; break; }
            if (left < 0 || right < 0 || right - left < 10 * th) return Rectangle.Empty;
            int topY = -1;
            for (int yy = title.Y - 2; yy >= Math.Max(0, title.Y - 8 * th); yy--) if (gold(title.Left + title.Width / 2, yy)) { topY = yy; break; }
            if (topY < 0) return Rectangle.Empty;
            int bottom = y, miss = 0;
            for (int yy = y; yy < f.Height && miss < 6; yy++)
            {
                if (gold(left, yy) || gold(left + 1, yy) || gold(left - 1, yy)) { bottom = yy; miss = 0; }
                else miss++;
            }
            if (bottom - topY < 10 * th) return Rectangle.Empty;
            return Rectangle.FromLTRB(left + 2, topY + 2, right - 1, bottom - 1);
        }

        static Point WindowCross(Frame f, Rectangle box, OcrLine title)
        {
            int th = Math.Max(8, title.Box.Height);
            int x0 = Math.Max(title.Box.Right + th, box.Right - 8 * th), x1 = Math.Min(f.Width - 1, box.Right - 2);
            int y0 = Math.Max(0, title.Box.Y - th), y1 = Math.Min(f.Height - 1, title.Box.Bottom + th);
            int l = int.MaxValue, t = int.MaxValue, r = -1, b = -1, n = 0;
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    var c = f.Pixel(x, y);

                    if (Math.Min(c.R, Math.Min(c.G, c.B)) < 120 || Math.Max(c.R, Math.Max(c.G, c.B)) - Math.Min(c.R, Math.Min(c.G, c.B)) > 25) continue;
                    l = Math.Min(l, x); r = Math.Max(r, x); t = Math.Min(t, y); b = Math.Max(b, y); n++;
                }
            if (r < 0) return Point.Empty;
            int w = r - l + 1, hh = b - t + 1;
            if (w < th / 2 || hh < th / 2 || w > 3 * th || hh > 3 * th || n < w * hh / 6) return Point.Empty;
            return new Point(l + w / 2, t + hh / 2);
        }

        static bool Picked(Frame f, Rectangle word)
        {
            int h = Math.Max(6, word.Height), tinted = 0, n = 0;
            foreach (int side in new[] { -1, 1 })
            {
                int x0 = side < 0 ? word.X - 2 * h : word.Right + h, x1 = x0 + h;
                for (int x = x0; x <= x1; x++)
                    for (int y = word.Y + h / 4; y <= word.Bottom - h / 4; y++)
                    {
                        if (x < 0 || y < 0 || x >= f.Width || y >= f.Height) continue;
                        var c = f.Pixel(x, y);
                        int mx = Math.Max(c.R, Math.Max(c.G, c.B)), mn = Math.Min(c.R, Math.Min(c.G, c.B));
                        if (mx > 110) continue;
                        n++;
                        if (mx - mn >= 12) tinted++;
                    }
            }
            return n > 0 && tinted * 10 >= n * 7;
        }

        public string AutoRollWhy;

        public bool AutoRollNoPass;

        public AutoRollWindow AutoRollSeen;

        public bool StartAutoRoll(CrewRow row, string replaces, CrewRoll tier, int rarity)
        {
            AutoRollWhy = null; AutoRollNoPass = false; AutoRollSeen = null;
            if (row == null || row.Auto == null) { AutoRollWhy = "no AUTO button on its row"; return false; }
            if (!row.AutoActive)
            {
                AutoRollNoPass = true; AutoRollWhy = "its AUTO button is greyed (the game's Auto Roll pass isn't on this account)";

                Snapshot("crew auto roll greyed", 60);
                return false;
            }
            PressCrew(row.Auto, row.AutoActive && (row.Empty ? Parse.Key(row.Auto.Text) == "AUTOROII" : row.Auto.Text == "AUTO"), "not a row's own AUTO");
            AutoRollWindow w = null;
            for (int look = 0; look < 4 && w == null; look++)
            {
                Wait(look == 0 ? 700 : 500);
                using (var f = Capture())
                {

                    try { CheckForPurchasePrompt(Ocr.Read(f, PopupArea(f), PopupScale(f), Prep.None)); }
                    catch (NeedUserException) { AutoRollNoPass = true; throw; }
                    w = ReadAutoRollWindow(f);
                }
            }
            if (w == null)
            {

                List<OcrLine> pop;
                using (var f = Capture()) pop = Ocr.Read(f, PopupArea(f), PopupScale(f), Prep.None);
                AutoRollNoPass = pop.Any(l => RobuxWords(l.Text));
                AutoRollWhy = AutoRollNoPass ? "AUTO pressed: the game offered something for Robux or a pass instead (" + string.Join(" | ", pop.Select(l => l.Text.Trim()).Take(8)) + ")"
                                             : "AUTO pressed, but no AUTO ROLL window showed";
                Snapshot(AutoRollNoPass ? "crew auto roll pass offer" : "crew auto roll no window", 60, false, !AutoRollNoPass);
                return false;
            }
            for (int step = 0; step < 4; step++)
            {
                AutoRollSeen = w;
                string wrong = WindowWrong(w, replaces);
                if (wrong != null)
                {
                    AutoRollWhy = wrong;
                    if (w.Robux || !w.Usable) { AutoRollNoPass = w.Robux; Snapshot("crew auto roll window " + (w.Robux ? "robux" : "not read"), 60, false, !w.Robux); }
                    else Snapshot("crew auto roll window wrong", 60);
                    CloseAutoRoll(w);
                    return false;
                }
                OcrLine press = null;
                string what = null;
                if (w.Tier != (int)tier)
                {
                    press = w.Tiers[(int)tier]; what = tier.ToString().ToUpperInvariant();
                    if (press == null || !w.TierLight[(int)tier]) { AutoRollWhy = "its " + what + " tile " + (press == null ? "didn't read" : "is greyed (the cash on hand doesn't cover it)"); CloseAutoRoll(w); return false; }
                }
                else if (w.Rarity != rarity)
                {
                    press = w.Rarities[rarity]; what = RarityNames[rarity];
                    if (press == null || !w.RarityLight[rarity]) { AutoRollWhy = "its " + what + " tile " + (press == null ? "didn't read" : "is greyed"); CloseAutoRoll(w); return false; }
                }
                else if (!w.ThisSlotPicked || w.EverySlotPicked)
                {
                    press = w.ThisSlot; what = "THIS SLOT ONLY";
                    if (press == null) { AutoRollWhy = "its THIS SLOT ONLY didn't read"; CloseAutoRoll(w); return false; }
                }
                else break;

                PressCrew(press, w.Box.Contains(press.CenterX, press.CenterY) && Parse.Key(press.Text).Length >= 4, "not the AUTO ROLL window's " + what);
                AutoRollWindow again = null;
                for (int look = 0; look < 3 && again == null; look++)
                {
                    Wait(look == 0 ? 400 : 500);
                    using (var f = Capture()) again = ReadAutoRollWindow(f);
                }
                if (again == null) { AutoRollWhy = "the AUTO ROLL window went away after " + what; Snapshot("crew auto roll window gone", 60); return false; }
                w = again;
                if (step == 3) { AutoRollWhy = "the window's picks didn't take (" + w + ")"; Snapshot("crew auto roll picks", 60); CloseAutoRoll(w); return false; }
            }

            AutoRollWindow last = null;
            using (var f = Capture()) last = ReadAutoRollWindow(f);
            if (last == null) { AutoRollWhy = "the AUTO ROLL window went away before START"; return false; }
            AutoRollSeen = last;
            string bad = WindowWrong(last, replaces);
            if (bad == null && (last.Tier != (int)tier || last.Rarity != rarity || !last.ThisSlotPicked || last.EverySlotPicked))
                bad = "its picks read back as " + last;
            if (bad == null && !last.StartGold) bad = "its START isn't gold";
            if (bad != null) { AutoRollWhy = bad; Snapshot("crew auto roll read back", 60); CloseAutoRoll(last); return false; }
            PressCrew(last.Start, Parse.Key(last.Start.Text) == "START" && last.StartGold && last.Box.Contains(last.Start.CenterX, last.Start.CenterY)
                                  && last.ThisSlot != null && last.Start.Box.Y > last.ThisSlot.Box.Bottom, "not the AUTO ROLL window's START");

            for (int look = 0; look < 4; look++)
            {
                Wait(look == 0 ? 900 : 600);
                using (var f = Capture())
                {
                    if (look == 0) CheckForPurchasePrompt(Ocr.Read(f, PopupArea(f), PopupScale(f), Prep.None));
                    var p = ReadCrew(f);
                    if (p.Page && p.AutoRolls) return true;
                    if (p.AutoRollWindow && look == 3)
                    {
                        var open = ReadAutoRollWindow(f);
                        if (open != null) CloseAutoRoll(open);
                    }
                }
            }
            AutoRollWhy = "START pressed, but no slot rolls";
            Snapshot("crew auto roll not rolling", 60);
            return false;
        }

        internal static string WindowWrong(AutoRollWindow w, string replaces)
        {
            if (w.Robux) return "the AUTO ROLL window offers Robux or a pass (" + w.Words + ")";
            if (!w.Usable) return "the AUTO ROLL window didn't read (" + w.Words + ")";
            if (replaces == null && w.Name != null) return "the AUTO ROLL window would replace " + w.Name + " - an empty slot was picked";
            if (replaces != null && !w.Names(replaces)) return "the AUTO ROLL window names " + (w.Name ?? "nobody") + ", not " + replaces;
            return null;
        }

        public bool CloseAutoRoll(AutoRollWindow w)
        {
            if (w == null || w.X.IsEmpty) { log("Crew: the AUTO ROLL window's X wasn't found - left for you to close"); Snapshot("crew auto roll no x", 60); return false; }
            PressCrew(new OcrLine { Text = "X", Box = new Rectangle(w.X.X - 2, w.X.Y - 2, 5, 5) }, w.Box.Contains(w.X), "not the AUTO ROLL window's X");
            for (int look = 0; look < 3; look++)
            {
                Wait(500);
                using (var f = Capture()) if (ReadAutoRollWindow(f) == null) return true;
            }
            return false;
        }

        public bool CloseStrayAutoRoll()
        {
            AutoRollWindow w;
            using (var f = Capture()) w = ReadAutoRollWindow(f);
            if (w == null) return false;
            log("Crew: an AUTO ROLL window was open (" + w + ") - closed with its X, nothing pressed in it");
            CloseAutoRoll(w);
            return true;
        }

        public CrewRow FindRollingRow(out CrewPage page)
        {
            return FindCrewRow(r => r.AutoRolling, out page, true, false, true);
        }

        public bool StopAutoRoll(CrewRow row)
        {
            if (row == null || !row.AutoRolling || row.RollStop == null || row.RollLine == null) return false;
            int h = Math.Max(8, row.RollLine.Box.Height);
            PressCrew(row.RollStop, Parse.Key(row.RollStop.Text) == "STOP" && row.RollStopGold && row.RollStop.Box.X > row.RollLine.Box.Right
                                    && Math.Abs(row.RollStop.CenterY - row.RollLine.CenterY) <= 3 * h, "not a rolling slot's own STOP");
            int y = row.Anchor.CenterY;
            for (int look = 0; look < 4; look++)
            {
                Wait(look == 0 ? 800 : 600);
                using (var f = Capture())
                {
                    if (look == 0) CheckForPurchasePrompt(Ocr.Read(f, PopupArea(f), PopupScale(f), Prep.None));
                    var p = ReadCrew(f);
                    if (!p.Page) continue;
                    if (!p.Rolling.Any(r => r.Top <= y && r.Bottom > y)) return true;
                }
            }
            return false;
        }
    }
}
