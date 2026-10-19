using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text.RegularExpressions;

namespace IdleMafiaBot
{

    enum CrewRoll { Street, Professional, Elite }

    enum LuckRoll { Same, Professional, Elite }

    enum Rarity { Common, Uncommon, Rare, Epic, Legendary, Mythic, Secret, Forbidden }

    sealed class CrewInfo
    {
        public int[] ByRarity = new int[8];
        public int Henchmen = -1, Slots = -1, Empty;
        public int Attack = -1, Defense = -1;
        public double SlotPrice = -1;
        public string Worst = "";
        public string Status = "";
        public DateTime At;

        public static readonly double[,] Odds =
        {
            { 44.85, 26.9, 16.6, 8.5, 2.5, 0.6, 0.05, 0 },
            { 0, 0, 58.75, 28, 11, 2, 0.25, 0 },
            { 0, 0, 0, 50.2, 37, 10, 2.6, 0.2 },
        };

        public static double HiresFor(CrewRoll roll, int rarity)
        {
            double p = 0;
            for (int r = Math.Max(0, rarity); r < 8; r++) p += Odds[(int)roll, r];
            return p <= 0 ? -1 : 100 / p;
        }
    }

    sealed partial class Game
    {
        public static readonly string[] RarityNames = { "COMMON", "UNCOMMON", "RARE", "EPIC", "LEGENDARY", "MYTHIC", "SECRET", "FORBIDDEN" };
        static readonly string[] RarityKeys = RarityNames.Select(Parse.Key).ToArray();

        public static int BestRoll(CrewRoll r) { return r == CrewRoll.Elite ? 7 : 6; }

        static readonly Regex HireRx = new Regex(@"^\s*(STREET|PROFESSIONAL|ELITE)\s*\$\s?([0-9OoIlS]{1,3}(?:[,.][0-9OoIlS]{1,3})*\s*[KMBT]?)\s*$", RegexOptions.IgnoreCase);
        static readonly Regex UnlockRx = new Regex(@"^\s*UN[LI1]OCK\s*S[LI1]OT\s*\$\s?([0-9OoIlS]{1,3}(?:[,.][0-9OoIlS]{1,3})*\s*[KMBT]?)\s*$", RegexOptions.IgnoreCase);

        internal static readonly Regex HenchmenRx = new Regex(@"([0-9]{1,3}?)\s*[O0]F\s*([0-9]{1,3})\s*HENCHMEN", RegexOptions.IgnoreCase);

        static readonly Regex HenchmenLooseRx = new Regex(@"(?<![A-Za-z])([0-9IlO|]{1,3}?)\s*[O0]F\s*([0-9IlO|]{1,3})\s*HENCHMEN");

        static readonly Regex CountAloneRx = new Regex(@"^\s*[0-9OoIlSB|]{1,3}\s*[O0]F\s*[0-9OoIlSB|]{1,3}\s*$", RegexOptions.IgnoreCase);

        internal static List<OcrLine> CountWrapped(List<OcrLine> lines)
        {
            return JoinWrapped(lines, t => CountAloneRx.IsMatch(t), t => Parse.Key(t).StartsWith("HENCHMEN"));
        }

        internal static bool HenchmenCount(string text, out int n, out int m)
        {
            n = m = -1;
            var x = HenchmenRx.Match(text ?? "");

            string fixedBS = Regex.Replace(text ?? "", @"(?<=[0-9])[BS]|[BS](?=[0-9])", c => c.Value == "B" ? "8" : "5");
            if (!x.Success) x = HenchmenRx.Match(fixedBS);
            Func<string, string> digits = s => s;
            if (!x.Success)
            {
                x = HenchmenLooseRx.Match(fixedBS);
                digits = s => Regex.Replace(Regex.Replace(s, "[Il|]", "1"), "O", "0");
            }
            if (!x.Success || !int.TryParse(digits(x.Groups[1].Value), out n) || !int.TryParse(digits(x.Groups[2].Value), out m) || n > m || m > 99) { n = m = -1; return false; }
            return true;
        }

        static readonly Regex PowerRx = new Regex(@"(?:\+\s*|•\s*(?=[0-9]))([0-9OoIlS]{1,5})");

        static readonly Regex GluedRx = new Regex(@"^\s*((?:[""“”„″•'’-]\s*)?[A-Z][a-z]{2,}[a-z.'’]*[""”]?)(?:\s+\S{0,2}\s*\+\s*[0-9OoIlS].*|\s+\S{1,2})?$");

        static readonly Regex TrainedRx = new Regex(@"(?:^|\s)\+\s?([1-9]|10)\s*$");

        internal static readonly Regex PowerInNameRx = new Regex(@"^(.*[A-Za-z.'’""”])\s+(?:[^\s+]{0,2}\s*)?\+\s*([0-9OoIlS]{1,5})\s*(A\s?[TI]\s?[TI]?\s?A\s?C\s?K|D\s?E\s?F\s?E\s?N\s?S\s?E)\b.*$", RegexOptions.IgnoreCase);

        static readonly Regex TrainLeftRx = new Regex(@"\b(\d{1,2}):(\d{2})(?::(\d{2}))?\s*[lI1]eft\b", RegexOptions.IgnoreCase);

        static bool GoldText(Frame f, Rectangle box)
        {
            box.Intersect(new Rectangle(0, 0, f.Width, f.Height));
            int gold = 0, n = 0;
            for (int y = box.Top; y < box.Bottom; y++)
                for (int x = box.Left; x < box.Right; x++)
                {
                    var c = f.Pixel(x, y); n++;
                    if (c.R >= 150 && c.R - c.B >= 70 && c.G >= 100) gold++;
                }
            return n > 0 && gold * 100 >= 4 * n;
        }

        public sealed class CrewRow
        {
            public string Name = "";
            public int Rarity = -1, Attack = -1, Defense = -1;
            public bool Empty, Locked;
            public bool Cut;
            public OcrLine Anchor;
            public int Top, Bottom;
            public OcrLine Dismiss;
            public Point Up = Point.Empty, Down = Point.Empty;
            public OcrLine[] Hire = new OcrLine[3];
            public double[] HirePrice = { -1, -1, -1 };
            public bool[] HireGold = new bool[3];
            public OcrLine Unlock;
            public double UnlockPrice = -1;
            public bool UnlockGold;

            public OcrLine Train;
            public bool TrainGold;
            public bool Training;
            public int TrainLevel;
            public int TrainSecondsLeft = -1;

            public bool Stationed;
            public string StationedAt = "";
            public OcrLine Recall;
            public bool Recovering;
            public OcrLine Heal;
            public int HealCost = -1;
            public int ReadPage = -1;
            public bool Recruit;

            public OcrLine Auto;
            public bool AutoActive;

            public bool AutoRolling;
            public int RollFor = -1;
            public int RollHires = -1;
            public double RollSpent = -1;
            public CrewRoll RollTier = CrewRoll.Street;
            public bool RollTierRead;
            public OcrLine RollLine, RollStop;
            public bool RollStopGold;

            public int Power { get { return Attack >= 0 && Defense >= 0 ? Attack + Defense : -1; } }

            public string Key { get { return Parse.Key(Name) + "|" + Rarity + "|" + Attack + "|" + Defense; } }
            public string RarityName { get { return RarityTitle(Rarity); } }
            public override string ToString()
            {
                if (Empty) return "an empty slot";
                if (Locked) return "a locked slot";

                string power = Power >= 0 ? ", +" + Attack + "/+" + Defense
                             : Attack >= 0 || Defense >= 0 ? ", +" + (Attack >= 0 ? Attack.ToString() : "?") + "/+" + (Defense >= 0 ? Defense.ToString() : "?")
                             : "";
                return (Name.Length > 0 ? Name : "a henchman") + " (" + RarityName + power + ")";
            }
        }

        public static string RarityTitle(int r)
        {
            return r >= 0 && r < RarityNames.Length ? RarityNames[r].Substring(0, 1) + RarityNames[r].Substring(1).ToLowerInvariant() : "?";
        }

        public sealed class CrewPage
        {
            public List<CrewRow> Rows = new List<CrewRow>();
            public int Henchmen = -1, Slots = -1;
            public bool Page;
            public bool TopInSight, FootInSight;

            public bool AutoRolls;
            public List<CrewRow> Rolling = new List<CrewRow>();

            public bool AutoRollWindow;
        }

        public static bool SortsAbove(CrewRow a, CrewRow b) { return SortsAbove(a, b, null); }

        public static bool SortsAbove(CrewRow a, CrewRow b, List<OwnStats> own)
        {
            if (a.Rarity < 0 || b.Rarity < 0) return false;
            if (a.Rarity != b.Rarity) return a.Rarity > b.Rarity;
            var x = OwnOf(a, own);
            var y = OwnOf(b, own);
            return x != null && y != null && x.Sum > y.Sum;
        }

        public static CrewRow NextToMoveUp(List<CrewRow> inSight, out int noArrow) { return NextToMoveUp(inSight, out noArrow, null); }

        public static CrewRow NextToMoveUp(List<CrewRow> inSight, out int noArrow, List<OwnStats> own) { return NextToMoveUp(inSight, out noArrow, own, null); }

        public static CrewRow NextToMoveUp(List<CrewRow> inSight, out int noArrow, List<OwnStats> own, Func<CrewRow, bool> skip)
        {
            noArrow = 0;
            for (int i = inSight.Count - 1; i >= 1; i--)
                if (SortsAbove(inSight[i], inSight[i - 1], own))
                {
                    if (skip != null && skip(inSight[i])) continue;
                    if (inSight[i].Up.IsEmpty) { noArrow++; continue; }
                    return inSight[i];
                }
            return null;
        }

        public sealed class OwnStats
        {
            public string Name = "";
            public int Rarity = -1, Attack = -1, Defense = -1;
            public int Sum { get { return Attack + Defense; } }
            public override string ToString() { return "+" + Attack + "/+" + Defense; }
        }

        public static OwnStats OwnOf(CrewRow row, List<OwnStats> own)
        {
            if (row == null || own == null || row.Empty || row.Locked || row.Rarity < 0) return null;
            var hits = own.Where(o => o.Rarity == row.Rarity && NamesAlike(o.Name, row.Name)).Take(2).ToList();
            return hits.Count == 1 ? hits[0] : null;
        }

        static bool NamesAlike(string a, string b)
        {
            string x = Parse.Key(a), y = Parse.Key(b);
            if (x.Length < 3 || y.Length < 3) return false;
            if (x == y) return true;
            int shorter = Math.Min(x.Length, y.Length);
            if (shorter >= 10 && View.Distance(x.Substring(0, shorter), y.Substring(0, shorter)) <= 1) return true;
            int n = Math.Min(4, Math.Min(x.Length, y.Length));
            return x.Substring(0, n) == y.Substring(0, n) && View.Distance(x, y) <= Math.Max(1, Math.Min(x.Length, y.Length) / 4);
        }

        public static void NoteOwn(List<OwnStats> own, CrewRow row)
        {

            if (own == null || row == null || row.Empty || row.Locked || row.Training || row.Stationed || row.Rarity < 0 || row.Attack < 0 || row.Defense < 0 || Parse.Key(row.Name).Length < 3) return;
            own.RemoveAll(o => o.Rarity == row.Rarity && NamesAlike(o.Name, row.Name));
            own.Add(new OwnStats { Name = row.Name, Rarity = row.Rarity, Attack = row.Attack, Defense = row.Defense });
        }

        public static CrewRow WeakestOwn(List<CrewRow> crew, List<OwnStats> own, int rarity, int min, out int unknown)
        {
            unknown = 0;
            CrewRow pick = null;
            int low = int.MaxValue;
            foreach (var r in crew.Where(r => r.Rarity == rarity))
            {
                var o = OwnOf(r, own);
                if (o == null) { unknown++; continue; }
                if (o.Attack >= min && o.Defense >= min) continue;
                if (o.Sum < low) { pick = r; low = o.Sum; }
            }
            return pick;
        }

        internal static bool CutName(string n)
        {
            return Regex.IsMatch(n ?? "", @"^\S+ ""[^""]*$") && Regex.Split(n.Trim(), @"\s+").Length <= 3;
        }

        static string NameAgain(Frame f, int s, List<OcrLine> nameLines, OcrLine rarity, int h, string name)
        {
            int left = Math.Min(rarity.Box.X, nameLines.Min(l => l.Box.X)) - h, top = nameLines.Min(l => l.Box.Y) - h / 2;
            var r = Rectangle.Intersect(Rectangle.FromLTRB(Math.Max(0, left), Math.Max(0, top), left + 16 * h, rarity.Box.Y), new Rectangle(0, 0, f.Width, f.Height));
            if (r.Width < 10 || r.Height < 6) return name;
            string first = Parse.Key(Regex.Split(name.Trim(), @"\s+")[0]);
            foreach (var t in new[] { Tuple.Create(s + 1, Prep.None), Tuple.Create(s + 2, Prep.None), Tuple.Create(s + 1, Prep.WhiteText) })
            {
                var lines = Ocr.Read(f, r, t.Item1, t.Item2).Where(l => !TrainedRx.IsMatch(l.Text) || Regex.IsMatch(l.Text, "[A-Za-z]{2}")).OrderBy(l => l.Box.Y).ToList();
                string again = TidyName(Regex.Replace(string.Join(" ", lines.Select(l => l.Text.Trim())), @"\s+", " "));
                again = TrainedRx.Replace(again, "").Trim();
                var words = Regex.Split(again, @"\s+");
                if (!CutName(again) && words.Length >= 3 && Parse.Key(words[0]) == first && Parse.Key(again).Length > Parse.Key(name).Length && !PowerInNameRx.IsMatch(again))
                    return again;
            }
            return name;
        }

        internal static string TidyName(string n)
        {
            if (string.IsNullOrEmpty(n)) return n ?? "";
            n = Regex.Replace(n, "[•“”„″]", "\"");
            n = Regex.Replace(n, @"^(\S+) [-'] (?=[A-Z][^""]*"")", "$1 \"");
            n = Regex.Replace(n, @"^(\S+ ""[^""]+?)[-'] (\S+)$", "$1\" $2");
            n = Regex.Replace(n, @"^(\S+) ""\s+", "$1 \"");
            n = Regex.Replace(n, @"\s+""\s+(\S+)$", "\" $1");
            n = Regex.Replace(n, @"\.{2,}['’`]?$", "...");
            return n;
        }

        public static bool SameHenchman(CrewRow a, CrewRow b)
        {
            if (a == null || b == null || a.Empty || a.Locked || b.Empty || b.Locked || a.Rarity != b.Rarity) return false;
            string x = Parse.Key(a.Name), y = Parse.Key(b.Name);

            if (x.Length >= 10 && y.Length >= 10 && View.Distance(x, y) <= 1) return true;
            if (a.Attack >= 0 && b.Attack >= 0 && a.Attack != b.Attack) return false;
            if (a.Defense >= 0 && b.Defense >= 0 && a.Defense != b.Defense) return false;
            if (x.Length < 3 || y.Length < 3) return a.Power >= 0 && a.Power == b.Power;
            int n = Math.Min(4, Math.Min(x.Length, y.Length));
            return x.Substring(0, n) == y.Substring(0, n) || View.Distance(x, y) <= Math.Max(2, Math.Min(x.Length, y.Length) / 3);
        }

        public static bool SameAcrossPages(CrewRow a, CrewRow b)
        {
            if (a == null || b == null || a.Empty || a.Locked || b.Empty || b.Locked || a.Rarity != b.Rarity) return false;
            if (a.Attack >= 0 && b.Attack >= 0 && a.Attack != b.Attack) return false;
            if (a.Defense >= 0 && b.Defense >= 0 && a.Defense != b.Defense) return false;
            string x = Parse.Key(a.Name), y = Parse.Key(b.Name);
            if (x.Length < 3 || y.Length < 3) return a.Attack >= 0 && b.Attack >= 0 && a.Defense >= 0 && b.Defense >= 0;

            if (Math.Min(x.Length, y.Length) >= 10 && (x.StartsWith(y) || y.StartsWith(x))) return true;
            return x == y || View.Distance(x, y) <= Math.Max(1, Math.Min(x.Length, y.Length) / 6);
        }

        static double ReadCost(CrewRow a, CrewRow b)
        {
            if (a.Rarity < 0 || a.Rarity != b.Rarity) return -1;
            string x = Parse.Key(a.Name), y = Parse.Key(b.Name);
            double name;
            if (a.Cut || b.Cut || x.Length < 3 || y.Length < 3) name = 0.25;
            else
            {
                int shorter = Math.Min(x.Length, y.Length), longer = Math.Max(x.Length, y.Length);
                name = Math.Min((double)View.Distance(x, y) / longer, (double)View.Distance(x.Substring(0, shorter), y.Substring(0, shorter)) / shorter);
                if (name > 0.6 || (name > 0.35 && x.Substring(0, 3) != y.Substring(0, 3))) return -1;
            }
            double numbers = 0;
            if (a.Attack >= 0 && b.Attack >= 0 && a.Attack != b.Attack) numbers += 0.4;
            if (a.Defense >= 0 && b.Defense >= 0 && a.Defense != b.Defense) numbers += 0.4;
            return name + numbers;
        }

        internal static int OverlapLength(List<CrewRow> seen, List<CrewRow> page)
        {
            int best = 0;
            double bestCost = double.MaxValue;
            for (int k = Math.Min(Math.Min(seen.Count, page.Count), 10); k >= 1; k--)
            {
                double sum = 0;
                bool fits = true;
                for (int i = 0; i < k && fits; i++)
                {
                    double c = ReadCost(seen[seen.Count - k + i], page[i]);
                    if (c < 0) fits = false; else sum += c;
                }

                if (fits && sum / k <= 0.55 && sum / k < bestCost) { best = k; bestCost = sum / k; }
            }
            return best;
        }

        internal static void AddPage(List<CrewRow> all, List<CrewRow> rows, int pageNo)
        {
            var seen = all.Where(r => !r.Empty && !r.Locked).ToList();
            if (seen.Count > 12) seen = seen.Skip(seen.Count - 12).ToList();
            var men = rows.Where(r => !r.Empty && !r.Locked).ToList();
            int overlap = OverlapLength(seen, men);
            int n = 0;
            foreach (var r in rows)
            {
                if (r.Empty || r.Locked) { r.ReadPage = pageNo; all.Add(r); continue; }
                int i = n++;
                if (i < overlap)
                {
                    var old = seen[seen.Count - overlap + i];
                    if (!r.Cut)
                    {
                        if (BetterName(old.Name, r.Name) && ReadCost(old, r) >= 0) old.Name = r.Name;
                        if (r.Attack >= 0) old.Attack = r.Attack;
                        if (r.Defense >= 0) old.Defense = r.Defense;
                    }
                    continue;
                }
                if (!r.Cut) { r.ReadPage = pageNo; all.Add(r); }
            }
        }

        internal static bool BetterName(string kept, string next)
        {
            string a = Parse.Key(kept), b = Parse.Key(next);
            Func<string, int> quotes = s => Regex.Matches(s ?? "", "[\"“”„″]").Count;
            if (quotes(next) == 2 && quotes(kept) < 2 && b.Length >= 3) return true;
            return b.Length > a.Length && (b.StartsWith(a) || WordsAlike(next, kept) || WholeNameSame(kept, next));
        }

        public static bool SameForPress(CrewRow a, CrewRow b)
        {
            if (a == null || b == null || a.Empty || a.Locked || b.Empty || b.Locked || a.Rarity < 0 || a.Rarity != b.Rarity) return false;
            if (!WholeNameSame(a.Name, b.Name) && !UnnamedLike(a, b)) return false;

            if (a.Attack >= 0 && b.Attack >= 0 && a.Attack != b.Attack) return false;
            if (a.Defense >= 0 && b.Defense >= 0 && a.Defense != b.Defense) return false;
            return true;
        }

        public static bool NamedLike(CrewRow a, CrewRow b)
        {
            if (a == null || b == null || a.Empty || a.Locked || b.Empty || b.Locked || a.Rarity < 0 || a.Rarity != b.Rarity) return false;

            if (WholeNameSame(a.Name, b.Name)) return true;
            if (a.Attack >= 0 && b.Attack >= 0 && a.Attack != b.Attack) return false;
            if (a.Defense >= 0 && b.Defense >= 0 && a.Defense != b.Defense) return false;
            return UnnamedLike(a, b) || EndsLike(a, b);
        }

        internal static bool EndsLike(CrewRow a, CrewRow b)
        {
            if (a.Rarity < 0 || a.Rarity != b.Rarity || a.Attack < 0 || a.Defense < 0 || a.Attack != b.Attack || a.Defense != b.Defense) return false;
            if (a.Cut || b.Cut || a.Training || b.Training || a.Stationed || b.Stationed) return false;
            var x = Words(a.Name); var y = Words(b.Name);
            if (x.Length < 2 || y.Length < 2) return false;
            return x[0] == y[0] && View.Distance(x[x.Length - 1], y[y.Length - 1]) <= 1;
        }

        static string[] Words(string name)
        {
            return (name ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(w => Parse.Key(w)).Where(w => w.Length >= 3).ToArray();
        }

        internal static bool UnnamedLike(CrewRow a, CrewRow b)
        {
            if (a == null || b == null || a.Empty || a.Locked || b.Empty || b.Locked || a.Cut || b.Cut || a.Training || b.Training || a.Stationed || b.Stationed) return false;
            if (a.Rarity < 0 || a.Rarity != b.Rarity || a.Rarity >= (int)Rarity.Epic) return false;
            if (a.Attack < 0 || a.Defense < 0 || a.Attack != b.Attack || a.Defense != b.Defense) return false;
            return Parse.Key(a.Name).Length < 3 && Parse.Key(b.Name).Length < 3;
        }

        internal static bool WholeNameSame(string a, string b, int minSlips = 1)
        {
            string x = Parse.Key(a), y = Parse.Key(b);
            if (x.Length < 3 || y.Length < 3) return false;
            if (x == y) return true;
            int shorter = Math.Min(x.Length, y.Length);
            if (shorter >= 10 && View.Distance(x.Substring(0, shorter), y.Substring(0, shorter)) <= 1) return true;
            return View.Distance(x, y) <= Math.Max(minSlips, shorter / 6);
        }

        static int RarityOf(string key)
        {
            for (int i = 0; i < RarityKeys.Length; i++) if (key == RarityKeys[i]) return i;
            return -1;
        }

        static readonly Regex HireWordRx = new Regex(@"^\s*(STREET|PROFESSIONAL|ELITE)\s*$", RegexOptions.IgnoreCase);

        static List<OcrLine> HireJoined(List<OcrLine> lines)
        {
            var all = new List<OcrLine>(lines);
            foreach (var w in lines.Where(l => HireWordRx.IsMatch(l.Text)))
            {
                int h = Math.Max(8, w.Box.Height);
                var price = lines.Where(l => l != w && l.Text.TrimStart().StartsWith("$") && Math.Abs(l.CenterY - w.CenterY) <= h
                                             && l.Box.X > w.Box.Right && l.Box.X - w.Box.Right < 4 * h)
                                 .OrderBy(l => l.Box.X).FirstOrDefault()

                            ?? lines.Where(l => l != w && l.Text.TrimStart().StartsWith("$") && l.Box.Y >= w.Box.Bottom - h / 2
                                                && l.Box.Y - w.Box.Bottom <= 3 * h && l.CenterX > w.Box.X - 3 * h && l.CenterX < w.Box.Right + 3 * h)
                                    .OrderBy(l => Math.Abs(l.CenterX - w.CenterX)).ThenBy(l => l.Box.Y).FirstOrDefault();
                if (price == null) continue;
                var box = Rectangle.Union(w.Box, price.Box);

                if (box.Height > h * 3 / 2) box = Rectangle.FromLTRB(box.Left, box.Top + box.Height / 2 - h / 2, box.Right, box.Top + box.Height / 2 + h / 2);
                all.Add(new OcrLine { Text = w.Text.Trim() + " " + price.Text.Trim(), Box = box });
            }
            return all;
        }

        internal static double EliteB8(string digits, double professional, double street)
        {
            digits = (digits ?? "").Trim();
            if (digits.Length < 2 || digits[digits.Length - 1] != '8') return -1;
            double v;
            if (!Parse.Money("$" + digits.Substring(0, digits.Length - 1).TrimEnd() + "B", out v) || v < 1e9) return -1;
            Func<double, bool> same = want => Math.Abs(v - want) <= want * 0.001;
            if (professional > 0) return same(professional * 1000) ? v : -1;
            if (street > 0) return same(street * 1e6) ? v : -1;
            return -1;
        }

        public CrewPage LookAtCrew()
        {
            using (var f = Capture())
            {
                var p = ReadCrew(f);
                return p.Page ? p : null;
            }
        }

        public CrewPage CrewPageSeen;

        public bool CrewRollsSeen;
        public CrewRow CrewRollingSeen;

        public CrewPage ReadCrew(Frame f, bool unlock = false)
        {
            var area = PageArea(f);
            int s = PageScale(f);
            var lines = Ocr.Read(f, area, s, Prep.None);
            var page = new CrewPage();

            var heads = HeaderWrapped(CountWrapped(lines));
            foreach (var l in heads)
            {
                int hn, hm;
                if (HenchmenCount(l.Text, out hn, out hm)) { page.Henchmen = hn; page.Slots = hm; page.Page = true; }
                string k = Parse.Key(l.Text);

                if (k.Contains("AUTOEQUIPBEST")) page.Page = true;
                if (k == "CREWIEADER") page.TopInSight = true;
                if (k.StartsWith("RECAIIAII") || k.Contains("HENCHMENSTATIONED") || k.Contains("STATIONEDHENCHMEN") || k.Contains("NOHENCHMEN")) page.FootInSight = true;

                if (k.Contains("STOPAIIAUTOROII") || k.Contains("AIIAUTOROIIS")) page.AutoRolls = true;
            }

            if (lines.Any(l => { string k = Parse.Key(l.Text); return k == "CIOSE" || k.Contains("INCREASEYOURIUCK"); })) page.Page = false;

            if (lines.Any(l => AutoRollWindowWord(Parse.Key(l.Text)))) { page.Page = false; page.AutoRollWindow = true; }
            if (!page.Page) return page;
            var anchors = new List<CrewRow>();

            var columns = lines.FirstOrDefault(l => Parse.Key(l.Text) == "HENCHMAN");
            var weaponHead = columns == null ? null : lines.FirstOrDefault(l => Parse.Key(l.Text) == "WEAPON" && l.Box.X > columns.Box.Right
                                                                                && Math.Abs(l.CenterY - columns.CenterY) <= Math.Max(8, columns.Box.Height));
            Func<OcrLine, bool> gearKind = l =>
            {
                int gh = Math.Max(8, l.Box.Height);
                return (weaponHead != null && l.Box.Right > weaponHead.Box.X)
                       || lines.Any(u => u != l && Regex.IsMatch(Parse.Key(u.Text), "^(WEAPON|ARMOR|VEHICIE)$") && u.Box.Y >= l.Box.Bottom - 3
                                         && u.Box.Y <= l.Box.Bottom + gh && Math.Abs(u.Box.X - l.Box.X) <= gh);
            };
            foreach (var l in lines)
            {
                string k = Parse.Key(l.Text);
                int r = RarityOf(k);

                if (r >= 0 && l.CenterX < area.X + area.Width * 0.45 && !gearKind(l)) anchors.Add(new CrewRow { Rarity = r, Anchor = l });
                else if (Regex.IsMatch(k, "^EMP[A-Z]{1,3}SIOT$")) anchors.Add(new CrewRow { Empty = true, Anchor = l });
                else if (Regex.IsMatch(k, "^[IL]OCKEDSIOT$")) anchors.Add(new CrewRow { Locked = true, Anchor = l });

                else if (k == "ROIIING" && l.CenterX < area.X + area.Width * 0.3) anchors.Add(new CrewRow { AutoRolling = true, Anchor = l });
            }

            foreach (var l in lines.Where(l => RollingWord(Parse.Key(l.Text), l.CenterX < area.X + area.Width * 0.45)))
                if (!anchors.Any(a => Math.Abs(a.Anchor.CenterY - l.CenterY) <= 3 * Math.Max(8, Math.Max(l.Box.Height, a.Anchor.Box.Height))))
                    anchors.Add(new CrewRow { AutoRolling = true, Anchor = l });
            AddMissedRows(f, area, s, lines, anchors);
            anchors = anchors.OrderBy(a => a.Anchor.CenterY).ToList();
            if (anchors.Any(a => a.Locked)) page.FootInSight = true;

            var header = lines.FirstOrDefault(l => Parse.Key(l.Text) == "HENCHMAN" && l.CenterX < area.X + area.Width * 0.45);
            for (int i = 0; i < anchors.Count; i++)
            {
                var row = anchors[i];
                var a = row.Anchor;
                int h = Math.Max(8, a.Box.Height);
                row.Top = i > 0 ? (anchors[i - 1].Anchor.CenterY + a.CenterY) / 2 : Math.Max(area.Top, a.CenterY - 5 * h);
                row.Bottom = i + 1 < anchors.Count ? (a.CenterY + anchors[i + 1].Anchor.CenterY) / 2 : Math.Min(area.Bottom, a.CenterY + 5 * h);
            }

            var dark = new List<OcrLine>();
            var slots = anchors.Where(r => r.Empty || (r.Locked && unlock)).ToList();
            if (slots.Count > 0)
            {
                int m = 2 * Math.Max(8, slots.Max(r => r.Anchor.Box.Height));
                var band = Rectangle.Intersect(Rectangle.FromLTRB(area.Left, slots.Min(r => r.Top) - m, area.Right, slots.Max(r => r.Bottom) + m), area);
                if (band.Width > 0 && band.Height > 0) dark = Ocr.Read(f, band, s, Prep.DarkText);
            }
            for (int i = 0; i < anchors.Count; i++)
            {
                var row = anchors[i];
                var a = row.Anchor;
                int h = Math.Max(8, a.Box.Height);
                Func<OcrLine, bool> inBand = l => l.CenterY >= row.Top && l.CenterY < row.Bottom;
                if (row.Empty)
                {
                    var eliteB8 = new List<Tuple<OcrLine, string>>();
                    foreach (var l in HireJoined(dark.Concat(lines).Where(inBand).ToList()))
                    {
                        var m = HireRx.Match(l.Text);
                        if (!m.Success) continue;
                        int t = Array.IndexOf(new[] { "STREET", "PROFESSIONAL", "ELITE" }, m.Groups[1].Value.ToUpperInvariant());
                        double price;

                        if (t < 0 || row.Hire[t] != null) continue;
                        if (!Parse.Money("$" + m.Groups[2].Value, out price) || price < 1000) { if (t == 2) eliteB8.Add(Tuple.Create(l, m.Groups[2].Value)); continue; }
                        row.Hire[t] = l; row.HirePrice[t] = price; row.HireGold[t] = GoldFace(f, l.Box);
                    }

                    if (row.Hire[2] == null)
                        foreach (var e in eliteB8)
                        {
                            double mended = EliteB8(e.Item2, row.HirePrice[1] > 0 ? row.HirePrice[1] : -1, row.HirePrice[0] > 0 ? row.HirePrice[0] : -1);
                            if (mended <= 0) continue;
                            row.Hire[2] = e.Item1; row.HirePrice[2] = mended; row.HireGold[2] = GoldFace(f, e.Item1.Box);
                            break;
                        }

                    for (int t = 1; t < 3; t++)
                        if (row.Hire[t] != null && row.Hire[t - 1] != null && row.HirePrice[t] <= row.HirePrice[t - 1]) { row.Hire[t] = null; row.HirePrice[t] = -1; }

                    row.Auto = lines.FirstOrDefault(l => inBand(l) && Parse.Key(l.Text) == "AUTOROII" && l.CenterX < area.X + area.Width / 2);
                    if (row.Auto != null) row.AutoActive = LightInk(f, row.Auto.Box);
                }
                else if (row.AutoRolling) ReadRoll(f, area, lines, row, inBand);
                else if (row.Locked)
                {

                    foreach (var l in unlock ? lines.Concat(dark).Where(inBand) : Enumerable.Empty<OcrLine>())
                    {
                        var m = UnlockRx.Match(l.Text);
                        double price;
                        if (!m.Success || !Parse.Money("$" + m.Groups[1].Value, out price) || price < 1000) continue;
                        row.Unlock = l; row.UnlockPrice = price; row.UnlockGold = GoldFace(f, l.Box);
                        break;
                    }
                }
                else
                {

                    var nameLines = lines.Where(l => l != a && l.Box.Bottom <= a.Box.Y + 3 && l.Box.Y >= a.Box.Y - 4 * h && Math.Abs(l.Box.X - a.Box.X) <= 2 * h
                                                     && Regex.IsMatch(l.Text, "[A-Za-z]{2}") && !Regex.IsMatch(Parse.Key(l.Text), "^(YOU|CREWIEADER|WITHYOU|HENCHMAN|POWER|TRAINING)$"))
                                         .OrderBy(l => l.Box.Y).ToList();
                    var name = new List<string>();
                    foreach (var nl in nameLines)
                    {

                        int gap = Math.Max(h, nl.Box.Height), slack = Math.Max(3, nl.Box.Height / 5);
                        string text = nl.Text.Trim();
                        var last = nl;
                        for (int piece = 0; piece < 3; piece++)
                        {
                            var end = last;
                            var glued = lines.Where(l => !nameLines.Contains(l) && l != end && !PowerOnly(l.Text) && Math.Abs(l.Box.Y - nl.Box.Y) <= slack
                                                         && l.Box.X >= end.Box.Right - 2 && l.Box.X - end.Box.Right <= gap).OrderBy(l => l.Box.X).FirstOrDefault();
                            var g = glued != null ? GluedRx.Match(glued.Text) : Match.Empty;
                            if (!g.Success) break;
                            text += " " + g.Groups[1].Value;
                            if (g.Groups[1].Index + g.Groups[1].Length < glued.Text.TrimEnd().Length) break;
                            last = glued;
                        }
                        name.Add(text);
                    }
                    row.Name = TidyName(Regex.Replace(string.Join(" ", name), @"\s+", " "));
                    if (nameLines.Count > 0 && CutName(row.Name)) row.Name = NameAgain(f, s, nameLines, a, h, row.Name);

                    var level = lines.Where(l => l != a && l.Box.Bottom <= a.Box.Y + 3 && l.Box.Y >= a.Box.Y - 4 * h && Math.Abs(l.Box.X - a.Box.X) <= 2 * h)
                                     .Select(l => TrainedRx.Match(l.Text)).FirstOrDefault(m => m.Success);
                    if (level != null) row.TrainLevel = int.Parse(level.Groups[1].Value);
                    row.Name = TrainedRx.Replace(row.Name, "").Trim();

                    if (header != null && a.Box.Y > header.CenterY && a.Box.Y - header.Box.Bottom < 3.5 * h) { row.Cut = true; row.Name = ""; }

                    var near = lines.Where(l => inBand(l) && l.Box.X > a.Box.X && Math.Abs(l.CenterY - a.CenterY) <= 4 * h).ToList();
                    row.Attack = PowerOf(f, s, near, "ATTACK");
                    row.Defense = PowerOf(f, s, near, "DEFENSE");
                    var inName = PowerInNameRx.Match(row.Name);
                    if (inName.Success)
                    {
                        int n;
                        bool attack = Parse.Key(inName.Groups[3].Value).StartsWith("A");
                        if (int.TryParse(Parse.Digits(inName.Groups[2].Value), out n))
                        {
                            if (attack && row.Attack < 0) row.Attack = n;
                            else if (!attack && row.Defense < 0) row.Defense = n;
                        }
                        row.Name = Regex.Replace(inName.Groups[1].Value.Trim(), @"\s+[A-Z@©•]$", "");
                    }
                    if (row.Attack < 0 && row.Defense >= 0) row.Attack = AttackAboveDefense(f, s, near);

                    if (!row.Cut && (row.Attack < 0 || row.Defense < 0))
                    {
                        var zone = Rectangle.Intersect(Rectangle.FromLTRB(a.Box.X + 6 * h, row.Top, a.Box.X + 24 * h, row.Bottom), area);
                        if (zone.Width > 8 && zone.Height > 8)
                        {
                            var power = new List<OcrLine>();
                            foreach (var t in new[] { Tuple.Create(s + 1, Prep.None), Tuple.Create(s, Prep.WhiteSoft),
                                                      Tuple.Create(s + 1, Prep.Contrast), Tuple.Create(s + 1, Prep.DarkText),
                                                      Tuple.Create(s + 2, Prep.None), Tuple.Create(s, Prep.WhiteText) })
                            {
                                var more = Ocr.Read(f, zone, t.Item1, t.Item2);
                                if (more.Count == 0) continue;
                                power.AddRange(more);
                                if (row.Attack < 0) row.Attack = PowerOf(f, t.Item1, more, "ATTACK");
                                if (row.Defense < 0) row.Defense = PowerOf(f, t.Item1, more, "DEFENSE");
                                if (row.Attack >= 0 && row.Defense >= 0) break;
                            }
                            if (row.Attack < 0 || row.Defense < 0)
                            {
                                int atkY = StatWordY(near, "A[TI][TI]ACK"), defY = StatWordY(near, "DEFENSE");
                                foreach (var l in power.Where(l => PowerRx.IsMatch(l.Text) && !StatWord(l.Text) && l.Box.X > a.Box.X + 6 * h).OrderBy(l => l.Box.Y))
                                {
                                    int n;
                                    if (!int.TryParse(Parse.Digits(PowerRx.Match(l.Text).Groups[1].Value), out n)) continue;
                                    bool nearDef = defY >= 0 && (atkY < 0 || Math.Abs(l.CenterY - defY) < Math.Abs(l.CenterY - atkY));
                                    if (nearDef ? row.Defense < 0 : row.Attack < 0) { if (nearDef) row.Defense = n; else row.Attack = n; }
                                    else if (row.Defense < 0) row.Defense = n;
                                    else if (row.Attack < 0) row.Attack = n;
                                    else break;
                                }
                            }
                        }
                    }
                    row.Dismiss = lines.FirstOrDefault(l => inBand(l) && l.CenterX > area.X + area.Width / 2 && Math.Abs(l.CenterY - a.CenterY) <= 3 * h
                                                            && (Parse.Key(l.Text) == "DISMISS" || Parse.Key(l.Text) == "CONFIRM" || Parse.Key(l.Text) == "CONFIRMDISMISS"));
                    row.Train = lines.FirstOrDefault(l => inBand(l) && l.CenterX > area.X + area.Width / 2 && Math.Abs(l.CenterY - a.CenterY) <= 3 * h
                                                          && Parse.Key(l.Text) == "TRAIN");

                    if (row.Dismiss == null) row.Dismiss = TrashIcon(f, area, row.Train, a, h);
                    if (row.Train != null) row.TrainGold = GoldText(f, row.Train.Box);
                    foreach (var l in lines.Where(l => inBand(l) && Math.Abs(l.CenterY - a.CenterY) <= 3 * h))
                    {
                        string k = Parse.Key(l.Text);
                        if ((k == "TRAINING" && l.CenterX < area.X + area.Width * 0.45) || k.StartsWith("TRAININGTO")) row.Training = true;
                        if ((k == "ONDUTY" && l.CenterX < area.X + area.Width * 0.45) || k.StartsWith("STATIONEDAT")) row.Stationed = true;
                        var left = TrainLeftRx.Match(l.Text);
                        if (left.Success)
                        {
                            row.Training = true;
                            int a1 = int.Parse(left.Groups[1].Value), a2 = int.Parse(left.Groups[2].Value);
                            row.TrainSecondsLeft = left.Groups[3].Success ? (a1 * 60 + a2) * 60 + int.Parse(left.Groups[3].Value) : a1 * 60 + a2;
                        }

                        if (RollingWord(k, l.CenterX < area.X + area.Width * 0.45)) row.AutoRolling = true;

                        if (k.StartsWith("STATIONEDAT")) row.StationedAt = Regex.Replace(l.Text, @"^.*?STATIONED\s*AT\s*", "", RegexOptions.IgnoreCase).Trim();
                        if (k == "RECAII" && l.CenterX > area.X + area.Width / 2) row.Recall = l;
                        if (k.StartsWith("RECOVERING")) row.Recovering = true;
                        if (k.StartsWith("HEAI") && l.CenterX > area.X + area.Width / 2 && row.Heal == null)
                        {
                            row.Heal = l;

                            int right = lines.Where(c => Math.Abs(c.CenterY - l.CenterY) < h && c.Box.X >= l.Box.X && c.Box.X - l.Box.Right < 3 * h
                                                         && (Parse.Key(c.Text).StartsWith("HEAI") || Parse.Key(c.Text) == "NOW" || Parse.Has(c.Text, "MIN")))
                                             .Select(c => c.Box.Right).DefaultIfEmpty(l.Box.Right).Max();
                            var digit = HealCostInk(f, Rectangle.FromLTRB(right + 2, l.CenterY - h, Math.Min(f.Width - 1, right + 6 * h), l.CenterY + h));
                            if (!digit.IsEmpty) row.HealCost = ReadLoneNumber(f, digit);
                        }
                    }
                    if (row.AutoRolling)
                    {

                        row.Dismiss = null; row.Train = null; row.TrainGold = false; row.Up = row.Down = Point.Empty;
                        ReadRoll(f, area, lines, row, inBand);
                    }
                    else
                    {
                        FindArrows(f, area, row, h);

                        if (row.Dismiss != null && row.Dismiss.Text == "DISMISS" && !row.Training && !row.Stationed)
                        {
                            bool active;
                            row.Auto = AutoIcon(f, area, row.Train, row.Dismiss, a, h, out active);
                            row.AutoActive = row.Auto != null && active;
                        }
                    }
                }
                if (row.AutoRolling) { page.Rolling.Add(row); page.AutoRolls = true; }
                else page.Rows.Add(row);
            }
            CrewPageSeen = page;
            return page;
        }

        static OcrLine TrashIcon(Frame f, Rectangle area, OcrLine train, OcrLine anchor, int h)
        {
            int x0 = train != null ? train.Box.Right + train.Box.Height : area.X + area.Width * 5 / 6;
            int x1 = area.Right - 2, y0 = Math.Max(area.Y, anchor.CenterY - 3 * h), y1 = Math.Min(area.Bottom - 1, anchor.CenterY + 3 * h);
            int l = int.MaxValue, t = int.MaxValue, r = -1, b = -1, n = 0;
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    var c = f.Pixel(x, y);

                    if (c.R < 140 || c.R - c.G < 70 || c.R - c.B < 80 || c.G > 120 || c.B > 110 || c.B > c.G + 10) continue;
                    l = Math.Min(l, x); r = Math.Max(r, x); t = Math.Min(t, y); b = Math.Max(b, y); n++;
                }
            if (r < 0) return null;
            int w = r - l + 1, hh = b - t + 1;
            if (w < h || hh < h || w > 6 * h || hh > 6 * h || n < w * hh / 5) return null;
            return new OcrLine { Text = "DISMISS", Box = Rectangle.FromLTRB(l, t, r + 1, b + 1) };
        }

        internal static bool ConfirmCovers(Frame f, OcrLine confirm, OcrLine d)
        {
            if (confirm == null || d == null) return false;
            if (confirm.Box.Contains(d.CenterX, d.CenterY)) return true;
            int r = Math.Max(2, d.Box.Height / 6), red = 0, n = 0;
            for (int y = d.CenterY - r; y <= d.CenterY + r; y++)
                for (int x = d.CenterX - r; x <= d.CenterX + r; x++)
                {
                    if (x < 0 || y < 0 || x >= f.Width || y >= f.Height) continue;
                    var c = f.Pixel(x, y); n++;
                    if (c.R >= 150 && c.R - c.G >= 70 && c.R - c.B >= 70) red++;
                }
            return n > 0 && red * 10 >= n * 8;
        }

        void AddMissedRows(Frame f, Rectangle area, int s, List<OcrLine> lines, List<CrewRow> anchors)
        {

            if (lines.Any(l => { string k = Parse.Key(l.Text); return k.StartsWith("DISMISS") && k.EndsWith("HENCHMAN") || k.Contains("AREYOUSURE"); })) return;

            var buttons = lines.Where(l => { string k = Parse.Key(l.Text); return (k == "DISMISS" || k == "CONFIRM" || k == "CONFIRMDISMISS" || k == "STOP" || k == "TRAIN") && l.CenterX > area.X + area.Width * 0.75; }).ToList();
            foreach (var d in buttons)
            {
                int h = Math.Max(8, d.Box.Height);
                if (anchors.Any(a => Math.Abs(a.Anchor.CenterY - d.CenterY) <= 3 * h)) continue;
                var band = Rectangle.Intersect(Rectangle.FromLTRB(area.X, d.CenterY - 4 * h, area.X + (int)(area.Width * 0.45), d.CenterY + 3 * h), area);
                if (band.Width < 20 || band.Height < 20) continue;
                foreach (var t in new[] { Tuple.Create(s, Prep.None), Tuple.Create(s + 1, Prep.None), Tuple.Create(s, Prep.Contrast) })
                {
                    var more = Ocr.Read(f, band, t.Item1, t.Item2);
                    var word = more.FirstOrDefault(l => RarityOf(Parse.Key(l.Text)) >= 0 && Math.Abs(l.CenterY - d.CenterY) <= 3 * h);
                    if (word == null) continue;

                    foreach (var l in more)
                        if (!lines.Any(x => { var o = Rectangle.Intersect(x.Box, l.Box); return o.Width * o.Height * 2 > Math.Min(x.Box.Width * x.Box.Height, l.Box.Width * l.Box.Height); }))
                            lines.Add(l);
                    anchors.Add(new CrewRow { Rarity = RarityOf(Parse.Key(word.Text)), Anchor = word });
                    break;
                }
            }
        }

        static int LookWaitMs(int look) { return look == 0 ? 250 : look < 3 ? 350 : 500; }

        static bool StatWord(string t) { return Regex.IsMatch(Parse.Key(t ?? ""), "A[TI][TI]ACK|DEFENSE"); }

        static bool PowerOnly(string t)
        {
            return StatWord(t) && Regex.IsMatch(t ?? "", @"^\s*(?:\S{0,2}\s+)?[+•]?\s*[0-9OoIlS,.]*\s*(?:A\s?[TI]|D\s?E\s?F)");
        }

        static int StatWordY(List<OcrLine> lines, string rx)
        {
            var l = lines.Where(x => Regex.IsMatch(Parse.Key(x.Text), rx)).OrderBy(x => x.Box.Y).FirstOrDefault();
            return l == null ? -1 : l.CenterY;
        }

        static int PowerOf(Frame f, int scale, List<OcrLine> near, string word)
        {

            string want = Parse.Key(word) == "ATTACK" ? "A[TI][TI]ACK" : Regex.Escape(Parse.Key(word));
            foreach (var l in near.Where(l => Regex.IsMatch(Parse.Key(l.Text), want)).OrderBy(l => l.Box.Y))
            {
                var m = PowerRx.Match(l.Text);
                int h = Math.Max(8, l.Box.Height);
                if (!m.Success)
                {
                    var num = near.Where(x => x != l && PowerRx.IsMatch(x.Text) && !Regex.IsMatch(Parse.Key(x.Text), "A[TI][TI]ACK|DEFENSE")
                                              && ((x.Box.Right <= l.Box.X + h && l.Box.X - x.Box.Right < 4 * h && x.Box.Bottom > l.Box.Y - h / 2 && x.Box.Y < l.Box.Bottom + h / 2)
                                                  || (x.Box.Bottom <= l.Box.Y + 3 && l.Box.Y - x.Box.Bottom < h && Math.Abs(x.Box.X - l.Box.X) < 3 * h)))
                                  .OrderBy(x => Math.Abs(x.CenterY - l.CenterY)).FirstOrDefault();
                    if (num != null) m = PowerRx.Match(num.Text);
                }
                if (!m.Success && f != null)
                {
                    var spot = Rectangle.Intersect(Rectangle.FromLTRB(l.Box.X - 4 * h, l.Box.Y - 2 * h, l.Box.Right, l.Box.Bottom + 2), new Rectangle(0, 0, f.Width, f.Height));
                    foreach (var t in new[] { Tuple.Create(scale + 1, Prep.None), Tuple.Create(scale + 1, Prep.WhiteText), Tuple.Create(scale, Prep.WhiteSoft), Tuple.Create(scale + 2, Prep.Contrast) })
                    {
                        if (spot.Width < 8 || spot.Height < 8) break;
                        m = PowerRx.Match(Ocr.ReadText(f, spot, t.Item1, t.Item2));
                        if (m.Success) break;
                    }
                }
                int n;
                if (m.Success && int.TryParse(Parse.Digits(m.Groups[1].Value), out n)) return n;
            }
            return -1;
        }

        static readonly Regex AttackLineRx = new Regex(@"\+\s*([0-9OoIlS]{1,5})\s*A\s?[TI]\s?[TI]\s?A\s?C\s?K", RegexOptions.IgnoreCase);

        static int AttackAboveDefense(Frame f, int scale, List<OcrLine> near)
        {
            if (f == null) return -1;
            var def = near.Where(l => Regex.IsMatch(Parse.Key(l.Text), "DEFENSE")).OrderBy(l => l.Box.Y).FirstOrDefault();
            if (def == null) return -1;
            int h = Math.Max(8, def.Box.Height);
            var spot = Rectangle.Intersect(Rectangle.FromLTRB(def.Box.X - 2 * h, def.Box.Y - 2 * h - h / 2, def.Box.Right + 2 * h, def.Box.Y + 1), new Rectangle(0, 0, f.Width, f.Height));
            if (spot.Width < 8 || spot.Height < 8) return -1;
            foreach (var t in new[] { Tuple.Create(scale + 1, Prep.None), Tuple.Create(scale + 1, Prep.WhiteText), Tuple.Create(scale + 2, Prep.Contrast), Tuple.Create(scale, Prep.Contrast) })
            {
                var m = AttackLineRx.Match(Ocr.ReadText(f, spot, t.Item1, t.Item2));
                int n;
                if (m.Success && int.TryParse(Parse.Digits(m.Groups[1].Value), out n)) return n;
            }
            return -1;
        }

        static bool IsArrowGlyph(Color c) { return c.R >= 165 && c.R <= 225 && Math.Abs(c.R - c.G) <= 8 && Math.Abs(c.G - c.B) <= 8; }
        static bool IsArrowFace(Color c) { return Math.Abs(c.R - 38) <= 7 && Math.Abs(c.G - 38) <= 7 && Math.Abs(c.B - 40) <= 7; }

        static void FindArrows(Frame f, Rectangle area, CrewRow row, int h)
        {
            var a = row.Anchor;
            int x0 = area.Left, x1 = a.Box.X - 2, y0 = Math.Max(row.Top, a.CenterY - 4 * h), y1 = Math.Min(row.Bottom, a.CenterY + 4 * h);
            if (x1 - x0 < 10 || y1 - y0 < 10) return;

            var seen = new bool[x1 - x0, y1 - y0];
            var glyphs = new List<Rectangle>();
            for (int y = y0; y < y1; y++)
                for (int x = x0; x < x1; x++)
                {
                    if (seen[x - x0, y - y0] || !IsArrowGlyph(f.Pixel(x, y))) continue;

                    int minX = x, maxX = x, minY = y, maxY = y, n = 0;
                    var stack = new Stack<Point>();
                    stack.Push(new Point(x, y)); seen[x - x0, y - y0] = true;
                    while (stack.Count > 0 && n < 2000)
                    {
                        var p = stack.Pop(); n++;
                        minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X); minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
                        for (int dy = -1; dy <= 1; dy++)
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                int nx = p.X + dx, ny = p.Y + dy;
                                if (nx < x0 || nx >= x1 || ny < y0 || ny >= y1 || seen[nx - x0, ny - y0]) continue;
                                if (!IsArrowGlyph(f.Pixel(nx, ny))) continue;
                                seen[nx - x0, ny - y0] = true; stack.Push(new Point(nx, ny));
                            }
                    }
                    var box = Rectangle.FromLTRB(minX, minY, maxX + 1, maxY + 1);

                    if (box.Width < 4 || box.Height < 4 || box.Width > 2 * h || box.Height > 2 * h) continue;
                    int face = 0, looks = 0;
                    foreach (var q in new[] { new Point(box.Left - 3, box.Top + box.Height / 2), new Point(box.Right + 2, box.Top + box.Height / 2),
                                              new Point(box.Left + box.Width / 2, box.Top - 3), new Point(box.Left + box.Width / 2, box.Bottom + 2) })
                    {
                        if (q.X < 0 || q.Y < 0 || q.X >= f.Width || q.Y >= f.Height) continue;
                        looks++;
                        if (IsArrowFace(f.Pixel(q.X, q.Y))) face++;
                    }
                    if (looks >= 3 && face >= 3) glyphs.Add(box);
                }
            glyphs = glyphs.OrderBy(g => g.Top).ToList();
            if (glyphs.Count == 2 && Math.Abs(glyphs[0].X - glyphs[1].X) <= 3)
            {
                if (Tip(f, glyphs[0]) != 1 && Tip(f, glyphs[1]) != -1)
                {
                    row.Up = Middle(glyphs[0]);
                    row.Down = Middle(glyphs[1]);
                }
            }
            else if (glyphs.Count == 1)
            {
                int t = Tip(f, glyphs[0]);
                if (t < 0) row.Up = Middle(glyphs[0]);
                else if (t > 0) row.Down = Middle(glyphs[0]);
            }
        }

        static Point Middle(Rectangle r) { return new Point(r.Left + r.Width / 2, r.Top + r.Height / 2); }

        static int Tip(Frame f, Rectangle g)
        {
            Func<int, int> width = y => { int n = 0; for (int x = g.Left; x < g.Right; x++) if (IsArrowGlyph(f.Pixel(x, y))) n++; return n; };
            int top = width(g.Top), bottom = width(g.Bottom - 1);
            return top < bottom ? -1 : bottom < top ? 1 : 0;
        }

        public CrewTotals CrewTotalsSeen;

        public int CrewCountSeen = -1;

        public CrewPage CrewListEnd(int dir)
        {
            CrewPage p = null;
            string sig = null;
            for (int roll = 0; roll < 4; roll++)
            {
                ScrollPage(0.62, 40 * dir);
                using (var f = Capture()) p = ReadCrew(f);
                if (!p.Page || (dir > 0 ? p.TopInSight : p.FootInSight)) return p;
                string now = string.Join(",", p.Rows.Select(r => r.Empty ? "empty" : r.Locked ? "locked" : r.Key));
                if (now == sig) return p;
                sig = now;
            }
            return p;
        }

        public int ReadCrewCount(Frame f, out int slots)
        {
            slots = -1;
            var area = PageArea(f);
            var strip = new Rectangle(area.X, area.Y, area.Width, Math.Max(60, area.Height / 4));
            int s = PageScale(f);
            foreach (var t in new[] { Tuple.Create(s + 1, Prep.None), Tuple.Create(s, Prep.WhiteText), Tuple.Create(s + 1, Prep.WhiteSoft), Tuple.Create(s, Prep.DarkText), Tuple.Create(s + 2, Prep.Contrast) })
                foreach (var l in CountWrapped(Ocr.Read(f, strip, t.Item1, t.Item2)))
                {
                    int n, m;
                    if (HenchmenCount(l.Text, out n, out m)) { slots = m; return n; }
                }

            var hench = Ocr.ReadWords(f, strip, s, Prep.None).Concat(Ocr.ReadWords(f, strip, s + 1, Prep.None))
                           .FirstOrDefault(w => Parse.Key(w.Text).StartsWith("HENCHMEN"));
            if (hench == null) return -1;
            int h = Math.Max(6, hench.Box.Height);
            var line = Rectangle.Intersect(Rectangle.FromLTRB(hench.Box.X - 9 * h, hench.Box.Y - h / 2, hench.Box.Right + h, hench.Box.Bottom + h / 2), strip);
            foreach (int z in new[] { 3, 2, 4, 5, 6 })
                foreach (var p in new[] { Prep.None, Prep.Contrast })
                {
                    int n, m;
                    if (HenchmenCount(Ocr.ReadText(f, line, z, p), out n, out m)) { slots = m; return n; }
                }

            var groups = InkGroups(f, Rectangle.FromLTRB(line.X, hench.Box.Y - h / 3, hench.Box.X - 1, hench.Box.Bottom + h / 3), Math.Max(3, h / 2));
            if (groups.Count >= 3)
            {
                int a = ReadLoneNumber(f, groups[groups.Count - 3]), b = ReadLoneNumber(f, groups[groups.Count - 1]);
                if (a > 0 && b >= a && b <= 99) { slots = b; return a; }
            }
            return -1;
        }

        static List<Rectangle> InkGroups(Frame f, Rectangle r, int gap)
        {
            r.Intersect(new Rectangle(0, 0, f.Width, f.Height));
            var groups = new List<Rectangle>();
            int start = -1, lastInk = -1;
            for (int x = r.Left; x <= r.Right; x++)
            {
                bool ink = false;
                if (x < r.Right)
                    for (int y = r.Top; y < r.Bottom && !ink; y++) { var c = f.Pixel(x, y); ink = (c.R + c.G + c.B) / 3 >= 150; }
                if (ink) { if (start < 0) start = x; lastInk = x; }
                else if (start >= 0 && x - lastInk >= gap)
                {
                    var box = InkBox(f, Rectangle.FromLTRB(start, r.Top, lastInk + 1, r.Bottom));
                    if (!box.IsEmpty) groups.Add(box);
                    start = -1;
                }
            }
            return groups;
        }

        public List<CrewRow> ReadWholeCrew(out CrewPage last, int step = 4)
        {
            last = null;
            CrewCountSeen = -1;
            CrewRollsSeen = false;
            CrewRollingSeen = null;
            var all = new List<CrewRow>();
            if (!OpenTab(Tab.Crew)) return all;
            CrewListEnd(1);
            string lastSig = null;
            for (int page = 0; page < 30; page++)
            {
                var f = Capture();
                try
                {
                    last = ReadCrew(f);

                    for (int again = 0; again < 2 && !last.Page; again++)
                    {
                        Wait(700);
                        f.Dispose();
                        f = Capture();
                        last = ReadCrew(f);
                    }
                    if (page == 0 && last.Page)
                    {
                        CrewTotalsSeen = ReadCrewTotals(f);

                        if (last.Henchmen < 0 && CrewTotalsSeen.Henchmen < 0)
                        {
                            int slots, n = ReadCrewCount(f, out slots);
                            if (n >= 0) { CrewTotalsSeen.Henchmen = n; CrewTotalsSeen.Slots = slots; }
                        }
                    }
                }
                finally { f.Dispose(); }
                if (!last.Page) break;
                if (last.Henchmen >= 0 && CrewCountSeen < 0) CrewCountSeen = last.Henchmen;

                if (last.AutoRolls) CrewRollsSeen = true;
                if (CrewRollingSeen == null) CrewRollingSeen = last.Rolling.FirstOrDefault(r => !r.Cut);

                AddPage(all, last.Rows, page);
                string sig = string.Join(",", last.Rows.Select(r => r.Empty ? "empty" : r.Locked ? "locked" : r.Key));
                if (sig == lastSig || last.Rows.Any(r => r.Locked)) break;
                lastSig = sig;
                ScrollPage(0.62, -ListNotches(step));
            }
            if (CrewCountSeen < 0 && CrewTotalsSeen != null && CrewTotalsSeen.Henchmen >= 0) CrewCountSeen = CrewTotalsSeen.Henchmen;

            return all.Where((r, i) => !(r.Empty || r.Locked) || all.FindIndex(x => x.Empty == r.Empty && x.Locked == r.Locked && (x.Empty || x.Locked)
                                                                                         && Math.Abs(x.Anchor.CenterY - r.Anchor.CenterY) < 4) == i).ToList();
        }

        public static List<CrewRow> MergeRepeats(List<CrewRow> rows, int over)
        {
            if (rows == null || over <= 0) return null;
            var pairs = new List<Tuple<int, int>>();
            for (int i = 0; i < rows.Count; i++)
                for (int j = i + 1; j < rows.Count; j++)
                {
                    CrewRow a = rows[i], b = rows[j];
                    if (a.Empty || a.Locked || b.Empty || b.Locked || a.ReadPage < 0 || b.ReadPage != a.ReadPage + 1) continue;
                    if (a.Rarity < 0 || a.Rarity != b.Rarity || a.Attack < 0 || a.Defense < 0 || a.Attack != b.Attack || a.Defense != b.Defense) continue;
                    pairs.Add(Tuple.Create(i, j));
                }
            if (pairs.Count != over || pairs.Select(p => p.Item1).Concat(pairs.Select(p => p.Item2)).Distinct().Count() != 2 * over) return null;
            var drop = new HashSet<int>(pairs.Select(p => p.Item2));
            return rows.Where((r, i) => !drop.Contains(i)).ToList();
        }

        static Rectangle HealCostInk(Frame f, Rectangle near)
        {
            near = Rectangle.Intersect(near, new Rectangle(0, 0, f.Width, f.Height));
            int y = near.Top + near.Height / 2;
            Func<Color, bool> dark = c => Math.Max(c.R, Math.Max(c.G, c.B)) < 40;
            Func<Color, bool> blue = c => c.B >= 120 && c.B - c.R >= 50;

            int left = -1, right = -1;
            for (int x = near.Left; x < near.Right; x++) if (dark(f.Pixel(x, y))) { if (left < 0) left = x; right = x; }
            if (left < 0 || right - left < near.Height / 2) return Rectangle.Empty;
            int top = y, bottom = y;
            while (top > near.Top && dark(f.Pixel(left + 1, top - 1))) top--;
            while (bottom < near.Bottom - 1 && dark(f.Pixel(left + 1, bottom + 1))) bottom++;
            int runStart = -1, lastX0 = -1, lastX1 = -1, gap = 0;
            for (int x = left + 2; x < right - 1; x++)
            {
                bool ink = false;
                for (int yy = top + 1; yy < bottom && !ink; yy++) ink = blue(f.Pixel(x, yy));
                if (ink) { if (runStart < 0) runStart = x; gap = 0; lastX0 = runStart; lastX1 = x; }
                else if (runStart >= 0 && ++gap > 2) runStart = -1;
            }
            if (lastX1 < 0) return Rectangle.Empty;
            int y0 = int.MaxValue, y1 = -1;
            for (int yy = top + 1; yy < bottom; yy++)
                for (int x = lastX0; x <= lastX1; x++) if (blue(f.Pixel(x, yy))) { y0 = Math.Min(y0, yy); y1 = Math.Max(y1, yy); }
            return y1 - y0 < 5 ? Rectangle.Empty : Rectangle.FromLTRB(lastX0, y0, lastX1 + 1, y1 + 1);
        }

        public int RecallFrom(string district)
        {
            int recalled = 0;
            string want = Parse.Key(district);
            for (int i = 0; i < 40; i++)
            {
                CrewPage page;
                var row = FindCrewRow(r => r.Stationed && r.Recall != null && View.Distance(Parse.Key(r.StationedAt), want) <= 2, out page, true);
                if (row == null) break;
                PressCrew(row.Recall, Parse.Key(row.Recall.Text) == "RECAII", "not a RECALL of a henchman stationed at " + district);
                Wait(900);
                recalled++;
            }
            return recalled;
        }

        public int HealRecovering(int stamina, int keep)
        {
            int spent = 0;
            for (int i = 0; i < 40; i++)
            {
                CrewPage page;
                var row = FindCrewRow(r => r.Recovering && r.Heal != null && r.HealCost > 0 && stamina - spent - r.HealCost >= keep, out page, true);
                if (row == null) break;
                PressCrew(row.Heal, Parse.Key(row.Heal.Text).StartsWith("HEAI"), "not a recovering henchman's HEAL");
                spent += row.HealCost;
                Wait(900);
            }
            return spent;
        }

        public CrewRow FindCrewRow(Func<CrewRow, bool> which, out CrewPage page) { return FindCrewRow(which, out page, false); }

        public CrewRow FindCrewRow(Func<CrewRow, bool> which, out CrewPage page, bool inSightFirst, bool unlock = false, bool rolling = false)
        {
            page = null;
            Func<CrewPage, List<CrewRow>> rows = pg => rolling ? pg.Rolling : pg.Rows;
            if (inSightFirst)
            {
                using (var f = Capture()) page = ReadCrew(f, unlock);
                if (!page.Page) return null;
                var seen = rows(page).FirstOrDefault(r => which(r) && !r.Cut);
                if (seen != null) return seen;
            }
            CrewListEnd(-1);
            string lastSig = null;
            int lateWaits = 0;
            for (int look = 0; look < 20; look++)
            {
                using (var f = Capture()) page = ReadCrew(f, unlock);
                if (!page.Page) return null;

                if (look == 0 && page.TopInSight && !page.Rows.Any(r => r.Locked) && WaitLateRoll(4000))
                    using (var f = Capture()) page = ReadCrew(f, unlock);
                if (!page.Page) return null;
                var hit = rows(page).FirstOrDefault(r => which(r) && !r.Cut);
                if (hit != null) return hit;
                string sig = string.Join(",", page.Rows.Select(r => r.Empty ? "empty" : r.Locked ? "locked" : r.Key));
                if (sig == lastSig)
                {

                    if (page.TopInSight || lateWaits >= 2 || !WaitLateRoll(5000)) return null;
                    lateWaits++;
                    lastSig = null;
                    continue;
                }
                lastSig = sig;
                ScrollPage(0.62, ListNotches(3));
            }
            return null;
        }

        public string CrewFootSeen()
        {
            var p = CrewListEnd(-1);
            if (p.Page && p.TopInSight && !p.Rows.Any(r => r.Locked)) WaitLateRoll(4000);
            using (var f = Capture()) p = ReadCrew(f);
            if (!p.Page) return "not the crew page";
            return p.Rows.Count == 0 ? "no rows read" : string.Join(", ", p.Rows.Select(r => r.Empty ? "an empty slot" : r.Locked ? "a locked slot" : r.ToString()));
        }

        void PressCrew(OcrLine b, bool ok, string why)
        {
            if (b == null || !ok) throw new NeverPressException(b != null ? b.Text : "crew button", why);
            NotDisconnected();
            if (OnlyNavigate) RefuseInCheck(b.Text);
            Click(b.CenterX, b.CenterY, 0);
        }

        public double UnlockCrewSlot(double cash, double keep, out double price)
        {
            price = -1;
            CrewPage page;
            var row = FindCrewRow(r => r.Locked, out page, false, true);
            if (row == null || row.Unlock == null) return -1;
            price = row.UnlockPrice;
            if (!row.UnlockGold || price > cash - Math.Max(0, keep)) return 0;
            int before = page.Slots, emptyBefore = page.Rows.Count(r => r.Empty);
            PressCrew(row.Unlock, UnlockRx.IsMatch(row.Unlock.Text) && row.UnlockGold && price <= cash - Math.Max(0, keep), "not the crew's UNLOCK SLOT within the cash on hand");

            for (int look = 0; look < 4; look++)
            {
                Wait(look == 0 ? 900 : 600);
                using (var f = Capture())
                {
                    if (look == 0) CheckForPurchasePrompt(Ocr.Read(f, PopupArea(f), PopupScale(f), Prep.None));
                    var after = ReadCrew(f);
                    if (!after.Page) continue;
                    if (before > 0 && after.Slots > 0) { if (after.Slots > before) return price; continue; }
                    if (after.Rows.Count(r => r.Empty) > emptyBefore) return price;
                }
            }
            return -1;
        }

        public CrewRow HireInto(CrewRow empty, CrewRoll roll, bool cheaperToo, double cash, double keep, out CrewRoll used, out double paid)
        {
            used = roll; paid = -1;
            HiredUnseen = null; HireBusy = false;
            int t = (int)roll;
            while (t >= 0 && (empty.Hire[t] == null || !empty.HireGold[t] || empty.HirePrice[t] > cash - Math.Max(0, keep))) t = cheaperToo ? t - 1 : -1;
            if (t < 0) return null;
            used = (CrewRoll)t; paid = empty.HirePrice[t];
            var b = empty.Hire[t];

            var before = CrewPageSeen != null ? CrewPageSeen.Rows.Where(r => !r.Empty && !r.Locked && r.Name.Length > 0).Select(r => r.Name).ToList() : new List<string>();
            PressCrew(b, HireRx.IsMatch(b.Text) && empty.HireGold[t] && paid <= cash - Math.Max(0, keep), "not a crew hire within the cash on hand");
            int y = empty.Anchor.CenterY;
            for (int look = 0; look < 5; look++)
            {
                Wait(LookWaitMs(look));
                using (var f = Capture())
                {

                    var got = ReadCrew(f).Rows.Where(r => !r.Empty && !r.Locked && r.Top <= y && r.Bottom > y).FirstOrDefault();
                    if (got != null && got.Rarity >= 0) return got;

                    if (look == 0) CheckForPurchasePrompt(Ocr.Read(f, PopupArea(f), PopupScale(f), Prep.None));
                }
            }

            using (var f = Capture()) if (CrewBusyToast(f)) { HireBusy = true; paid = -1; return null; }
            string named;
            int rarity;
            using (var f = Capture()) if (!HiredNotification(f, out named, out rarity) || before.Any(n => WindowNames(named, n))) return null;
            for (int look = 0; look < 8; look++)
            {
                Wait(LookWaitMs(look));
                using (var f = Capture())
                {
                    var got = ReadCrew(f).Rows.FirstOrDefault(r => !r.Empty && !r.Locked && !r.Cut && r.Rarity >= 0 && (rarity < 0 || r.Rarity == rarity) && WindowNames(named, r.Name));
                    if (got != null) return got;
                }
            }
            HiredUnseen = new CrewRow { Name = named, Rarity = rarity };
            return null;
        }

        public CrewRow HiredUnseen;

        public bool HireBusy;

        internal static bool CrewBusyToast(Frame f)
        {
            var area = Rectangle.FromLTRB(f.Width * 60 / 100, 0, f.Width - 2, f.Height / 4);
            int s = View.ScaleFor(f, 14, 14.0 * f.Height / 1009);
            foreach (var prep in new[] { Prep.None, Prep.WhiteText })
                if (Ocr.Read(f, area, s, prep).Any(l => Parse.Has(l.Text, "BEING UPDATED") || Parse.Has(l.Text, "TRY AGAIN SHORTLY"))) return true;
            return false;
        }

        static readonly Regex HiredRx = new Regex(@"^\s*H\s*[iIl1]\s*r\s*e\s*d\s+(.+?)\s*[\(\[{]\s*([A-Za-z ]{3,14}?)\s*[\)\]}]", RegexOptions.IgnoreCase);

        internal static bool HiredNotification(Frame f, out string name, out int rarity)
        {
            name = null; rarity = -1;
            var area = Rectangle.FromLTRB(f.Width * 60 / 100, 0, f.Width - 2, f.Height / 4);
            int s = View.ScaleFor(f, 14, 14.0 * f.Height / 1009);
            foreach (int z in new[] { s, s - 1, s + 1 }.Where(z => z >= 1))
            {
                var lines = Ocr.Read(f, area, z, Prep.None).Where(l => Regex.IsMatch(l.Text, "[A-Za-z]{2}")).OrderBy(l => l.Box.Y).ToList();
                if (lines.Count == 0) continue;
                var top = lines[0];
                int h = Math.Max(8, top.Box.Height);
                var m = HiredRx.Match(string.Join(" ", lines.Where(l => Math.Abs(l.CenterY - top.CenterY) <= h / 2).OrderBy(l => l.Box.X).Select(l => l.Text.Trim())));
                if (!m.Success) continue;
                string n = TidyName(Regex.Replace(m.Groups[1].Value, @"\s+", " ").Trim());
                if (Parse.Key(n).Length < 3) continue;
                if (name == null) name = n;

                rarity = RarityNear(Parse.Key(m.Groups[2].Value).Replace("Q", "R"));
                if (rarity >= 0) { name = n; return true; }
            }
            return name != null;
        }

        public string TrainWhy;

        public CrewRow EmptyRowNear(int y)
        {
            using (var f = Capture())
                return ReadCrew(f).Rows.Where(r => r.Empty).OrderBy(r => Math.Abs(r.Anchor.CenterY - y)).FirstOrDefault();
        }

        public sealed class DismissWindow
        {
            public int Rarity = -1;
            public string Name = "";
            public OcrLine Dismiss, Cancel;

            public bool Names(string rowName) { return WindowNames(Name, rowName); }
        }

        internal static bool WindowNames(string window, string row)
        {
            foreach (var w in NameForms(window))
                foreach (var r in NameForms(row))
                {
                    if (WholeNameSame(w, r, 2) || WordsAlike(w, r)) return true;
                    string x = Parse.Key(w), y = Parse.Key(r);
                    if (x.Length >= 14 && y.Length >= 14 && View.Distance(x.Substring(0, 14), y.Substring(0, 14)) <= 1) return true;
                }
            return false;
        }

        internal static bool WordsAlike(string window, string row)
        {
            var w = NameWords(window);
            var r = NameWords(row);
            if (w.Count < 2 || r.Count < 2 || !WordSame(w[0], r[0]) || !WordSame(w[w.Count - 1], r[r.Count - 1], 4)) return false;

            int n = r.Count, m = w.Count;
            var cost = new int[n + 1, m + 1];
            for (int i = n; i >= 0; i--)
                for (int j = m; j >= 0; j--)
                {
                    if (i == n) { cost[i, j] = m - j; continue; }
                    int best = 99;
                    if (j < m)
                    {
                        if (WordFits(r[i], w[j])) best = cost[i + 1, j + 1];
                        else if (i > 0 && i < n - 1 && Garbled(r[i], w[j])) best = cost[i + 1, j + 1] + 1;
                        best = Math.Min(best, cost[i, j + 1] + 1);
                    }
                    cost[i, j] = best;
                }
            return cost[0, 0] <= 1;
        }

        static List<string> NameWords(string s)
        {
            return Regex.Split(s ?? "", @"[\s""“”„″'’`]+").Select(Parse.Key).Where(k => k.Length > 0).ToList();
        }

        static bool WordSame(string a, string b, int slipFrom = 5)
        {
            a = FoldW(a); b = FoldW(b);
            return a == b || (Math.Min(a.Length, b.Length) >= slipFrom && View.Distance(a, b) <= 1);
        }

        static string FoldW(string k) { return k.Replace("OV", "W").Replace("OW", "W").Replace("V", "W"); }

        static bool WordFits(string rowWord, string windowWord)
        {
            return WordSame(rowWord, windowWord) || (rowWord.Length >= 2 && FoldW(windowWord).StartsWith(FoldW(rowWord)));
        }

        static bool Garbled(string rowWord, string windowWord)
        {
            return rowWord.Length >= 2 && windowWord.Length >= 2 && rowWord.Substring(0, 2) == windowWord.Substring(0, 2) && Math.Abs(rowWord.Length - windowWord.Length) <= 2;
        }

        static IEnumerable<string> NameForms(string s)
        {
            yield return s ?? "";
            yield return Regex.Replace(s ?? "", "[0-9]", " ");

            var quoted = Regex.Replace(s ?? "", @"^(\S+\s+)(?:II|Il|lI|ll|U)\s?(?=[A-Z][a-z])", "$1\"");
            if (quoted != (s ?? "")) yield return quoted;
        }

        static readonly Regex DismissQuestionRx = new Regex(@"^(.+?)\s+[iIl]s\s*an?\s+([A-Za-z\s]+?)\s+h\s*e\s*n\s*c\s*h\s*m\s*a\s*n\b", RegexOptions.IgnoreCase);

        internal static int RarityNear(string key)
        {
            int exact = RarityOf(key), near = -1;
            if (exact >= 0) return exact;
            for (int i = 0; i < RarityKeys.Length; i++)
            {
                if (RarityKeys[i].Length < 6 || View.Distance(key, RarityKeys[i]) > 1) continue;
                if (near >= 0) return -1;
                near = i;
            }
            return near;
        }

        internal static int TitleRarity(string key)
        {
            for (int i = RarityKeys.Length - 1; i >= 0; i--) if (key.Contains(RarityKeys[i])) return i;
            if (!key.StartsWith("DISMISS") || !key.EndsWith("HENCHMAN") || key.Length <= 15) return -1;
            return RarityNear(key.Substring(7, key.Length - 15));
        }

        static int UnderTitle(Frame f, int h, int titleHeights)
        {
            return Math.Max(titleHeights * h, (int)(titleHeights * 20 * Math.Min(f.Height / 1009.0, f.Width / 1790.0)));
        }

        internal static bool DismissButton(string key) { return key == "DISMISS" || key == "DISMISSHENCHMAN"; }

        internal static void ReadDismissQuestion(DismissWindow window, string question)
        {
            var match = DismissQuestionRx.Match(question);
            if (!match.Success) return;
            int rarity = RarityNear(Parse.Key(match.Groups[2].Value));

            if (rarity < 0 || rarity != window.Rarity) { window.Rarity = -1; return; }
            window.Name = match.Groups[1].Value.Trim();
        }

        public DismissWindow ReadDismissWindow(Frame f, string picked = null)
        {
            var lines = Ocr.Read(f, PopupArea(f), PopupScale(f), Prep.None);
            var title = lines.FirstOrDefault(l => { string k = Parse.Key(l.Text); return k.StartsWith("DISMISS") && k.EndsWith("HENCHMAN") && k.Length > 15; });
            if (title == null) return null;
            int h = Math.Max(8, title.Box.Height), scale = PopupScale(f);
            var w = new DismissWindow();
            w.Rarity = TitleRarity(Parse.Key(title.Text));

            if (w.Rarity < 0)
            {
                var line = Rectangle.Inflate(title.Box, h / 2, h / 4 + 2);
                foreach (int s in new[] { scale, scale - 1, scale + 1 }.Where(s => s >= 1))
                    if ((w.Rarity = TitleRarity(Parse.Key(Ocr.ReadText(f, line, s, Prep.Contrast)))) >= 0) break;
            }
            var buttons = lines;
            if (!lines.Any(l => DismissButton(Parse.Key(l.Text))) || !lines.Any(l => Parse.Key(l.Text) == "CANCEI"))
                buttons = lines.Concat(Ocr.Read(f, PopupArea(f), scale, Prep.WhiteText)).ToList();

            int reach = UnderTitle(f, h, 16);
            foreach (var b in buttons.Where(l => DismissButton(Parse.Key(l.Text)) && l.Box.Y > title.Box.Bottom && l.Box.Y < title.Box.Bottom + reach
                                               && l.Box.X > title.Box.X - 4 * h && l.Box.X < title.Box.Right + 4 * h).OrderBy(l => l.Box.Y))
            {
                var c = buttons.FirstOrDefault(l => Parse.Key(l.Text) == "CANCEI" && Math.Abs(l.CenterY - b.CenterY) <= h && l.Box.X > b.Box.Right
                                                  && l.Box.X - b.Box.Right < 4 * b.Box.Width);
                if (c == null) continue;
                w.Dismiss = b; w.Cancel = c;
                break;
            }
            if (w.Dismiss == null) return null;

            string q = string.Join(" ", lines.Where(l => l.Box.Y > title.Box.Bottom - 2 && l.Box.Bottom <= w.Dismiss.Box.Y + 2
                                                         && l.Box.X >= title.Box.X - h && l.Box.X < w.Cancel.Box.Right)
                                              .OrderBy(l => l.Box.Y).ThenBy(l => l.Box.X).Select(l => l.Text.Trim()));
            ReadDismissQuestion(w, q);

            if (w.Rarity >= 0 && (w.Name.Length == 0 || (picked != null && !w.Names(picked))))
            {
                var area = Rectangle.FromLTRB(title.Box.X - h, title.Box.Bottom, Math.Min(PopupArea(f).Right, w.Cancel.Box.Right + 3 * h), w.Dismiss.Box.Y);
                foreach (int s in new[] { scale - 1, scale + 1 }.Where(s => s >= 1))
                {
                    var again = new DismissWindow { Rarity = w.Rarity };
                    ReadDismissQuestion(again, string.Join(" ", Ocr.Read(f, area, s, Prep.None).OrderBy(l => l.Box.Y).ThenBy(l => l.Box.X).Select(l => l.Text.Trim())));
                    if (again.Rarity < 0) { w.Rarity = -1; break; }
                    if (again.Name.Length == 0) continue;
                    if (w.Name.Length == 0 || again.Names(picked)) w.Name = again.Name;
                    if (picked == null || w.Names(picked)) break;
                }
            }
            return w;
        }

        CrewRow RowToPress(CrewRow found, CrewRow picked)
        {
            if (found == null || picked == null || !NamedLike(found, picked)) return null;

            if ((found.Attack < 0 || found.Defense < 0) && !WholeNameSame(found.Name, picked.Name)) return null;
            for (int look = 0; look < 2; look++)
            {
                if (look > 0) Wait(300);
                CrewPage page;
                using (var f = Capture()) page = ReadCrew(f);
                var inSight = page.Rows.Where(r => !r.Cut && !r.Empty && !r.Locked).ToList();
                if (inSight.Count(r => NamedLike(r, picked)) > 1) continue;
                var hits = inSight.Where(r => SameForPress(r, found)).ToList();
                if (hits.Count == 1) return hits[0];
            }
            return null;
        }

        internal static bool MayAskAfterConfirm(CrewRow row, CrewRow picked)
        {
            if (picked.Rarity < 0 || picked.Rarity >= (int)Rarity.Mythic) return true;
            return row.TrainLevel > 0 || picked.TrainLevel > 0 || (picked.Rarity >= (int)Rarity.Epic && !picked.Recruit);
        }

        CrewRow TurnedBack(CrewRow found, CrewRow picked, CrewRow row, out CrewRow emptied)
        {
            emptied = null;
            for (int look = 0; look < 5; look++)
            {
                Wait(look == 0 ? 500 : 400);
                var again = RowToPress(found, picked);
                if (again != null && again.Dismiss != null && Parse.Key(again.Dismiss.Text) == "DISMISS") return again;
                if (again != null) continue;
                using (var f = Capture())
                    emptied = ReadCrew(f).Rows.FirstOrDefault(r => r.Empty && r.Top <= row.Anchor.CenterY + row.Anchor.Box.Height && r.Bottom > row.Anchor.CenterY - 3 * row.Anchor.Box.Height);
                if (emptied != null) return null;
            }
            return null;
        }

        public bool DismissHenchman(CrewRow found, CrewRow picked, bool sure, out CrewRow emptied)
        {
            emptied = null;
            var row = RowToPress(found, picked);
            if (row == null || row.Dismiss == null) return false;
            var d = row.Dismiss;
            string k = Parse.Key(d.Text);
            if (k != "DISMISS" && k != "CONFIRM" && k != "CONFIRMDISMISS") return false;
            OcrLine confirm = null;
            int h = Math.Max(8, d.Box.Height);

            Func<Frame, OcrLine> confirmOnSpot = f =>
            {
                var spot = Rectangle.Intersect(Rectangle.FromLTRB(d.Box.Left - 8 * h, d.Box.Top - h, d.Box.Right + 2 * h, d.Box.Bottom + h), new Rectangle(0, 0, f.Width, f.Height));
                var c = Ocr.Read(f, spot, PageScale(f), Prep.None).FirstOrDefault(l => Parse.Key(l.Text) == "CONFIRM" || Parse.Key(l.Text) == "CONFIRMDISMISS");
                return c != null && ConfirmCovers(f, c, d) ? c : null;
            };
            if (k == "CONFIRM" || k == "CONFIRMDISMISS")
            {

                var back = TurnedBack(found, picked, row, out emptied);
                if (back == null) return emptied != null;
                row = back; d = back.Dismiss; k = Parse.Key(d.Text); h = Math.Max(8, d.Box.Height);
            }
            if (k == "DISMISS")
            {
                PressCrew(d, true, "");

                int pressedAt = Environment.TickCount, lookMs = 0;
                for (int look = 0; look < 20 && confirm == null; look++)
                {
                    if (look > 0 && Environment.TickCount - pressedAt >= 800 + 6 * lookMs / look) break;
                    Wait(look == 0 ? 80 : 60);
                    int at = Environment.TickCount;
                    using (var f = Capture()) confirm = confirmOnSpot(f);
                    lookMs += Environment.TickCount - at;
                }
            }
            if (confirm == null)
            {

                DismissWindow win = null;
                for (int look = 0; look < 3 && win == null; look++)
                {
                    if (look > 0) Wait(300);
                    using (var f = Capture()) win = ReadDismissWindow(f, picked.Name);
                }
                if (win == null) return false;
                bool ours = win.Rarity >= 0 && win.Rarity == picked.Rarity && win.Names(picked.Name);
                if (!sure || !ours)
                {
                    if (!ours) Snapshot("crew dismiss question unreadable", 60);
                    log("Crew: the game asked \"are you sure?\" about " + (win.Name.Length > 0 ? win.Name : "a henchman") + " - pressed CANCEL"
                        + (ours ? "" : " (not the henchman the bot picked, " + picked.Name + ")"));
                    PressCrew(win.Cancel, Parse.Key(win.Cancel.Text) == "CANCEI", "not the dismiss window's CANCEL");
                    Wait(500);
                    return false;
                }
                PressCrew(win.Dismiss, sure && ours && DismissButton(Parse.Key(win.Dismiss.Text)), "not the dismiss window's DISMISS for the henchman picked");

                for (int look = 0; look < 6; look++)
                {
                    Wait(LookWaitMs(look));
                    using (var f = Capture())
                    {
                        emptied = ReadCrew(f).Rows.FirstOrDefault(r => r.Empty && r.Top <= row.Anchor.CenterY + row.Anchor.Box.Height && r.Bottom > row.Anchor.CenterY - 3 * row.Anchor.Box.Height);
                        if (emptied != null) { Snapshot("crew dismiss window", 0, true); return true; }
                        confirm = confirmOnSpot(f);
                        if (confirm != null) break;
                    }
                }
                if (confirm == null) return false;
                Snapshot("crew dismiss window then confirm", 0, true);
            }

            PressCrew(new OcrLine { Text = confirm.Text, Box = d.Box }, Math.Abs(confirm.CenterY - d.CenterY) <= Math.Max(8, d.Box.Height) && Math.Abs(confirm.CenterX - d.CenterX) <= 8 * Math.Max(8, d.Box.Height),
                      "a CONFIRM elsewhere than on the DISMISS just pressed");
            bool asked = false, mayAsk = MayAskAfterConfirm(row, picked);
            for (int look = 0; look < 6; look++)
            {
                Wait(LookWaitMs(look));
                using (var f = Capture())
                {
                    var p = ReadCrew(f);
                    emptied = p.Rows.FirstOrDefault(r => r.Empty && r.Top <= row.Anchor.CenterY + row.Anchor.Box.Height && r.Bottom > row.Anchor.CenterY - 3 * row.Anchor.Box.Height);
                    if (emptied != null) return true;

                    if (asked || (!mayAsk && look < 5)) continue;

                    var win = ReadDismissWindow(f, picked.Name);
                    if (win == null) continue;
                    asked = true;
                    bool ours = win.Rarity >= 0 && win.Rarity == picked.Rarity && win.Names(picked.Name);
                    Snapshot("crew dismiss window after confirm", 0, true);
                    if (!sure || !ours)
                    {
                        if (!ours) Snapshot("crew dismiss question unreadable", 60);
                        log("Crew: the game asked \"are you sure?\" about " + (win.Name.Length > 0 ? win.Name : "a henchman") + " - pressed CANCEL"
                            + (ours ? "" : " (not the henchman the bot picked, " + picked.Name + ")"));
                        PressCrew(win.Cancel, Parse.Key(win.Cancel.Text) == "CANCEI", "not the dismiss window's CANCEL");
                        Wait(500);
                        return false;
                    }
                    PressCrew(win.Dismiss, sure && ours && DismissButton(Parse.Key(win.Dismiss.Text)), "not the dismiss window's DISMISS for the henchman picked");
                }
            }
            return false;
        }

        public void CrewMoveUp(CrewRow row)
        {
            if (row.Up.IsEmpty || row.Anchor == null || row.Up.X >= row.Anchor.Box.X || row.Up.Y < row.Top || row.Up.Y >= row.Bottom)
                throw new NeverPressException("crew arrow", "no up arrow found on that row");
            NotDisconnected();
            if (OnlyNavigate) RefuseInCheck("crew arrow");
            Click(row.Up.X, row.Up.Y, 450);
        }

        public sealed class TrainWindow
        {
            public string Name = "";
            public int ToLevel = -1;
            public double Price = -1;
            public int Minutes = -1;
            public OcrLine Train, Cancel;
            public string Words = "";

            public bool Names(string rowName) { return WindowNames(Name, rowName) || FirstNameLost(Name, rowName); }
        }

        internal static bool FirstNameLost(string window, string row)
        {
            var m = Regex.Match(window ?? "", @"^\s*[^A-Za-z0-9\s""“”„″]*\s*([""“”„″][A-Za-z].*)$");
            if (!m.Success || Regex.IsMatch(window.TrimStart(), "^[A-Za-z]")) return false;
            string rest = m.Groups[1].Value, rowRest = Regex.Replace((row ?? "").Trim(), @"^[A-Za-z]+\s+(?=[""“”„″'•-])", "");
            if (rowRest == (row ?? "").Trim() || Parse.Key(rest).Length < 10) return false;
            var w = NameWords(rest); var r = NameWords(rowRest);
            return w.Count >= 2 && r.Count >= 2 && WordSame(w[w.Count - 1], r[r.Count - 1], 4) && (WholeNameSame(rest, rowRest, 2) || WordsAlike(rest, rowRest));
        }

        static readonly Regex TrainOfferRx = new Regex(@"trains\s*to\s*\+\s*([0-9]{1,2})\s*for\s*\$\s?([0-9OoIlS]{1,3}(?:[,.][0-9OoIlS]{1,3})*\s*[KMBT]?)", RegexOptions.IgnoreCase);
        static readonly Regex OffDutyRx = new Regex(@"off\s*duty\s*for\s*(?:([0-9]+)\s*h[a-z]*\s*)?(?:([0-9]+)\s*min)?", RegexOptions.IgnoreCase);

        static readonly Regex TrainTimeRx = new Regex(@"^\s*(?:([0-9]+)\s*h[a-z]*\s*)?(?:([0-9]+)\s*m[a-z]*)?\s*$", RegexOptions.IgnoreCase);
        static readonly Regex TrainLevelRx = new Regex(@"\+\s*([0-9]{1,2})");

        public TrainWindow ReadTrainWindow(Frame f)
        {
            var lines = Ocr.Read(f, PopupArea(f), PopupScale(f), Prep.None);
            var title = lines.FirstOrDefault(l => { string k = Parse.Key(l.Text); return k.StartsWith("TRAIN") && !k.StartsWith("TRAINING") && k.Length >= 9; });
            if (title == null) return null;
            int h = Math.Max(8, title.Box.Height);
            var w = new TrainWindow { Name = TrainedRx.Replace(Regex.Replace(title.Text.Trim(), @"^\S+\s+", ""), "").Trim() };

            Func<OcrLine, OcrLine, int> gap = (a, b) => b.Box.X > a.Box.Right ? b.Box.X - a.Box.Right : a.Box.X > b.Box.Right ? a.Box.X - b.Box.Right : -1;

            int reach = UnderTitle(f, h, 24);
            for (int pass = 0; pass < 2 && w.Train == null; pass++)
            {
                var buttons = pass == 0 ? lines
                    : lines.Concat(Ocr.Read(f, PopupArea(f), PopupScale(f), Prep.WhiteText)).Concat(Ocr.Read(f, PopupArea(f), PopupScale(f), Prep.DarkText)).ToList();
                foreach (var b in buttons.Where(l => Parse.Key(l.Text) == "TRAIN" && l.Box.Y > title.Box.Bottom && l.Box.Y < title.Box.Bottom + reach
                                                   && l.Box.X > title.Box.X - 4 * h && l.Box.X < title.Box.Right + 4 * h).OrderBy(l => l.Box.Y))
                {
                    var c = buttons.FirstOrDefault(l => Parse.Key(l.Text) == "CANCEI" && Math.Abs(l.CenterY - b.CenterY) <= h
                                                      && gap(b, l) >= 0 && gap(b, l) < 4 * b.Box.Width);
                    if (c == null) continue;
                    w.Train = b; w.Cancel = c;
                    break;
                }
            }
            if (w.Train == null) return w;
            int left = Math.Min(w.Train.Box.X, w.Cancel.Box.X), right = Math.Max(w.Train.Box.Right, w.Cancel.Box.Right);

            var inside = lines.Where(l => l.Box.Y > title.Box.Bottom - 2 && l.Box.Bottom <= w.Train.Box.Y + 2 && l.Box.X >= Math.Min(title.Box.X, left) - 3 * h
                                          && l.Box.X < Math.Max(title.Box.Right, right)).OrderBy(l => l.Box.Y).ThenBy(l => l.Box.X).ToList();
            string q = string.Join(" ", inside.Select(l => l.Text.Trim()));
            w.Words = q;
            var m = TrainOfferRx.Match(q);
            double price;

            if (m.Success && Parse.Money("$" + m.Groups[2].Value, out price) && price >= 1000) { w.ToLevel = int.Parse(m.Groups[1].Value); w.Price = price; }
            var d = OffDutyRx.Match(q);
            if (d.Success && (d.Groups[1].Success || d.Groups[2].Success))
                w.Minutes = (d.Groups[1].Success ? int.Parse(d.Groups[1].Value) * 60 : 0) + (d.Groups[2].Success ? int.Parse(d.Groups[2].Value) : 0);
            if (m.Success || Parse.Key(w.Name) != "HENCHMAN") return w;

            var cost = inside.FirstOrDefault(l => Parse.Key(l.Text) == "COST");
            var time = inside.FirstOrDefault(l => Parse.Key(l.Text) == "TIME");
            var name = inside.FirstOrDefault(l => l.Box.Y > title.Box.Bottom && (cost == null || l.Box.Bottom < cost.Box.Y) && !l.Text.TrimStart().StartsWith("+")
                                                  && l.Text.Count(char.IsLetter) >= 3);
            if (name != null)
            {

                var parts = inside.Where(l => Math.Abs(l.CenterY - name.CenterY) <= Math.Max(4, name.Box.Height / 2) && l.Text.Count(char.IsLetter) >= 1
                                              && !l.Text.TrimStart().StartsWith("+")).OrderBy(l => l.Box.X).ToList();
                w.Name = TrainedRx.Replace(string.Join(" ", parts.Select(l => l.Text.Trim())), "").Trim();
            }

            Func<OcrLine, OcrLine> under = label => label == null ? null : inside.Where(l => l != label && l.Box.Y > label.Box.Bottom - 2 && l.Box.Y < label.Box.Bottom + 3 * h
                                                                                         && Math.Abs(l.CenterX - label.CenterX) < 2 * h).OrderBy(l => l.Box.Y).FirstOrDefault();

            Func<string, double> money = s =>
            {
                double v;
                s = s.Trim();
                if (Parse.Money(s, out v) && v >= 1000) return v;
                var e8 = Regex.Match(s, @"^\$\s?([0-9]{1,3}(?:[.,][0-9]{1,2})?)8$");
                return e8.Success && Parse.Money("$" + e8.Groups[1].Value.Replace(',', '.') + "B", out v) && v >= 1e9 ? v : -1;
            };
            var p = under(cost);
            if (p != null) w.Price = money(p.Text);

            if (w.Price <= 0 && cost != null)
            {
                int hc = Math.Max(8, cost.Box.Height);
                var box = Rectangle.Intersect(new Rectangle(cost.CenterX - 6 * hc, cost.Box.Bottom + 1, 12 * hc, 5 * hc), new Rectangle(0, 0, f.Width, f.Height));
                foreach (var tryRead in new[] { Tuple.Create(Prep.None, 1), Tuple.Create(Prep.Contrast, 1), Tuple.Create(Prep.Contrast, 2), Tuple.Create(Prep.None, 2) })
                {
                    if (box.Width < 8 || box.Height < 8) break;
                    var v = Ocr.Read(f, box, tryRead.Item2, tryRead.Item1, true).Where(l => l.Text.Contains("$")).Select(l => money(l.Text)).FirstOrDefault(x => x > 0);
                    if (v > 0) { w.Price = v; break; }
                }
            }
            var t = under(time);
            var tm = t != null ? TrainTimeRx.Match(t.Text) : Match.Empty;
            if (tm.Success && (tm.Groups[1].Success || tm.Groups[2].Success))
                w.Minutes = (tm.Groups[1].Success ? int.Parse(tm.Groups[1].Value) * 60 : 0) + (tm.Groups[2].Success ? int.Parse(tm.Groups[2].Value) : 0);

            if (name != null)
            {
                int cx = (left + right) / 2, hn = Math.Max(8, name.Box.Height);
                int top = name.Box.Bottom + 2, bottom = cost != null ? cost.Box.Y - 2 : name.Box.Bottom + 4 * hn;
                Func<int, int, int> number = (x0, x1) =>
                {
                    var r = Rectangle.Intersect(Rectangle.FromLTRB(x0, top, x1, bottom), new Rectangle(0, 0, f.Width, f.Height));
                    if (r.Width < 8 || r.Height < 8) return -1;

                    foreach (int zoom in new[] { 3, 2 })
                        foreach (var prep in new[] { Prep.None, Prep.Contrast })
                            foreach (var l in Ocr.Read(f, r, zoom, prep))
                            {
                                var n = TrainLevelRx.Match(l.Text);
                                if (n.Success) return int.Parse(n.Groups[1].Value);
                            }
                    return -1;
                };
                int from = number(cx - 4 * hn, cx - hn * 4 / 5), to = number(cx + hn * 4 / 5, cx + 4 * hn);
                if (to < 0) to = number(cx + hn * 6 / 5, cx + 4 * hn);
                if (to >= 1 && to <= 10 && (from < 0 || to == from + 1)) w.ToLevel = to;
                else if (to < 0 && from >= 0 && from < 10) w.ToLevel = from + 1;
            }
            return w;
        }

        public int TrainHenchman(CrewRow found, CrewRow picked, double cash, double keep, out TrainWindow offer)
        {
            offer = null;
            var row = RowToPress(found, picked);

            TrainWhy = row == null ? "its row didn't read again" : row.Training && row.TrainSecondsLeft > 0 ? "it's training already" : row.Train == null ? "its TRAIN didn't read"
                     : !row.TrainGold ? "its TRAIN isn't gold" : null;
            if (TrainWhy != null) return -1;
            TrainWhy = "the training window didn't read";
            PressCrew(row.Train, Parse.Key(row.Train.Text) == "TRAIN" && row.TrainGold && !(row.Training && row.TrainSecondsLeft > 0), "not a row's gold TRAIN");
            for (int look = 0; look < 5 && (offer == null || offer.Train == null); look++)
            {
                Wait(look == 0 ? 500 : 300);
                using (var f = Capture())
                {
                    CheckForPurchasePrompt(Ocr.Read(f, PopupArea(f), PopupScale(f), Prep.None));
                    offer = ReadTrainWindow(f);
                }
            }
            if (offer == null || offer.Train == null)
            {
                Snapshot("crew train window not read", 60);
                if (offer != null && offer.Cancel != null) PressCrew(offer.Cancel, Parse.Key(offer.Cancel.Text) == "CANCEI", "not the training window's CANCEL");
                return -1;
            }

            for (int look = 0; look < 3 && offer.Price <= 0; look++)
            {
                Wait(400);
                TrainWindow again;
                using (var f = Capture()) again = ReadTrainWindow(f);
                if (again != null && again.Train != null && again.Price > 0) offer = again;
            }
            Snapshot("crew train window", 0, true);
            bool named = offer.Names(picked.Name);

            if (named && offer.ToLevel < 1 && row.TrainLevel > 0 && row.TrainLevel < 10) offer.ToLevel = row.TrainLevel + 1;
            bool ours = named && (offer.ToLevel <= 0 || offer.ToLevel <= 10 && (row.TrainLevel <= 0 || offer.ToLevel == row.TrainLevel + 1));
            bool afford = offer.Price > 0 && offer.Price <= cash - Math.Max(0, keep);
            if (!ours || !afford)
            {

                if (offer.Price <= 0) Snapshot("crew train window no price", 60);

                if (!ours)
                {
                    Snapshot("crew train window not ours", 60);
                    log("Crew: the training window named " + (offer.Name.Length > 0 ? offer.Name : "someone else")
                        + (offer.ToLevel > 0 ? " (to +" + offer.ToLevel + ")" : " (no level read)") + ", the bot meant " + picked.Name
                        + (row.TrainLevel > 0 ? " (at +" + row.TrainLevel + ")" : "") + (named ? " - the level didn't fit" : "") + " - pressed CANCEL");
                }
                PressCrew(offer.Cancel, Parse.Key(offer.Cancel.Text) == "CANCEI", "not the training window's CANCEL");
                Wait(500);
                return 0;
            }
            PressCrew(offer.Train, ours && afford && Parse.Key(offer.Train.Text) == "TRAIN", "not the training window's TRAIN for the henchman picked within the cash on hand");
            for (int look = 0; look < 5; look++)
            {
                Wait(look == 0 ? 600 : 400);
                using (var f = Capture())
                {
                    if (look == 0) CheckForPurchasePrompt(Ocr.Read(f, PopupArea(f), PopupScale(f), Prep.None));
                    if (ReadCrew(f).Rows.Any(r => r.Training && !r.Cut && r.Rarity == row.Rarity && WholeNameSame(r.Name, row.Name))) { Snapshot("crew training started", 0, true); return 1; }
                }
            }

            using (var f = Capture())
            {
                var still = ReadTrainWindow(f);
                if (still != null && still.Cancel != null) PressCrew(still.Cancel, Parse.Key(still.Cancel.Text) == "CANCEI", "not the training window's CANCEL");
            }
            Snapshot("crew training not started", 60);
            return -1;
        }

        static readonly string[] HeaderKeys = { "GEARSETS", "UNEQUIPAII", "AUTOSORTCREW", "AUTOEQUIPBEST" };

        internal OcrLine FindAutoSort(Frame f, out Rectangle box, out string why)
        {
            box = Rectangle.Empty;
            const string key = "AUTOSORTCREW";
            var lines = SplitHeaderLine(f, CrewHeaderRead(f), key);
            var button = lines.FirstOrDefault(l => Parse.Key(l.Text) == key);
            if (button == null)
            {
                why = lines.Any(l => Parse.Key(l.Text).Contains("AUTOSORT")) ? "its text ran together with other words" : "it wasn't found on the Crew page";
                return null;
            }
            var b = ButtonBox(f, button.Box);
            int h = Math.Max(8, button.Box.Height);
            if (b.IsEmpty || !b.Contains(button.Box) || b.Width > 2 * button.Box.Width + 4 * h || b.Height > 5 * h) { why = "its button's frame wasn't found around it"; return null; }
            var other = lines.FirstOrDefault(l => l != button && b.Contains(l.CenterX, l.CenterY));
            if (other != null) { why = "\"" + other.Text + "\" is inside its frame too"; return null; }
            foreach (var u in lines.Where(l => l != button && HeaderKeys.Any(k => k != key && Parse.Key(l.Text).Contains(k))))
            {
                var ub = ButtonBox(f, u.Box);
                if (u.Box.IntersectsWith(b) || (!ub.IsEmpty && ub.IntersectsWith(b))) { why = "\"" + u.Text + "\"'s box touches its box"; return null; }
            }
            box = b;
            why = null;
            return button;
        }

        public bool AutoSortCrew(out string why)
        {
            OcrLine button;
            Rectangle box;
            using (var f = Capture())
            {
                why = !ReadCrew(f).Page ? "not on the Crew page" : ReadDismissWindow(f) != null || ReadTrainWindow(f) != null ? "a window is open over the Crew page" : null;
                if (why != null) return false;
                button = FindAutoSort(f, out box, out why);
            }
            if (button == null) return false;
            Press(button.Text, box.X + box.Width / 2, box.Y + box.Height / 2, 800);
            Snapshot("crew auto sort", 0, true);
            return true;
        }
    }
}
