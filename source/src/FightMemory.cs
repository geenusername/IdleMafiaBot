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
        sealed class Fight { public DateTime At; public bool Won; public double Took = -1; }

        readonly Dictionary<string, List<Fight>> fights = new Dictionary<string, List<Fight>>();
        readonly Dictionary<string, string> names = new Dictionary<string, string>();
        readonly string file;

        public static readonly TimeSpan Keep = TimeSpan.FromDays(3), LeftAlone = TimeSpan.FromDays(2), BigWinFor = TimeSpan.FromDays(1);
        const int PerPlayer = 10;

        public FightMemory(string file) { this.file = file; Load(); }

        public int Count { get { return fights.Count; } }

        static string Key(string name) { return Parse.Key(name); }

        public void Add(string name, bool won, double took, DateTime at)
        {
            string k = Key(name);
            if (k.Length < 3) return;
            List<Fight> list;
            if (!fights.TryGetValue(k, out list)) fights[k] = list = new List<Fight>();
            names[k] = name.Trim();
            list.Add(new Fight { At = at, Won = won, Took = won ? took : -1 });
            if (list.Count > PerPlayer) list.RemoveRange(0, list.Count - PerPlayer);
            Save();
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
            var list = For(name);
            if (list == null) return -1;
            var wins = list.Where(f => f.Won && f.Took >= 0 && now - f.At < BigWinFor).ToList();
            return wins.Count == 0 ? -1 : wins.Max(f => f.Took);
        }

        public DateTime LeftAloneUntil(string name, DateTime now)
        {
            var list = For(name);
            if (list == null || list.Count < 2) return DateTime.MinValue;
            var last = list[list.Count - 1];
            var before = list[list.Count - 2];
            if (last.Won || before.Won || now - before.At > LeftAlone) return DateTime.MinValue;
            var until = last.At + LeftAlone;
            return until > now ? until : DateTime.MinValue;
        }

        public string Record(string name)
        {
            var list = For(name);
            if (list == null) return "";
            int w = list.Count(f => f.Won), l = list.Count - w;
            return w + " won, " + l + " lost";
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
                    if (p.Length < 4 || line.StartsWith("#")) continue;
                    if (!DateTime.TryParseExact(p[1], "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out at)) continue;
                    if (at < cutoff) continue;
                    if (!double.TryParse(p[3], NumberStyles.Float, CultureInfo.InvariantCulture, out took)) took = -1;
                    string k = Key(p[0]);
                    if (k.Length < 3) continue;
                    List<Fight> list;
                    if (!fights.TryGetValue(k, out list)) fights[k] = list = new List<Fight>();
                    names[k] = p[0];
                    list.Add(new Fight { At = at, Won = p[2] == "W", Took = took });
                }
                foreach (var list in fights.Values) list.Sort((a, b) => a.At.CompareTo(b.At));
            }
            catch (Exception) { fights.Clear(); names.Clear(); }
        }

        void Save()
        {
            try
            {
                var cutoff = DateTime.UtcNow - Keep;
                foreach (var k in fights.Keys.ToList())
                {
                    fights[k].RemoveAll(f => f.At < cutoff);
                    if (fights[k].Count == 0) { fights.Remove(k); names.Remove(k); }
                }
                var sb = new StringBuilder();
                sb.AppendLine("# Idle Mafia Bot's player memory for fights: the last 3 days, one fight a line (name, UTC time, Won/Lost, cash a win took). Delete it to forget.");
                foreach (var kv in fights)
                    foreach (var f in kv.Value)
                        sb.AppendLine(names[kv.Key] + "\t" + f.At.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + "\t" + (f.Won ? "W" : "L") + "\t"
                                      + f.Took.ToString("0", CultureInfo.InvariantCulture));
                File.WriteAllText(file, sb.ToString(), Encoding.UTF8);
            }
            catch (Exception) { }
        }
    }
}
