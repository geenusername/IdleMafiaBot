using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text.RegularExpressions;

namespace IdleMafiaBot
{

    public sealed class HeistOffer
    {
        public string Name = "";
        public int Level = -1, Energy = -1, Helps = -1, Minutes = -1;
        public double Stake = -1;
        public OcrLine Start;
        public override string ToString()
        {
            return Name + " (stake " + SafehouseInfo.Money(Stake) + ", " + Energy + " energy, needs " + Helps + " helps" + (Minutes > 0 ? " in " + Minutes + " min" : "") + ")";
        }
    }

    sealed partial class Game
    {

        public static readonly string[] HeistNames = { "Corner Store Smash", "Armored Truck Ambush", "Museum After Dark", "Grand Vault of Caldera",
                                                       "The Sterling Bullion Run", "The Vault of the First Oath", "The Cistern Vault" };
        public static readonly int[] HeistLevels = { 12, 20, 35, 60, 105, 150, 174 };

        static readonly Regex StakeRx = new Regex(@"STAKE\s*\$\s?([0-9OoIl][0-9OoIl,.]*\s?[KMBT]?)", RegexOptions.IgnoreCase);
        static readonly Regex OfferEnergyRx = new Regex(@"([0-9OoIl]{1,3})\s*ENERGY", RegexOptions.IgnoreCase);
        static readonly Regex OfferHelpsRx = new Regex(@"NEEDS\s*([0-9OoIl]{1,3})\s*HELPS?", RegexOptions.IgnoreCase);
        static readonly Regex OfferLevelRx = new Regex(@"^\s*LEVEL\s*([0-9OoIl]{1,3})\s*$", RegexOptions.IgnoreCase);
        static readonly Regex OfferTimeRx = new Regex(@"(?:([0-9]{1,2})\s*hr)?\s*(?:([0-9]{1,2})\s*min)?", RegexOptions.IgnoreCase);

        public OcrLine NoActiveHeist(Frame f)
        {
            var area = PageArea(f);
            var lines = Ocr.Read(f, area, PageScale(f), Prep.None);
            if (!lines.Any(l => Parse.Has(l.Text, "NO ACTIVE HEIST"))) return null;
            return lines.FirstOrDefault(l => Parse.Key(l.Text) == "STARTAHEIST" && l.CenterX > area.Left + area.Width * 0.6 && l.CenterY < area.Top + area.Height * 0.4);
        }

        public void PressStartAHeist(OcrLine button)
        {
            if (button == null || Parse.Key(button.Text) != "STARTAHEIST") throw new NeverPressException(button == null ? "" : button.Text, "not the Heists page's START A HEIST");
            NotDisconnected();
            if (OnlyNavigate) RefuseInCheck(button.Text);
            Click(button.CenterX, button.CenterY, 1200);
        }

        public List<HeistOffer> ReadHeistOffers(Frame f, out OcrLine close)
        {
            var lines = Ocr.Read(f, PopupArea(f), PopupScale(f), Prep.None);
            CheckForPurchasePrompt(lines);
            return ReadHeistOffers(f, lines, out close);
        }

        internal static List<HeistOffer> ReadHeistOffers(Frame f, List<OcrLine> lines, out OcrLine close)
        {
            close = null;
            var title = lines.FirstOrDefault(l => Parse.Key(l.Text) == "STARTAHEIST");
            if (title == null || !lines.Any(l => Parse.Has(l.Text, "ONE HEIST AT A TIME") || Parse.Has(l.Text, "STAKE COMES BACK"))) return null;
            int th = Math.Max(8, title.Box.Height);
            close = lines.Where(l => Regex.IsMatch(l.Text.Trim(), "^[xX×]$") && l.CenterX > title.Box.Right && Math.Abs(l.CenterY - title.CenterY) < 2 * th).OrderByDescending(l => l.CenterX).FirstOrDefault();
            if (close == null)
            {

                int x0 = title.Box.Right + 4 * th, y0 = Math.Max(0, title.Box.Y - th), y1 = Math.Min(f.Height, title.Box.Bottom + th);
                int minX = int.MaxValue, maxX = -1, minY = int.MaxValue, maxY = -1;
                for (int y = y0; y < y1; y++)
                    for (int x = x0; x < f.Width; x++)
                    {
                        var c = f.Pixel(x, y);
                        if (c.R < 120 || c.G < 120 || c.B < 120 || Math.Max(c.R, Math.Max(c.G, c.B)) - Math.Min(c.R, Math.Min(c.G, c.B)) > 25) continue;
                        minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                    }
                if (maxX >= 0 && maxX - minX > th / 2 && maxY - minY > th / 2 && maxX - minX < 4 * th)
                    close = new OcrLine { Text = "X", Box = Rectangle.FromLTRB(minX, minY, maxX + 1, maxY + 1) };
            }
            var offers = new List<HeistOffer>();
            foreach (var stake in lines.Where(l => StakeRx.IsMatch(l.Text)).OrderBy(l => l.Box.Y))
            {
                int h = Math.Max(8, stake.Box.Height);
                var o = new HeistOffer();
                double v;
                if (Parse.Money("$" + StakeRx.Match(stake.Text).Groups[1].Value, out v)) o.Stake = v;

                var row = lines.Where(l => Math.Abs(l.CenterY - stake.CenterY) < h && l.Box.X >= stake.Box.X - h).ToList();
                string rowText = string.Join("  ", row.OrderBy(l => l.Box.X).Select(l => l.Text));
                int n;
                var em = OfferEnergyRx.Match(rowText);
                if (em.Success && int.TryParse(Parse.Digits(em.Groups[1].Value), out n)) o.Energy = n;
                var hm = OfferHelpsRx.Match(rowText);
                if (hm.Success && int.TryParse(Parse.Digits(hm.Groups[1].Value), out n)) o.Helps = n;
                var tm = Regex.Match(rowText, @"([0-9]{1,2})\s*hr(?:\s*([0-9]{1,2})\s*min)?|([0-9]{1,2})\s*min", RegexOptions.IgnoreCase);
                if (tm.Success) o.Minutes = tm.Groups[1].Success ? int.Parse(tm.Groups[1].Value) * 60 + (tm.Groups[2].Success ? int.Parse(tm.Groups[2].Value) : 0) : int.Parse(tm.Groups[3].Value);

                var above = lines.Where(l => l.Box.Bottom <= stake.Box.Y + h / 3 && stake.Box.Y - l.Box.Bottom < 2 * h).ToList();
                var name = above.Where(l => Math.Abs(l.Box.X - stake.Box.X) < 3 * h && Regex.Matches(l.Text, "[a-z]").Count >= 3).OrderByDescending(l => l.Box.Bottom).FirstOrDefault();
                if (name != null) o.Name = name.Text.Trim();
                var lv = above.Select(l => OfferLevelRx.Match(l.Text)).FirstOrDefault(m => m.Success);
                if (lv != null && int.TryParse(Parse.Digits(lv.Groups[1].Value), out n)) o.Level = n;

                int top = name != null ? name.Box.Y - h : stake.Box.Y - 2 * h, bottom = stake.Box.Bottom + 2 * h;
                o.Start = lines.FirstOrDefault(l => Parse.Key(l.Text) == "START" && l.Box.X > stake.Box.Right + 4 * h && l.CenterY > top && l.CenterY < bottom
                                                    && GoldIsh(f, l.Box.X - Math.Max(4, h / 2), l.CenterY));
                if (o.Name.Length > 0 && o.Stake > 0) offers.Add(o);
            }
            return offers;
        }

        public static bool SameHeist(string read, string want)
        {
            string a = Parse.Key(read), b = Parse.Key(want);
            if (a.Length < 5 || b.Length < 5) return false;
            return a == b || View.Distance(a, b) <= Math.Max(1, Math.Min(a.Length, b.Length) / 8);
        }

        public void PressHeistStart(HeistOffer o)
        {
            if (o == null || o.Start == null || Parse.Key(o.Start.Text) != "START") throw new NeverPressException(o == null || o.Start == null ? "" : o.Start.Text, "not a heist's START in the START A HEIST window");
            NotDisconnected();
            if (OnlyNavigate) RefuseInCheck(o.Start.Text);
            Click(o.Start.CenterX, o.Start.CenterY, 1500);
        }

        public void CloseHeistOffers(OcrLine close)
        {
            if (close != null) Press(close, 800);
        }
    }
}
