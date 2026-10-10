using System;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace IdleMafiaBot
{

    static class Updater
    {
        internal static string LatestUrl = "https://api.github.com/repos/geenusername/IdleMafiaBot/releases/latest";

        internal static string PublicKey = "<RSAKeyValue><Modulus>2ShnG78eNnuxKJe4LSFty84YKNyRIPGOgsiCSXmweHPn6G/h/bw3VhIm0J6pXRd70JjnxbPeL1WoeErZ9hFuKptFZpBfuzUHJ8C7e7Y/Gewvz8cEYX0jUtykpjLcBchuzQiQyWuj6nULAVtcHVlxTJHQuQYFbtAWqGRjbJ2zYxNvf62My9jL86od/GDWQV8Y2nD+EkzMu5oLccAEZIunUQ0BJGN7eiO/NaRd4m4r2zfNADGHNeOnQH43UDwMe9ZChNCWVx2KoetS+SHnxfo+P4j77j9J0M/Mi705a6LcPb5Kdty2/CDuG3MEfY0yrO6v6NSCk1ZmI1DHqHBJ7GweS1O0kliJhJo0hLq76nkz8kylmKHXQW2mh1ErL9I2UXy/Qv0Lb+5JXLcX5zgW7+Q2Bbh4E7TqO8809huFQCMtxy0gIe5svKpKx/G6pskMQXrpojzzwVJ/5wgwOyGotdH7KTQmoS7CNVNVzlVAeeCsof2XsAN+4QJkfvRo2mJMHAYl</Modulus><Exponent>AQAB</Exponent></RSAKeyValue>";

        public sealed class Release
        {
            public Version Version;
            public string Tag = "";
            public string ZipUrl;
            public string Sha256;
        }

        static readonly Regex TagRx = new Regex("\"tag_name\"\\s*:\\s*\"v?([0-9]+(?:\\.[0-9]+){1,3})\"");
        static readonly Regex ZipRx = new Regex("\"browser_download_url\"\\s*:\\s*\"(https://[^\"]+?\\.zip)\"", RegexOptions.IgnoreCase);

        static readonly Regex ShaRx = new Regex(@"SHA-?256\s+of\s+\W{0,3}IdleMafiaBot\.exe\W{0,6}([0-9A-Fa-f]{64})(?![0-9A-Fa-f])", RegexOptions.IgnoreCase);

        internal static bool AllowedHost(Uri u)
        {
            if (u == null || u.Scheme != Uri.UriSchemeHttps) return false;
            string h = u.Host.ToLowerInvariant();
            return h == "api.github.com" || h == "github.com" || h.EndsWith(".githubusercontent.com", StringComparison.Ordinal);
        }

        internal static Release Parse(string json)
        {
            var t = TagRx.Match(json ?? "");
            Version v;
            if (!t.Success || !Version.TryParse(t.Groups[1].Value, out v)) return null;
            var r = new Release { Version = v, Tag = t.Groups[1].Value };
            var z = ZipRx.Match(json);
            if (z.Success) r.ZipUrl = z.Groups[1].Value;
            var s = ShaRx.Match(json);
            if (s.Success) r.Sha256 = s.Groups[1].Value.ToUpperInvariant();
            return r;
        }

        public static bool Newer(Release r) { return r != null && r.Version > Version.Parse(Program.Version); }

        public static Release Check()
        {
            var r = Parse(Encoding.UTF8.GetString(Get(LatestUrl, 1 << 20, true)));
            if (r == null) throw new Exception("GitHub's answer had no version in it");
            return r;
        }

        public static string Install(Release r, string exePath)
        {
            if (r == null || r.ZipUrl == null) return "the release on GitHub has no download";
            if (r.Sha256 == null) return "the release on GitHub doesn't say its fingerprint (SHA-256), so the download can't be checked - download it from the GitHub page";
            byte[] zip;
            try { zip = Get(r.ZipUrl, 30 << 20, false); }
            catch (Exception e) { return "couldn't download it (" + e.Message + ")"; }
            return InstallFrom(zip, r.Sha256, exePath);
        }

        internal static string InstallFrom(byte[] zip, string sha256, string exePath)
        {
            byte[] exe = null, readme = null, sig = null;
            try
            {
                using (var a = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read))
                    foreach (var e in a.Entries)
                    {
                        if (e.Length > 20 << 20) continue;
                        if (e.Name.Equals("IdleMafiaBot.exe", StringComparison.OrdinalIgnoreCase)) exe = Bytes(e);
                        else if (e.Name.Equals("README.txt", StringComparison.OrdinalIgnoreCase)) readme = Bytes(e);
                        else if (e.Name.Equals("IdleMafiaBot.exe.sig", StringComparison.OrdinalIgnoreCase) && e.Length <= 4096) sig = Bytes(e);
                    }
            }
            catch (Exception e) { return "the download isn't a zip that can be read (" + e.Message + ")"; }
            if (exe == null || exe.Length < 2 || exe[0] != 'M' || exe[1] != 'Z') return "the download has no IdleMafiaBot.exe in it";
            string got;
            using (var h = SHA256.Create()) got = string.Concat(h.ComputeHash(exe).Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));
            if (!got.Equals(sha256, StringComparison.OrdinalIgnoreCase)) return "the downloaded exe isn't the one the release names (its fingerprint differs) - nothing was changed";
            if (sig == null) return "the download isn't signed by the bot's maker - nothing was changed. Download it from the GitHub page or our Discord";
            if (!Signed(exe, sig, PublicKey)) return "the download's signature isn't the bot maker's - nothing was changed. Download it from the GitHub page or our Discord";

            string dir = Path.GetDirectoryName(exePath), name = Path.GetFileNameWithoutExtension(exePath);
            string fresh = Path.Combine(dir, name + ".new.exe"), old = OldCopy(exePath);
            try
            {
                File.WriteAllBytes(fresh, exe);
                if (File.Exists(old)) File.Delete(old);
                File.Move(exePath, old);
            }
            catch (Exception e)
            {
                try { File.Delete(fresh); } catch (Exception) { }
                return "couldn't write in the bot's folder (" + e.Message + ") - move the bot to a folder of your own, like Documents";
            }
            try { File.Move(fresh, exePath); }
            catch (Exception e)
            {
                try { File.Move(old, exePath); } catch (Exception) { }
                try { File.Delete(fresh); } catch (Exception) { }
                return "couldn't put the new exe in place (" + e.Message + ")";
            }
            if (readme != null) try { File.WriteAllBytes(Path.Combine(dir, "README.txt"), readme); } catch (Exception) { }
            return null;
        }

        internal static string Undo(string exePath)
        {
            string old = OldCopy(exePath), aside = Path.Combine(Path.GetDirectoryName(exePath), Path.GetFileNameWithoutExtension(exePath) + ".new.exe");
            if (!File.Exists(old)) return "the old version isn't there any more";
            try
            {
                if (File.Exists(aside)) File.Delete(aside);
                File.Move(exePath, aside);
            }
            catch (Exception e) { return "couldn't move the new version aside (" + e.Message + ")"; }
            try { File.Move(old, exePath); }
            catch (Exception e)
            {
                try { File.Move(aside, exePath); } catch (Exception) { }
                return "couldn't put the old version back (" + e.Message + ")";
            }
            try { File.Delete(aside); } catch (Exception) { }
            return null;
        }

        internal static bool Signed(byte[] exe, byte[] sig, string publicKeyXml)
        {
            if (exe == null || sig == null || sig.Length == 0 || string.IsNullOrEmpty(publicKeyXml)) return false;
            try
            {
                using (var rsa = new RSACryptoServiceProvider())
                {
                    rsa.PersistKeyInCsp = false;
                    rsa.FromXmlString(publicKeyXml);
                    if (!rsa.PublicOnly || rsa.KeySize < 3072) return false;
                    return rsa.VerifyData(exe, "SHA256", sig);
                }
            }
            catch (Exception) { return false; }
        }

        internal static string OldCopy(string exePath)
        {
            return Path.Combine(Path.GetDirectoryName(exePath), Path.GetFileNameWithoutExtension(exePath) + ".old.exe");
        }

        static byte[] Bytes(ZipArchiveEntry e)
        {
            using (var s = e.Open())
            using (var m = new MemoryStream())
            {
                s.CopyTo(m);
                return m.ToArray();
            }
        }

        static byte[] Get(string url, int max, bool api)
        {

            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
            var u = new Uri(url);
            for (int hop = 0; hop < 5; hop++)
            {
                if (!AllowedHost(u)) throw new Exception("not GitHub: " + u.Host);
                var req = (HttpWebRequest)WebRequest.Create(u);
                req.AllowAutoRedirect = false;
                req.UserAgent = "IdleMafiaBot/" + Program.Version;
                if (api) req.Accept = "application/vnd.github+json";
                req.Timeout = 20000;
                req.ReadWriteTimeout = 60000;
                HttpWebResponse resp;
                try { resp = (HttpWebResponse)req.GetResponse(); }
                catch (WebException e)
                {
                    var r = e.Response as HttpWebResponse;
                    if (r == null) throw new Exception("couldn't reach GitHub: " + e.Message);
                    using (r) throw new Exception("GitHub answered " + (int)r.StatusCode + (r.StatusCode == (HttpStatusCode)403 ? " (asked too often: try again in an hour)" : ""));
                }
                using (resp)
                {
                    int code = (int)resp.StatusCode;
                    if (code >= 300 && code < 400)
                    {
                        string to = resp.Headers["Location"];
                        if (string.IsNullOrEmpty(to)) throw new Exception("GitHub sent it on without saying where");
                        u = new Uri(u, to);
                        continue;
                    }
                    if (resp.ContentLength > max) throw new Exception("the file is too big");
                    using (var s = resp.GetResponseStream())
                    using (var m = new MemoryStream())
                    {
                        var buf = new byte[81920];
                        int n;
                        while ((n = s.Read(buf, 0, buf.Length)) > 0)
                        {
                            m.Write(buf, 0, n);
                            if (m.Length > max) throw new Exception("the file is too big");
                        }
                        return m.ToArray();
                    }
                }
            }
            throw new Exception("too many redirects");
        }
    }
}
