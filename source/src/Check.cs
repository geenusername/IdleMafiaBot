using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text.RegularExpressions;

namespace IdleMafiaBot
{
    sealed partial class Game
    {

        public bool OnlyNavigate;

        static readonly HashSet<string> CloseKeys = Keys("DONE", "OK", "OKAY", "CONTINUE", "CLOSE", "NICE", "AWESOME", "COOL", "GOT IT");

        static bool IsMenuEntry(string text)
        {
            var one = new List<OcrLine> { new OcrLine { Text = text ?? "" } };
            return View.TabLabels.Values.Any(l => View.Label(one, l) != null);
        }

        static bool IsSubTab(string text)
        {
            string k = Parse.Key(text);
            return FamilyTabKeys.Contains(k) || k == "AUDITIOG" || k == "BROWSEFAMIIIES" || IsBackToMyFamily(new OcrLine { Text = text ?? "" });
        }

        internal bool NavigationPress(string text, int x, int y)
        {
            var v = view;
            bool known = v != null && v.Problem == null;
            if (IsMenuEntry(text) && known && x < v.Menu.Right) return true;
            if (IsSubTab(text) && known && y < v.Page.Top + v.Page.Height / 4) return true;
            return CloseKeys.Contains(Parse.Key(text));
        }

        readonly HashSet<string> refusedInCheck = new HashSet<string>();

        void RefuseInCheck(string text)
        {
            if (refusedInCheck.Add(Parse.Key(text))) log("The check didn't press \"" + (text ?? "").Trim() + "\": it only opens tabs and reads");
            throw new NeverPressException(text, "the check only opens tabs");
        }

        public sealed class MenuRead { public List<Tab> Seen = new List<Tab>(); public bool Whole; public int Unread; }

        public MenuRead ReadWholeMenu()
        {
            var r = new MenuRead();
            var seen = new HashSet<Tab>();
            using (var f = Capture()) if (HideChat(f)) ForgetView();
            Dictionary<Tab, int> last = null;
            bool atTop = false;
            for (int step = 0; step < 16; step++)
            {
                View v;
                Dictionary<Tab, int> now;
                using (var f = Capture())
                {
                    v = ViewOf(f);
                    if (v.Problem != null || !v.ReadMenu(f)) return null;
                    int unread = MenuInSight(f, v);
                    now = v.MenuSignature();
                    if (atTop)
                    {

                        foreach (var kv in v.Tabs) if (!IsLockedEntry(f, v, kv.Value)) seen.Add(kv.Key);
                        r.Unread = Math.Max(r.Unread, unread);
                    }
                }
                bool moved = last == null || !View.SameMenu(last, now);
                last = now;
                if (!atTop)
                {

                    if (!moved) { atTop = true; step--; last = null; continue; }
                    Scroll(v.Menu.X + v.Menu.Width / 2, v.Menu.Y + v.Menu.Height / 2, 5);
                    ForgetView();
                    continue;
                }
                if (!moved) { r.Whole = true; break; }
                Scroll(v.Menu.X + v.Menu.Width / 2, v.Menu.Y + v.Menu.Height / 2, -5);
                ForgetView();
            }
            r.Seen = seen.OrderBy(t => (int)t).ToList();
            return r;
        }

        internal static int MenuInSight(Frame f, View v)
        {
            for (int i = 0; i < 3 && v.UnreadButtons(f) > 0 && v.ReadAgain(f, v.Strip); i++) { }
            return v.UnreadButtons(f);
        }

        public int ReadBankFee(Frame f, out string line)
        {
            line = "";
            var lines = Ocr.Read(f, PageArea(f), PageScale(f), Prep.None);
            var card = BankCard(lines);
            if (card == null) return -1;

            var words = lines.Where(l => l.Box.Bottom > card.Box.Y).OrderBy(l => l.Box.Y).ThenBy(l => l.Box.X).Select(l => l.Text.Trim()).ToList();

            int from = words.FindIndex(w => Parse.Has(w, "FEE") || w.Contains("%"));
            if (from >= 0) line = string.Join(" ", words.Skip(from));
            if (line.Length > 220) line = line.Substring(0, 220);
            return FeeIn(string.Join(" ", words));
        }

        internal static int FeeIn(string text)
        {
            if (string.IsNullOrEmpty(text)) return -1;
            foreach (Match m in Regex.Matches(text, @"(?<![0-9A-Za-z.,$])([0-9][0-9Oo]?)\s*%"))
            {
                int n;
                if (int.TryParse(Parse.Digits(m.Groups[1].Value), out n) && n > 0) return n;
            }
            if (NearIn(Parse.Key(text), "NODEPOSITFEENOBANKFEESPASS", 2)) return 0;
            if (Regex.IsMatch(text, @"\(\s*no\s+fee\s*\)", RegexOptions.IgnoreCase)) return 0;
            return -1;
        }

        static bool NearIn(string key, string want, int off)
        {
            if (key.Contains(want)) return true;
            for (int start = 0; start + want.Length - off <= key.Length; start++)
                for (int len = want.Length - off; len <= want.Length + off && start + len <= key.Length; len++)
                    if (View.Distance(key.Substring(start, len), want) <= off) return true;
            return false;
        }

        public sealed class JobsLook { public List<int> Cities = new List<int>(), Folded = new List<int>(); public int Cash = -1; }

        public JobsLook ReadJobsList(List<JobInfo> catalog)
        {
            var r = new JobsLook();
            JobsTop();
            MeasureNotch(catalog);
            string last = null;
            for (int page = 0; page < 40; page++)
            {
                string sig;
                using (var f = Capture()) sig = JobsPage(f, catalog, r);
                if (sig == last) break;
                last = sig;
                ScrollPage(0.54, -ListNotches(6));
            }
            JobsTop();
            return r;
        }

        public string JobsPage(Frame f, List<JobInfo> catalog, JobsLook r)
        {
            var lines = Ocr.Read(f, JobsArea(f), 2, Prep.None);
            foreach (var h in ReadCityHeaders(f, lines))
            {
                if (!r.Cities.Contains(h.Index)) r.Cities.Add(h.Index);
                if (h.Collapsed && !r.Folded.Contains(h.Index)) r.Folded.Add(h.Index);
            }

            if (r.Cash < 0 && CardsFromLines(f, lines, catalog, false).Count > 0)
                r.Cash = lines.Any(l => Regex.IsMatch(l.Text, @"\$\s?[0-9OoIlS]") && (Parse.Has(l.Text, "XP") || Parse.Has(l.Text, "ITEM"))) ? 1 : 0;
            return string.Join(",", lines.Select(l => l.Text + "@" + l.Box.Y));
        }

        public sealed class BossLook { public int Total, Open, Ready, Unsure, Lowest = -1, Next = -1; }

        public BossLook ReadBossList(int level)
        {
            BossesTop();
            var seen = new List<BossCard>();
            string last = null;
            for (int page = 0; page < 14; page++)
            {
                string sig;
                using (var f = Capture()) sig = BossPage(f, seen);
                if (sig == last) break;
                last = sig;
                BossesDown();
            }
            BossesTop();
            return SumBosses(seen, level);
        }

        public string BossPage(Frame f, List<BossCard> seen)
        {
            var cards = ReadBosses(f);
            foreach (var c in cards) if (!seen.Any(o => SameCard(o, c))) seen.Add(c);
            return string.Join(",", cards.Select(c => c.Name + c.Level));
        }

        public static BossLook SumBosses(List<BossCard> seen, int level)
        {
            var r = new BossLook { Total = seen.Count };
            var levels = seen.Where(c => c.Level > 0).Select(c => c.Level).ToList();
            if (level > 0 && levels.Any(l => l > level)) r.Next = levels.Where(l => l > level).Min();
            foreach (var c in seen)
            {
                string k = Parse.Key(c.ButtonText);
                bool locked = c.Level > 0 && level > 0 && c.Level > level || Regex.IsMatch(k, "^IEVEI[0-9OIS]");
                if (locked) continue;
                if (c.Level <= 0) r.Unsure++;
                r.Open++;
                if (k.Contains("FIGHT") || k.Contains("ATTACK")) r.Ready++;
            }
            if (levels.Count > 0 && r.Unsure == 0) r.Lowest = levels.Min();
            return r;
        }

        static bool SameCard(BossCard a, BossCard b)
        {
            string ka = Parse.Key(a.Name), kb = Parse.Key(b.Name);
            if (ka.Length >= 3 && kb.Length >= 3) return View.Distance(ka, kb) <= Math.Max(1, Math.Min(ka.Length, kb.Length) / 5);
            return a.Level > 0 && a.Level == b.Level && Math.Abs(a.ButtonX - b.ButtonX) <= 40;
        }

        public bool OffersOfflineOps(Frame f)
        {
            return Ocr.Read(f, PageArea(f), PageScale(f), Prep.None).Any(l => Parse.Key(l.Text).StartsWith("OFFIINEOPERATION"));
        }
    }
}
