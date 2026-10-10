using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace IdleMafiaBot
{

    enum Ping { Test, Problem, Rarity, Property, Summary, Level, Boss }

    sealed class Discord
    {
        sealed class Msg
        {
            public Ping Kind;
            public string Title, Text, Link;
            public byte[] Jpg;
            public Func<byte[]> Shot;
            public int Color;
        }

        sealed class Batch
        {
            public readonly List<string> Lines = new List<string>();
            public string Title;
            public Func<byte[]> Shot;
            public int Color;
            public DateTime Last = DateTime.MinValue;
        }

        static readonly TimeSpan Gap = TimeSpan.FromMinutes(10);
        static readonly Ping[] Batched = { Ping.Rarity, Ping.Property, Ping.Boss };

        public const int Gold = 0xD4AF37, Red = 0xC84B3F, Green = 0x7EBE5A, Purple = 0x8B6ABB;

        public static readonly int[] RarityColors = { 0xB4B4B4, 0x7CBC7C, 0x6C9CDC, 0xAC7CDC, 0xD4AC34, 0xEC547C, 0x5CDCD4, 0xDC2424 };

        readonly Settings s;
        readonly Action<string> log;
        readonly object gate = new object(), sendLock = new object();
        readonly Queue<Msg> queue = new Queue<Msg>();
        readonly Dictionary<Ping, Batch> batches = new Dictionary<Ping, Batch>();
        readonly Dictionary<string, DateTime> problemAt = new Dictionary<string, DateTime>();
        readonly List<DateTime> problemTimes = new List<DateTime>();
        readonly AutoResetEvent wake = new AutoResetEvent(false);
        Thread thread;
        bool sending, failing, fullSaid;

        string deadLink;
        DateTime deadAt;
        static readonly TimeSpan DeadFor = TimeSpan.FromHours(1);
        volatile string trouble, troubleLink;
        DateTime lastSend = DateTime.MinValue;

        public event Action Changed;

        public string Trouble { get { string t = trouble; return t != null && troubleLink == Link ? t : null; } }

        void SetTrouble(string link, string why)
        {
            if (trouble == why && troubleLink == link) return;
            trouble = why; troubleLink = link;
            var h = Changed;
            if (h != null) h();
        }

        bool Dead(string link) { return link == deadLink && DateTime.UtcNow - deadAt < DeadFor; }

        public Discord(Settings settings, Action<string> log)
        {
            s = settings;
            this.log = log;
            foreach (var k in Batched) batches[k] = new Batch();
        }

        public static bool IsLink(string link)
        {
            return link != null && Regex.IsMatch(link.Trim(), @"^https://(?:(?:ptb|canary)\.)?discord(?:app)?\.com/api/(?:v\d+/)?webhooks/\d{5,30}/[A-Za-z0-9_\-]{20,100}/?$");
        }

        string Link { get { return (s.DiscordLink ?? "").Trim(); } }

        public bool On(Ping k)
        {
            string link = Link;
            if (!IsLink(link) || Dead(link)) return false;
            switch (k)
            {
                case Ping.Problem: return s.DiscordProblems;
                case Ping.Rarity: return s.DiscordRarity;
                case Ping.Property: return s.DiscordProperty;
                case Ping.Summary: return s.DiscordSummary;
                case Ping.Level: return s.DiscordLevel;
                case Ping.Boss: return s.DiscordBoss;
                default: return true;
            }
        }

        public void Post(Ping k, string title, string text, Func<byte[]> shot, int color = Gold)
        {
            if (!On(k)) return;
            var now = DateTime.UtcNow;
            lock (gate)
            {
                Batch b;
                if (batches.TryGetValue(k, out b))
                {
                    if (b.Lines.Count > 0 || now - b.Last < Gap)
                    {
                        b.Lines.Add(text); b.Title = title; b.Color = color;
                        if (shot != null) b.Shot = shot;
                        return;
                    }
                    b.Last = now;
                }
            }
            byte[] jpg = Take(shot);
            lock (gate) Enqueue(new Msg { Kind = k, Title = title, Text = text, Jpg = jpg, Color = color });
        }

        public void Problem(string key, string title, string text, Func<byte[]> shot, bool small = false, int minutes = 60, int color = Red)
        {
            if (!On(Ping.Problem)) return;
            var now = DateTime.UtcNow;
            lock (gate)
            {
                DateTime at;
                if (problemAt.TryGetValue(key, out at) && now - at < TimeSpan.FromMinutes(minutes)) return;
                problemTimes.RemoveAll(t => now - t > TimeSpan.FromHours(1));
                if (small && problemTimes.Count >= 3) return;
                problemAt[key] = now;
                if (small) problemTimes.Add(now);
            }
            Post(Ping.Problem, title, text, shot, color);
        }

        public void Test(string link, Func<byte[]> shot, Action<string> done)
        {
            link = (link ?? "").Trim();
            if (!IsLink(link)) { done(link.Length == 0 ? "Paste your webhook link first." : "That isn't a Discord webhook link. It starts with https://discord.com/api/webhooks/"); return; }
            var t = new Thread(() =>
            {
                var m = new Msg
                {
                    Kind = Ping.Test, Link = link, Color = Gold, Jpg = Take(shot),
                    Title = "✅ Idle Mafia Bot is connected",
                    Text = "Its messages will show up here. It sends them only to this channel.",
                };
                done(Send(m));
            }) { IsBackground = true, Name = "discord test" };
            t.Start();
        }

        public void Flush(int ms)
        {
            lock (gate)
            {
                if (thread == null) return;
                Due(DateTime.UtcNow, true);
            }
            wake.Set();
            int end = Environment.TickCount + ms;
            while (end - Environment.TickCount > 0)
            {
                lock (gate) if (queue.Count == 0 && !sending) return;
                Thread.Sleep(100);
            }
        }

        void Enqueue(Msg m)
        {

            if (queue.Count >= 10)
            {
                if (!fullSaid) { fullSaid = true; log("Discord: too many messages waiting - left one out (\"" + m.Title + "\")"); }
                return;
            }
            fullSaid = false;
            queue.Enqueue(m);
            if (thread == null)
            {
                thread = new Thread(Loop) { IsBackground = true, Name = "discord" };
                thread.Start();
            }
            wake.Set();
        }

        void Due(DateTime now, bool all)
        {
            foreach (var kv in batches)
            {
                var b = kv.Value;
                if (b.Lines.Count == 0 || (!all && now - b.Last < Gap)) continue;
                string title = b.Lines.Count == 1 ? b.Title : Many(kv.Key, b.Lines.Count) ?? b.Title;
                string text = string.Join("\n", b.Lines.Take(10)) + (b.Lines.Count > 10 ? "\nand " + (b.Lines.Count - 10) + " more" : "");
                Enqueue(new Msg { Kind = kv.Key, Title = title, Text = text, Shot = b.Shot, Color = b.Color });
                b.Lines.Clear(); b.Shot = null; b.Last = now;
            }
        }

        static string Many(Ping k, int n)
        {
            switch (k)
            {
                case Ping.Rarity: return "\U0001F3B2 " + n + " new henchmen";
                case Ping.Property: return "\U0001F3E0 " + n + " new properties";
                case Ping.Boss: return "\U0001F44A " + n + " bosses beaten";
                default: return null;
            }
        }

        void Loop()
        {
            while (true)
            {
                Msg m = null;
                lock (gate)
                {
                    if (queue.Count == 0) Due(DateTime.UtcNow, false);
                    if (queue.Count > 0) { m = queue.Dequeue(); sending = true; }
                }
                if (m == null) { wake.WaitOne(1000); continue; }
                try { Send(m); }
                catch (Exception) { }
                finally { lock (gate) sending = false; }
            }
        }

        string Send(Msg m)
        {
            string link = m.Link ?? Link;
            if (m.Kind != Ping.Test && Dead(link)) return "The link doesn't work any more.";
            if (m.Kind != Ping.Test && !On(m.Kind)) return "Switched off.";
            if (!IsLink(link)) return "No webhook link.";
            if (m.Jpg == null && m.Shot != null) { m.Jpg = Take(m.Shot); m.Shot = null; }
            lock (sendLock)
            {
                string why = null;
                for (int tries = 0; tries < 4; tries++)
                {
                    int wait = 2000 - (int)(DateTime.UtcNow - lastSend).TotalMilliseconds;
                    if (wait > 0) Thread.Sleep(wait);
                    int retryMs;
                    why = Post(link, m, out retryMs);
                    lastSend = DateTime.UtcNow;
                    if (why == null)
                    {
                        if (m.Kind != Ping.Test) log("Discord: sent \"" + Plain(m.Title) + "\"");
                        if (deadLink == link) deadLink = null;
                        if (failing) { failing = false; if (m.Kind != Ping.Test) log("Discord: sending works again"); }
                        SetTrouble(link, null);
                        return "";
                    }
                    if (retryMs < 0) break;
                    if (tries >= 1 && retryMs == 15000) break;
                    if (tries == 3) break;
                    Thread.Sleep(retryMs);
                }
                if (m.Kind != Ping.Test)
                {
                    if (!failing) log("Discord: couldn't send \"" + Plain(m.Title) + "\" - " + why);
                    failing = true;
                    SetTrouble(link, Dead(link) ? "Discord turned this link down (deleted, or not copied whole). Paste a new one, or send a test."
                                                : "The last message didn't go out: " + why.TrimEnd('.') + ". It tries again with the next one.");
                }
                return why;
            }
        }

        string Post(string link, Msg m, out int retryMs)
        {
            retryMs = 15000;
            try
            {

                ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
                string boundary = "----IdleMafiaBot" + DateTime.UtcNow.Ticks.ToString("x", CultureInfo.InvariantCulture);
                var body = new MemoryStream();
                Action<string> write = t => { var b = Encoding.UTF8.GetBytes(t); body.Write(b, 0, b.Length); };
                write("--" + boundary + "\r\nContent-Disposition: form-data; name=\"payload_json\"\r\nContent-Type: application/json\r\n\r\n" + Payload(m) + "\r\n");
                if (m.Jpg != null)
                {
                    write("--" + boundary + "\r\nContent-Disposition: form-data; name=\"files[0]\"; filename=\"game.jpg\"\r\nContent-Type: image/jpeg\r\n\r\n");
                    body.Write(m.Jpg, 0, m.Jpg.Length);
                    write("\r\n");
                }
                write("--" + boundary + "--\r\n");

                var req = (HttpWebRequest)WebRequest.Create(link);
                req.Method = "POST";
                req.ContentType = "multipart/form-data; boundary=" + boundary;
                req.UserAgent = "IdleMafiaBot/" + Program.Version;
                req.Timeout = 20000;
                req.ReadWriteTimeout = 20000;
                req.ContentLength = body.Length;
                using (var rs = req.GetRequestStream()) body.WriteTo(rs);
                using (var resp = (HttpWebResponse)req.GetResponse()) { }
                return null;
            }
            catch (WebException e)
            {
                var r = e.Response as HttpWebResponse;
                if (r == null) return "couldn't reach Discord (" + Tidy(e.Message, link) + ")";
                using (r)
                {
                    int code = (int)r.StatusCode;
                    if (code == 429)
                    {
                        double sec;
                        string after = r.Headers["Retry-After"];
                        retryMs = after != null && double.TryParse(after, NumberStyles.Float, CultureInfo.InvariantCulture, out sec) ? (int)Math.Min(60000, sec * 1000 + 250) : 5000;
                        return "Discord asked to slow down";
                    }
                    if (code == 401 || code == 404)
                    {
                        retryMs = -1;
                        if (m.Kind != Ping.Test && !Dead(link))
                        {
                            deadLink = link; deadAt = DateTime.UtcNow;
                            log("Discord: the webhook link doesn't work any more (deleted in Discord?) - no messages for an hour, or until it's changed or a test goes through");
                        }
                        return "Discord says this link doesn't work (deleted, or not copied whole).";
                    }

                    if (code == 407) { retryMs = -1; return "a proxy on this PC blocked it (407)"; }
                    if (code == 403) { retryMs = -1; return "Discord refused it (403), which a VPN or a school or work network can cause"; }
                    if (code >= 400 && code < 500) { retryMs = -1; return "Discord refused the message (" + code + ")"; }
                    return "Discord had a problem (" + code + ")";
                }
            }
            catch (Exception e) { return "couldn't send (" + Tidy(e.Message, link) + ")"; }
        }

        string Payload(Msg m)
        {
            var sb = new StringBuilder();
            sb.Append("{\"username\":\"Idle Mafia Bot\",\"allowed_mentions\":{\"parse\":[]},\"embeds\":[{");
            sb.Append("\"title\":").Append(Json(Cut(m.Title, 250)));
            if (!string.IsNullOrEmpty(m.Text)) sb.Append(",\"description\":").Append(Json(Cut(m.Text, 3500)));
            sb.Append(",\"color\":").Append(m.Color.ToString(CultureInfo.InvariantCulture));
            if (m.Jpg != null) sb.Append(",\"image\":{\"url\":\"attachment://game.jpg\"}");
            string who = (s.Account ?? "").Trim();
            sb.Append(",\"footer\":{\"text\":").Append(Json("Idle Mafia Bot " + Program.Version + (who.Length > 0 ? " - " + who : ""))).Append("}");
            sb.Append(",\"timestamp\":\"").Append(DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)).Append("\"");
            sb.Append("}]");
            if (m.Jpg != null) sb.Append(",\"attachments\":[{\"id\":0,\"filename\":\"game.jpg\"}]");
            sb.Append("}");
            return sb.ToString();
        }

        static string Json(string t)
        {
            var sb = new StringBuilder("\"");
            foreach (char c in t ?? "")
            {
                if (c == '"') sb.Append("\\\"");
                else if (c == '\\') sb.Append("\\\\");
                else if (c == '\n') sb.Append("\\n");
                else if (c == '\r') { }
                else if (c == '\t') sb.Append("\\t");
                else if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                else sb.Append(c);
            }
            return sb.Append('"').ToString();
        }

        static string Cut(string t, int max) { t = t ?? ""; return t.Length <= max ? t : t.Substring(0, max - 3) + "..."; }

        static string Plain(string t) { return Regex.Replace(t ?? "", @"^[^A-Za-z0-9$]+", ""); }

        static string Tidy(string msg, string link) { return (msg ?? "").Replace(link, "the link").Trim().TrimEnd('.'); }

        static byte[] Take(Func<byte[]> shot)
        {
            if (shot == null) return null;
            try { return shot(); }
            catch (Exception) { return null; }
        }

        public static byte[] Jpg(Bitmap b)
        {
            if (b == null || b.Width < 1 || b.Height < 1) return null;
            double k = Math.Min(1.0, 1280.0 / b.Width);
            int w = Math.Max(1, (int)Math.Round(b.Width * k)), h = Math.Max(1, (int)Math.Round(b.Height * k));
            using (var small = new Bitmap(w, h, PixelFormat.Format24bppRgb))
            {
                using (var g = Graphics.FromImage(small))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.DrawImage(b, 0, 0, w, h);
                }
                var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
                using (var ps = new EncoderParameters(1))
                using (var ms = new MemoryStream())
                {
                    ps.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 85L);
                    small.Save(ms, codec, ps);
                    return ms.ToArray();
                }
            }
        }

        public static byte[] JpgOf(string file)
        {
            using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var b = new Bitmap(fs)) return Jpg(b);
        }

        public static byte[] ShotOfRoblox()
        {
            var w = new RobloxWindow();
            if (!w.Find() || w.Minimized) return null;
            using (var f = w.Capture()) return Jpg(f.Bitmap);
        }
    }
}
