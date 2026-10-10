using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text.RegularExpressions;

namespace IdleMafiaBot
{

    sealed partial class Game
    {

        public sealed class AttackEntry
        {
            public string Name = "";
            public bool Won, Lost;
            public int WaitSeconds = -1;
            public OcrLine Again;
            public int Top, Bottom;
            public override string ToString()
            {
                return Name + (Won ? " won" : Lost ? " lost" : " ?") + (WaitSeconds >= 0 ? " wait " + WaitSeconds + "s" : "") + (Again != null ? " AGAIN" : "");
            }
        }

        static readonly Regex AttackedRx = new Regex(@"^\W{0,3}You\s*attac\s?ked\s*(.*)$", RegexOptions.IgnoreCase);
        static readonly Regex AttackedYouRx = new Regex(@"attac\s?ked\s*you\s*$", RegexOptions.IgnoreCase);
        static readonly Regex HistoryWaitRx = new Regex(@"WA\s?[I1l]T\s*\(?\s*([0-9OoIlS]{1,3})\s*s", RegexOptions.IgnoreCase);

        public List<AttackEntry> ReadAttackHistory(Frame f)
        {
            var p = PageArea(f);
            int left = p.Left + (int)(p.Width * 0.6);
            var area = new Rectangle(left, p.Top, p.Right - left, p.Height);
            var entries = new List<AttackEntry>();

            var lines = Ocr.Read(f, area, Math.Min(5, PageScale(f) + 1), Prep.None);
            var heads = lines.Where(l => AttackedRx.IsMatch(l.Text) || AttackedYouRx.IsMatch(l.Text)).OrderBy(l => l.Box.Y).ToList();
            for (int i = 0; i < heads.Count; i++)
            {
                var head = heads[i];
                if (!AttackedRx.IsMatch(head.Text)) continue;
                int h = Math.Max(8, head.Box.Height);
                var e = new AttackEntry { Top = head.Box.Y - h, Bottom = i + 1 < heads.Count ? heads[i + 1].Box.Y - h / 2 : head.Box.Bottom + 6 * h };
                e.Name = AttackedRx.Match(head.Text).Groups[1].Value.Trim();
                if (e.Name.Length == 0)
                {

                    var name = lines.Where(l => l != head && l.Box.X > head.Box.Right && l.Box.X - head.Box.Right < 3 * h && Math.Abs(l.CenterY - head.CenterY) <= h / 2)
                                    .OrderBy(l => l.Box.X).FirstOrDefault();
                    if (name != null) e.Name = name.Text.Trim();
                }
                foreach (var l in lines.Where(l => l != head && l.CenterY > head.Box.Y && l.CenterY < e.Bottom))
                {
                    string k = Parse.Key(l.Text);
                    if (k.Contains("WONTHEATTACK") || k.StartsWith("YOUWON")) e.Won = true;
                    else if (k.Contains("FOUGHTYOUOFF")) e.Lost = true;
                    var w = HistoryWaitRx.Match(l.Text);
                    int s;
                    if (w.Success && int.TryParse(Parse.Digits(w.Groups[1].Value), out s)) e.WaitSeconds = s;
                    if (k == "ATTACKAGAIN") e.Again = l;
                }
                entries.Add(e);
            }
            return entries;
        }

        public static bool SamePlayer(string a, string b)
        {
            string ka = Parse.Key(a), kb = Parse.Key(b);
            if (ka.Length < 3 || kb.Length < 3) return false;
            if (ka == kb) return true;
            if (Math.Min(ka.Length, kb.Length) >= 5 && (ka.StartsWith(kb) || kb.StartsWith(ka))) return true;
            int n = Math.Min(ka.Length, kb.Length);
            return n >= 6 && View.Distance(ka, kb) <= (n >= 10 ? 2 : 1);
        }

        public void AttackAgain(AttackEntry e) { Press(e.Again, 400); }
    }
}
