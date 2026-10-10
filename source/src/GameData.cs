using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace IdleMafiaBot
{
    sealed class JobInfo
    {
        public string City = "", Name = "";
        public int Tier, TierLevel, Cost, Xp, Order;
        public int MasteryRank = -1;
        public int MasteryCur = -1, MasteryGoal = -1;
        public int Unseen;
        public double XpPerEnergy { get { return Cost > 0 ? (double)Xp / Cost : 0; } }
        public override string ToString() { return Name; }
        public JobInfo Copy() { return (JobInfo)MemberwiseClone(); }

        public string MasteryLabel
        {
            get
            {
                if (MasteryRank < 0) return "not read yet";
                if (MasteryRank >= 3) return "Gold, maxed";
                string[] next = { "Bronze", "Silver", "Gold" };
                return MasteryCur >= 0 ? string.Format("{0}/{1} to {2}", MasteryCur, MasteryGoal, next[MasteryRank])
                                       : "working towards " + next[MasteryRank];
            }
        }
    }

    static class GameData
    {
        public static readonly string[] Cities = { "NEW ASHPORT", "PORT CALDERA", "VOLKOVSK", "JADE HARBOR", "STERLING CROSS", "VEILMONT", "VESPERA", "THE EXILES" };
        public static readonly int[] CityLevels = { 1, 30, 54, 78, 102, 126, 150, 174 };

        static readonly string[] DefaultJobs =
        {
            "NEW ASHPORT|1|1|Keep Watch on the Corner|1|2",
            "NEW ASHPORT|1|2|Tag Rival Turf|2|4",
            "NEW ASHPORT|1|3|Boost a Parked Car|3|7",
            "NEW ASHPORT|1|4|Shake Down the Newsstand|3|7",
            "NEW ASHPORT|1|5|Fence Fake Watches|4|10",
            "NEW ASHPORT|1|6|Rough Up a Pickpocket|5|13",
            "NEW ASHPORT|2|8|Hijack a Delivery Truck|6|18",
            "NEW ASHPORT|2|9|Run the Chop Shop Night Shift|7|22",
            "NEW ASHPORT|2|10|Bribe a Dock Inspector|8|26",
            "NEW ASHPORT|2|12|Rob the Pawn Shop Safe|9|30",
            "NEW ASHPORT|2|14|Forge Gallery Paintings|10|35",
            "NEW ASHPORT|2|16|Torch a Rival's Warehouse|12|42",
            "NEW ASHPORT|3|18|Crack the Vault at First National|14|55",
            "NEW ASHPORT|3|20|Ambush the Kovac Convoy|16|65",
            "NEW ASHPORT|3|18|Rig the Union Election|18|75",
            "NEW ASHPORT|3|18|Heist the Museum Gala|20|85",
            "NEW ASHPORT|3|18|Silence a Witness|22|95",
            "NEW ASHPORT|3|18|Take Over the Waterfront|24|110",
            "PORT CALDERA|4|30|Smuggle Contraband Past the Coast Guard|26|130",
            "PORT CALDERA|4|30|Shake Down the Cane Fields|27|140",
            "PORT CALDERA|4|30|Rig the Marina Boat Races|28|150",
            "PORT CALDERA|4|30|Hijack a Smuggler's Speedboat|30|165",
            "PORT CALDERA|4|30|Bribe the Harbor Master|32|180",
            "PORT CALDERA|4|30|Rob the Gold Bullion Ferry|34|200",
            "PORT CALDERA|5|42|Run a Convoy to the Rebels|36|220",
            "PORT CALDERA|5|42|Blackmail the Governor's Aide|37|230",
            "PORT CALDERA|5|42|Heist the Sugar Baron's Vault|38|245",
            "PORT CALDERA|5|42|Sink a Rival Smuggler's Fleet|40|260",
            "PORT CALDERA|5|42|Seize the Grand Pavilion|42|280",
            "PORT CALDERA|5|42|Take Over the Island Trade|44|300",
            "VOLKOVSK|6|54|Raid an Abandoned Supply Depot|46|330",
            "VOLKOVSK|6|54|Fix the Underground Boxing Circuit|47|345",
            "VOLKOVSK|6|54|Shake Down the Icehouse District|48|360",
            "VOLKOVSK|6|54|Bribe the Rail Yard Commissar|50|380",
            "VOLKOVSK|6|54|Steal Kovac's Ice Trucks|52|400",
            "VOLKOVSK|6|54|Torch the Kolyev Social Club|54|425",
            "VOLKOVSK|7|66|Rob the State Bank of Volkovsk|55|450",
            "VOLKOVSK|7|66|Ambush the Diamond Courier Train|56|465",
            "VOLKOVSK|7|66|Silence the Prosecutor General|57|480",
            "VOLKOVSK|7|66|Heist the Winter Palace Auction|58|500",
            "VOLKOVSK|7|66|Break the Kovac Blockade|60|525",
            "VOLKOVSK|7|66|Take Over the Volkovsk Underworld|62|550",
            "JADE HARBOR|8|78|Smuggle Jade Through Customs|64|590",
            "JADE HARBOR|8|78|Fix the Dragon Den Prizefights|65|610",
            "JADE HARBOR|8|78|Shake Down the Night Market|66|630",
            "JADE HARBOR|8|78|Hijack a Freighter of Counterfeits|68|655",
            "JADE HARBOR|8|78|Bribe the Jade Court Captains|70|680",
            "JADE HARBOR|8|78|Rob the Golden Lotus Vault|72|710",
            "JADE HARBOR|9|90|Steal the Emperor's Jade Seal|74|755",
            "JADE HARBOR|9|90|Ambush the Jade Court Summit|75|775",
            "JADE HARBOR|9|90|Heist the Floating Palace|76|800",
            "JADE HARBOR|9|90|Silence the Dragon Head's Heir|78|830",
            "JADE HARBOR|9|90|Burn the Rival Fleet at Anchor|80|865",
            "JADE HARBOR|9|100|Take Over Jade Harbor|82|900",
        };

        public static int CityIndex(string city)
        {
            string k = Parse.Key(city);
            for (int i = 0; i < Cities.Length; i++) if (k.StartsWith(Parse.Key(Cities[i]))) return i;
            return -1;
        }

        public static bool InCityLevels(string city, int level)
        {
            int ci = CityIndex(city);
            if (ci < 0 || level <= 0) return false;
            return level >= CityLevels[ci] && (ci + 1 < CityLevels.Length ? level < CityLevels[ci + 1] : level <= 999);
        }

        public static List<JobInfo> LoadCatalog(string file)
        {
            var list = new List<JobInfo>();
            IEnumerable<string> rows = File.Exists(file) ? File.ReadAllLines(file) : DefaultJobs;
            foreach (var row in rows)
            {
                var p = row.Split('|');
                if (p.Length < 6) continue;
                var j = new JobInfo
                {
                    City = p[0], Tier = ToInt(p[1]), TierLevel = ToInt(p[2]), Name = p[3], Cost = ToInt(p[4]), Xp = ToInt(p[5])
                };
                if (p.Length >= 9) { j.MasteryRank = ToInt(p[6]); j.MasteryCur = ToInt(p[7]); j.MasteryGoal = ToInt(p[8]); }
                if (p.Length >= 10) j.Unseen = Math.Max(0, ToInt(p[9]));
                j.Order = list.Count;
                list.Add(j);
            }
            if (list.Count == 0) return LoadCatalog(null);
            Renumber(list);
            return list;
        }

        public static void SaveCatalog(string file, List<JobInfo> list)
        {
            var sb = new StringBuilder();
            foreach (var j in list)
                sb.AppendLine(string.Join("|", new[] { j.City, j.Tier.ToString(), j.TierLevel.ToString(), j.Name, j.Cost.ToString(), j.Xp.ToString(),
                    j.MasteryRank.ToString(), j.MasteryCur.ToString(), j.MasteryGoal.ToString(), j.Unseen.ToString() }));

            string tmp = file + ".tmp";
            File.WriteAllText(tmp, sb.ToString());
            if (File.Exists(file)) File.Replace(tmp, file, null);
            else File.Move(tmp, file);
        }

        public static void Renumber(List<JobInfo> list)
        {
            list.Sort((a, b) =>
            {
                int c = CityIndex(a.City).CompareTo(CityIndex(b.City));
                if (c == 0) c = a.Tier.CompareTo(b.Tier);
                if (c == 0) c = a.Cost.CompareTo(b.Cost);
                if (c == 0) c = a.Order.CompareTo(b.Order);
                return c;
            });
            for (int i = 0; i < list.Count; i++) list[i].Order = i;
        }

        static int ToInt(string s)
        {
            int v;
            return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : -1;
        }
    }
}
