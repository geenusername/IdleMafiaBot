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

    enum TrainOrder { Best = 0, Evenly = 1 }

    enum OtherDeviceWait { Off = 0, M15 = 15, M30 = 30, H1 = 60, H2 = 120 }

    enum ThemeChoice { Mafia = 0, Halloween = 1, Auto = 2 }

    sealed class Settings
    {

        public bool Jobs = true;
        public JobMode JobMode = JobMode.BestXp;

        public string OneJob = "";

        public bool Heists = true;
        public bool HeistsFamilyOnly = false;

        public bool StartHeists = false;
        public string StartHeist = "Grand Vault of Caldera";

        public string HeistsToday = "";

        public bool WouldHelp(bool yourFamily, int xpPerHelp, double jobXpForHelp)
        {
            if (yourFamily) return true;
            return !HeistsFamilyOnly && xpPerHelp > 0 && xpPerHelp > jobXpForHelp;
        }

        public bool Fights = false;
        public bool WarTargetsOnly = true;

        public bool ScoutFights = true;

        public int FightWinChance = 90;

        public bool OnlyFightsSpendAll = false;

        public int FightLowHealth = 10;

        public bool WhaleHunt = false;

        public bool RememberPlayers = true;
        public long WhaleCash = 100000000;

        public static readonly long[] WhaleAmounts = { 100000, 1000000, 10000000, 100000000, 1000000000, 5000000000, 10000000000, 100000000000 };

        public int WhaleHits = 5;
        public const int WhaleHitsMax = 20;

        public int WhaleHitsNow { get { return Math.Max(1, Math.Min(WhaleHitsMax, WhaleHits)); } }

        public long BigPayerCash = 100000000;

        public int FightStaminaReserve = 15;

        public bool Bosses = true;

        public bool BossWeakestFirst = true;

        public static readonly string[] Retired = { "BossHitsPerRound", "FightsPerRound", "SpecificJob", "MasteryTarget", "CrewTarget", "CrewKeepGoing",
            "OpenNewCities", "HeistMinXp", "HeistsFamilyFirst", "FightMinHealth", "FightTarget", "BankMinutes", "BankWithFee", "WithdrawForPurchases",
            "OpsCollect", "OpsStart", "OpsName", "ShopMaxPrice", "ShopGold", "Collection", "CrewSort", "StaminaKeepPct", "IdleSeconds" };

        public bool Bank = true;

        public long BankAt = 0;
        public static readonly long[] BankAts = { 0, 1000000000, 5000000000, 10000000000, 50000000000, 100000000000, 500000000000 };

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

        public static readonly bool MarketRelistShown = true;

        public int MarketMinutes = 30;
        public const int MarketMinutesLeast = 30;

        public string MarketPending = "";

        public bool Properties = true;

        public int GoldLots = 0;

        public string PropertyOnly = "";
        public string PropertySeen = "";

        public bool SafehouseUpgrade = true;

        public bool CrewSlots = true;
        public bool CrewFill = true;
        public CrewRoll CrewRollType = CrewRoll.Street;

        public LuckRoll CrewLuckRoll = LuckRoll.Same;
        public bool CrewReroll = false;

        public int CrewRerollStats = 0;

        public bool RerollsFirst = false;

        public bool CrewAutoRoll = false;
        public long AutoRollLimit = 1000000000;

        public static readonly long[] AutoRollLimits = { 100000000, 500000000, 1000000000, 5000000000, 10000000000, 100000000000,
            500000000000, 1000000000000, 10000000000000, 50000000000000, 100000000000000 };

        public bool TrainCrew = true;
        public int TrainAtOnce = 1;

        public TrainOrder TrainOrder = TrainOrder.Best;

        public bool DefendTerritory = false;
        public bool DefendHeal = false;
        public const int MaxTraining = 2;
        public const int Perfect = 45;
        public const int PerfectForbidden = 70;
        public static readonly int[] StatsGoals = { 0, Perfect };
        public static readonly string[] StatsGoalNames = { "Off: stop at the best rarity", "On: until every one is perfect" };

        public static int PerfectFor(int rarity) { return rarity >= (int)Rarity.Forbidden ? PerfectForbidden : Perfect; }

        public static string StatsGoalText(int n) { return n >= Perfect ? Perfect + " attack and defense" : n + "+ attack and defense"; }

        public bool FamilyWatch = true;
        public bool GameStats = false;
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
        public OtherDeviceWait OtherDeviceRejoin = OtherDeviceWait.Off;

        public string DiscordLink = "";
        public bool DiscordProblems = true;
        public bool DiscordRarity = true;
        public bool DiscordProperty = true;
        public bool DiscordSummary = true;
        public bool DiscordLevel = false;
        public bool DiscordBoss = false;

        public string StatsLink = "";
        public string StatsMessage = "";

        public bool RemoteControl = false;
        public string RemoteMessage = "";

        public Focus Focus = Focus.Balanced;

        public string Account = "";
        public int LastLevel = -1;
        public int LastPrestige = -1;
        public string Totals = "";
        public string BossesDoneUntil = "";
        public string CratesLeft = "";
        public int LastAttackPower = -1;
        public int LastDefensePower = -1;
        public string BarMax = "";
        public bool WelcomeSeen = false;
        public string CheckOffered = "";
        public int CheckHintLevel = 0;
        public string FamilyDistrictsAt = "";
        public string RobloxUsers = "";
        public string JobsReadAt = "";
        public bool CrewGearOff = false;
        public bool ScreenFirst = false;
        public ThemeChoice Theme = ThemeChoice.Auto;

        public ThemeChoice ThemeShown
        {
            get { return Theme == ThemeChoice.Auto ? (DateTime.Now.Month == 10 ? ThemeChoice.Halloween : ThemeChoice.Mafia) : Theme; }
        }

        public static readonly string[] PcFields = { "WelcomeSeen", "WaitWhileBusy", "GiveFocusBack", "Rejoin", "OtherDeviceRejoin", "KeepAlive",
            "DiscordLink", "DiscordProblems", "DiscordRarity", "DiscordProperty", "DiscordSummary", "DiscordLevel", "DiscordBoss", "StatsLink", "RemoteControl", "RemoteMessage", "ScreenFirst", "Theme" };

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
                SetText(s, field, line.Substring(eq + 1).Trim());
            }
            if (s.PointsStat < 0 || s.PointsStat >= Game.StatNames.Length) s.PointsStat = 0;
            return s;
        }

        internal static bool SetText(Settings s, FieldInfo field, string v)
        {
            try
            {
                int n;
                long l;
                if (field.FieldType == typeof(bool)) field.SetValue(s, v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase));
                else if (field.FieldType == typeof(int)) { if (!int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) return false; field.SetValue(s, n); }
                else if (field.FieldType == typeof(long)) { if (!long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out l) || l < 0 || (l == 0 && field.Name != "BankAt")) return false; field.SetValue(s, l); }
                else if (field.FieldType == typeof(string)) field.SetValue(s, v);
                else if (field.FieldType.IsEnum)
                {
                    object e = Enum.Parse(field.FieldType, v);
                    if (!Enum.IsDefined(field.FieldType, e)) return false;
                    field.SetValue(s, e);
                }
                else return false;
                return true;
            }
            catch (Exception) { return false; }
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
