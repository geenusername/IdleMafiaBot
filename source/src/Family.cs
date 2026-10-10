using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text.RegularExpressions;

namespace IdleMafiaBot
{

    sealed class FamilyOverview
    {
        public string Name = "", Tag = "", Founded = "", Donated = "";
        public int Level = -1, Members = -1, MembersMax = -1, Respect = -1, PerkLevels = -1, PerkLevelsMax = -1;
        public int Territories = -1, TerritoriesMax = -1, Gold = -1;
        public long Xp = -1, XpNext = -1;
        public double Vault = -1;
    }

    sealed class TakedownState
    {
        public bool Page;
        public string Name = "";
        public int Free = -1, FreeMax = -1;
        public int Segments = -1;
        public bool Full;
        public string Under = "";
        public int NextFreeSeconds = -1;
        public int Tickets = -1;
        public long Damage = -1;
        public string Place = "";
        public int Health = -1, HealthMax = -1;
        public OcrLine Attack;
        public bool AttackBelow;
        public Point ScrollAt;
        public int Stage = -1, Cleared = -1;
        public long StageHp = -1, StageHpMax = -1;
        public string StageName = "";
        public int NewSeconds = -1;
    }

    sealed class PerkRow
    {
        public string Name = "", Effect = "";
        public int Level = -1, LevelMax = -1;
        public long Have = -1, Need = -1;
        public OcrLine Give5;
        public bool GiveReady;
        public Point Over;
        public long Left { get { return Have >= 0 && Need > 0 && Have <= Need ? Need - Have : -1; } }
        public bool Maxed { get { return Level > 0 && LevelMax > 0 && Level >= LevelMax; } }
    }

    enum FamilyNav { Open, NotInFamily, Failed }

    sealed class FamilyInfo
    {
        public bool NotInFamily;
        public FamilyOverview Overview; public DateTime OverviewAt;
        public TakedownState Takedown; public DateTime TakedownAt;
        public List<PerkRow> Perks; public DateTime PerksAt;
        public int Attacks, StaminaGiven;
        public FamilyInfo Copy() { return (FamilyInfo)MemberwiseClone(); }
    }

    sealed partial class Game
    {

        static readonly HashSet<string> FamilyTabKeys = Keys("OVERVIEW", "MEMBERS", "PERKS", "TAKEDOWN", "WAR", "AUDIT", "LOG", "MANAGE", "BROWSE", "FAMILIES");

        internal List<OcrLine> FamilyStrip(Frame f) { return FamilyStrips(f).FirstOrDefault(); }

        IEnumerable<List<OcrLine>> FamilyStrips(Frame f)
        {
            var p = PageArea(f);
            var area = new Rectangle(p.X, p.Y, p.Width, Math.Max(40, p.Height / 4));
            int s = PageScale(f);
            foreach (var t in new[] { Tuple.Create(s, Prep.None), Tuple.Create(s + 1, Prep.None), Tuple.Create(s, Prep.Contrast), Tuple.Create(s + 2, Prep.None), Tuple.Create(s + 1, Prep.Contrast) })
            {
                var words = Ocr.ReadWords(f, area, t.Item1, t.Item2);
                foreach (var w in words.Where(x => FamilyTabKeys.Contains(Parse.Key(x.Text))).OrderBy(x => x.Box.Y))
                {
                    int tol = Math.Max(4, w.Box.Height * 2 / 3);
                    var row = words.Where(o => Math.Abs(o.CenterY - w.CenterY) <= tol).OrderBy(o => o.Box.X).ToList();
                    if (row.Select(o => Parse.Key(o.Text)).Where(k => FamilyTabKeys.Contains(k)).Distinct().Count() < 3) continue;
                    yield return row;
                    break;
                }
            }
        }

        internal Rectangle? FindSubTab(Frame f, string sub, out bool strip)
        {
            strip = false;
            foreach (var row in FamilyStrips(f))
            {
                strip = true;
                var r = SubTabIn(row, sub);
                if (r != null) return r;
            }
            return null;
        }

        internal static Rectangle? SubTabIn(List<OcrLine> strip, string label)
        {
            var want = label.Split(' ').Select(Parse.Key).ToArray();
            for (int i = 0; i + want.Length <= strip.Count; i++)
            {
                bool ok = true;
                for (int k = 0; k < want.Length && ok; k++)
                    ok = Parse.Key(strip[i + k].Text) == want[k] && (k == 0 || strip[i + k].Box.X > strip[i + k - 1].Box.Right);
                if (!ok) continue;
                Rectangle first = strip[i].Box, last = strip[i + want.Length - 1].Box;
                return Rectangle.FromLTRB(first.Left, Math.Min(first.Top, last.Top), last.Right, Math.Max(first.Bottom, last.Bottom));
            }
            return null;
        }

        internal static bool SubTabSelected(Frame f, Rectangle word)
        {
            int m = Math.Max(3, word.Height / 2), y = word.Y + word.Height / 2;
            return GoldIsh(f, word.X - m, y) && GoldIsh(f, word.Right + m, y);
        }

        internal static bool FamilyShows(string sub, List<OcrLine> lines)
        {
            Func<string, bool> has = w => { string k = Parse.Key(w); return lines.Any(l => Parse.Key(l.Text).Contains(k)); };
            switch (sub)
            {
                case "OVERVIEW":
                    return lines.Any(l => { string k = Parse.Key(l.Text); return k.StartsWith("FOUNDED") || k == "IEAVEFAMIIY" || k == "ACTIVEPERKS"; });

                case "PERKS": return has("STAMINA PERKS") || has("CASH PERKS") || has("GOLD PERKS")
                                     || (lines.Any(l => Parse.Key(l.Text) == "GIVES") && lines.Any(l => Parse.Key(l.Text) == "GIVEI"));
                case "TAKEDOWN": return has("FREE ATTACKS") && (has("TAKEDOWN TICKETS") || has("WEEKLY DAMAGE"));
            }
            return false;
        }

        internal static bool IsBackToMyFamily(OcrLine l)
        {
            string k = Parse.Key(l.Text), want = Parse.Key("BACK TO MY FAMILY");
            return k.EndsWith(want) && k.Length <= want.Length + 2;
        }

        internal static bool NoFamilyPage(List<OcrLine> lines)
        {
            if (lines.Any(IsBackToMyFamily)) return false;
            return lines.Any(l =>
            {
                string k = Parse.Key(l.Text);
                return k.Contains("FINDAFAMIIY") || k.Contains("CREATEAFAMIIY") || k.Contains("CREATEFAMIIY") || k.Contains("JOINAFAMIIY") || k.Contains("NOTINAFAMIIY");
            });
        }

        public FamilyNav OpenFamily(string sub)
        {
            if (!OpenTab(Tab.Family)) return FamilyNav.Failed;
            for (int attempt = 0; attempt < 5; attempt++)
            {
                List<OcrLine> lines;
                Rectangle page;
                Rectangle? target;
                bool selected = false, strip;
                using (var f = Capture())
                {
                    page = PageArea(f);
                    lines = Ocr.Read(f, page, PageScale(f), Prep.None);
                    CheckForPurchasePrompt(lines);
                    if (FamilyShows(sub, lines)) return FamilyNav.Open;
                    target = FindSubTab(f, sub, out strip);
                    if (target != null) selected = SubTabSelected(f, target.Value);
                }
                if (!strip)
                {
                    var back = lines.FirstOrDefault(l => IsBackToMyFamily(l) && l.CenterY < page.Top + page.Height / 4);
                    if (back != null)
                    {
                        log("FAMILY is showing BROWSE FAMILIES - going back to your family");
                        Press(back, 900);
                        continue;
                    }
                    if (NoFamilyPage(lines)) return FamilyNav.NotInFamily;
                    Wait(900);
                    continue;
                }
                if (target == null) { Wait(700); continue; }
                if (selected)
                {

                    Scroll(page.Left + page.Width / 2, page.Top + page.Height / 2, 30);
                    Wait(400);
                    continue;
                }
                Press(sub, target.Value.X + target.Value.Width / 2, target.Value.Y + target.Value.Height / 2, 300);
                if (WaitForFamily(sub, 5000)) return FamilyNav.Open;
            }
            log("Couldn't open FAMILY > " + sub);
            Snapshot("could not open family " + sub, 60);
            return FamilyNav.Failed;
        }

        bool WaitForFamily(string sub, int ms)
        {
            int end = Environment.TickCount + ms;
            while (true)
            {
                using (var f = Capture()) if (FamilyShows(sub, Ocr.Read(f, PageArea(f), PageScale(f), Prep.None))) return true;
                if (end - Environment.TickCount <= 0) return false;
                Wait(300);
            }
        }

        const string Dg = "[0-9OoIlS]";

        public FamilyOverview ReadFamilyOverview(Frame f)
        {
            var o = new FamilyOverview();
            var p = PageArea(f);
            int s = PageScale(f);
            var lines = Ocr.Read(f, p, s, Prep.None).OrderBy(l => l.Box.Y).ThenBy(l => l.Box.X).ToList();
            string all = string.Join("\n", lines.Select(l => l.Text));
            Match m;

            var head = lines.FirstOrDefault(l => l.CenterY < p.Top + p.Height / 2 && Regex.IsMatch(l.Text, @"[A-Za-z0-9].*\[\s*[A-Za-z0-9]{2,6}"));
            if (head != null && (m = Regex.Match(head.Text, @"^\s*(.+?)\s*\[\s*([A-Za-z0-9]{2,5})")).Success)
            {
                o.Name = m.Groups[1].Value.Trim();
                o.Tag = m.Groups[2].Value.ToUpperInvariant();
            }
            if ((m = Regex.Match(all, @"Founded\s+([^\n]+)", RegexOptions.IgnoreCase)).Success) o.Founded = m.Groups[1].Value.Trim();
            int a, b;
            if ((m = Regex.Match(all, @"FAMILY\s*LEVEL\s*(" + Dg + "{1,3})(?![0-9])", RegexOptions.IgnoreCase)).Success && int.TryParse(Parse.Digits(m.Groups[1].Value), out a)) o.Level = a;
            if (Pair(all, "(" + Dg + @"{1,3})\s*/\s*(" + Dg + @"{1,3})\s*MEMBERS", out a, out b)) { o.Members = a; o.MembersMax = b; }
            if ((m = Regex.Match(all, "(?<![0-9,.])(" + Dg + @"[0-9OoIlS,.]*)\s*RESPECT", RegexOptions.IgnoreCase)).Success && int.TryParse(Parse.Digits(m.Groups[1].Value), out a)) o.Respect = a;
            if (Pair(all, "(" + Dg + @"{1,3})\s*/\s*(" + Dg + @"{1,4})\s*PE[RA]K", out a, out b)) { o.PerkLevels = a; o.PerkLevelsMax = b; }
            if (Pair(all, @"TERRITORIES\s*(" + Dg + @"{1,3})\s*/\s*(" + Dg + "{1,3})", out a, out b)) { o.Territories = a; o.TerritoriesMax = b; }

            double money;
            var cash = lines.FirstOrDefault(l => Parse.Has(l.Text, "CASH") && l.Text.Contains("$") && Parse.Money(l.Text, out money));
            if (cash != null && Parse.Money(cash.Text, out money)) o.Vault = money;
            foreach (var l in lines)
                if ((m = Regex.Match(l.Text, "(?<![A-Za-z0-9])(" + Dg + @"[0-9OoIlS,]*)\s*GOLD\s*BARS\s*$", RegexOptions.IgnoreCase)).Success
                    && int.TryParse(Parse.Digits(m.Groups[1].Value), out a)) { o.Gold = a; break; }
            if ((m = Regex.Match(all, @"donated\s*(\$\s?[0-9][0-9.,]*\s?[KMBT]?)\s*and\s*(" + Dg + @"[0-9OoIlS,]*)\s*gold", RegexOptions.IgnoreCase)).Success)
                o.Donated = m.Groups[1].Value.Replace(" ", "") + " and " + Parse.Digits(m.Groups[2].Value) + " gold bars";

            var xp = lines.FirstOrDefault(l => { string k = Parse.Key(l.Text); return k.EndsWith("XP") && (k.Contains("AMI") || k.Contains("MIIY")); });
            if (xp != null)
            {
                int h = Math.Max(8, xp.Box.Height), left = p.Left + (int)(p.Width * 0.4);
                foreach (var c in lines.Where(l => l != xp && Math.Abs(l.CenterY - xp.CenterY) < 2 * h && Regex.IsMatch(Parse.Key(l.Text), "PERK|PEAK|RESPECT|MEMBERS|IEVEIS")))
                    left = Math.Max(left, c.Box.Right + 2);
                var band = Rectangle.FromLTRB(left, xp.Box.Y - h, p.Right - 2, xp.Box.Bottom + h);
                string own = string.Join(" ", lines.Where(l => band.Contains(l.CenterX, l.CenterY)).OrderBy(l => l.Box.X).Select(l => l.Text));
                var tries = new[] { Tuple.Create(s, Prep.WhiteText), Tuple.Create(s + 1, Prep.WhiteText), Tuple.Create(s, Prep.None), Tuple.Create(s + 1, Prep.None), Tuple.Create(s, Prep.WhiteSoft) };
                var seen = new List<Tuple<long, long>>();
                for (int i = -1; i < tries.Length && o.Xp < 0; i++)
                {
                    long cur, max;
                    if (!FamilyXp(i < 0 ? own : Ocr.ReadText(f, band, tries[i].Item1, tries[i].Item2), out cur, out max)) continue;
                    if (seen.Any(x => x.Item1 == cur && x.Item2 == max)) { o.Xp = cur; o.XpNext = max; }
                    else seen.Add(Tuple.Create(cur, max));
                }
                if (o.Xp < 0 && seen.Count > 0) { o.Xp = seen[0].Item1; o.XpNext = seen[0].Item2; }
            }
            return o;
        }

        public FamilyOverview ReadFamilyOverviewAll()
        {
            FamilyOverview o;
            using (var f = Capture()) o = ReadFamilyOverview(f);
            if (o.Vault >= 0 && o.Gold >= 0 && o.Territories >= 0) return o;
            ScrollPage(0.3, -ListNotches(4));
            Wait(500);
            FamilyOverview more;
            using (var f = Capture()) more = ReadFamilyOverview(f);
            ScrollPage(0.3, 10);
            if (o.Name.Length == 0) { o.Name = more.Name; o.Tag = more.Tag; }
            if (o.Founded.Length == 0) o.Founded = more.Founded;
            if (o.Donated.Length == 0) o.Donated = more.Donated;
            if (o.Level < 0) o.Level = more.Level;
            if (o.Members < 0) { o.Members = more.Members; o.MembersMax = more.MembersMax; }
            if (o.Respect < 0) o.Respect = more.Respect;
            if (o.PerkLevels < 0) { o.PerkLevels = more.PerkLevels; o.PerkLevelsMax = more.PerkLevelsMax; }
            if (o.Territories < 0) { o.Territories = more.Territories; o.TerritoriesMax = more.TerritoriesMax; }
            if (o.Gold < 0) o.Gold = more.Gold;
            if (o.Xp < 0) { o.Xp = more.Xp; o.XpNext = more.XpNext; }
            if (o.Vault < 0) o.Vault = more.Vault;
            return o;
        }

        static bool Pair(string text, string pattern, out int a, out int b)
        {
            a = b = -1;
            var m = Regex.Match(text, pattern, RegexOptions.IgnoreCase);
            return m.Success && int.TryParse(Parse.Digits(m.Groups[1].Value), out a) && int.TryParse(Parse.Digits(m.Groups[2].Value), out b) && a <= b;
        }

        internal static bool FamilyXp(string text, out long cur, out long max)
        {
            cur = max = -1;
            if (string.IsNullOrEmpty(text)) return false;
            var cut = Regex.Match(text, "FAM|AMI|XP", RegexOptions.IgnoreCase);
            string before = cut.Success ? text.Substring(0, cut.Index) : text;
            if (!BigFraction(before, out cur, out max))
            {
                var nums = Regex.Matches(before, @"[0-9][0-9,.]*[0-9]|[0-9]").Cast<Match>().Select(x => Parse.Digits(x.Value)).ToList();
                if (nums.Count < 2 || !long.TryParse(nums[nums.Count - 2], out cur) || !long.TryParse(nums[nums.Count - 1], out max)) { cur = max = -1; return false; }
            }
            if (max > 0 && cur >= 0 && cur <= max) return true;
            cur = max = -1;
            return false;
        }

        public TakedownState ReadTakedown(Frame f)
        {
            var t = new TakedownState();
            var p = PageArea(f);
            int s = PageScale(f);
            var lines = Ocr.Read(f, p, s, Prep.None);
            var free = View.Label(lines, "FREE ATTACKS");
            var tickets = View.Label(lines, "TAKEDOWN TICKETS");
            var damage = View.Label(lines, "YOUR WEEKLY DAMAGE");
            t.Page = free != null && (tickets != null || damage != null);
            if (!t.Page) return t;
            Match m;
            var title = lines.FirstOrDefault(l => Parse.Has(l.Text, "WEEKLY TAKEDOWN"));
            if (title != null && (m = Regex.Match(title.Text, @":\s*(.+)$")).Success) t.Name = m.Groups[1].Value.Trim();
            var next = lines.FirstOrDefault(l => Parse.Has(l.Text, "NEW TAKEDOWN"));
            if (next != null && (m = Regex.Match(next.Text, @"\bin\b(.*)$", RegexOptions.IgnoreCase)).Success) t.NewSeconds = Span(m.Groups[1].Value);

            int h = Math.Max(6, free.Box.Height);
            int tileRight = tickets != null && tickets.Box.X > free.Box.Right ? tickets.Box.X - 3 * Math.Max(6, tickets.Box.Height) : free.Box.X + p.Width / 3;

            AgreedFraction(f, lines, Rectangle.FromLTRB(free.Box.X - 2 * h, free.Box.Y - 3, free.Box.Right + h, free.Box.Bottom + 4 * h + 4), s, out t.Free, out t.FreeMax);
            t.Segments = GoldSegments(f, Rectangle.FromLTRB(free.Box.Right + h, free.Box.Y - h, tileRight, free.Box.Bottom + 3 * h));

            if (t.Segments <= 0)
                t.Segments = GoldSegments(f, Rectangle.FromLTRB(free.Box.X - 3 * h, free.Box.Bottom + 2 * h, tileRight, free.Box.Bottom + 6 * h));
            var under = lines.Where(l => l != free && l.Box.X > free.Box.Right + h && l.Box.Right <= tileRight + h
                                         && l.CenterY > free.Box.Y && l.CenterY < free.Box.Bottom + 4 * h).OrderBy(l => l.Box.X).ToList();
            t.Under = string.Join(" ", under.Select(l => l.Text)).Trim();
            t.Full = under.Any(l => Parse.Key(l.Text) == "FUII");
            if (!t.Full) t.NextFreeSeconds = Span(t.Under);

            if (tickets != null) t.Tickets = NumberUnder(f, lines, tickets, s);
            if (damage != null)
            {
                int dh = Math.Max(6, damage.Box.Height);
                var area = Rectangle.FromLTRB(damage.Box.X - dh, damage.Box.Bottom + 1, damage.Box.Right + 2 * dh, damage.Box.Bottom + 4 * dh + 4);
                var v = lines.FirstOrDefault(l => area.Contains(l.CenterX, l.CenterY) && Regex.IsMatch(l.Text, @"^\s*[0-9OoIlS][0-9OoIlS,.\s]*$"));
                long n;
                if (long.TryParse(Parse.Digits(v != null ? v.Text : Ocr.ReadText(f, area, s + 1, Prep.None)), out n)) t.Damage = n;
                var place = lines.FirstOrDefault(l => l.Text.Contains("#") && l.Box.X > damage.Box.X && Math.Abs(l.CenterY - (area.Top + area.Height / 2)) < 3 * dh);
                if (place != null) t.Place = Regex.Replace(place.Text, @"\s+", " ").Replace("Fam ily", "Family").Trim();
            }

            var you = lines.FirstOrDefault(l => Parse.Key(l.Text) == "YOU" && l.Box.Y > free.Box.Bottom);
            if (you != null)
            {
                int a, b;
                var hp = lines.FirstOrDefault(l => l.Box.X > you.Box.Right && Math.Abs(l.CenterY - you.CenterY) <= you.Box.Height && l.Text.Contains("/"));
                if (hp != null && Parse.Fraction(hp.Text, out a, out b, true) && b > 0 && a <= b) { t.Health = a; t.HealthMax = b; }
            }

            t.Attack = GoldAttack(f, lines.Where(l => l.Box.Y > free.Box.Bottom));

            t.ScrollAt = new Point(p.Left + p.Width / 2, p.Top + p.Height / 2);
            t.AttackBelow = t.Attack == null && (you == null || you.Box.Bottom > p.Bottom - 6 * Math.Max(6, you.Box.Height));

            t.Cleared = lines.Count(l => Parse.Key(l.Text) == "CIEARED");
            var stages = lines.Where(l => Regex.IsMatch(Parse.Key(l.Text), "^STAGE[0-9OIS]{1,2}$")).OrderBy(l => l.Box.X).ToList();
            for (int i = 0; i < stages.Count && t.Stage < 0; i++)
            {
                var st = stages[i];
                int sh = Math.Max(6, st.Box.Height), right = i + 1 < stages.Count ? stages[i + 1].Box.X : p.Right;
                string row = string.Join(" ", lines.Where(l => l != st && l.Box.X > st.Box.Right && l.Box.Right <= right && Math.Abs(l.CenterY - st.CenterY) < 2 * sh)
                                                   .OrderBy(l => l.Box.X).Select(l => l.Text));
                long hp, max;
                if (!TwoNumbers(row, out hp, out max)) continue;
                int n;
                if (int.TryParse(Parse.Digits(Parse.Key(st.Text).Substring(5)), out n)) t.Stage = n;
                t.StageHp = hp; t.StageHpMax = max;
                var name = lines.FirstOrDefault(l => Math.Abs(l.Box.X - st.Box.X) <= 2 * sh && l.Box.Y > st.Box.Bottom && l.Box.Y < st.Box.Bottom + 3 * sh);
                if (name != null) t.StageName = name.Text.Trim();
            }
            return t;
        }

        public TakedownState ReadTakedownSteady()
        {
            TakedownState a = null;
            for (int i = 0; i < 16; i++)
            {
                TakedownState b;
                using (var f = Capture()) b = ReadTakedown(f);

                bool scene = b.Free == 0 || ((b.Attack != null || b.AttackBelow) && b.Health >= 0);
                if (a != null && a.Page && b.Page && b.Free >= 0 && a.Free == b.Free && a.FreeMax == b.FreeMax && (scene || i >= 15)) return b;
                a = b;
                Wait(500);
            }
            a.Free = -1;
            return a;
        }

        public bool TakedownAttack(TakedownState t)
        {
            if (t.Attack != null) { Press(t.Attack, 300); return true; }
            if (!t.AttackBelow) return false;
            Scroll(t.ScrollAt.X, t.ScrollAt.Y, -3);
            Wait(500);
            OcrLine a;
            using (var f = Capture()) a = TakedownAttackIn(f);
            if (a != null) Press(a, 300);
            Scroll(t.ScrollAt.X, t.ScrollAt.Y, 5);
            return a != null;
        }

        public OcrLine TakedownAttackIn(Frame f)
        {
            var lines = Ocr.Read(f, PageArea(f), PageScale(f), Prep.None);
            var a = GoldAttack(f, lines);
            if (a == null) return null;
            int h = Math.Max(6, a.Box.Height);
            bool you = lines.Any(y => Parse.Key(y.Text) == "YOU" && y.CenterY < a.CenterY && a.CenterY - y.CenterY < 8 * h && Math.Abs(y.Box.X - a.Box.X) < 10 * h);
            return you ? a : null;
        }

        static OcrLine GoldAttack(Frame f, IEnumerable<OcrLine> lines)
        {
            foreach (var a in lines.Where(l => Parse.Key(l.Text) == "ATTACK").OrderBy(l => l.Box.Y))
            {
                int mg = Math.Max(4, a.Box.Height * 4 / 5);
                if (GoldIsh(f, a.Box.X - mg, a.CenterY) && GoldIsh(f, a.Box.Right + mg, a.CenterY)) return a;
            }
            return null;
        }

        public TakedownState WaitTakedownResult(int before, Action<string> seen)
        {
            TakedownState last;
            bool closed = false;
            int start = Environment.TickCount;
            while (true)
            {
                using (var f = Capture())
                {
                    CheckForPurchasePrompt(Ocr.Read(f, PopupArea(f), PopupScale(f), Prep.None));
                    last = ReadTakedown(f);
                }
                if (last.Page && last.Free >= 0 && last.Free < before) return last;
                int waited = Environment.TickCount - start;
                if (waited > 15000) return last;
                if (waited > 8000 && !closed && !last.Page && CloseTakedownWindow(seen)) { closed = true; continue; }
                Wait(400);
            }
        }

        public bool CloseTakedownWindow(Action<string> seen)
        {
            List<OcrLine> pop;
            using (var f = Capture()) pop = Ocr.Read(f, PopupArea(f), PopupScale(f), Prep.None);
            CheckForPurchasePrompt(pop);
            var close = pop.FirstOrDefault(l => SafeCloseKeys.Contains(Parse.Key(l.Text)));
            if (close == null) return false;
            seen(string.Join(" | ", pop.OrderBy(l => l.Box.Y).ThenBy(l => l.Box.X).Select(l => l.Text)));
            Press(close, 800);
            return true;
        }

        static void AgreedFraction(Frame f, List<OcrLine> page, Rectangle area, int s, out int cur, out int max)
        {
            cur = max = -1;
            var seen = new List<Tuple<int, int>>();
            var tries = new[] { Tuple.Create(s, Prep.None), Tuple.Create(s + 1, Prep.None), Tuple.Create(s, Prep.WhiteText), Tuple.Create(s + 1, Prep.WhiteText), Tuple.Create(s + 2, Prep.None) };
            for (int i = -1; i < tries.Length; i++)
            {
                string text = i < 0 ? string.Join(" ", page.Where(l => area.Contains(l.CenterX, l.CenterY)).Select(l => l.Text))
                                    : Ocr.ReadText(f, area, tries[i].Item1, tries[i].Item2);
                int a, b;
                if (!Parse.Fraction(text, out a, out b, true) || b < 1 || b > 99 || a < 0 || a > b) continue;
                if (seen.Any(x => x.Item1 == a && x.Item2 == b)) { cur = a; max = b; return; }
                seen.Add(Tuple.Create(a, b));
            }
        }

        static int NumberUnder(Frame f, List<OcrLine> page, OcrLine label, int s)
        {
            int h = Math.Max(6, label.Box.Height);
            var area = Rectangle.FromLTRB(label.Box.X - h, label.Box.Y - 3, label.Box.Right + h, label.Box.Bottom + 4 * h + 4);
            for (int i = -1; i < 3; i++)
            {
                var lines = i < 0 ? page.Where(l => area.Contains(l.CenterX, l.CenterY)) : Ocr.Read(f, area, new[] { s + 1, s, s + 2 }[i], Prep.None);
                foreach (var l in lines.Where(l => l.Box.Y > label.Box.Bottom - 2 && l.Box.X < label.Box.X + 4 * h))
                {
                    int n;
                    if (Regex.IsMatch(l.Text, @"^\s*[0-9OoIlSD]{1,4}\s*$") && int.TryParse(Parse.Digits(l.Text), out n)) return n;
                }
            }
            return -1;
        }

        static int GoldSegments(Frame f, Rectangle area)
        {
            int best = 0;
            for (int y = Math.Max(0, area.Top); y < Math.Min(f.Height, area.Bottom); y++)
            {
                int runs = 0, run = 0;
                for (int x = Math.Max(0, area.Left); x <= area.Right; x++)
                {
                    var c = x < area.Right ? f.Pixel(x, y) : Color.Black;
                    if (c.R >= 170 && c.G >= 135 && c.B <= 100 && c.R - c.B >= 100) { run++; continue; }
                    if (run >= 3) runs++;
                    run = 0;
                }
                best = Math.Max(best, runs);
            }
            return best;
        }

        public static readonly string[] StaminaPerkNames = { "Enforcers", "Guardians", "Hustlers", "Medics", "Runners", "Bagmen", "Insurance",
                                                             "Veterans", "Demolition", "Boss Hunters" };

        public List<PerkRow> ReadStaminaPerks(Frame f)
        {
            var p = PageArea(f);
            int s = PageScale(f);
            var lines = Ocr.Read(f, p, s, Prep.None);
            var rows = new List<PerkRow>();
            foreach (var g in lines.Where(l => Parse.Key(l.Text) == "GIVES").OrderBy(l => l.Box.Y))
            {
                int h = Math.Max(6, g.Box.Height);
                var g1 = lines.FirstOrDefault(l => Parse.Key(l.Text) == "GIVEI" && l.Box.Right <= g.Box.X && Math.Abs(l.CenterY - g.CenterY) < h);
                int buttons = (g1 ?? g).Box.X;

                var desc = lines.Where(l => Parse.Key(l.Text).StartsWith("EVERYMEMBER") && l.Box.X < buttons && l.CenterY < g.Box.Y && l.CenterY > g.CenterY - 8 * h)
                                .OrderByDescending(l => l.Box.X).ThenByDescending(l => l.Box.Y).FirstOrDefault();
                int col = desc != null ? desc.Box.X : buttons - 2 * (g.Box.Right - buttons) - 4 * h;
                var r = new PerkRow { Give5 = g, GiveReady = GiveFace(f, g) };

                int nameBottom = desc != null ? desc.Box.Y : g.Box.Y - 2 * h;
                Func<OcrLine, bool> nameLike = l => l != desc && Math.Abs(l.Box.X - col) <= 2 * h && l.CenterY < nameBottom && l.CenterY > g.CenterY - 8 * h
                                                    && Regex.Matches(l.Text, "[A-Za-z]").Count >= 3 && !Parse.Key(l.Text).StartsWith("EVERY") && !Parse.Has(l.Text, "LEVEL")
                                                    && !FamilyTabKeys.Contains(Parse.Key(l.Text));
                var name = lines.Where(nameLike).OrderByDescending(l => l.Box.Y).FirstOrDefault();
                if (name == null)
                {

                    var top = Rectangle.FromLTRB(col - h, g.CenterY - 8 * h, Math.Max(col + 14 * h, buttons), nameBottom);
                    foreach (int z in new[] { s + 1, s + 2 })
                        if ((name = Ocr.Read(f, top, z, Prep.None).Where(l => nameLike(l) && KnownPerk(l.Text) != null).OrderByDescending(l => l.Box.Y).FirstOrDefault()) != null) break;
                }
                if (name != null)
                {
                    r.Name = PerkName(f, name, s);
                    var lv = lines.FirstOrDefault(l => l.Box.X > name.Box.Right && Math.Abs(l.CenterY - name.CenterY) < h && Parse.Has(l.Text, "LEVEL"));
                    const string LevelPattern = @"LEVEL\s*(" + Dg + @"{1,3})\s*/\s*(" + Dg + "{1,3})";
                    int a, b;
                    if (lv != null && Pair(lv.Text, LevelPattern, out a, out b)) { r.Level = a; r.LevelMax = b; }
                    else if (lv != null)
                    {

                        var box = lv.Box;
                        box.Inflate(Math.Max(4, h / 2), Math.Max(3, h / 3));
                        foreach (var t in new[] { Tuple.Create(s + 1, Prep.None), Tuple.Create(s + 2, Prep.None), Tuple.Create(s, Prep.Contrast) })
                            if (Pair(Ocr.ReadText(f, box, t.Item1, t.Item2), LevelPattern, out a, out b)) { r.Level = a; r.LevelMax = b; break; }
                    }
                }
                if (desc != null)
                {
                    var eff = lines.FirstOrDefault(l => l.Box.X > desc.Box.Right && Math.Abs(l.CenterY - desc.CenterY) < h && Parse.Key(l.Text).EndsWith("NOW"));
                    if (eff != null) r.Effect = Regex.Replace(Regex.Replace(eff.Text, @"\s*now\s*$", "", RegexOptions.IgnoreCase), @"\s+", " ").Trim();
                }

                var band = Rectangle.FromLTRB(col - 5 * h, g.CenterY - (int)(1.2 * h), buttons - h / 2, g.CenterY + (int)(1.2 * h));
                r.Over = new Point(band.Left + band.Width / 2, g.CenterY);
                long have, need;
                if (Progress(f, lines, band, g.CenterY, s, out have, out need)) { r.Have = have; r.Need = need; }
                rows.Add(r);
            }
            return rows;
        }

        static bool Progress(Frame f, List<OcrLine> page, Rectangle band, int cy, int s, out long have, out long need)
        {
            double fill = PerkFill(f, band, cy);
            long needSeen = -1;
            Func<long, long, bool> ok = (a, b) =>
            {
                if (b <= 0 || a > b) return false;
                needSeen = b;
                return fill < 0 || Math.Abs(a - fill * b) <= Math.Max(1.5, 0.03 * b);
            };
            string own = string.Join(" ", page.Where(l => band.Contains(l.CenterX, l.CenterY) && l.Text.Contains("/")).Select(l => l.Text));
            if (BigFraction(own, out have, out need) && ok(have, need)) return true;
            foreach (var t in new[] { Tuple.Create(s, Prep.WhiteText), Tuple.Create(s + 1, Prep.WhiteText), Tuple.Create(s + 1, Prep.None), Tuple.Create(s + 2, Prep.None), Tuple.Create(s, Prep.WhiteSoft) })
                if (BigFraction(Ocr.ReadText(f, band, t.Item1, t.Item2), out have, out need) && ok(have, need)) return true;
            if (fill >= 0 && needSeen > 0) { need = needSeen; have = (long)Math.Round(fill * need); return true; }
            have = need = -1;
            return false;
        }

        static double PerkFill(Frame f, Rectangle band, int cy)
        {
            if (cy < 2 || cy >= f.Height - 2) return -1;
            int x = Math.Max(2, band.Left), end = Math.Min(f.Width - 3, band.Right + band.Height);

            while (x < end && !((Blue(f.Pixel(x, cy)) || PerkTrack(f.Pixel(x, cy))) && PerkFrame(f.Pixel(x - 1, cy)))) x++;
            if (x + 60 >= end) return -1;
            int top = cy, bottom = cy, col = x + 1;
            while (top > cy - band.Height && (Blue(f.Pixel(col, top - 1)) || PerkTrack(f.Pixel(col, top - 1)))) top--;
            while (bottom < cy + band.Height && (Blue(f.Pixel(col, bottom + 1)) || PerkTrack(f.Pixel(col, bottom + 1)))) bottom++;
            int h = bottom - top;
            if (h < 10) return -1;
            double a = PerkFillOnRow(f, x, end, top + (int)Math.Round(h * 0.12)), b = PerkFillOnRow(f, x, end, bottom - (int)Math.Round(h * 0.12));
            return a < 0 || b < 0 || Math.Abs(a - b) > 0.02 ? -1 : (a + b) / 2;
        }

        static double PerkFillOnRow(Frame f, int left, int end, int y)
        {
            int x = left, fillEnd = left, gap = 0;
            bool track = false;
            for (; x < end && gap <= 8; x++)
            {
                var c = f.Pixel(x, y);
                if (Blue(c)) { if (track) return -1; fillEnd = x + 1; gap = 0; }
                else if (PerkTrack(c)) { track = true; gap = 0; }
                else if (PerkFrame(c)) break;
                else gap++;
            }
            if (x >= end || !PerkFrame(f.Pixel(x, y)) || x - left < 60) return -1;
            return (double)(fillEnd - left) / (x - left);
        }

        static bool PerkFrame(Color c) { return Math.Abs(c.R - 56) <= 7 && Math.Abs(c.G - 56) <= 7 && Math.Abs(c.B - 59) <= 7; }

        static bool PerkTrack(Color c) { return Math.Max(c.R, Math.Max(c.G, c.B)) <= 22; }

        static bool GiveFace(Frame f, OcrLine g)
        {
            int h = Math.Max(6, g.Box.Height), m = Math.Max(3, h * 3 / 5), v = Math.Max(2, h * 2 / 5);
            return Blue(f.Pixel(g.Box.X - m, g.CenterY)) && Blue(f.Pixel(g.Box.Right + m, g.CenterY))
                && Blue(f.Pixel(g.CenterX, g.Box.Y - v)) && Blue(f.Pixel(g.CenterX, g.Box.Bottom + v));
        }

        static bool Blue(Color c) { return c.B >= 130 && c.B - c.R >= 50 && c.G >= c.R; }

        static string PerkName(Frame f, OcrLine l, int s)
        {
            string known = KnownPerk(l.Text);
            if (known != null) return known;
            var area = l.Box;
            area.Inflate(Math.Max(4, l.Box.Height / 2), Math.Max(3, l.Box.Height / 3));
            foreach (int z in new[] { s + 1, s + 2 })
                if ((known = KnownPerk(Ocr.ReadText(f, area, z, Prep.None))) != null) return known;
            return Regex.Replace(l.Text, @"\s+", " ").Trim();
        }

        static string KnownPerk(string text)
        {
            string k = Parse.Key(text);
            if (k.Length < 3) return null;
            foreach (var n in StaminaPerkNames)
            {
                string nk = Parse.Key(n);
                if (k == nk || View.Distance(k, nk) <= Math.Max(1, nk.Length / 4)) return n;
            }
            return null;
        }

        public static bool SamePerk(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            string ka = KnownPerk(a), kb = KnownPerk(b);
            return Parse.Key(a) == Parse.Key(b) || (ka != null && ka == kb);
        }

        static bool OneColumn(List<PerkRow> rows, Rectangle page)
        {
            return rows.Count > 0 && rows.Average(r => r.Over.X) < page.Left + page.Width / 2;
        }

        List<PerkRow> StaminaListTop(out Point over)
        {
            List<PerkRow> rows;
            Rectangle p;
            using (var f = Capture()) { rows = ReadStaminaPerks(f); p = PageArea(f); }
            over = new Point(p.Left + p.Width / 2, p.Top + p.Height / 2);
            bool page = rows.Count == 0 || OneColumn(rows, p);
            if (page)
            {
                Scroll(over.X, over.Y, 30);
                Wait(400);
                for (int step = 0; step < 12; step++)
                {
                    using (var f = Capture()) rows = ReadStaminaPerks(f);
                    if (rows.Count > 0) break;
                    Scroll(over.X, over.Y, -3);
                    Wait(400);
                }
                if (rows.Count == 0 || OneColumn(rows, p)) return rows;
            }
            over = rows[0].Over;
            Scroll(over.X, over.Y, 10);
            Wait(400);
            using (var f = Capture()) return ReadStaminaPerks(f);
        }

        public List<PerkRow> WalkStaminaPerks()
        {
            var all = new List<PerkRow>();
            Point over;
            var rows = StaminaListTop(out over);
            if (rows.Count == 0) return all;
            string last = null;
            for (int step = 0; step < 14; step++)
            {
                if (step > 0) using (var f = Capture()) rows = ReadStaminaPerks(f);
                foreach (var r in rows.Where(x => x.Name.Length > 0))
                {
                    int i = all.FindIndex(x => SamePerk(x.Name, r.Name));
                    if (i < 0) all.Add(r);
                    else if (r.Have >= 0 || all[i].Have < 0) all[i] = r;
                }
                string sig = string.Join(",", rows.Select(x => x.Name + "@" + x.Give5.CenterY));
                if (sig == last) break;
                last = sig;
                Scroll(over.X, over.Y, -2);
                Wait(400);
            }
            return all;
        }

        public PerkRow PerkRowInSight(string name, bool countOnly = false)
        {
            using (var f = Capture()) return ReadStaminaPerks(f).FirstOrDefault(r => SamePerk(r.Name, name) && (countOnly ? r.Have >= 0 : r.GiveReady));
        }

        public PerkRow FindPerkRow(string name)
        {
            var seen = PerkRowInSight(name);
            if (seen != null) return seen;
            Point over;
            var rows = StaminaListTop(out over);
            if (rows.Count == 0) return null;
            string last = null;
            bool stuck = false;
            for (int step = 0; step < 18; step++)
            {
                if (step > 0) using (var f = Capture()) rows = ReadStaminaPerks(f);
                if (rows.Count == 0) return null;
                var r = rows.FirstOrDefault(x => SamePerk(x.Name, name));
                if (r != null && r.GiveReady) return r;
                string sig = string.Join(",", rows.Select(x => x.Name + "@" + x.Give5.CenterY));

                if (sig == last)
                {
                    if (stuck) return FindPerkRowUp(name, over);
                    stuck = true;
                    Wait(600);
                    Scroll(over.X, over.Y, -2);
                    Wait(700);
                    continue;
                }
                stuck = false;
                last = sig;
                Scroll(over.X, over.Y, r != null ? -1 : -2);
                Wait(400);
            }
            return FindPerkRowUp(name, over);
        }

        PerkRow FindPerkRowUp(string name, Point over)
        {
            for (int step = 0; step < 16; step++)
            {
                Scroll(over.X, over.Y, 1);
                Wait(400);
                List<PerkRow> rows;
                using (var f = Capture()) rows = ReadStaminaPerks(f);
                if (rows.Count == 0) return null;
                var r = rows.FirstOrDefault(x => SamePerk(x.Name, name));
                if (r != null && r.GiveReady) return r;
            }
            return null;
        }

        public void GiveStamina(PerkRow r)
        {
            Press(r.Give5, 700);
            CheckForPurchaseWindow();
        }

        internal static bool BigFraction(string s, out long a, out long b)
        {
            a = b = -1;
            if (string.IsNullOrEmpty(s)) return false;
            const string N = "[0-9OoIlSsB]{1,3}(?:[,. ][0-9OoIlSsB]{3})*";
            var m = Regex.Match(s, "(?<![A-Za-z0-9])(" + N + @")\s*[/|]\s*(" + N + ")(?![0-9])");
            if (!m.Success || !long.TryParse(Parse.Digits(m.Groups[1].Value), out a) || !long.TryParse(Parse.Digits(m.Groups[2].Value), out b)) { a = b = -1; return false; }
            return true;
        }

        static bool TwoNumbers(string s, out long a, out long b)
        {
            if (BigFraction(s, out a, out b) && a <= b && b > 0) return true;
            var nums = Regex.Matches(s ?? "", @"[0-9][0-9,.]*[0-9]|[0-9]").Cast<Match>().Select(x => Parse.Digits(x.Value)).ToList();
            if (nums.Count >= 2 && long.TryParse(nums[nums.Count - 2], out a) && long.TryParse(nums[nums.Count - 1], out b) && a <= b && b > 0) return true;
            a = b = -1;
            return false;
        }

        internal static int Span(string s)
        {
            int d = Parse.Duration(s);
            if (d >= 0) return d;
            int total = 0;
            bool any = false, digit = false;
            foreach (Match m in Regex.Matches(s ?? "", @"(?<![A-Za-z0-9])([0-9OoIl]{1,3})\s?([dhms])(?![A-Za-z])", RegexOptions.IgnoreCase))
            {
                int n;
                if (!Regex.IsMatch(m.Groups[1].Value, "[0-9]") && m.Groups[1].Value.Length < 2) continue;
                if (!int.TryParse(Parse.Digits(m.Groups[1].Value), out n)) continue;
                digit |= Regex.IsMatch(m.Groups[1].Value, "[0-9]");
                switch (char.ToLowerInvariant(m.Groups[2].Value[0]))
                {
                    case 'd': total += n * 86400; break;
                    case 'h': total += n * 3600; break;
                    case 'm': total += n * 60; break;
                    default: total += n; break;
                }
                any = true;
            }
            return any && digit ? total : -1;
        }
    }
}
