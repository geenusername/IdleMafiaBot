using System;
using System.IO;
using System.Text.RegularExpressions;

namespace IdleMafiaBot
{

    static class Accounts
    {
        public static string Folder(string dir, string name) { return Path.Combine(Path.Combine(dir, "accounts"), Profile.Key(name)); }
        public static string Ini(string dir, string name) { return Path.Combine(Folder(dir, name), "IdleMafiaBot.ini"); }
        public static string Jobs(string dir, string name) { return Path.Combine(Folder(dir, name), "jobs.txt"); }

        public static bool Plausible(string name) { return name != null && Regex.IsMatch(name.Trim(), @"^[A-Za-z0-9_]{3,20}$"); }

        public static bool Same(string a, string b)
        {
            string x = Parse.Key(a ?? ""), y = Parse.Key(b ?? "");
            if (x.Length == 0 || y.Length == 0) return false;
            if (x == y) return true;
            if (Math.Min(x.Length, y.Length) < 8 || View.Distance(x, y) > 1) return false;
            if (x.Length != y.Length) return !(x.StartsWith(y) || y.StartsWith(x));

            string p = Plain(a), q = Plain(b);
            int i = 0;
            while (i < x.Length && x[i] == y[i]) i++;
            if (p.Length == x.Length && q.Length == y.Length) return !char.IsDigit(p[i]) && !char.IsDigit(q[i]);
            return !char.IsDigit(x[i]) && !char.IsDigit(y[i]);
        }

        static string Plain(string s)
        {
            var sb = new System.Text.StringBuilder();
            foreach (char ch in (s ?? "").ToUpperInvariant()) if (char.IsLetterOrDigit(ch)) sb.Append(ch);
            return sb.ToString();
        }

        public static void ForgetLink(string dir, string playing)
        {
            try
            {
                string root = Path.Combine(dir, "accounts");
                if (!Directory.Exists(root)) return;
                foreach (var ini in Directory.GetFiles(root, "IdleMafiaBot.ini", SearchOption.AllDirectories))
                {
                    if (!string.IsNullOrEmpty(playing) && string.Equals(ini, Ini(dir, playing), StringComparison.OrdinalIgnoreCase)) continue;
                    var o = Settings.Load(ini);
                    if (o.Unreadable == null && (o.DiscordLink ?? "").Trim().Length > 0) o.Save(ini, true);
                }
            }
            catch (Exception) { }
        }

        public static string OwnerHint(string dir)
        {
            try
            {
                var files = Directory.GetFiles(dir, "profile-*.txt");
                if (files.Length != 1) return null;
                var p = Profile.Load(files[0]);
                return p != null && Plausible(p.Name) ? p.Name.Trim() : null;
            }
            catch (Exception) { return null; }
        }
    }
}
