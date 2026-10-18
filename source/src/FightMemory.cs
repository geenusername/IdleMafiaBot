using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace IdleMafiaBot
{

    sealed class FightMemory
    {
        sealed class Fight { public DateTime At; public bool Won; public double Took = -1; public int Respect; public int Defense = -1, Attack = -1, OwnRoll = -1, OtherRoll = -1; }

        // What the fight window showed of a fight: the band the bot's attack was rolled in, the band
        // the other player's defense was rolled in, and the two rolls themselves. The middle of a
        // band is the figure the game used, which is exact - better than anything read off a profile.
        // Null when the window couldn't be read.
        public sealed class Rolls
        {
            public int OwnAttack = -1, TheirDefense = -1, OwnRoll = -1, TheirRoll = -1;
        }

        readonly Dictionary<string, List<Fight>> fights = new Dictionary<string, List<Fight>>();
        readonly Dictionary<string, string> names = new Dictionary<string, string>();
        readonly string file;

        readonly object gate = new object();

        public event Action Changed;

        public static readonly TimeSpan Keep = TimeSpan.FromDays(3), LeftAlone = TimeSpan.FromDays(2), BigWinFor = TimeSpan.FromDays(1);
        const int PerPlayer = 10;

        public FightMemory(string file) { this.file = file; if (file != null) Load(); }

        public int Count { get { lock (gate) return fights.Count; } }

        static string Key(string name) { return Parse.Key(name); }

        public void Add(string name, bool won, double took, int respect, int defense, Rolls roll, DateTime at)
        {
            string k = Key(name);
            if (k.Length < 3) return;
            if (roll != null && roll.TheirDefense > 0) defense = roll.TheirDefense;
            lock (gate)
            {
                List<Fight> list;
                if (!fights.TryGetValue(k, out list)) fights[k] = list = new List<Fight>();
                names[k] = name.Trim();
                list.Add(new Fight
                {
                    At = at, Won = won, Took = won ? took : -1, Respect = respect, Defense = defense,
                    Attack = roll == null ? -1 : roll.OwnAttack, OwnRoll = roll == null ? -1 : roll.OwnRoll, OtherRoll = roll == null ? -1 : roll.TheirRoll,
                });
                if (list.Count > PerPlayer) list.RemoveRange(0, list.Count - PerPlayer);
                Save();
            }
            var c = Changed;
            if (c != null) c();
        }

        public sealed class Payer
        {
            public string Name = "";
            public double Biggest = -1, Last = -1;
            public DateTime LastBig;
            public int Fights, Won, Lost;
        }

        public List<Payer> BigPayers(double atLeast, DateTime now, int most = 30)
        {
            var all = new List<Payer>();
            lock (gate)
                foreach (var kv in fights)
                {
                    var list = kv.Value.Where(f => now - f.At < Keep).ToList();
                    var wins = list.Where(f => f.Won && f.Took >= 0).ToList();
                    if (wins.Count == 0) continue;
                    double big = wins.Max(f => f.Took);
                    if (big < atLeast) continue;
                    all.Add(new Payer
                    {
                        Name = names[kv.Key], Biggest = big, Last = wins[wins.Count - 1].Took,
                        LastBig = wins.Where(f => f.Took >= atLeast).Max(f => f.At),
                        Fights = list.Count, Won = list.Count(f => f.Won), Lost = list.Count(f => !f.Won),
                    });
                }
            return all.OrderByDescending(p => p.Biggest).ThenByDescending(p => p.LastBig).Take(most).ToList();
        }

        public bool Forget(string name)
        {
            string k = Key(name);
            lock (gate)
            {
                if (!fights.Remove(k)) return false;
                names.Remove(k);
                Save();
            }
            var c = Changed;
            if (c != null) c();
            return true;
        }

        List<Fight> For(string name)
        {
            string k = Key(name);
            if (k.Length < 3) return null;
            List<Fight> list;
            if (fights.TryGetValue(k, out list)) return list;
            if (k.Length < 6) return null;

            var near = fights.Keys.FirstOrDefault(x => x.Length >= 6 && View.Distance(x, k) <= (Math.Min(x.Length, k.Length) >= 10 ? 2 : 1));
            return near != null ? fights[near] : null;
        }

        public double BigWin(string name, DateTime now)
        {
            lock (gate)
            {
                var list = For(name);
                if (list == null) return -1;
                var wins = list.Where(f => f.Won && f.Took >= 0 && now - f.At < BigWinFor).ToList();
                return wins.Count == 0 ? -1 : wins.Max(f => f.Took);
            }
        }

        public DateTime LeftAloneUntil(string name, DateTime now)
        {
            lock (gate) return LeftAloneUntilHere(name, now);
        }

        DateTime LeftAloneUntilHere(string name, DateTime now)
        {
            var list = For(name);
            if (list == null || list.Count < 2) return DateTime.MinValue;
            var last = list[list.Count - 1];
            var before = list[list.Count - 2];
            if (last.Won || before.Won || now - before.At > LeftAlone) return DateTime.MinValue;
            var until = last.At + LeftAlone;
            return until > now ? until : DateTime.MinValue;
        }

        // What the fights of the last few days were worth in respect, for the log and the window.
        public string RespectSummary(DateTime now)
        {
            lock (gate)
            {
                int count = 0, respect = 0;
                foreach (var list in fights.Values)
                    foreach (var f in list)
                        if (now - f.At < Keep) { count++; respect += f.Respect; }
                if (count == 0) return "no player fight written down yet.";
                return (respect >= 0 ? "+" : "") + respect.ToString("N0", CultureInfo.InvariantCulture) + " respect in " + count
                       + " fight" + (count == 1 ? "" : "s") + " over the last " + (int)Keep.TotalDays + " days.";
            }
        }

        public string Record(string name)
        {
            lock (gate)
            {
                var list = For(name);
                if (list == null) return "";
                int w = list.Count(f => f.Won), l = list.Count - w;
                return w + " won, " + l + " lost";
            }
        }

        void Load()
        {
            try
            {
                if (!File.Exists(file)) return;
                var cutoff = DateTime.UtcNow - Keep;
                foreach (var line in File.ReadAllLines(file, Encoding.UTF8))
                {
                    var p = line.Split('\t');
                    DateTime at;
                    double took;
                    int respect, defense, attack, ownRoll, otherRoll;
                    if (p.Length < 4 || line.StartsWith("#")) continue;
                    if (!DateTime.TryParseExact(p[1], "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out at)) continue;
                    if (at < cutoff) continue;
                    if (!double.TryParse(p[3], NumberStyles.Float, CultureInfo.InvariantCulture, out took)) took = -1;
                    respect = 0; defense = -1; attack = -1; ownRoll = -1; otherRoll = -1;
                    if (p.Length > 4) int.TryParse(p[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out respect);
                    if (p.Length > 5) int.TryParse(p[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out defense);
                    if (p.Length > 6) int.TryParse(p[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out attack);
                    if (p.Length > 7) int.TryParse(p[7], NumberStyles.Integer, CultureInfo.InvariantCulture, out ownRoll);
                    if (p.Length > 8) int.TryParse(p[8], NumberStyles.Integer, CultureInfo.InvariantCulture, out otherRoll);
                    string k = Key(p[0]);
                    if (k.Length < 3) continue;
                    List<Fight> list;
                    if (!fights.TryGetValue(k, out list)) fights[k] = list = new List<Fight>();
                    names[k] = p[0];
                    list.Add(new Fight { At = at, Won = p[2] == "W", Took = took, Respect = respect, Defense = defense, Attack = attack, OwnRoll = ownRoll, OtherRoll = otherRoll });
                }
                foreach (var list in fights.Values) list.Sort((a, b) => a.At.CompareTo(b.At));
            }
            catch (Exception) { fights.Clear(); names.Clear(); }
        }

        void Save()
        {
            if (file == null) return;
            try
            {
                var cutoff = DateTime.UtcNow - Keep;
                foreach (var k in fights.Keys.ToList())
                {
                    fights[k].RemoveAll(f => f.At < cutoff);
                    if (fights[k].Count == 0) { fights.Remove(k); names.Remove(k); }
                }
                var sb = new StringBuilder();
                sb.AppendLine("# Idle Mafia Bot's player memory for fights: the last 3 days, one fight a line (name, UTC time, Won/Lost, cash a win took, respect it was worth, the defense it was fought at, the attack it was fought with, and the two rolls the fight window showed). Delete it to forget.");
                foreach (var kv in fights)
                    foreach (var f in kv.Value)
                        sb.AppendLine(names[kv.Key] + "\t" + f.At.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + "\t" + (f.Won ? "W" : "L") + "\t"
                                      + f.Took.ToString("0", CultureInfo.InvariantCulture) + "\t" + f.Respect.ToString(CultureInfo.InvariantCulture) + "\t"
                                      + f.Defense.ToString(CultureInfo.InvariantCulture) + "\t" + f.Attack.ToString(CultureInfo.InvariantCulture) + "\t"
                                      + f.OwnRoll.ToString(CultureInfo.InvariantCulture) + "\t" + f.OtherRoll.ToString(CultureInfo.InvariantCulture));
                File.WriteAllText(file, sb.ToString(), Encoding.UTF8);
            }
            catch (Exception) { }
        }
    }
}
