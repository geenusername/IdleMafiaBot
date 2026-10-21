using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace IdleMafiaBot
{

    sealed class RemoteControl
    {
        static readonly TimeSpan Every = TimeSpan.FromSeconds(15);
        static readonly TimeSpan TooOld = TimeSpan.FromMinutes(2);
        const int Timeout = 20000;

        public static readonly string[] Commands = { "start", "stop", "status", "preset", "report", "addpreset", "shared" };

        readonly Settings s;
        readonly Action<string> log;
        readonly Action save;
        readonly StatsCard stats;
        readonly AutoResetEvent wake = new AutoResetEvent(false);
        readonly object gate = new object();
        Thread thread;

        public Action<string, string, Action<bool, string>> Handler;

        public Func<List<string>> PresetNames;

        string postedCore;
        string lastDone;
        readonly HashSet<string> seen = new HashSet<string>();
        DateTime nextTry = DateTime.MinValue;
        volatile bool dirty;
        bool failing;

        string shareId, shareCode;
        DateTime shareAt;

        public Action<string> ShareLost;

        public RemoteControl(Settings settings, Action<string> log, Action save, StatsCard stats)
        {
            s = settings;
            this.log = log;
            this.save = save;
            this.stats = stats;
        }

        string Link { get { return (s.StatsLink ?? "").Trim(); } }

        public bool On { get { return s.RemoteControl && stats.On; } }

        public void Begin() { if (On) Start(); }

        public void Wake()
        {
            dirty = true;
            if (On) Start();
            wake.Set();
        }

        void Start()
        {
            lock (gate)
            {
                if (thread != null) return;
                thread = new Thread(Loop) { IsBackground = true, Name = "remote" };
                thread.Start();
            }
        }

        void Loop()
        {
            bool wasOn = false;
            while (true)
            {
                wake.WaitOne(Every);
                try
                {
                    bool on = On;
                    if (on != wasOn) log(on ? "Remote control from Discord: on (it looks for a /remote command every 15 seconds)" : "Remote control from Discord: off");
                    if (!on && wasOn) Retire();
                    wasOn = on;
                    if (on && DateTime.UtcNow >= nextTry) Tick();
                }
                catch (Exception) { }
            }
        }

        public string Share(string code)
        {
            if (!On) return "off";
            lock (gate)
            {
                shareId = "s" + Guid.NewGuid().ToString("N").Substring(0, 12);
                shareCode = code;
                shareAt = DateTime.UtcNow;
            }
            if (Json(null, false, "").Length > 1850) { lock (gate) shareId = shareCode = null; return "too big"; }
            Wake();
            return null;
        }

        string MessageOf(string link)
        {
            string saved = (s.RemoteMessage ?? "").Trim(), wid = StatsCard.WebhookOf(link);
            int slash = saved.IndexOf('/');
            if (slash <= 0 || wid.Length == 0 || saved.Substring(0, slash) != wid) return null;
            string mid = saved.Substring(slash + 1);
            return Regex.IsMatch(mid, @"^\d{5,30}$") ? mid : null;
        }

        internal string Json(string doneId, bool ok, string text)
        {
            var names = PresetNames != null ? PresetNames() : new List<string>();
            string json = "{\n  \"imb\": 1,\n  \"remote\": 1,\n  \"app\": " + StatsCard.Q(Program.Version)
                          + ",\n  \"presets\": [" + string.Join(", ", names.Take(25).Select(n => StatsCard.Q(n.Length > 60 ? n.Substring(0, 60) : n))) + "]";
            string sid, scode;
            lock (gate) { sid = shareId; scode = shareCode; }
            if (sid != null) json += ",\n  \"share\": { \"id\": " + StatsCard.Q(sid) + ", \"code\": " + StatsCard.Q(scode) + " }";
            if (doneId != null) json += ",\n  \"done\": { \"id\": " + StatsCard.Q(doneId) + ", \"ok\": " + (ok ? "true" : "false") + ", \"text\": " + StatsCard.Q(text.Length > 900 ? text.Substring(0, 900) : text) + " }";
            return json + "\n}";
        }

        static string Payload(string json)
        {
            return "{\"username\":\"Idle Mafia Bot\",\"allowed_mentions\":{\"parse\":[]},\"content\":" + StatsCard.Q("```json\n" + json + "\n```") + "}";
        }

        void Tick()
        {
            string link = Link, mid = MessageOf(link), answer, after;
            if (mid == null)
            {
                string core = Json(null, false, "");
                int code = StatsCard.Call("POST", StatsCard.Base(link) + "?wait=true", Payload(core), Timeout, out answer, out after);
                string id = code == 200 ? StatsCard.TopLevel(answer, "id") : null;
                if (id == null || !Regex.IsMatch(id, @"^\d{5,30}$")) { Failed(code, answer, link); return; }
                s.RemoteMessage = StatsCard.WebhookOf(link) + "/" + id;
                save();
                postedCore = core;
                dirty = false;
                return;
            }
            DateTime discordNow;
            int got = Get(StatsCard.Base(link) + "/messages/" + mid, out answer, out discordNow);
            if (got == 404 && StatsCard.ErrorCode(answer) != 10015) { s.RemoteMessage = ""; save(); return; }
            if (got != 200) { Failed(got, answer, link); return; }
            if (failing) { failing = false; log("Remote control from Discord: works again"); }
            nextTry = DateTime.MinValue;
            Command c = ReadCommand(StatsCard.TopLevel(answer, "content"));
            bool lost;
            lock (gate)
            {

                lost = shareId != null && (c == null || c.Do != "shared") && DateTime.UtcNow - shareAt > TooOld;
                if (lost || (c != null && c.Do == "shared" && c.Id != lastDone && !seen.Contains(c.Id))) { shareId = shareCode = null; dirty = true; }
            }
            if (lost && ShareLost != null) ShareLost("No answer from our Discord about the share. Try again, or press Copy code and type /preset share there.");
            if (c != null && c.Id != lastDone && !seen.Contains(c.Id))
            {
                seen.Add(c.Id);
                lastDone = c.Id;
                if (discordNow - c.At > TooOld || c.At - discordNow > TooOld)
                {
                    Answer(link, mid, c.Id, false, "That command came in while the bot was off or couldn't reach Discord, so it wasn't done. Send it again.");
                    return;
                }
                log("Remote control from Discord: " + c.Do + (c.Arg.Length > 0 ? " " + c.Arg : ""));
                var done = new ManualResetEvent(false);
                bool ok = false;
                string text = "The bot didn't answer.";
                var h = Handler;
                if (h == null) text = "The bot's window isn't ready yet. Try again in a minute.";
                else
                {
                    h(c.Do, c.Arg, (o, t) => { ok = o; text = t; done.Set(); });
                    done.WaitOne(TimeSpan.FromSeconds(c.Do == "report" ? 120 : 20));
                }
                Answer(link, mid, c.Id, ok, text);
                return;
            }
            if (!dirty) return;
            string now = Json(null, false, "");
            if (now != postedCore)
            {
                int code = StatsCard.Call("PATCH", StatsCard.Base(link) + "/messages/" + mid, Payload(now), Timeout, out answer, out after);
                if (code != 200 && code != 204) { Failed(code, answer, link); return; }
                postedCore = now;
            }
            dirty = false;
        }

        void Answer(string link, string mid, string id, bool ok, string text)
        {
            string answer, after;
            string core = Json(null, false, "");
            int code = StatsCard.Call("PATCH", StatsCard.Base(link) + "/messages/" + mid, Payload(Json(id, ok, text)), Timeout, out answer, out after);
            if (code == 200 || code == 204) postedCore = core;
            else log("Remote control from Discord: the answer didn't go (" + StatsCard.Why(code, answer, link) + ")");
        }

        void Failed(int code, string answer, string link)
        {
            var now = DateTime.UtcNow;
            nextTry = now.AddMinutes(code == 429 ? 0.5 : code < 0 ? 1 : code >= 500 ? 2 : 10);
            if (!failing) log("Remote control from Discord: couldn't reach its message (" + StatsCard.Why(code, answer, link) + ")");
            failing = true;
        }

        void Retire()
        {
            string link = Link, mid = MessageOf(link), answer, after;
            postedCore = null;
            if (mid == null || !StatsCard.IsLink(link)) return;
            StatsCard.Call("DELETE", StatsCard.Base(link) + "/messages/" + mid, null, Timeout, out answer, out after);
            s.RemoteMessage = "";
            save();
        }

        internal sealed class Command { public string Id, Do, Arg; public DateTime At; }

        internal static Command ReadCommand(string content)
        {
            string obj = StatsCard.TopLevel(content, "command");
            if (string.IsNullOrEmpty(obj) || obj[0] != '{') return null;
            string id = StatsCard.TopLevel(obj, "id"), what = StatsCard.TopLevel(obj, "do"), arg = StatsCard.TopLevel(obj, "arg") ?? "", at = StatsCard.TopLevel(obj, "at");
            DateTime when;
            if (id == null || !Regex.IsMatch(id, @"^[A-Za-z0-9_-]{4,40}$") || Array.IndexOf(Commands, what) < 0
                || !DateTime.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out when)) return null;
            int max = what == "addpreset" ? 1800 : what == "shared" ? 300 : 60;
            return new Command { Id = id, Do = what, Arg = arg.Length > max ? arg.Substring(0, max) : arg, At = when };
        }

        static int Get(string url, out string answer, out DateTime discordNow)
        {
            answer = ""; discordNow = DateTime.UtcNow;
            try
            {
                ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "GET";
                req.UserAgent = "IdleMafiaBot/" + Program.Version;
                req.Timeout = Timeout;
                req.ReadWriteTimeout = Timeout;
                using (var resp = (HttpWebResponse)req.GetResponse())
                {
                    DateTime d;
                    if (DateTime.TryParse(resp.Headers["Date"], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out d)) discordNow = d;
                    using (var st = resp.GetResponseStream())
                    using (var r = new StreamReader(st, Encoding.UTF8)) answer = r.ReadToEnd();
                    return (int)resp.StatusCode;
                }
            }
            catch (WebException e)
            {
                var r = e.Response as HttpWebResponse;
                if (r == null) { answer = e.Message; return -1; }
                using (r)
                using (var st = r.GetResponseStream())
                using (var sr = new StreamReader(st, Encoding.UTF8)) { answer = sr.ReadToEnd(); return (int)r.StatusCode; }
            }
            catch (Exception e) { answer = e.Message; return -1; }
        }
    }
}
