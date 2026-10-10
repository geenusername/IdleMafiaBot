using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace IdleMafiaBot
{

    enum JobMode { BestXp = 0, Mastery = 1, OneJob = 2 }

    enum Focus { Balanced = 0, XP = 1, Money = 2, Family = 3 }

    sealed class Settings
    {

        public bool Jobs = true;
        public JobMode JobMode = JobMode.BestXp;

        public string OneJob = "";

        public bool Heists = true;
        public bool HeistsFamilyOnly = false;

        public bool StartHeists = false;
        public string StartHeist = "Grand Vault of Caldera";

        public bool WouldHelp(bool yourFamily, int xpPerHelp, double jobXpFor5)
        {
            if (yourFamily) return true;
            return !HeistsFamilyOnly && xpPerHelp > 0 && xpPerHelp > jobXpFor5;
        }

        public bool Fights = false;
        public bool WarTargetsOnly = true;

        public bool ScoutFights = false;

        public bool WhaleHunt = false;

        public bool RememberPlayers = true;
        public long WhaleCash = 100000000;

        public static readonly long[] WhaleAmounts = { 100000, 1000000, 10000000, 100000000, 1000000000, 5000000000, 10000000000, 100000000000 };

        public int FightStaminaReserve = 15;

        public bool Bosses = true;

        public static readonly string[] Retired = { "BossHitsPerRound", "FightsPerRound", "SpecificJob", "MasteryTarget", "CrewTarget", "CrewKeepGoing",
            "OpenNewCities", "HeistMinXp", "HeistsFamilyFirst", "FightMinHealth", "FightTarget", "BankMinutes", "BankWithFee", "WithdrawForPurchases",
            "OpsCollect", "OpsStart", "OpsName", "ShopMaxPrice", "ShopGold", "Collection", "CrewSort", "StaminaKeepPct", "IdleSeconds" };

        public bool Bank = true;

        public bool Operations = true;

        public bool Playtime = true;
        public bool Contracts = true;
        public bool Crates = true;

        public bool Briefcases = true;
        public bool Events = true;
        public string CratesOpenedAt = "";

        public bool ShopBuy = true;

        public bool ShopWithdraw = true;

        public bool ShopCommon = true, ShopUncommon = true, ShopRare = true, ShopEpic = true, ShopLegendary = true, ShopMythic = true,
                    ShopSecret = true, ShopForbidden = true;

        public bool ShopBankMythic = true, ShopBankSecret = true, ShopBankForbidden = true;

        public bool ShopAnyOn() { return ShopCommon || ShopUncommon || ShopRare || ShopEpic || ShopLegendary || ShopMythic || ShopSecret || ShopForbidden; }

        public bool ShopWants(int rarity)
        {
            var on = new[] { ShopCommon, ShopUncommon, ShopRare, ShopEpic, ShopLegendary, ShopMythic, ShopSecret, ShopForbidden };
            if (on.All(x => x)) return true;
            return rarity >= 0 && on[Math.Min(rarity, on.Length - 1)];
        }

        public bool ShopBankFor(int rarity)
        {
            if (!ShopWithdraw || rarity < (int)Rarity.Mythic || !ShopWants(rarity)) return false;
            return rarity == (int)Rarity.Mythic ? ShopBankMythic : rarity == (int)Rarity.Secret ? ShopBankSecret : ShopBankForbidden;
        }

        public bool EquipBest = true;

        public bool MarketRelist = false;

        public static readonly bool MarketRelistShown = false;
        public int MarketMinutes = 30;

        public string MarketPending = "";

        public bool Properties = true;

        public int GoldLots = 0;

        public bool SafehouseUpgrade = true;

        public bool CrewSlots = true;
        public bool CrewFill = true;
        public CrewRoll CrewRollType = CrewRoll.Street;
        public bool CrewReroll = false;

        public int CrewRerollStats = 0;

        public bool TrainCrew = true;
        public int TrainAtOnce = 1;
        public const int MaxTraining = 2;
        public const int Perfect = 45;
        public static readonly int[] StatsGoals = { 0, Perfect };
        public static readonly string[] StatsGoalNames = { "Off: stop at the best rarity", "On: until every one is 45/45" };

        public static string StatsGoalText(int n) { return n >= Perfect ? Perfect + " attack and defense" : n + "+ attack and defense"; }

        public bool FamilyWatch = true;
        public bool Takedown = true;
        public bool GiveStamina = true;
        public string StaminaPerk = "";

        public bool SpendPoints = true;
        public int PointsStat = 0;

        public bool WaitWhileBusy = true;

        public int IdleSeconds { get { return 15; } }
        public bool GiveFocusBack = true;

        public bool Rejoin = true;
        public bool KeepAlive = true;

        public string DiscordLink = "";
        public bool DiscordProblems = true;
        public bool DiscordRarity = true;
        public bool DiscordProperty = true;
        public bool DiscordSummary = true;
        public bool DiscordLevel = false;
        public bool DiscordBoss = false;

        public string StatsLink = "";
        public string StatsMessage = "";

        public Focus Focus = Focus.Balanced;

        public string Account = "";
        public int LastLevel = -1;
        public int LastAttackPower = -1;
        public int LastDefensePower = -1;
        public bool WelcomeSeen = false;
        public string CheckOffered = "";
        public int CheckHintLevel = 0;
        public string JobsReadAt = "";
        public bool CrewGearOff = false;
        public bool ScreenFirst = false;

        public static readonly string[] PcFields = { "WelcomeSeen", "WaitWhileBusy", "GiveFocusBack", "Rejoin", "KeepAlive",
            "DiscordLink", "DiscordProblems", "DiscordRarity", "DiscordProperty", "DiscordSummary", "DiscordLevel", "DiscordBoss", "StatsLink", "ScreenFirst" };

        public static readonly string[] LinkFields = { "DiscordLink", "StatsLink" };

        public void TakeAccountFields(Settings o)
        {
            foreach (var f in typeof(Settings).GetFields(BindingFlags.Public | BindingFlags.Instance))
                if (Array.IndexOf(PcFields, f.Name) < 0) f.SetValue(this, f.GetValue(o));
        }

        public string Unreadable { get; set; }

        public static Settings Load(string file)
        {
            var s = new Settings();
            string[] lines;
            for (int tries = 1; ; tries++)
            {
                try { if (!File.Exists(file)) return s; lines = File.ReadAllLines(file); break; }
                catch (Exception e)
                {
                    if (tries < 8) { System.Threading.Thread.Sleep(250); continue; }
                    string why = (e.Message ?? "").Replace(file, Path.GetFileName(file)).Trim().TrimEnd('.');
                    s.Unreadable = why.Length > 0 ? why : "it can't be opened";
                    return s;
                }
            }

            if (Array.IndexOf(lines, "OpsCollect=False") >= 0 && Array.IndexOf(lines, "OpsStart=False") >= 0 && !Array.Exists(lines, l => l.StartsWith("Operations=")))
                s.Operations = false;
            foreach (var line in lines)
            {
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                var field = typeof(Settings).GetField(line.Substring(0, eq).Trim());
                if (field == null) continue;
                string v = line.Substring(eq + 1).Trim();
                try
                {
                    int n;
                    if (field.FieldType == typeof(bool)) field.SetValue(s, v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase));
                    else if (field.FieldType == typeof(int)) { if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) field.SetValue(s, n); }
                    else if (field.FieldType == typeof(long)) { long l; if (long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out l) && l > 0) field.SetValue(s, l); }
                    else if (field.FieldType == typeof(string)) field.SetValue(s, v);
                    else if (field.FieldType.IsEnum)
                    {
                        object e = Enum.Parse(field.FieldType, v);
                        if (Enum.IsDefined(field.FieldType, e)) field.SetValue(s, e);
                    }
                }
                catch (Exception) { }
            }
            if (s.PointsStat < 0 || s.PointsStat >= Game.StatNames.Length) s.PointsStat = 0;
            return s;
        }

        static readonly object SaveLock = new object();

        public void Save(string file, bool accountCopy = false)
        {
            if (Unreadable != null) return;
            var sb = new StringBuilder();
            foreach (var f in typeof(Settings).GetFields(BindingFlags.Public | BindingFlags.Instance))
                sb.AppendLine(f.Name + "=" + (accountCopy && Array.IndexOf(LinkFields, f.Name) >= 0 ? "" : Convert.ToString(f.GetValue(this), CultureInfo.InvariantCulture)));
            lock (SaveLock)
            {

                string tmp = file + ".tmp";
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
                using (var w = new StreamWriter(fs))
                {
                    w.Write(sb.ToString());
                    w.Flush();
                    fs.Flush(true);
                }
                if (File.Exists(file)) File.Replace(tmp, file, null);
                else File.Move(tmp, file);
            }
        }
    }
}
