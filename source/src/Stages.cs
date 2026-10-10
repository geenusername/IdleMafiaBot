using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text.RegularExpressions;

namespace IdleMafiaBot
{

    public sealed class BossStage
    {
        public int Stage = -1;
        public string Boss = "";
        public int Health = -1, HealthMax = -1;
        public OcrLine Attack;
        public bool Cooldown;
        public bool Cleared;
        public bool DefeatedToday;
        public int Damage = -1;
        public int Highest = -1;
        public int ResetSeconds = -1;
        public int Tokens = -1;
        public int NeedLevel = -1;
        public OcrLine StagesTab;
        public OcrLine DefenseLine;
        public OcrLine Next;
        public System.Drawing.Point ListAt;
        public string Said = "";
        public override string ToString() { return "stage " + Stage + (Boss.Length > 0 ? ", " + Boss : "") + (HealthMax > 0 ? " (" + Health + " / " + HealthMax + ")" : ""); }
    }

    sealed partial class Game
    {
        static readonly Regex StageTitleRx = new Regex(@"^\s*STAGE\s*([0-9IlOoS|]{1,3})\s*$", RegexOptions.IgnoreCase);
        static readonly Regex StageAttackRx = new Regex(@"^\s*ATTACK\s*\(?\s*[1Il|]\s*STAMINA\s*\)?\s*$", RegexOptions.IgnoreCase);
        static readonly Regex StageHealthRx = new Regex(@"^\s*([0-9Oo][0-9Oo,.]*)\s*[/|Il]?\s*([0-9Oo][0-9Oo,.]*)\s*$");
        static readonly Regex StageDamageRx = new Regex(@"([0-9][0-9,.]*)\s*DAMAGE", RegexOptions.IgnoreCase);
        static readonly Regex StageHighestRx = new Regex(@"HIGHEST\s*STA[GC]E\s*([0-9IlOo]{1,3})", RegexOptions.IgnoreCase);
        static readonly Regex StageResetRx = new Regex(@"RESETS\s*IN\s*([0-9]{1,2})\s*:\s*([0-9]{2})\s*:\s*([0-9]{2})", RegexOptions.IgnoreCase);
        static readonly Regex StageTokensRx = new Regex(@"([0-9OoIl][0-9OoIl,]*)\s*BOSS\s*TOKENS", RegexOptions.IgnoreCase);
        static readonly Regex StageLevelRx = new Regex(@"LEVEL\s*([0-9IlOo]{2,3})\b", RegexOptions.IgnoreCase);

        public BossStage ReadBossStage(Frame f)
        {
            var area = PageArea(f);
            int s = PageScale(f);
            var lines = Ocr.Read(f, area, s, Prep.None);
            CheckForPurchasePrompt(lines);
            var st = ReadBossStage(lines, area);

            if (st != null && st.HealthMax < 0 && st.DefenseLine != null)
            {
                var d = st.DefenseLine;
                int dh = Math.Max(8, d.Box.Height);
                var spot = Rectangle.Intersect(Rectangle.FromLTRB(d.CenterX - 13 * dh, d.Box.Y - 7 * dh, d.CenterX + 13 * dh, d.Box.Y - 4 * dh), area);
                if (spot.Width > 10 && spot.Height > 5)
                    foreach (var t in new[] { Tuple.Create(s + 1, Prep.None), Tuple.Create(s, Prep.WhiteText), Tuple.Create(s + 1, Prep.WhiteText), Tuple.Create(s + 2, Prep.None) })
                    {
                        var hit = Ocr.Read(f, spot, t.Item1, t.Item2).FirstOrDefault(l => StageHealthRx.IsMatch(l.Text));
                        if (hit != null && StageHealth(hit.Text, st)) break;
                    }
            }
            return st;
        }

        static bool StageHealth(string text, BossStage st)
        {
            var m = StageHealthRx.Match(text ?? "");
            int cur, max;
            if (!m.Success || !int.TryParse(Parse.Digits(m.Groups[1].Value), out cur) || !int.TryParse(Parse.Digits(m.Groups[2].Value), out max) || max <= 0 || cur > max) return false;
            st.Health = cur; st.HealthMax = max;
            return true;
        }

        internal static BossStage ReadBossStage(List<OcrLine> lines, Rectangle area)
        {
            var st = new BossStage();
            string all = string.Join(" | ", lines.Select(l => l.Text));
            bool list = Parse.Has(all, "HIGHEST STAGE") || Parse.Has(all, "RESETS IN");

            int split = area.Left + (int)(area.Width * 0.70);
            var main = lines.Where(l => l.CenterX < split).ToList();
            var topRow = lines.Where(l => l.CenterY < area.Top + area.Height / 10).ToList();
            var title = main.Where(l => StageTitleRx.IsMatch(l.Text) && l.CenterY > area.Top + area.Height / 10).OrderByDescending(l => l.Box.Height).FirstOrDefault();
            int n;
            if (title != null && int.TryParse(Parse.Digits(StageTitleRx.Match(title.Text).Groups[1].Value), out n)) st.Stage = n;
            st.StagesTab = topRow.Where(l => Parse.Key(l.Text) == "STAGES").OrderBy(l => l.Box.Y).FirstOrDefault();
            if (st.Stage < 0 && !list) return null;
            st.Attack = main.FirstOrDefault(l => StageAttackRx.IsMatch(l.Text));
            st.Cooldown = main.Any(l => Parse.Has(l.Text, "COOLDOWN"));
            st.Cleared = lines.Any(l => Parse.Has(l.Text, "ALREADY CLEARED"));
            st.DefeatedToday = main.Any(l => Parse.Has(l.Text, "DEFEATED TODA"));
            var dm = main.Select(l => StageDamageRx.Match(l.Text)).FirstOrDefault(m => m.Success);
            if (dm != null && int.TryParse(Parse.Digits(dm.Groups[1].Value), out n)) st.Damage = n;
            var hm = lines.Select(l => StageHighestRx.Match(l.Text)).FirstOrDefault(m => m.Success);
            if (hm != null && int.TryParse(Parse.Digits(hm.Groups[1].Value), out n)) st.Highest = n;
            var rm = lines.Select(l => StageResetRx.Match(l.Text)).FirstOrDefault(m => m.Success);
            if (rm != null) st.ResetSeconds = int.Parse(rm.Groups[1].Value) * 3600 + int.Parse(rm.Groups[2].Value) * 60 + int.Parse(rm.Groups[3].Value);
            var tm = topRow.Select(l => StageTokensRx.Match(l.Text)).FirstOrDefault(m => m.Success);
            if (tm != null && int.TryParse(Parse.Digits(tm.Groups[1].Value), out n)) st.Tokens = n;

            int mid = title != null ? title.CenterX : area.Left + (split - area.Left) / 2;
            st.DefenseLine = main.Where(l => l.CenterX > mid && Parse.Key(l.Text).StartsWith("DEFENSE")).OrderBy(l => l.Box.Y).FirstOrDefault();
            if (st.DefenseLine != null)
            {
                var d = st.DefenseLine;
                int dh = Math.Max(8, d.Box.Height);

                var name = main.Where(l => l.Box.Bottom <= d.Box.Y && d.Box.Y - l.Box.Bottom < 14 * dh && Math.Abs(l.CenterX - d.CenterX) < 14 * dh
                                           && Regex.Matches(l.Text, "[a-z]").Count >= 3)
                               .OrderByDescending(l => l.Box.Bottom).FirstOrDefault();
                if (name != null) st.Boss = name.Text.Trim();

                var health = main.Where(l => Math.Abs(l.CenterX - d.CenterX) < 14 * dh && l.Box.Bottom < d.Box.Y - 2 * dh && (name == null || l.Box.Y > name.Box.Y)
                                             && StageHealthRx.IsMatch(l.Text)).OrderBy(l => l.Box.Y).FirstOrDefault();
                if (health != null) StageHealth(health.Text, st);
            }

            var lv = main.Where(l => !Parse.Has(l.Text, "FAMILY") && (Parse.Has(l.Text, "REQUIRE") || Parse.Has(l.Text, "REACH") || Parse.Has(l.Text, "UNLOCK")))
                         .Select(l => StageLevelRx.Match(l.Text)).FirstOrDefault(m => m.Success);
            if (lv != null && int.TryParse(Parse.Digits(lv.Groups[1].Value), out n)) st.NeedLevel = n;

            st.Next = lines.FirstOrDefault(l => l.CenterX >= split && Parse.Key(l.Text) == "NEXT");
            var head = lines.FirstOrDefault(l => l.CenterX >= split && StageHighestRx.IsMatch(l.Text));
            if (head != null) st.ListAt = new System.Drawing.Point(head.CenterX + head.Box.Width / 4, area.Top + (int)(area.Height * 0.62));
            st.Said = string.Join(" | ", main.Where(l => l.CenterY > area.Top + area.Height / 10).Select(l => l.Text.Trim()));
            return st;
        }

        public void StageAttack(BossStage st) { if (st != null && st.Attack != null && StageAttackRx.IsMatch(st.Attack.Text)) Press(st.Attack, 300); }

        public void StageNext(BossStage st) { if (st != null && st.Next != null && Parse.Key(st.Next.Text) == "NEXT") Press(st.Next, 1200); }

        public void StagesListScroll(BossStage st, int notches) { if (st != null && !st.ListAt.IsEmpty) Scroll(st.ListAt.X, st.ListAt.Y, notches); }

        public void StagesTab(BossStage st) { if (st != null && st.StagesTab != null) Press(st.StagesTab, 900); }
    }
}
