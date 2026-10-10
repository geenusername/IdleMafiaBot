using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace IdleMafiaBot
{

    sealed class StatsCard
    {

        static readonly TimeSpan Every = TimeSpan.FromMinutes(10);

        static readonly TimeSpan EveryStopped = TimeSpan.FromHours(1);

        static readonly TimeSpan Gap = TimeSpan.FromSeconds(20);
        const int Timeout = 20000, ClosingTimeout = 2500;

        public static readonly string[] RarityKeys = { "common", "uncommon", "rare", "epic", "legendary", "mythic", "secret", "forbidden" };

        readonly Bot bot;
        readonly Settings s;
        readonly Action<string> log;
        readonly Action save;
        readonly DateTime openedAt = DateTime.UtcNow;
        readonly object gate = new object(), sendLock = new object();
        readonly AutoResetEvent wake = new AutoResetEvent(false);
        Thread thread;

        int level, levelRead = -1;
        readonly List<double> cashReads = new List<double>(), bankReads = new List<double>();
        SafehouseInfo safehouse;
        CrewInfo crew;

        string sentState, sentCore;
        DateTime sentAt = DateTime.MinValue, nextTry = DateTime.MinValue;
        int fails;
        bool failing;
        volatile bool forced, accountChanged;

        string lastLink, lastMessage, lastAccount, lastState;
        string deadLink;
        volatile string trouble, troubleLink;
        volatile string sentAtText;

        public event Action Changed;

        public StatsCard(Bot bot, Settings settings, Action<string> log, Action save)
        {
            this.bot = bot;
            s = settings;
            this.log = log;
            this.save = save;
            level = Math.Max(0, s.LastLevel);
            bot.HeaderRead += NoteHeader;
            bot.CrewRead += c => { lock (gate) crew = c; };
            bot.SafehouseRead += i => { lock (gate) safehouse = i; };

            bot.AccountChanged += a =>
            {
                lock (gate) { level = Math.Max(0, s.LastLevel); levelRead = -1; cashReads.Clear(); bankReads.Clear(); safehouse = null; crew = null; }
                accountChanged = true;
                sentState = null;
                wake.Set();
            };
        }

        public static bool IsLink(string link) { return Discord.IsLink(link); }

        string Link { get { return (s.StatsLink ?? "").Trim(); } }

        public bool On { get { string l = Link; return IsLink(l) && l != deadLink; } }

        public string Trouble { get { string t = trouble; return t != null && troubleLink == Link ? t : null; } }

        public string Status
        {
            get
            {
                string at = sentAtText;
                return at != null ? "On. Sent at " + at + ". Type /stats in our Discord to see it." : "On. It sends in a moment.";
            }
        }

        public void Begin() { if (On) StartThread(); }

        public void Wake()
        {
            if (!On) return;
            forced = true;
            StartThread();
            wake.Set();
        }

        public void Removed(string oldLink)
        {
            oldLink = (oldLink ?? "").Trim();
            string mid = MessageOf(oldLink);
            bool off = !On;
            s.StatsMessage = "";
            sentState = null;
            if (!IsLink(oldLink) || mid == null || oldLink == deadLink) { if (off) log("Stats in our Discord: off"); return; }
            var t = new Thread(() =>
            {
                lock (sendLock)
                {
                    string answer, after;
                    int code = Call("DELETE", Base(oldLink) + "/messages/" + mid, null, Timeout, out answer, out after);
                    if (code == 204 || code == 200) log("Stats in our Discord: " + (off ? "off, and your stats there deleted" : "the old link's stats deleted"));
                    else if (off) log("Stats in our Discord: off" + (code == 404 || code == 401 ? "" : " (your stats there couldn't be deleted: " + Why(code, answer, oldLink) + ")"));
                }
            }) { IsBackground = true, Name = "stats remove" };
            t.Start();
        }

        public void Closing(string closedBy)
        {
            string by = closedBy ?? "";
            if (!(by.StartsWith("you") || by.StartsWith("--exit")) || !On) return;
            if (!Monitor.TryEnter(sendLock, ClosingTimeout)) return;
            try { Send("closed", ClosingTimeout); }
            catch (Exception) { }
            finally { Monitor.Exit(sendLock); }
        }

        public string Preview() { return Json(StateNow(), true); }

        void Retire()
        {
            string link = Link, mid = lastMessage, account = lastAccount;
            lastMessage = null;
            if (mid == null || lastLink != link || account == null || account == Fingerprint(s.Account) || (lastState != "playing" && lastState != "offline")) return;
            string json = "{\n  \"imb\": 1,\n  \"app\": " + Q(Program.Version) + ",\n  \"account\": " + Q(account) + ",\n  \"state\": \"stopped\"\n}";
            string answer, after;
            int code = Call("PATCH", Base(link) + "/messages/" + mid, Payload(json), Timeout, out answer, out after);
            if (code == 200 || code == 204) log("Stats in our Discord: the other account's stats say stopped");
        }

        void NoteHeader(Header h)
        {
            lock (gate)
            {
                if (h.Level > 0) { if (h.Level == levelRead) level = h.Level; levelRead = h.Level; }
                if (h.Cash >= 0) Keep(cashReads, h.Cash);
                if (h.Banked >= 0) Keep(bankReads, h.Banked);
            }
        }

        static void Keep(List<double> reads, double v) { reads.Add(v); if (reads.Count > 3) reads.RemoveAt(0); }

        static double Middle(List<double> reads)
        {
            if (reads.Count == 0) return -1;
            var o = reads.OrderBy(x => x).ToList();
            return o[(o.Count - 1) / 2];
        }

        string StateNow() { return !bot.Running ? "stopped" : bot.OutOfGame ? "offline" : "playing"; }

        internal string Json(string state, bool session) { return Json(state, session, Fingerprint(s.Account)); }

        string Json(string state, bool session, string acct)
        {
            var lines = new List<string>();
            Action<string, string> add = (k, v) => lines.Add("  " + Q(k) + ": " + v);
            add("imb", "1");
            add("app", Q(Program.Version));
            if (acct != null) add("account", Q(acct));
            add("state", Q(state));
            lock (gate)
            {
                if (level > 0) add("level", N(level));
                double cash = Middle(cashReads), bank = Middle(bankReads);
                if (cash >= 0) add("cash", N(cash));
                if (bank >= 0) add("bank", N(bank));
                var sh = safehouse;
                if (sh != null)
                {
                    if (sh.Respect >= 0) add("respect", N(sh.Respect));
                    if (sh.AttackPower >= 0) add("attack", N(sh.AttackPower));
                    if (sh.DefensePower >= 0) add("defense", N(sh.DefensePower));
                    if (sh.Income >= 0) add("income", N(sh.Income));
                }
                var c = crew;
                if (c != null && c.ByRarity.Sum() > 0)
                {
                    var parts = new List<string>();
                    if (c.Henchmen >= 0) parts.Add(Q("henchmen") + ": " + N(c.Henchmen));
                    if (c.Slots > 0) parts.Add(Q("slots") + ": " + N(c.Slots));
                    if (c.Attack >= 0) parts.Add(Q("attack") + ": " + N(c.Attack));
                    if (c.Defense >= 0) parts.Add(Q("defense") + ": " + N(c.Defense));
                    var rar = new List<string>();
                    for (int r = 0; r < RarityKeys.Length && r < c.ByRarity.Length; r++)
                        if (c.ByRarity[r] > 0) rar.Add(Q(RarityKeys[r]) + ": " + N(c.ByRarity[r]));
                    parts.Add(Q("rarities") + ": { " + string.Join(", ", rar) + " }");
                    add("crew", "{ " + string.Join(", ", parts) + " }");
                }
            }
            if (session)
            {
                var k = bot.Count;
                add("session", "{ " + Q("minutes") + ": " + N(Math.Floor((DateTime.UtcNow - openedAt).TotalMinutes)) + ", " + Q("jobs") + ": " + N(k.Jobs)
                    + ", " + Q("fights") + ": " + N(k.Fights) + ", " + Q("bosses") + ": " + N(k.BossesBeaten) + ", " + Q("properties") + ": " + N(k.Properties)
                    + ", " + Q("rerolls") + ": " + N(k.Rerolls) + " }");
            }
            return "{\n" + string.Join(",\n", lines) + "\n}";
        }

        internal static string Fingerprint(string account)
        {
            account = (account ?? "").Trim();
            if (!Accounts.Plausible(account)) return null;
            using (var sha = SHA256.Create())
            {
                var b = sha.ComputeHash(Encoding.UTF8.GetBytes("idle mafia bot stats: " + account.ToUpperInvariant()));
                return string.Concat(b.Take(5).Select(x => x.ToString("x2", CultureInfo.InvariantCulture)));
            }
        }

        static string N(double v) { return Math.Round(Math.Min(v, 1e21)).ToString("0", CultureInfo.InvariantCulture); }

        static string Q(string t)
        {
            var sb = new StringBuilder("\"");
            foreach (char c in t ?? "")
            {
                if (c == '"') sb.Append("\\\"");
                else if (c == '\\') sb.Append("\\\\");
                else if (c == '\n') sb.Append("\\n");
                else if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                else sb.Append(c);
            }
            return sb.Append('"').ToString();
        }

        static string Payload(string json)
        {
            return "{\"username\":\"Idle Mafia Bot\",\"allowed_mentions\":{\"parse\":[]},\"content\":" + Q("```json\n" + json + "\n```") + "}";
        }

        void StartThread()
        {
            lock (gate)
            {
                if (thread != null) return;
                thread = new Thread(Loop) { IsBackground = true, Name = "stats" };
                thread.Start();
            }
        }

        void Loop()
        {
            while (true)
            {
                wake.WaitOne(5000);
                try { lock (sendLock) Tick(); }
                catch (Exception) { }
            }
        }

        void Tick()
        {
            if (!On) return;
            if (accountChanged) { accountChanged = false; Retire(); }
            var now = DateTime.UtcNow;
            bool force = forced;
            if (!force && now < nextTry) return;
            string state = StateNow();
            bool due = force || sentState == null || state != sentState
                       || (state != "stopped" && now - sentAt >= Every)
                       || (now - sentAt >= EveryStopped && Json(state, false) != sentCore);
            if (!due || (!force && now - sentAt < Gap)) return;
            forced = false;
            Send(state, Timeout);
        }

        void Send(string state, int timeoutMs)
        {
            string link = Link;
            if (!IsLink(link) || link == deadLink) return;
            string account = s.Account, fp = Fingerprint(account), wid = WebhookOf(link), mid = MessageOf(link);
            string json = Json(state, true, fp), core = Json(state, false, fp);
            string body = Payload(json), answer = "", after = null;
            int code = 0;
            if (mid != null)
            {
                code = Call("PATCH", Base(link) + "/messages/" + mid, body, timeoutMs, out answer, out after);

                if (code == 404 && ErrorCode(answer) != 10015) mid = null;
            }
            if (mid == null)
            {
                code = Call("POST", Base(link) + "?wait=true", body, timeoutMs, out answer, out after);
                string id = code == 200 ? TopLevel(answer, "id") : null;
                if (id != null && Regex.IsMatch(id, @"^\d{5,30}$"))
                {
                    mid = id;

                    if (s.Account == account && Link == link) { s.StatsMessage = wid + "/" + id; save(); }
                }
            }
            var now = DateTime.UtcNow;
            if (code == 200 || code == 204)
            {
                bool first = sentState == null, changed = state != sentState;
                lastLink = link; lastMessage = mid; lastAccount = fp; lastState = state;
                sentState = state; sentCore = core; sentAt = now; fails = 0; nextTry = DateTime.MinValue;
                sentAtText = DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture);
                if (failing) { failing = false; log("Stats in our Discord: sending works again"); }
                else if (first || changed) log("Stats in our Discord: sent (" + state + ")");
                SetTrouble(link, null, true);
                return;
            }
            fails++;
            string why;
            if (code == 401 || code == 404)
            {
                deadLink = link;
                why = "Discord says this link doesn't work any more (deleted with /unlink, or not copied whole). Type /link in our Discord for your link.";
                log("Stats in our Discord: the link doesn't work any more (/unlink, or a new one from /link?) - nothing more is sent to it");
            }
            else
            {
                double sec;
                int waitMin = code == 429 ? 0 : code < 0 ? (fails == 1 ? 1 : 5) : code >= 500 ? 2 : 10;
                nextTry = now.AddMinutes(waitMin);
                if (code == 429 && after != null && double.TryParse(after, NumberStyles.Float, CultureInfo.InvariantCulture, out sec)) nextTry = now.AddSeconds(Math.Min(600, sec + 1));
                else if (code == 429) nextTry = now.AddSeconds(30);
                why = "The last update didn't go out: " + Why(code, answer, link) + ". It tries again shortly.";
                if (!failing) log("Stats in our Discord: couldn't send (" + Why(code, answer, link) + ")");
                failing = true;
            }
            SetTrouble(link, why, false);
        }

        void SetTrouble(string link, string why, bool sent)
        {
            if (!sent && trouble == why && troubleLink == link) return;
            trouble = why; troubleLink = link;
            var h = Changed;
            if (h != null) h();
        }

        static string Why(int code, string answer, string link)
        {
            if (code < 0) return "couldn't reach Discord (" + (answer ?? "").Replace(link, "the link").Trim().TrimEnd('.') + ")";
            if (code == 429) return "Discord asked to slow down";
            if (code == 403) return "Discord refused it (403), which a VPN or a school or work network can cause";
            if (code == 407) return "a proxy on this PC blocked it (407)";
            if (code >= 400 && code < 500) return "Discord refused it (" + code + ")";
            return "Discord had a problem (" + code + ")";
        }

        static string WebhookOf(string link)
        {
            var m = Regex.Match(link ?? "", @"/webhooks/(\d+)/");
            return m.Success ? m.Groups[1].Value : "";
        }

        string MessageOf(string link)
        {
            string saved = (s.StatsMessage ?? "").Trim(), wid = WebhookOf(link);
            int slash = saved.IndexOf('/');
            if (slash <= 0 || wid.Length == 0 || saved.Substring(0, slash) != wid) return null;
            string mid = saved.Substring(slash + 1);
            return Regex.IsMatch(mid, @"^\d{5,30}$") ? mid : null;
        }

        static string Base(string link) { string b = link.Trim().TrimEnd('/'); return Rebase != null ? Rebase(b) : b; }

        internal static Func<string, string> Rebase = null;

        static int Call(string method, string url, string json, int timeoutMs, out string answer, out string retryAfter)
        {
            answer = ""; retryAfter = null;
            try
            {

                ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = method;
                req.UserAgent = "IdleMafiaBot/" + Program.Version;
                req.Timeout = timeoutMs;
                req.ReadWriteTimeout = timeoutMs;
                if (json != null)
                {
                    var body = Encoding.UTF8.GetBytes(json);
                    req.ContentType = "application/json";
                    req.ContentLength = body.Length;
                    using (var rs = req.GetRequestStream()) rs.Write(body, 0, body.Length);
                }
                using (var resp = (HttpWebResponse)req.GetResponse()) { answer = ReadAll(resp); return (int)resp.StatusCode; }
            }
            catch (WebException e)
            {
                var r = e.Response as HttpWebResponse;
                if (r == null) { answer = e.Message; return -1; }
                using (r) { answer = ReadAll(r); retryAfter = r.Headers["Retry-After"]; return (int)r.StatusCode; }
            }
            catch (Exception e) { answer = e.Message; return -1; }
        }

        static string ReadAll(HttpWebResponse r)
        {
            try
            {
                using (var st = r.GetResponseStream())
                using (var ms = new MemoryStream())
                {
                    var buf = new byte[8192];
                    int n;
                    while (ms.Length < 65536 && (n = st.Read(buf, 0, buf.Length)) > 0) ms.Write(buf, 0, n);
                    return Encoding.UTF8.GetString(ms.ToArray());
                }
            }
            catch (Exception) { return ""; }
        }

        static int ErrorCode(string answer)
        {
            int n;
            return int.TryParse(TopLevel(answer, "code"), NumberStyles.Integer, CultureInfo.InvariantCulture, out n) ? n : -1;
        }

        internal static string TopLevel(string json, string key)
        {
            if (string.IsNullOrEmpty(json)) return null;
            int i = json.IndexOf('{');
            if (i < 0) return null;
            i++;
            while (true)
            {
                SkipSpace(json, ref i);
                if (i >= json.Length || json[i] != '"') return null;
                string k = ReadString(json, ref i);
                SkipSpace(json, ref i);
                if (k == null || i >= json.Length || json[i] != ':') return null;
                i++;
                SkipSpace(json, ref i);
                if (i >= json.Length) return null;
                string v;
                if (json[i] == '"') v = ReadString(json, ref i);
                else
                {
                    int start = i;
                    if (!SkipValue(json, ref i)) return null;
                    v = json.Substring(start, i - start).Trim();
                }
                if (k == key) return v;
                SkipSpace(json, ref i);
                if (i < json.Length && json[i] == ',') { i++; continue; }
                return null;
            }
        }

        static void SkipSpace(string t, ref int i) { while (i < t.Length && char.IsWhiteSpace(t[i])) i++; }

        static string ReadString(string t, ref int i)
        {
            var sb = new StringBuilder();
            for (i++; i < t.Length; i++)
            {
                char c = t[i];
                if (c == '"') { i++; return sb.ToString(); }
                if (c != '\\') { sb.Append(c); continue; }
                if (++i >= t.Length) return null;
                char e = t[i];
                if (e == 'u' && i + 4 < t.Length)
                {
                    int u;
                    if (int.TryParse(t.Substring(i + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out u)) sb.Append((char)u);
                    i += 4;
                }
                else sb.Append(e == 'n' ? '\n' : e == 't' ? '\t' : e == 'r' ? '\r' : e == 'b' ? '\b' : e == 'f' ? '\f' : e);
            }
            return null;
        }

        static bool SkipValue(string t, ref int i)
        {
            int depth = 0;
            for (; i < t.Length; i++)
            {
                char c = t[i];
                if (c == '"') { if (ReadString(t, ref i) == null) return false; i--; continue; }
                if (c == '{' || c == '[') depth++;
                else if (c == '}' || c == ']')
                {
                    if (depth == 0) return true;
                    if (--depth == 0) { i++; return true; }
                }
                else if (c == ',' && depth == 0) return true;
            }
            return depth == 0;
        }
    }
}
