using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace IdleMafiaBot
{

    sealed class Preset
    {
        public string Name = "";

        public readonly List<KeyValuePair<string, string>> Values = new List<KeyValuePair<string, string>>();

        public int Newer;

        public const string Prefix = "IMB-PRESET-2:";
        public const int NameMax = 30;

        static readonly string[] Base2 = {
            "Jobs=True", "JobMode=BestXp", "OneJob=", "Heists=True", "HeistsFamilyOnly=False", "StartHeists=False", "StartHeist=Grand Vault of Caldera",
            "Fights=False", "WarTargetsOnly=True", "ScoutFights=True", "FightWinChance=90", "OnlyFightsSpendAll=False", "FightLowHealth=10",
            "WhaleHunt=False", "RememberPlayers=True", "WhaleCash=100000000", "WhaleHits=5", "BigPayerCash=100000000", "FightStaminaReserve=15",
            "Bosses=True", "BossWeakestFirst=True", "Bank=True", "BankAt=0", "Operations=True", "Playtime=True", "Contracts=True", "Crates=True",
            "Briefcases=True", "Events=True", "ShopBuy=True", "ShopWithdraw=True", "ShopCommon=True", "ShopUncommon=True", "ShopRare=True",
            "ShopEpic=True", "ShopLegendary=True", "ShopMythic=True", "ShopSecret=True", "ShopForbidden=True", "ShopBankMythic=True",
            "ShopBankSecret=True", "ShopBankForbidden=True", "EquipBest=True", "MarketRelist=False", "MarketMinutes=30", "Properties=True",
            "GoldLots=0", "PropertyOnly=", "PropertySeen=", "SafehouseUpgrade=True", "CrewSlots=True", "CrewFill=True", "CrewRollType=Street",
            "CrewLuckRoll=Same", "CrewReroll=False", "CrewRerollStats=0", "RerollsFirst=False", "CrewAutoRoll=False", "AutoRollLimit=1000000000",
            "TrainCrew=True", "TrainAtOnce=1", "TrainOrder=Best", "DefendTerritory=False", "DefendHeal=False", "FamilyWatch=True",
            "GameStats=False", "Takedown=True", "GiveStamina=True", "StaminaPerk=", "SpendPoints=True", "PointsStat=0" };

        static Dictionary<string, string> baseValues;
        static Dictionary<string, string> BaseValues()
        {
            if (baseValues == null)
            {
                var d = new Dictionary<string, string>();
                foreach (var line in Base2) { int eq = line.IndexOf('='); d[line.Substring(0, eq)] = line.Substring(eq + 1); }
                baseValues = d;
            }
            return baseValues;
        }
        const int TextMax = 64 * 1024;

        public static readonly string[] Remembered = { "HeistsToday", "CratesOpenedAt", "MarketPending", "StatsMessage", "Focus", "Account",
            "LastLevel", "LastPrestige", "CratesLeft", "BossesDoneUntil", "Totals", "LastAttackPower", "LastDefensePower", "BarMax", "CheckOffered", "CheckHintLevel", "JobsReadAt", "FamilyDistrictsAt", "CrewGearOff", "RobloxUsers" };

        public static IEnumerable<FieldInfo> Fields()
        {
            return typeof(Settings).GetFields(BindingFlags.Public | BindingFlags.Instance)
                .Where(f => Array.IndexOf(Settings.PcFields, f.Name) < 0 && Array.IndexOf(Remembered, f.Name) < 0);
        }

        static string Text(FieldInfo f, Settings s) { return Convert.ToString(f.GetValue(s), CultureInfo.InvariantCulture); }

        public static string CleanName(string name)
        {
            string n = Regex.Replace(name ?? "", @"\p{C}+", " ");
            n = Regex.Replace(n, @"\s+", " ").Trim();
            return n.Length > NameMax ? n.Substring(0, NameMax).Trim() : n;
        }

        public static Preset From(string name, Settings s)
        {
            var p = new Preset { Name = CleanName(name) };
            foreach (var f in Fields()) p.Values.Add(new KeyValuePair<string, string>(f.Name, Text(f, s)));
            return p;
        }

        public int ApplyTo(Settings s)
        {
            int skipped = 0;
            foreach (var v in Values)
            {
                var f = typeof(Settings).GetField(v.Key);
                if (f == null || !Settings.SetText(s, f, v.Value)) skipped++;
            }
            if (s.PointsStat < 0 || s.PointsStat >= Game.StatNames.Length) s.PointsStat = 0;
            return skipped;
        }

        public bool Matches(Settings s)
        {
            foreach (var v in Values)
            {
                var f = typeof(Settings).GetField(v.Key);
                if (f != null && Text(f, s) != v.Value) return false;
            }
            return true;
        }

        public string Value(string field)
        {
            foreach (var v in Values) if (v.Key == field) return v.Value;
            return null;
        }

        public string Code()
        {
            var sb = new StringBuilder();
            sb.Append("Name=").Append(Name).Append('\n');
            var b = BaseValues();
            string was;
            foreach (var v in Values)
                if (!b.TryGetValue(v.Key, out was) || was != v.Value) sb.Append(v.Key).Append('=').Append(v.Value).Append('\n');
            byte[] raw = Encoding.UTF8.GetBytes(sb.ToString());
            byte[] packed;
            using (var ms = new MemoryStream())
            {
                using (var z = new DeflateStream(ms, CompressionLevel.Optimal, true)) z.Write(raw, 0, raw.Length);
                packed = ms.ToArray();
            }
            string body = Convert.ToBase64String(packed).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            return Prefix + body + "." + Check(body);
        }

        static string Check(string body)
        {
            using (var sha = SHA256.Create())
            {
                byte[] h = sha.ComputeHash(Encoding.ASCII.GetBytes(body));
                return string.Concat(h.Take(4).Select(b => b.ToString("x2")));
            }
        }

        static readonly Regex CodeIn = new Regex(@"IMB-PRESET-([12]):([A-Za-z0-9_-]{8,20000})\.([0-9a-fA-F]{8})");

        public static Preset Parse(string pasted, out string why)
        {
            why = null;
            var m = CodeIn.Match(Regex.Replace(pasted ?? "", @"\s+", ""));
            if (!m.Success) { why = (pasted ?? "").Contains("IMB-PRESET") ? "That code isn't complete. Copy the whole line." : "That's not a preset code. It starts with " + Prefix; return null; }
            bool sparse = m.Groups[1].Value == "2";
            string body = m.Groups[2].Value;
            if (!string.Equals(Check(body), m.Groups[3].Value, StringComparison.OrdinalIgnoreCase)) { why = "That code is broken or was changed. Copy it again."; return null; }
            string text;
            try
            {
                string b64 = body.Replace('-', '+').Replace('_', '/');
                b64 += new string('=', (4 - b64.Length % 4) % 4);
                byte[] packed = Convert.FromBase64String(b64);
                using (var z = new DeflateStream(new MemoryStream(packed), CompressionMode.Decompress))
                using (var outp = new MemoryStream())
                {
                    var buf = new byte[4096];
                    int n;
                    while ((n = z.Read(buf, 0, buf.Length)) > 0)
                    {
                        outp.Write(buf, 0, n);
                        if (outp.Length > TextMax) { why = "That code is too big to be a preset."; return null; }
                    }
                    text = Encoding.UTF8.GetString(outp.ToArray());
                }
            }
            catch (Exception) { why = "That code is broken. Copy it again."; return null; }
            var p = new Preset();
            var got = new Dictionary<string, string>();
            int lines = 0;
            foreach (var line in text.Split('\n'))
            {
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line.Substring(0, eq).Trim(), val = line.Substring(eq + 1).Trim();
                if (key == "Name") { p.Name = CleanName(val); continue; }
                lines++;
                if (!got.ContainsKey(key)) got[key] = val;
            }

            var b = BaseValues();
            int read = 0;
            foreach (var f in Fields())
            {
                string val;
                bool sent = got.TryGetValue(f.Name, out val), given = sent && Settings.SetText(new Settings(), f, val);
                if (given) read++;
                else
                {
                    if (sent) p.Newer++;
                    if (!(sparse && b.TryGetValue(f.Name, out val) && Settings.SetText(new Settings(), f, val))) continue;
                }
                p.Values.Add(new KeyValuePair<string, string>(f.Name, val));
            }
            foreach (var key in got.Keys)
                if (typeof(Settings).GetField(key) == null) p.Newer++;
            if (read == 0 && (lines > 0 || !sparse)) { why = "That code holds no settings this version knows."; return null; }
            if (p.Name.Length == 0) p.Name = "Shared preset";
            return p;
        }

        public static string FileIn(string dir) { return Path.Combine(dir, "presets.txt"); }

        public static List<Preset> Load(string dir)
        {
            var list = new List<Preset>();
            try
            {
                if (!File.Exists(FileIn(dir))) return list;
                foreach (var line in File.ReadAllLines(FileIn(dir)))
                {
                    string why;
                    var p = Parse(line, out why);
                    if (p != null && !list.Any(o => SameName(o.Name, p.Name))) list.Add(p);
                }
            }
            catch (Exception) { }
            return list;
        }

        public static void Save(string dir, List<Preset> list)
        {
            string file = FileIn(dir), tmp = file + ".tmp";
            File.WriteAllLines(tmp, list.Select(p => p.Code()));
            if (File.Exists(file)) File.Replace(tmp, file, null);
            else File.Move(tmp, file);
        }

        public static bool SameName(string a, string b) { return string.Equals(CleanName(a), CleanName(b), StringComparison.OrdinalIgnoreCase); }
    }
}
