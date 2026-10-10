using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace IdleMafiaBot
{

    sealed class Profile
    {
        public DateTime Checked = DateTime.MinValue;
        public string Version = "", Window = "";

        public string Name = "";
        public int Level = -1, Xp = -1, XpNext = -1, SkillPoints = -1, EnergyMax = -1, StaminaMax = -1, HealthMax = -1;
        public double Cash = -1, Banked = -1;

        public string Tabs = "";
        public int MenuWhole = -1;

        public int SafeLevel = -1, SafeMax = -1, SafeBonus = -1, Respect = -1, AttackPower = -1, DefensePower = -1;
        public double Income = -1;
        public int Hired = -1, HireMax = -1, Gear = -1, GearMax = -1, Capacity = -1;

        public int InFamily = -1;
        public string Family = "", Rank = "";
        public int FamilyLevel = -1, Members = -1, MembersMax = -1;

        public int BankFee = -1;
        public string BankFeeFrom = "", BankLine = "";

        public int OpsSlots = -1, OpsOpen = -1, OpsPass = -1, OpsRunning = -1;
        public int OfflineOpsOffer = -1;

        public string CitiesSeen = "", CitiesFolded = "";
        public int JobCash = -1;

        public int Henchmen = -1, HenchSlots = -1, CrewAttack = -1, CrewDefense = -1;

        public int Bosses = -1, BossesOpen = -1, BossesReady = -1, BossLowest = -1, BossNext = -1;
        public int BossesUnsure = -1;

        public int Favors = -1, FamilyHeists = -1;

        public string Notes = "";

        public static readonly string[] Ranks = { "Boss", "Underboss", "Advisor", "Capo", "Soldier", "Associate" };

        public void Note(string what) { Notes = Notes.Length == 0 ? what : Notes + "; " + what; }

        public List<Tab> TabList
        {
            get
            {
                var list = new List<Tab>();
                foreach (var s in Tabs.Split(','))
                {
                    Tab t;
                    if (Enum.TryParse(s.Trim(), out t) && !list.Contains(t)) list.Add(t);
                }
                return list;
            }
        }

        public bool Has(Tab t) { return TabList.Contains(t); }

        public bool Lacks(Tab t) { return MenuWhole == 1 && !Has(t); }

        public int RankIndex { get { return Array.IndexOf(Ranks, Rank); } }

        public static string NormalRank(string read)
        {
            string k = Parse.Key(read);
            if (k.Length < 3) return "";
            string best = "";
            int bestD = int.MaxValue;
            foreach (var r in Ranks)
            {
                int d = View.Distance(k, Parse.Key(r));
                if (d < bestD) { bestD = d; best = r; }
            }
            return bestD <= Math.Max(1, best.Length / 4) ? best : "";
        }

        public static string FileFor(string dir, string name)
        {
            string safe = Regex.Replace(Regex.Replace((name ?? "").Trim(), @"\s+", ""), @"[^A-Za-z0-9_\-]", "_");
            if (safe.Trim('_').Length == 0) safe = "unknown";
            return Path.Combine(dir, "profile-" + safe + ".txt");
        }

        public static string Key(string name) { string f = Path.GetFileNameWithoutExtension(FileFor("", name)); return f.Substring("profile-".Length); }

        public void Save(string file)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# What Idle Mafia Bot's \"Check my game\" read from this account: it only opened tabs and rolled lists.");
            sb.AppendLine("# -1 means not read. Delete this file to be offered a check again.");
            foreach (var f in typeof(Profile).GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                object v = f.GetValue(this);
                string s = v is DateTime ? ((DateTime)v).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                         : v is double ? ((double)v).ToString("R", CultureInfo.InvariantCulture)
                         : Convert.ToString(v, CultureInfo.InvariantCulture);
                sb.AppendLine(f.Name + "=" + (s ?? "").Replace("\r", " ").Replace("\n", " "));
            }
            string tmp = file + ".tmp";
            File.WriteAllText(tmp, sb.ToString());
            if (File.Exists(file)) File.Replace(tmp, file, null);
            else File.Move(tmp, file);
        }

        public static Profile Load(string file)
        {
            string[] lines;
            try { if (!File.Exists(file)) return null; lines = File.ReadAllLines(file); }
            catch (Exception) { return null; }
            var p = new Profile();
            foreach (var line in lines)
            {
                int eq = line.IndexOf('=');
                if (line.StartsWith("#") || eq <= 0) continue;
                var f = typeof(Profile).GetField(line.Substring(0, eq).Trim());
                if (f == null) continue;
                string v = line.Substring(eq + 1).Trim();
                int n;
                double d;
                DateTime t;
                if (f.FieldType == typeof(string)) f.SetValue(p, v);
                else if (f.FieldType == typeof(int)) { if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) f.SetValue(p, n); }
                else if (f.FieldType == typeof(double)) { if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) f.SetValue(p, d); }
                else if (f.FieldType == typeof(DateTime)) { if (DateTime.TryParseExact(v, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out t)) f.SetValue(p, t); }
            }
            return p;
        }
    }

    sealed class Proposal
    {
        public string Field, Name, Reason;
        public object Value;

        public object Current(Settings s) { return typeof(Settings).GetField(Field).GetValue(s); }
        public bool Changes(Settings s) { return !Equals(Current(s), Value); }

        public bool Own(Settings s) { return !Equals(Current(s), typeof(Settings).GetField(Field).GetValue(new Settings())); }
    }

    static class Setup
    {
        static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        static readonly Dictionary<string, string> Names = new Dictionary<string, string>
        {
            { "Jobs", "Do jobs" }, { "JobMode", "Which job" }, { "Heists", "Help heists" },
            { "Fights", "Attack other players" }, { "Bosses", "Fight bosses" }, { "FightStaminaReserve", "Keep stamina for bosses and fights" },
            { "Bank", "Bank my cash" }, { "Operations", "Run operations" },
            { "Playtime", "Claim playtime rewards" }, { "Contracts", "Claim contract tasks" }, { "Crates", "Open crates" }, { "EquipBest", "Equip the best gear" },
            { "FamilyWatch", "Watch my family" }, { "Takedown", "Use free Takedown attacks" }, { "GiveStamina", "Give stamina to a perk" },
            { "StaminaPerk", "Perk that gets it" }, { "PointsStat", "Skill points on" },
        };

        public static string FocusHint(Focus f)
        {
            switch (f)
            {
                case Focus.XP: return "Levels first. Energy and stamina go where they give the most XP.";
                case Focus.Money: return "Cash first. Operations, the bank and well-paying heists count most.";
                case Focus.Family: return "Your family first. Its heists, Takedown and perks, with less stamina kept back.";
                default: return "A bit of everything. XP, cash and your family.";
            }
        }

        public static JobInfo BestJob(Profile p, List<JobInfo> catalog, bool anyEnergy = false)
        {
            if (catalog == null || p.Level <= 0) return null;
            int maxE = !anyEnergy && p.EnergyMax > 0 ? p.EnergyMax : int.MaxValue;
            return catalog.Where(j => j.TierLevel <= p.Level && j.Cost > 0 && j.Xp > 0 && j.Cost <= maxE)
                          .OrderByDescending(j => j.XpPerEnergy).ThenByDescending(j => j.Xp).FirstOrDefault();
        }

        static readonly string[] HeistNames = { "Grand Vault", "Museum After Dark", "Armored Truck Ambush", "Corner Store Smash" };
        static readonly int[] HeistXp = { 140, 60, 25, 12 };

        public static List<Proposal> Propose(Profile p, Focus focus, Settings current, List<JobInfo> catalog)
        {
            var list = new List<Proposal>();
            Action<string, object, string> add = (field, value, reason) => list.Add(new Proposal { Field = field, Name = Names[field], Value = value, Reason = reason });
            int level = p.Level;
            bool inFamily = p.InFamily == 1;
            string family = p.Family.Length > 0 ? p.Family : "your family";
            var best = BestJob(p, catalog);
            string bestText = best != null ? string.Format(IC, " (now {0}, {1:N0} XP for {2} energy)", best.Name, best.Xp, best.Cost) : "";
            Func<Tab, string, string> notYet = (t, what) => "The " + Title(View.TabLabels[t]) + " tab isn't in your menu yet. Once it shows up, " + what + ".";

            add("Jobs", true, focus == Focus.Money ? (p.JobCash == 1 ? "Every job pays cash as well as XP, and unused energy is wasted." : "Unused energy is wasted.")
                            : focus == Focus.Family ? "Energy has no family use, and your own levels open more of the game." : "Jobs never fail, and unused energy is wasted.");
            bool mastery = (focus == Focus.Balanced || focus == Focus.Family) && level >= 30;
            if (level <= 0) { }
            else if (mastery)
                add("JobMode", JobMode.Mastery, "Every job to Gold, top down. 100 times gives a skill point and +15% on that job.");
            else if (focus == Focus.Money)
                add("JobMode", JobMode.BestXp, p.JobCash == 1 ? "Higher jobs pay more cash per energy too, so the best job is also best for money" + bestText + "."
                                                              : "The job with the most XP per energy" + bestText + ". Higher jobs pay more cash too.");
            else if (focus == Focus.XP)
                add("JobMode", JobMode.BestXp, "The job with the most XP per energy" + bestText + ". Mastery would spend energy on weaker jobs.");
            else
                add("JobMode", JobMode.BestXp, "Early levels go fastest with the best job" + bestText + ". Mastery pays off from level 30.");

            string heists = "30 favors a day, 5 energy a help.";
            if (best != null)
            {
                double perHelp = best.XpPerEnergy * 5;
                var paying = Enumerable.Range(0, 4).Where(i => HeistXp[i] > perHelp).Select(i => HeistNames[i] + " (" + HeistXp[i] + ")").ToList();
                string them = paying.Count == 4 ? "every heist" : paying.Count == 0 ? "no heist" : paying.Count == 1 ? "only " + paying[0]
                            : "only " + string.Join(", ", paying.Take(paying.Count - 1)) + " and " + paying.Last();
                heists += string.Format(IC, " Those 5 energy give about {0:N0} XP in your best job, so it helps {1}", perHelp, them)
                        + (inFamily ? ", and every heist of " + family + " first." : ".");
            }
            else if (inFamily) heists += " Heists of " + family + " go first.";
            add("Heists", true, p.Lacks(Tab.Heists) ? notYet(Tab.Heists, "the bot starts helping") : heists);

            add("Fights", true, p.Lacks(Tab.Fight) ? notYet(Tab.Fight, "the bot attacks real players with the stamina bosses don't use")
                              : "Bosses go first while they're up. While they're away, fights use stamina that would otherwise go to waste. They attack real players, and a loss costs health.");
            add("Bosses", true, p.Lacks(Tab.Bosses) ? notYet(Tab.Bosses, "the bot fights them")
                              : p.BossesOpen > 0 && p.BossesUnsure <= 0 ? string.Format("{0} {1} at or below your level. The highest first, about 900 XP per stamina.", p.BossesOpen, p.BossesOpen == 1 ? "boss is" : "bosses are")
                              : p.BossesOpen == 0 && p.BossLowest > 0 ? "No boss is at your level yet (the first is level " + p.BossLowest + "). The bot fights them once you get there."
                              : "Every boss at or below your level, the highest first. The most XP per stamina.");

            int half = p.StaminaMax > 0 ? p.StaminaMax / 2 : -1;
            if (half >= 0 && current.FightStaminaReserve > half && !(inFamily && focus == Focus.Family))
                add("FightStaminaReserve", half, string.Format(IC, "Your stamina bar holds {0}. Keeping {1} for bosses leaves the rest for fights.", p.StaminaMax, half));

            if (p.BankFee == 0)
                add("Bank", true, "No fee with your No Bank Fees pass. It banks every few minutes, so a player who beats you gets little.");
            else if (p.BankFee > 0)
                add("Bank", false, string.Format("Without the No Bank Fees pass each deposit costs {0}%. Cash on hand is only at risk when a player beats you in a fight{1}.",
                    p.BankFee, focus == Focus.Money ? " (switch it on if that keeps happening)" : ""));
            else
                add("Bank", false, "The bank's fee didn't read. Without the No Bank Fees pass each deposit costs 10%. If you have the pass, keep it on.");
            string slots = p.OpsOpen > 0 ? string.Format("You have {0} slot{1}{2}", p.OpsOpen, p.OpsOpen == 1 ? "" : "s", p.OpsPass > 0 ? " (" + p.OpsPass + " more with the Extra Operations Slot pass)" : "") : null;
            if (p.Lacks(Tab.Operations))
                add("Operations", true, notYet(Tab.Operations, "the bot runs them"));
            else
            {
                string money = focus == Focus.Money ? "Money first. Operations pay cash every few hours. " : "";
                add("Operations", true, money + (p.OpsOpen == 0 ? "No slot is open yet. The bot starts one as soon as one opens."
                                                 : (slots != null ? slots + ". The bot collects each one and starts the next" : "The bot collects finished ones and keeps every free slot running")
                                                   + (p.OfflineOpsOffer == 1 ? ". They only run while you're in the game." : ".")));
            }

            add("Playtime", true, "Free every day. Cash, an energy refill, crates and a gold bar.");
            add("Contracts", true, p.Lacks(Tab.Contracts) ? notYet(Tab.Contracts, "the bot claims them") : "Free seals and cash for tasks you do anyway.");
            add("Crates", true, p.Lacks(Tab.Inventory) ? notYet(Tab.Inventory, "the bot opens them") : "Crates hold gear for your crew.");
            add("EquipBest", true, p.Lacks(Tab.Crew) ? notYet(Tab.Crew, "the bot equips your crew")
                                 : p.Henchmen >= 0 ? string.Format("New gear goes straight onto your crew ({0} of {1} henchmen).", p.Henchmen, p.HenchSlots) : "New gear goes straight onto your crew.");

            if (inFamily)
            {
                add("Takedown", true, "Your free attacks come back by themselves and are wasted when full. It never uses tickets.");
                add("GiveStamina", true, "Your stamina above the part kept for bosses and fights goes to " + family + "'s perks.");
                if (focus == Focus.Family)
                {
                    int keep = half >= 0 ? Math.Min(5, half) : 5;
                    add("FightStaminaReserve", keep, "Family first. Only " + keep + " stamina stays back for bosses and fights, the rest goes to the perks.");
                }
                if (focus == Focus.XP) add("StaminaPerk", "Hustlers", "Hustlers gives every member +1% job XP per level.");
                else if (focus == Focus.Family) add("StaminaPerk", "", "The perk closest to its next level, so your family gains the most perk levels.");
                int rank = p.RankIndex;
                bool leader = rank >= 0 && rank <= 2;
                if (focus == Focus.Family || focus == Focus.Balanced || leader)
                    add("FamilyWatch", true, leader ? "You're " + (p.Rank == "Boss" ? "the Boss" : Article(p.Rank) + " " + p.Rank) + ". The Stamina page shows its level, vault and Takedown."
                                                    : "It reads your family every 3 hours for the Stamina page. It only reads.");
                else
                    add("FamilyWatch", false, "It only reads the family's pages. " + (rank >= 0 ? "As " + Article(p.Rank) + " " + p.Rank : "As a member")
                                              + " you can do without, and the mouse is free more often.");
            }

            if (current.SpendPoints)
            {
                var top = BestJob(p, catalog, true);
                if (top != null && p.EnergyMax > 0 && top.Cost > p.EnergyMax)
                    add("PointsStat", 0, string.Format("Your best job, {0}, needs {1} energy and your bar holds {2}.", top.Name, top.Cost, p.EnergyMax));
            }
            return list;
        }

        public static string Show(string field, object v)
        {
            if (v is bool) return (bool)v ? "on" : "off";
            if (v is JobMode) return (JobMode)v == JobMode.BestXp ? "best XP per energy" : (JobMode)v == JobMode.Mastery ? "finish mastery (Gold)" : "one job";
            switch (field)
            {
                case "FightStaminaReserve": return v + " stamina";
                case "StaminaPerk": return ((string)v).Trim().Length == 0 ? "closest to its next level" : (string)v;
                case "PointsStat": { int i = (int)v; return i >= 0 && i < Game.StatNames.Length ? Game.StatNames[i] : v.ToString(); }
            }
            return Convert.ToString(v, IC);
        }

        public static string OwnChoice(Proposal p, Settings s)
        {
            if (!p.Own(s)) return null;
            var cur = p.Current(s);

            if (cur is JobMode && (JobMode)cur == JobMode.OneJob && s.OneJob.Trim().Length > 0)
                return "You picked one job yourself (" + s.OneJob.Trim() + "). It stays unless you switch this line on.";
            if (cur is bool) return (bool)cur ? "You switched this on yourself. It stays on unless you switch this line on."
                                              : "You switched this off yourself. It stays off unless you switch this line on.";
            return "You set this yourself (" + Show(p.Field, cur) + "). It stays unless you switch this line on.";
        }

        public static List<string[]> Found(Profile p)
        {
            var rows = new List<string[]>();
            Action<string, string> row = (t, s) => rows.Add(new[] { t, s });
            var parts = new List<string>();
            if (p.Level > 0) parts.Add("level " + p.Level + (p.Xp >= 0 && p.XpNext > 0 ? string.Format(IC, " ({0:N0} of {1:N0} XP)", p.Xp, p.XpNext) : ""));
            if (p.EnergyMax > 0) parts.Add(string.Format("energy {0}, stamina {1}, health {2}", p.EnergyMax, N(p.StaminaMax), N(p.HealthMax)));
            if (p.SkillPoints > 0) parts.Add(p.SkillPoints + " skill points to spend");
            row("You", (p.Name.Length > 0 ? p.Name : "Your name didn't read") + (parts.Count > 0 ? ", " + string.Join(", ", parts) : ""));

            if (p.InFamily == 1)
            {
                var f = new List<string> { p.Family.Length > 0 ? p.Family : "In a family" };
                if (p.Rank.Length > 0) f[0] += ", you're " + (p.Rank == "Boss" ? "the Boss" : Article(p.Rank) + " " + p.Rank);
                if (p.FamilyLevel > 0) f.Add("family level " + p.FamilyLevel);
                if (p.Members >= 0) f.Add(p.Members + " of " + p.MembersMax + " members");
                row("Family", string.Join(", ", f));
            }
            else row("Family", p.InFamily == 0 ? "Not in a family. The family parts wait until you join one." : "Not read.");

            var tabs = p.TabList;
            var lacking = View.TabLabels.Keys.Where(t => !tabs.Contains(t)).ToList();
            row("Game menu", p.MenuWhole == 1 && lacking.Count == 0 ? "All " + View.TabLabels.Count + " parts of the game are open."
                           : p.MenuWhole == 1 ? "Not open yet: " + string.Join(", ", lacking.Select(t => Title(View.TabLabels[t]))) + "."
                           : tabs.Count > 0 ? tabs.Count + " parts read. Some entries didn't read, so what's missing isn't known." : "Not read.");

            row("Bank", p.BankFee == 0 ? "No fee. You have the No Bank Fees pass" + (p.BankFeeFrom == "a deposit" ? " (a deposit's message said so)." : ".")
                      : p.BankFee > 0 ? "Each deposit costs " + p.BankFee + "%. You don't have the No Bank Fees pass."
                      : "The fee didn't read. The first deposit will tell.");

            if (p.Lacks(Tab.Operations)) row("Operations", "Not open yet.");
            else if (p.OpsSlots >= 0)
            {
                var o = new List<string> { p.OpsOpen + (p.OpsOpen == 1 ? " slot" : " slots") + (p.OpsPass > 0 ? " (" + p.OpsPass + " more with the Extra Operations Slot pass)" : "") };
                if (p.OpsSlots - p.OpsOpen - Math.Max(0, p.OpsPass) > 0) o.Add((p.OpsSlots - p.OpsOpen - Math.Max(0, p.OpsPass)) + " locked");
                if (p.OpsRunning > 0) o.Add(p.OpsRunning + " running");
                if (p.OfflineOpsOffer == 1) o.Add("they only run while you're in the game");
                row("Operations", string.Join(", ", o));
            }
            else row("Operations", "Not read.");

            if (p.Level > 0)
            {
                int open = GameData.CityLevels.Count(l => l <= p.Level), next = Array.FindIndex(GameData.CityLevels, l => l > p.Level);
                string cities = open + (open == 1 ? " city open, " : " cities open, ") + Title(GameData.Cities[0]) + (open > 1 ? " to " + Title(GameData.Cities[open - 1]) : "") + ".";
                if (next >= 0) cities += " Next is " + Title(GameData.Cities[next]) + " at level " + GameData.CityLevels[next] + ".";
                var folded = CityIndexes(p.CitiesFolded).Where(i => GameData.CityLevels[i] <= p.Level).ToList();
                if (folded.Count > 0) cities += " Still folded: " + string.Join(", ", folded.Select(i => Title(GameData.Cities[i]))) + ".";
                row("Jobs", cities);
            }

            if (p.Lacks(Tab.Crew)) row("Crew", "Not open yet.");
            else if (p.Henchmen >= 0 || p.CrewAttack >= 0)
                row("Crew", (p.Henchmen >= 0 ? p.Henchmen + " of " + p.HenchSlots + " henchmen" : "") + (p.CrewAttack >= 0 ? (p.Henchmen >= 0 ? ", " : "") + string.Format(IC, "+{0:N0} attack, +{1:N0} defense", p.CrewAttack, p.CrewDefense) : ""));

            if (p.SafeLevel > 0 || p.Respect >= 0)
            {
                var s = new List<string>();
                if (p.SafeLevel > 0) s.Add("level " + p.SafeLevel + (p.SafeMax > 0 ? " of " + p.SafeMax : "") + (p.SafeBonus >= 0 ? " (+" + p.SafeBonus + "%)" : ""));
                if (p.Respect >= 0) s.Add(string.Format(IC, "respect {0:N0}", p.Respect));
                if (p.Income >= 0) s.Add("income " + SafehouseInfo.Money(p.Income) + " an hour");
                row("Safehouse", string.Join(", ", s));
            }

            if (p.Lacks(Tab.Bosses)) row("Bosses", "Not open yet.");
            else if (p.Bosses >= 0 && p.BossesUnsure > 0)

                row("Bosses", "About " + p.Bosses + " in the list. " + (SmallWindow(p.Window) ? "This window is too small to read every boss's level."
                              : p.BossesUnsure + (p.BossesUnsure == 1 ? " boss's level" : " bosses' levels") + " didn't read this time.")
                              + (p.BossesReady > 0 ? " " + p.BossesReady + " ready now." : "") + (p.BossNext > 0 ? " The next one opens at level " + p.BossNext + "." : ""));
            else if (p.Bosses >= 0)
                row("Bosses", p.BossesOpen + " of about " + p.Bosses + " at or below your level" + (p.BossesReady > 0 ? ", " + p.BossesReady + " ready now" : "") + "."
                              + (p.BossNext > 0 ? " The next one opens at level " + p.BossNext + "." : ""));

            if (p.Lacks(Tab.Heists)) row("Heists", "Not open yet.");
            else if (p.Favors >= 0) row("Heists", p.Favors + " favors left today" + (p.FamilyHeists > 0 ? ", " + p.FamilyHeists + " of your family's heists up" : "") + ".");

            if (p.Notes.Length > 0) row("Not read", p.Notes + ".");
            return rows;
        }

        static bool SmallWindow(string size)
        {
            var m = Regex.Match(size ?? "", @"^(\d+)x(\d+)$");
            return !m.Success || int.Parse(m.Groups[1].Value) < 1100 || int.Parse(m.Groups[2].Value) < 700;
        }

        public static string RecheckHint(Profile p, int level, int putOffAt)
        {
            if (p == null || p.Level <= 0 || level <= p.Level) return null;
            int from = Math.Max(p.Level, putOffAt);
            if (level <= from) return null;
            string what = null;
            for (int i = 0; i < GameData.CityLevels.Length; i++)
                if (GameData.CityLevels[i] > from && GameData.CityLevels[i] <= level) what = Title(GameData.Cities[i]) + " has opened";
            if (what == null && p.BossNext > from && p.BossNext <= level) what = "a new boss has opened (level " + p.BossNext + ")";
            if (what == null && p.MenuWhole == 1 && p.TabList.Count < View.TabLabels.Count && p.Level + 5 > from && level >= p.Level + 5)
                what = "parts of the game that weren't open at level " + p.Level + " may be now";
            if (what == null && p.Level + 25 > from && level >= p.Level + 25) what = "you've gone up " + (level - p.Level) + " levels";
            if (what == null) return null;
            return string.Format(IC, "You're level {0} now, and {1} since the last check (level {2}, {3:dd-MM}). Check again?", level, what, p.Level, p.Checked);
        }

        static List<int> CityIndexes(string list)
        {
            return (list ?? "").Split(',').Select(s => GameData.CityIndex(s.Trim())).Where(i => i >= 0).Distinct().ToList();
        }

        static string N(int v) { return v >= 0 ? v.ToString(IC) : "?"; }

        static string Article(string word) { return word.Length > 0 && "AEIOU".IndexOf(char.ToUpperInvariant(word[0])) >= 0 ? "an" : "a"; }

        public static string Title(string upper) { return CultureInfo.InvariantCulture.TextInfo.ToTitleCase((upper ?? "").ToLowerInvariant()); }
    }
}
