using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace IdleMafiaBot
{
    sealed class Counters
    {
        public int Jobs, Helps, Fights, Wins, Losses, Deposits, OpsCollected, OpsStarted, BossHits, Rewards, Crates;
        public int TakedownAttacks, StaminaGiven, Bought, Collected, Hired, Rerolls;
        public int BossesBeaten, Properties;
        public int HeistsStarted;
        public double CrewCash;
        public Counters Copy() { return (Counters)MemberwiseClone(); }
        public override string ToString()
        {
            return string.Format("jobs {0} | heist helps {1} | fights {2} ({3} won, {4} lost) | boss hits {5} | deposits {6} | operations {7} collected, {8} started | rewards {9} | crates {10}",
                Jobs, Helps, Fights, Wins, Losses, BossHits, Deposits, OpsCollected, OpsStarted, Rewards, Crates)
                + (Bought > 0 || Collected > 0 ? string.Format(" | bought {0} | collected {1}", Bought, Collected) : "")
                + (Hired > 0 || Rerolls > 0 ? string.Format(" | crew hires {0} | rerolls {1}", Hired, Rerolls) : "")
                + (HeistsStarted > 0 ? " | heists started " + HeistsStarted : "")
                + (TakedownAttacks > 0 || StaminaGiven > 0 ? string.Format(" | Takedown attacks {0} | stamina given {1}", TakedownAttacks, StaminaGiven) : "");
        }
    }

    sealed class RegenGuess
    {
        public int Period, Max = -1;
        int prevTick = -1, value = -1, tick = -1;
        DateTime at;

        public bool HasValue { get { return value >= 0; } }

        public void Note(int v, int max, int t, DateTime now)
        {
            if (v < 0 || max <= 0) return;
            if (v < max && t > 0)
            {
                if (prevTick > 0 && t > prevTick + 2) Period = Math.Min(900, Math.Max(Period, t));
                prevTick = t;
            }
            else prevTick = -1;
            value = v; Max = max; tick = v >= max ? 0 : t; at = now;
        }

        public void Forget() { value = -1; prevTick = -1; }

        public int Guess(DateTime now)
        {
            if (value < 0) return -1;
            if (value >= Max || Period <= 0) return value;
            int next = tick >= 0 ? tick : Period;
            double s = (now - at).TotalSeconds;
            int gained = s < next ? 0 : 1 + (int)((s - next) / Period);
            return Math.Min(Max, value + gained);
        }
    }

    sealed class Bot
    {
        public readonly Settings S;
        public readonly Counters Count = new Counters();

        public readonly Discord Discord;

        public readonly StatsCard Stats;
        public List<JobInfo> Catalog;

        public volatile List<JobInfo> CatalogView;
        readonly string dir, catalogFile, logFile;
        readonly object logLock = new object();
        Game game;
        Thread thread;
        volatile bool stop;
        volatile bool scanRequested, checkRequested;

        public event Action<string> Logged;
        public event Action<string> StateChanged;
        public event Action<Header> HeaderRead;
        public event Action<string, string> InfoChanged;
        public event Action<List<OpSlot>> OpsRead;
        public event Action CatalogChanged;

        DateTime nextOps, nextBosses, nextPlaytime, nextContracts, nextCrates, nextHeists, nextFights, nextBank, nextPoints, nextJobs, nextLearn, lastBank;
        int level = -1, repeats, frontFails, logWrites;
        int xpNextLast = -1, xpNextSeen = -1;
        string lastError;
        bool learnedThisRun, waitingForUser, logBroken, catalogBuiltIn;
        volatile bool levelledStopped;
        readonly HashSet<string> firstSeen = new HashSet<string>();

        void SnapshotOnce(string what)
        {
            if (firstSeen.Add(what)) game.Snapshot(what, 0, true);
        }
        Header last;

        public Bot(Settings s, string dir)
        {
            S = s;
            this.dir = dir;
            catalogFile = Path.Combine(dir, "jobs.txt");
            logFile = Path.Combine(dir, "IdleMafiaBot.log");
            memory = new FightMemory(Path.Combine(dir, "fight memory.txt"));
            catalogBuiltIn = !File.Exists(catalogFile);
            Catalog = GameData.LoadCatalog(catalogFile);
            CatalogView = Catalog.Select(j => j.Copy()).ToList();
            level = s.LastLevel;
            crewUnequipped = s.CrewGearOff;
            if (s.ScreenFirst) RobloxWindow.ScreenFirst = true;
            Discord = new Discord(s, Log);
            HeaderRead += NoteTicks;
            Stats = new StatsCard(this, s, Log, SaveSettings);
        }

        public bool Running { get { return thread != null && thread.IsAlive; } }

        public bool Once;

        public void Start() { Begin(false); }

        public void RequestScan() { Begin(true); }

        public void RequestCheck() { Begin(false, true); }

        void Begin(bool scan, bool check = false)
        {
            if (Running) return;
            scanRequested = scan;
            checkRequested = check;
            stop = false;
            thread = new Thread(Run) { IsBackground = true, Name = "bot" };
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
        }

        public void Stop() { stop = true; stopWhy = null; }

        public void StartWatching()
        {
            var watcher = new Thread(() =>
            {
                var reader = new Game(msg => { }, () => false);
                var win = reader.Win;
                win.ScreenOnly = true;
                string seen = null;
                while (true)
                {
                    try
                    {
                        if (!Running)
                        {

                            string state = !win.Find() ? "not running" : win.Minimized ? "minimized"
                                         : RobloxWindow.ScreenFirst && !win.InSight() ? "covered" : null;
                            Header h = null;
                            if (state == null)
                                using (var f = win.Capture())
                                {
                                    var v = reader.ViewOf(f);
                                    if (v.Problem != null) state = "unreadable";
                                    else h = reader.ReadHeader(f, v);
                                }
                            NoteScreenFirst();
                            if ((state ?? "found") != seen)
                            {
                                seen = state ?? "found";
                                Info("roblox", seen);
                            }
                            if (h != null)
                            {

                                bool other = !string.IsNullOrWhiteSpace(h.Name) && NoteAccount(h.Name);

                                if (h.Level > 0 && !other) { if (level > 0 && h.Level > level) levelledStopped = true; level = h.Level; S.LastLevel = level; }
                                if (h.Energy < 0 && last != null) { h.Energy = last.Energy; h.EnergyMax = last.EnergyMax; }
                                last = h;
                                var hr = HeaderRead;
                                if (hr != null) hr(h);
                            }
                        }
                    }
                    catch (Exception e)
                    {

                        if (MemoryShort(e)) { SayMemory(); Thread.Sleep(60000); }
                    }
                    Thread.Sleep(Running ? 5000 : 3000);
                }
            }) { IsBackground = true, Name = "watch" };
            watcher.Start();
        }

        public void Log(string msg)
        {
            string line = DateTime.Now.ToString("HH:mm:ss") + "  " + msg, problem = null;
            lock (logLock)
            {

                if (logWrites++ % 200 == 0)
                    try
                    {
                        var fi = new FileInfo(logFile);
                        if (fi.Exists && fi.Length > 5 * 1024 * 1024)
                        {
                            string old = Path.Combine(dir, "IdleMafiaBot.1.log");
                            File.Delete(old);
                            File.Move(logFile, old);
                        }
                    }
                    catch (Exception) { }

                try { File.AppendAllText(logFile, DateTime.Now.ToString("yyyy-MM-dd ") + line + Environment.NewLine); }
                catch (Exception e) { if (!logBroken) problem = DateTime.Now.ToString("HH:mm:ss") + "  Can't write the log file: " + e.Message; logBroken = true; }
            }
            var h = Logged;
            if (h != null) { h(line); if (problem != null) h(problem); }
        }

        public void LogDetails(string text)
        {
            lock (logLock)
                try { File.AppendAllText(logFile, text + Environment.NewLine); }
                catch (Exception) { }
        }

        void State(string s) { var h = StateChanged; if (h != null) h(s); }
        void Info(string key, string value) { var h = InfoChanged; if (h != null) h(key, value); }

        void Run()
        {
            game = new Game(Log, () => stop) { ProblemsDir = Path.Combine(dir, "problems") };
            game.UserBack = () => acting && S.WaitWhileBusy && game.Win.UserInputSinceBot();

            game.Saved = (reason, file) => Discord.Problem("picture " + reason, "⚠️ Problem: " + reason,
                "The bot carries on. The picture is in its problems folder too.", () => Discord.JpgOf(file), true, 180);
            game.Failed = TaskFailed;
            ShowStuck();
            DiscordStart();
            firstSeen.Clear();
            redone.Clear();
            oneJobSaid = null;
            DateTime now = DateTime.UtcNow;
            nextOps = nextBosses = nextPlaytime = nextContracts = nextCrates = nextHeists = nextFights = nextBank = nextPoints = nextJobs = nextLearn = now;
            ResetMenuNumbers();
            fullClock.Reset();
            ResetFamily(now);
            nextEquip = nextShop = nextSafehouse = nextReroll = nextMarket = now;

            nextUpgrade = nextCrew = nextCrewSlot = now;
            if (levelledStopped) { levelledStopped = false; PropertiesLevelUp(); }
            nextBriefcases = nextEvents = now; badgeSig = eventsSig = null; eventsOn.Clear(); briefcasesSaid = null;
            rerollCrew = null; rerollSaid = null;
            lastBank = DateTime.MinValue;
            learnedThisRun = false;
            lastError = null;
            frontFails = 0;
            rejoiner = new Rejoiner();
            dialogSeen = null;
            dialogUp = disconnectHint = robloxUsed = false;
            robloxUsedAt = DateTime.MinValue;
            focusBack = IntPtr.Zero;
            gameInput = now;
            tutorialSince = now;

            string ocr = Ocr.Check();
            Log((scanRequested ? "Scanning the game screens" : checkRequested ? "Checking your game" : "Bot started") + " - version " + Program.Version
                + (ocr == null ? ", reading text in " + Ocr.Language : ""));
            try
            {
                if (ocr != null) { Log("Problem: " + ocr); Info("ocr", ocr); stopWhy = ocr; return; }
                while (!stop)
                {
                    try { Tick(); lastError = null; }
                    catch (StopException) { break; }
                    catch (NeedUserException e) { Log(e.Message); stop = true; stopWhy = e.Message; }
                    catch (UserBusyException)
                    {

                        if (!waitingForUser) Log(S.WaitWhileBusy ? "You started using the PC - stopped for now, waiting until you've been idle for " + S.IdleSeconds + " s"
                                                                 : "The mouse moved during a click - trying again in a moment");
                        waitingForUser = true;
                        State("Waiting until you stop using the mouse/keyboard");
                        if (S.WaitWhileBusy) Sleep(S.IdleSeconds * 1000);
                        else
                        {

                            Sleep(1500);
                            Native.POINT a, b;
                            Native.GetCursorPos(out a);
                            int since = Environment.TickCount, start = since;
                            while (!stop && Environment.TickCount - since < 1500 && Environment.TickCount - start < 30000)
                            {
                                Thread.Sleep(250);
                                Native.GetCursorPos(out b);
                                if (Math.Abs(b.X - a.X) > 3 || Math.Abs(b.Y - a.Y) > 3) { a = b; since = Environment.TickCount; }
                            }
                        }
                    }
                    catch (WindowChangedException e)
                    {

                        repeats = e.Message == lastError ? repeats + 1 : 0;
                        lastError = e.Message;
                        if (repeats == 0) Log("Stopped this round: " + e.Message);
                        State("Stopped this round: " + e.Message + " - looking again shortly");
                        if (e.Message == Game.Disconnected) disconnectHint = true;
                        game.ForgetView();
                        Sleep(repeats > 2 ? 60000 : 10000);
                    }
                    catch (OutOfMemoryPause e)
                    {

                        MemoryShort(e.InnerException);
                        WaitForMemory(SayMemory());
                    }
                    catch (Exception e)
                    {

                        bool memory = MemoryShort(e);
                        repeats = e.Message == lastError ? repeats + 1 : 0;
                        lastError = e.Message;
                        if (repeats % 20 == 0) Log("Problem: " + e.Message + (repeats > 0 ? " (again, " + (repeats + 1) + " times)" : ""));
                        if (!memory) SnapshotOnce("error " + e.Message);
                        if (repeats == 3) Discord.Problem("error " + e.Message, "⚠️ The same problem keeps coming back", ProblemReport.NoUser(e.Message), Shot, true, 180);
                        if (memory) WaitForMemory(SayMemory());
                        else Sleep(repeats > 5 ? 15000 : 3000);
                    }
                }
            }
            finally
            {
                RerollOnly = false;
                Log("Bot stopped");

                if (stopWhy != null) Discord.Problem("stopped", "\U0001F6D1 The bot stopped", stopWhy + "\nPress Start in the bot's window to go on.", Shot, false, 0);
                stopWhy = null;
                State("Stopped");
            }
        }

        void Sleep(int ms)
        {
            int end = Environment.TickCount + ms;
            while (!stop && end - Environment.TickCount > 0) Thread.Sleep(100);
        }

        readonly object memoryLock = new object();
        DateTime memoryAt = DateTime.MinValue, memorySaid = DateTime.MinValue;

        static Exception MemoryCause(Exception e)
        {
            for (int depth = 0; e != null && depth < 8; depth++)
            {
                if (e is OutOfMemoryException || e.HResult == unchecked((int)0x8007000E) || e.HResult == unchecked((int)0x80070008)) return e;
                var all = e as AggregateException;
                if (all != null && all.InnerExceptions.Count > 1) return all.InnerExceptions.Select(MemoryCause).FirstOrDefault(x => x != null);
                e = e.InnerException;
            }
            return null;
        }

        static bool PictureFailed(Exception e)
        {
            for (int depth = 0; e != null && depth < 8; depth++, e = e.InnerException)
                if (e is ArgumentException && (e.StackTrace ?? "").Contains("System.Drawing.")) return true;
            return false;
        }

        internal bool MemoryShort(Exception e)
        {
            lock (memoryLock)
            {
                if (MemoryCause(e) == null && !(PictureFailed(e) && DateTime.UtcNow < memoryAt.AddMinutes(5))) return false;
                memoryAt = DateTime.UtcNow;
                return true;
            }
        }

        bool SayMemory()
        {
            lock (memoryLock)
            {
                if (DateTime.UtcNow < memorySaid.AddMinutes(10)) return false;
                memorySaid = DateTime.UtcNow;
            }
            string text;
            try { text = Native.MemoryText(); }
            catch (Exception) { text = "not known"; }
            Log("Memory: " + text);
            return true;
        }

        public void NoteMemory(Exception e)
        {
            try { if (MemoryShort(e)) SayMemory(); }
            catch (Exception) { }
        }

        void WaitForMemory(bool say)
        {
            try
            {
                Ocr.Release();
                System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }
            catch (Exception) { }
            if (say) Log("The PC ran out of memory - the bot waits a minute before going on");
            State("The PC ran out of memory - waiting a minute before going on");
            Sleep(60000);
        }

        void Tick()
        {
            DiscordSummary();
            if (!game.Win.Find())
            {

                dialogUp = false;
                bool touched = rejoiner.Active ? Native.LastInputTick() != stepInput : game.Win.UserInputSinceBot();
                var step = rejoiner.OnClosed(DateTime.UtcNow, touched, S.Rejoin, robloxUsed);
                Away("closed", "⚠️ Roblox isn't open", S.Rejoin && (step != Rejoiner.Step.Wait || rejoiner.Active) ? "The bot starts the game again." : "The bot waits until Roblox is open again.");
                if (TakeRejoinStep(step)) return;
                State(rejoiner.Status ?? "Waiting for Roblox to be opened...");
                Sleep(4000);
                return;
            }
            if (game.Win.IsForeground && game.Win.UserActiveWithin(5000)) { robloxUsed = true; robloxUsedAt = DateTime.UtcNow; }

            Header h = null;
            if (!game.Win.Minimized)
            {

                string unreadable = null;
                Game.Disconnect dialog = null;
                bool looked;
                using (var f = game.Capture())
                {
                    var v = game.ViewOf(f);
                    if (v.Problem != null) { unreadable = v.Language != null ? v.Problem : string.Format("Can't read the game in this {0}x{1} window: {2}", f.Width, f.Height, v.Problem); NoteLoading(f); }
                    else
                    {
                        h = game.ReadHeader(f, v);
                        NoteRobloxBar(game.RobloxBar);
                        if (S.Events && !game.RobloxBar) NoteBadges(f);
                        NoteMenuNumbers(f);
                    }
                    looked = LookForDisconnect(f, h == null || (h.Energy < 0 && h.Stamina < 0 && h.Health < 0), ref dialog);
                }
                NoteEmptyPictures();
                if (looked) dialogUp = dialog != null;
                if (unreadable != null || dialogUp) readOk = false;
                if (unreadable != null && !dialogUp && !rejoiner.Active) Away("unreadable", "⚠️ The bot can't read the game", unreadable);
                if (unreadable != null || dialogUp || (rejoiner.Active && !looked))
                {

                    if (!looked || !Offline(dialog)) { State(rejoiner.Status ?? unreadable ?? Game.Disconnected); WatchRoblox(4000); }
                    return;
                }
                GameReadable();
                if (awaySince != DateTime.MinValue) BackIn();

                if (!string.IsNullOrWhiteSpace(h.Name) && NoteAccount(h.Name))
                {
                    State("Another account is in Roblox: taking its own settings");
                    Sleep(2500);
                    return;
                }
                if (h.Energy < 0 && last != null) { h.Energy = last.Energy; h.EnergyMax = last.EnergyMax; }

                bool xpUp = h.Level <= 0 && h.XpNext > 0 && h.XpNext == xpNextLast && xpNextSeen > 0 && h.XpNext > xpNextSeen;
                if (h.XpNext > 0 && h.XpNext == xpNextLast) xpNextSeen = h.XpNext;
                xpNextLast = h.XpNext;
                if (xpUp) { game.MenuChanged(); PropertiesLevelUp(); nextBosses = DateTime.UtcNow; }
                if (h.Level > 0)
                {

                    if (h.Level > level) { game.MenuChanged(); PropertiesLevelUp(); if (level > 0) nextBosses = DateTime.UtcNow; }
                    level = h.Level;
                    S.LastLevel = level;
                    LevelPing(h.Level, h.XpNext);
                }
                last = h;
                NoteFullBars(h);
                var hr = HeaderRead;
                if (hr != null) hr(h);

                if (h.Stamina < 0)
                {
                    int st = staminaRegen.Guess(DateTime.UtcNow);
                    if (st >= 0) { h.Stamina = st; h.StaminaMax = staminaRegen.Max; }
                }
            }

            if (scanRequested)
            {
                if (S.WaitWhileBusy && game.Win.UserActiveWithin(S.IdleSeconds * 1000))
                {
                    State("Waiting until you stop using the mouse/keyboard (scan ready)");
                    Sleep(1500);
                    return;
                }

                if (!Act(delegate { try { Scan(); } catch (NeverPressException) { Log("--- Scan stopped: a press was refused ---"); } })) return;
                scanRequested = false;
                stop = true;
                return;
            }

            if (checkRequested)
            {

                if (S.WaitWhileBusy && game.Win.UserActiveWithin(S.IdleSeconds * 1000))
                {
                    State("Waiting until you stop using the mouse/keyboard (check ready)");
                    Sleep(1500);
                    return;
                }
                if (!Act(delegate { CheckGame(); })) return;
                checkRequested = false;
                stop = true;
                return;
            }

            if (h == null)
            {

                if (game.Win.Handle == readHandle && readOk) rejoiner.Playing(DateTime.UtcNow);
                if (S.WaitWhileBusy && game.Win.UserActiveWithin(S.IdleSeconds * 1000))
                {
                    State("Roblox is minimized - restoring it once you stop using the mouse/keyboard");
                    Sleep(1500);
                    return;
                }
                if (frontFails == 0) Log("Roblox is minimized - restoring it");
                Act(delegate { });
                return;
            }

            if (SweepRobloxBar()) return;

            if (RerollOnly)
            {

                if (S.WaitWhileBusy && game.Win.UserActiveWithin(S.IdleSeconds * 1000))
                {
                    State("Waiting until you stop using the mouse/keyboard (only rerolling)");
                    Sleep(1500);
                    return;
                }

                if (DateTime.UtcNow < nextReroll)
                {
                    State("Only rerolling: the next go at " + nextReroll.ToLocalTime().ToString("HH:mm"));
                    Sleep(2000);
                    return;
                }
                if (DisconnectBeforeRound()) return;
                State("Working: only rerolling the crew");
                Act(delegate
                {
                    Log("Doing: only rerolling the crew" + (h.Cash >= 0 ? " (" + M(h.Cash) + " on hand)" : ""));
                    taskNow = "rerolls";
                    failedNow = neutralNow = false;
                    try { DoRerolls(true); if (!failedNow && !neutralNow) TaskWorked("rerolls"); }
                    finally { taskNow = null; if (failedNow) StreakCheck("rerolls"); }
                });
                return;
            }

            LookForTutorial();
            var due = WhatIsDue(h);
            if (due.Contains("learn jobs")) due = new List<string> { "learn jobs" };
            if (due.Count == 0)
            {
                if (Once)
                {

                    Log(string.Format("Test round finished: nothing was due (energy {0}/{1}, stamina {2}/{3}, health {4}/{5})",
                        h.Energy, h.EnergyMax, h.Stamina, h.StaminaMax, h.Health, h.HealthMax));
                    stop = true;
                    return;
                }
                State("Running - nothing to do right now");
                Sleep(2500);
                return;
            }
            if (S.WaitWhileBusy && game.Win.UserActiveWithin(S.IdleSeconds * 1000))
            {
                if (!waitingForUser) Log("You're using the PC - waiting until you've been idle for " + S.IdleSeconds + " s (" + string.Join(", ", due) + " ready)");
                waitingForUser = true;
                State("Waiting until you stop using the mouse/keyboard (" + string.Join(", ", due) + " ready)");
                Sleep(1500);
                return;
            }
            if (S.WaitWhileBusy) waitingForUser = false;
            if (DisconnectBeforeRound()) return;
            State("Working: " + string.Join(", ", due));
            bool acted = Act(delegate
            {

                Log(string.Format("Doing: {0}   (energy {1}/{2}, stamina {3}/{4}, health {5}/{6})",
                    string.Join(", ", due), h.Energy, h.EnergyMax, h.Stamina, h.StaminaMax, h.Health, h.HealthMax));
                for (int i = 0; i < due.Count && !RerollOnly; i++)
                {
                    State("Working: " + Doing(due, i));
                    game.Check();
                    RunTask(due[i], h);
                    BankNext(due, i);
                }
                if (!due.Contains("learn jobs") || RerollOnly) return;

                Header h2;
                using (var f = game.Capture()) h2 = game.ReadHeader(f);
                if (h2.Energy < 0 && last != null) { h2.Energy = last.Energy; h2.EnergyMax = last.EnergyMax; }
                last = h2;
                var more = WhatIsDue(h2);
                more.Remove("learn jobs");
                if (more.Count == 0) return;
                Log(string.Format("Doing: {0}   (energy {1}/{2}, stamina {3}/{4})", string.Join(", ", more), h2.Energy, h2.EnergyMax, h2.Stamina, h2.StaminaMax));
                for (int i = 0; i < more.Count; i++)
                {
                    State("Working: " + Doing(more, i));
                    game.Check();
                    RunTask(more[i], h2);
                    BankNext(more, i);
                }
            });
            if (acted) waitingForUser = false;
            if (Once && acted) { Log("Test round finished: " + Count); stop = true; }
        }

        int loadingLookAt = Environment.TickCount - 60000;
        string versionSaid;

        void NoteLoading(Frame f)
        {
            if (Environment.TickCount - loadingLookAt < 30000) return;
            loadingLookAt = Environment.TickCount;
            if (!Game.GameLoading(f)) return;
            string v = Game.LoadingVersion(f);
            if (v == null || v == versionSaid) return;
            versionSaid = v;
            Log("The game is loading" + (Game.GameUpdated(f) ? " its new version " : ", version ") + v);
        }

        static string Doing(List<string> tasks, int i)
        {
            var rest = tasks.Skip(i + 1).ToList();
            return tasks[i] + (rest.Count == 0 ? "" : ". Next: " + string.Join(", ", rest.Take(3)) + (rest.Count > 3 ? " and " + (rest.Count - 3) + " more" : ""));
        }

        bool acting;

        bool Act(Action work)
        {
            if (!game.Win.BeginAct())
            {

                if (frontFails++ == 0) Log("Couldn't bring Roblox to the front (PC locked, or an admin window in front?) - will keep trying");
                State("Can't bring Roblox to the front - is the PC locked or an admin window in front?");
                Sleep(Math.Min(3000 << Math.Min(frontFails - 1, 4), 30000));
                return false;
            }
            if (frontFails > 0) { Log("Roblox is in front again (after " + frontFails + " tries)"); frontFails = 0; }
            bool userBack = false;
            acting = true;
            try { work(); }
            catch (UserBusyException) { userBack = true; throw; }
            finally
            {
                acting = false;

                rejoiner.Playing(DateTime.UtcNow);

                game.Win.EndAct(S.GiveFocusBack && !stop, !stop && !userBack);
            }
            return true;
        }

        bool Can(Tab t) { return game == null || !game.NotInMenu(t); }

        internal static void FullBarsFirst(List<string> due, Header h)
        {
            bool energyFull = h.EnergyMax > 0 && h.Energy >= h.EnergyMax;
            bool staminaFull = h.StaminaMax > 0 && h.Stamina >= h.StaminaMax;
            if (!energyFull && !staminaFull) return;
            int at = due.TakeWhile(t => t == "gear back" || t == "events" || t == "tutorial" || t == "operations").Count();
            var first = due.Skip(at).Where(t => (energyFull && (t == "heists" || t == "jobs"))
                || (staminaFull && (t == "takedown" || t == "give stamina" || t == "bosses" || t == "fights"))).ToList();
            if (first.Count == 0) return;
            foreach (var t in first) due.Remove(t);
            due.InsertRange(at, first);
        }

        internal sealed class FullClock
        {
            readonly List<KeyValuePair<DateTime, double>>[] hour = { new List<KeyValuePair<DateTime, double>>(), new List<KeyValuePair<DateTime, double>>() };
            readonly double[] total = new double[2];
            readonly bool[] was = new bool[2];
            DateTime lastRead = DateTime.MinValue;
            public DateTime NextLog = DateTime.MinValue;

            public void Reset() { foreach (var l in hour) l.Clear(); total[0] = total[1] = 0; was[0] = was[1] = false; lastRead = NextLog = DateTime.MinValue; }

            public void Note(DateTime now, bool?[] full)
            {
                double gap = lastRead == DateTime.MinValue ? 0 : (now - lastRead).TotalSeconds;
                lastRead = now;
                for (int i = 0; i < 2; i++)
                {
                    bool isFull = full[i] == true;

                    if (isFull && was[i] && gap > 0 && gap <= 300) { hour[i].Add(new KeyValuePair<DateTime, double>(now, gap)); total[i] += gap; }
                    was[i] = isFull;
                    hour[i].RemoveAll(p => (now - p.Key).TotalMinutes > 60);
                }
            }

            public double HourMinutes(int bar) { return hour[bar].Sum(p => p.Value) / 60; }
            public double TotalMinutes(int bar) { return total[bar] / 60; }
        }

        readonly FullClock fullClock = new FullClock();
        DateTime fullShown = DateTime.MinValue;

        void NoteFullBars(Header h)
        {
            var now = DateTime.UtcNow;
            fullClock.Note(now, new bool?[] { h.EnergyMax > 0 && h.Energy >= 0 ? (bool?)(h.Energy >= h.EnergyMax) : null,
                                              h.StaminaMax > 0 && h.Stamina >= 0 ? (bool?)(h.Stamina >= h.StaminaMax) : null });
            if ((now - fullShown).TotalSeconds >= 20)
            {
                fullShown = now;
                Info("fullbars", string.Format("Bars sat full in the last hour: energy {0}, stamina {1}", FullMin(fullClock.HourMinutes(0)), FullMin(fullClock.HourMinutes(1))));
            }
            if (fullClock.NextLog == DateTime.MinValue) fullClock.NextLog = now.AddHours(1);
            else if (now >= fullClock.NextLog)
            {
                fullClock.NextLog = now.AddHours(1);
                Log(string.Format("Bars: energy sat full {0} and stamina {1} in the last hour ({2} and {3} since Start)",
                    FullMin(fullClock.HourMinutes(0)), FullMin(fullClock.HourMinutes(1)), FullMin(fullClock.TotalMinutes(0)), FullMin(fullClock.TotalMinutes(1))));
            }
        }

        static string FullMin(double minutes) { return minutes < 0.5 ? "0 min" : Math.Round(minutes) + " min"; }

        List<string> WhatIsDue(Header h, bool rerolls = true)
        {
            var now = DateTime.UtcNow;
            var due = new List<string>();

            if (crewUnequipped && now >= gearBackHold && Can(Tab.Crew)) due.Add("gear back");

            if (S.Events && !string.IsNullOrEmpty(badgeSig) && (badgeSig != eventsSig || now >= nextEvents)) due.Add("events");
            if (tutorialAction != null && (tutorialAction != "fight" || (h.Stamina > 0 && Can(Tab.Fight)))) due.Add("tutorial");

            if (S.Operations && Can(Tab.Operations) && ByNumber(Tab.Operations, nextOps, now)) due.Add("operations");

            bool bare = crewUnequipped;
            if (S.Takedown && !bare && Can(Tab.Family) && (now >= nextTakedown || (takedownWants > 0 && h.Stamina >= 0
                && h.Stamina - Kept(h.StaminaMax) >= Math.Min(5, takedownWants)))) due.Add("takedown");

            if (S.GiveStamina && now >= nextGive && WhyNotGive(h) == null && Can(Tab.Family) && !BossDamageHold()) due.Add("give stamina");
            NoteGiveWhy(h);
            if (S.Bosses && !bare && now >= nextBosses && Can(Tab.Bosses)) due.Add("bosses");
            if (S.Playtime && now >= nextPlaytime && Can(Tab.Safehouse)) due.Add("playtime rewards");
            if (S.Contracts && Can(Tab.Contracts) && ByNumber(Tab.Contracts, nextContracts, now)) due.Add("contracts");
            if (S.Briefcases && now >= nextBriefcases) due.Add("briefcases");

            double spend = Spendable(h);
            if (S.Properties && now >= nextProperties && (propLotsOpen != 0 || propReplace || propRecheck) && Can(Tab.Properties)
                && (propRecheck || propPrice <= 0 || (spend >= 0 && spend >= propPrice * (propReplace ? 1.01 : 1)))) due.Add("properties");

            if (S.SafehouseUpgrade && (now >= nextUpgrade || (upgradeNeedsLevel > 0 && h.Level >= upgradeNeedsLevel)) && Can(Tab.Safehouse)
                && (upgradePrice < 0 || (upgradePrice > 0 && spend >= 0 && spend >= upgradePrice + PropertyKeep()))) due.Add("safehouse upgrade");

            if ((ShopOn() && now >= nextShop && Can(Tab.Shop)) || (S.Crates && now >= nextCrates && Can(Tab.Inventory) && !CratesHeld(now))
                || (S.EquipBest && now >= nextEquip) || (now >= nextTrophies && Can(Tab.Inventory))) due.Add("gear");
            if (now >= nextSafehouse && Can(Tab.Safehouse)) due.Add("read safehouse");
            if (S.SpendPoints && h.SkillPoints >= game.PointsCost && now >= nextPoints) due.Add("skill points");
            if (S.Jobs && NeedLearn() && Can(Tab.Jobs)) due.Add("learn jobs");

            if (S.Heists && now >= nextHeists && (h.Energy >= helpEnergy || (S.StartHeists && now >= nextHeistStart)) && Can(Tab.Heists) && !(S.HeistsFamilyOnly && family.NotInFamily)) due.Add("heists");

            if (S.Jobs && S.JobMode == JobMode.OneJob) NoteOneJob(h); else oneJobSaid = null;
            if (S.Jobs && now >= nextJobs && JobReady(h) && Can(Tab.Jobs) && !HeistEnergyHeld(h)) due.Add("jobs");

            if (crewQuiet && (S.CrewSlots || S.CrewFill || S.CrewReroll || S.TrainCrew)) { crewQuiet = false; nextCrew = now; }
            if (Can(Tab.Crew) && (now >= nextCrew || (S.CrewSlots && crewSlotPrice > 0 && spend >= crewSlotPrice + PropertyKeep() && now >= nextCrewSlot))) due.Add("crew");

            if (S.Fights && !bare && now >= nextFights && FightsMayUse(h) && Can(Tab.Fight)) due.Add("fights");
            if (S.FamilyWatch && now >= nextFamily && Can(Tab.Family)) due.Add("family");

            if (Settings.MarketRelistShown && (S.MarketRelist || S.MarketPending.Length > 0) && now >= nextMarket && Can(Tab.BlackMarket)) due.Add("relist");

            if (S.Bank && (now >= nextBank || (due.Count > 0 && now >= lastBank.AddMinutes(1))) && Can(Tab.Bank)) due.Add("bank");

            bool lucky = S.Events && EventOn(GameEvent.RecruitLuck);
            if (rerolls && (due.Count == 0 || lucky) && S.CrewReroll && now >= nextReroll && RerollCashOk(h, now) && Can(Tab.Crew))
                due.Insert(lucky ? due.TakeWhile(t => t == "gear back" || t == "events" || t == "tutorial").Count() : due.Count, "rerolls");

            if (rerolls && S.KeepAlive && due.Count == 0 && now >= keepAliveHold && Rejoiner.KeepAliveDue(now, GameInput)) due.Add("stay in the game");
            FullBarsFirst(due, h);

            if (cashOut && S.Bank && Can(Tab.Bank))
            {
                due.Remove("bank");
                int at = due.TakeWhile(t => t == "gear back").Count();
                if (BankFee() != 0) at = Math.Max(at, Math.Max(due.LastIndexOf("properties"), due.LastIndexOf("safehouse upgrade")) + 1);
                due.Insert(at, "bank");
            }
            return due;
        }

        sealed class MenuNumber
        {
            public DateTime Red = DateTime.MinValue, Plain = DateTime.MinValue;
            public int RedReads;
            public DateTime Visited = DateTime.MinValue;
            public bool Anyway = true;
            public DateTime IgnoreUntil = DateTime.MinValue;
            public DateTime Safety = DateTime.MinValue;
        }

        readonly Dictionary<Tab, MenuNumber> menuNumbers = new Dictionary<Tab, MenuNumber>();

        void ResetMenuNumbers()
        {
            menuNumbers[Tab.Operations] = new MenuNumber();
            menuNumbers[Tab.Contracts] = new MenuNumber();
        }

        int menuNumbersAt = Environment.TickCount - 60000;

        void NoteMenuNumbers(Frame f)
        {
            if (menuNumbers.Count == 0 || Environment.TickCount - menuNumbersAt < 20000) return;
            menuNumbersAt = Environment.TickCount;
            var v = View.Read(f);
            if (v.Problem != null) return;
            var now = DateTime.UtcNow;
            foreach (var kv in menuNumbers)
            {
                var b = v.Badge(f, kv.Key);
                if (b == true) { if (++kv.Value.RedReads >= 2) kv.Value.Red = now; }
                else if (b == false) { kv.Value.RedReads = 0; kv.Value.Plain = now; }
            }
        }

        bool ByNumber(Tab t, DateTime next, DateTime now)
        {
            MenuNumber n;
            if (!menuNumbers.TryGetValue(t, out n)) return now >= next;
            return NumberSays(n.Red, n.Plain, n.Visited, n.Anyway, n.IgnoreUntil, n.Safety, next, now);
        }

        internal static bool NumberSays(DateTime red, DateTime plain, DateTime visited, bool anyway, DateTime ignoreUntil, DateTime safety, DateTime next, DateTime now)
        {
            var seen = red > plain ? red : plain;
            if (now - seen > TimeSpan.FromMinutes(2)) return now >= next;
            if (red > plain && now >= ignoreUntil) return now >= visited.AddMinutes(1);
            if (safety == DateTime.MinValue || safety > visited.AddHours(3)) safety = visited.AddHours(3);
            return now >= next && (anyway || now >= safety);
        }

        void Visited(Tab t, bool? worked, bool anyway, DateTime safety = default(DateTime))
        {
            MenuNumber n;
            if (!menuNumbers.TryGetValue(t, out n)) return;
            var now = DateTime.UtcNow;
            if (worked == false && n.Red > n.Plain && now - n.Red < TimeSpan.FromMinutes(2)) n.IgnoreUntil = now.AddMinutes(30);
            n.Visited = now;
            n.Anyway = anyway;
            n.Safety = safety;
            n.RedReads = 0;
            n.Red = DateTime.MinValue;
        }

        sealed class Streak { public DateTime Since; public int Fails; public string Why = ""; public bool Said; }
        readonly Dictionary<string, Streak> streaks = new Dictionary<string, Streak>();
        string taskNow;
        bool failedNow;
        bool neutralNow;

        void Neutral() { neutralNow = true; }

        static string TaskTitle(string task)
        {
            switch (task)
            {
                case "give stamina": return "Stamina gifts";
                case "gear back": return "Putting the crew's gear back";
                case "gear": return "Shop, gear and crates";
                case "read safehouse": return "Reading the Safehouse";
                case "learn jobs": return "Reading the job list";
                case "stay in the game": return "Staying in the game";
                case "relist": return "Black Market relisting";
                default: return char.ToUpperInvariant(task[0]) + task.Substring(1);
            }
        }

        void TaskFailed(string why, bool own)
        {
            string task = taskNow;
            if (task == null || string.IsNullOrWhiteSpace(why)) return;
            Streak s;
            if (!streaks.TryGetValue(task, out s)) streaks[task] = s = new Streak();
            var now = DateTime.UtcNow;
            if (failedNow)
            {

                if (own) { s.Why = why.Trim(); if (s.Said) ShowStuck(); }
                return;
            }
            failedNow = true;
            if (s.Fails == 0) s.Since = now;
            s.Fails++;
            s.Why = why.Trim();
        }

        void StreakCheck(string task)
        {
            Streak s;
            if (!streaks.TryGetValue(task, out s) || s.Said || (s.Fails < 3 && DateTime.UtcNow - s.Since < TimeSpan.FromHours(1))) return;
            s.Said = true;
            string line = StuckLine(task, s);
            Log(line);
            ShowStuck();
            Discord.Problem("stuck " + task, "⚠️ " + ProblemReport.NoUser(line), "The bot keeps trying. The pictures are in its problems folder.", Shot, false, 180);
        }

        void TaskWorked(string task)
        {
            Streak s;
            if (!streaks.TryGetValue(task, out s)) return;
            streaks.Remove(task);
            if (!s.Said) return;
            Log(TaskTitle(task) + ": working again (stuck from " + s.Since.ToLocalTime().ToString("HH:mm") + ")");
            ShowStuck();
        }

        static string StuckLine(string task, Streak s)
        {
            return TaskTitle(task) + ": stuck since " + s.Since.ToLocalTime().ToString("HH:mm") + " - " + s.Why + (s.Fails > 1 ? " (" + s.Fails + " tries)" : "");
        }

        void ShowStuck()
        {
            Info("stuck", string.Join("\n", streaks.Where(kv => kv.Value.Said).Select(kv => StuckLine(kv.Key, kv.Value))));
        }

        void RunTask(string task, Header h)
        {
            taskNow = task;
            failedNow = neutralNow = false;
            try { RunTaskWatched(task, h); if (!failedNow && !neutralNow) TaskWorked(task); }
            finally { taskNow = null; if (failedNow) StreakCheck(task); }
        }

        void RunTaskWatched(string task, Header h)
        {

            try { RunTaskNow(task, h); redone.Remove(task); }
            catch (UserBusyException) { if (redone.Add(task)) DueAgain(task); throw; }
            catch (WindowChangedException e)
            {
                if (redone.Add(task)) DueAgain(task);
                else if (e.Message != Game.Disconnected)
                {
                    redone.Remove(task);
                    Later(task, 10);
                    Log(TaskTitle(task) + ": cut short twice in a row (" + e.Message + ") - it waits 10 minutes, the rest go on");
                    game.NoteFailure("cut short twice in a row: " + e.Message);
                }
                throw;
            }

            catch (NeverPressException) { redone.Remove(task); Later(task, 30); }
            catch (StopException) { throw; }
            catch (NeedUserException) { throw; }
            catch (Exception e)
            {

                redone.Remove(task);
                Later(task, 15);
                var memory = MemoryShort(e) ? MemoryCause(e) ?? e : null;
                if (firstSeen.Add("task error " + task + " " + e.Message))
                {
                    if (memory != null) Log(TaskTitle(task) + ": the PC ran out of memory (" + memory.GetType().Name + ": " + memory.Message + ") - it waits 15 minutes");
                    else Log(TaskTitle(task) + ": an error (" + e.GetType().Name + ": " + e.Message + ") - it waits 15 minutes, the rest go on");
                    LogDetails(e.ToString());
                    if (memory == null) SnapshotOnce("error in " + task);
                }
                game.NoteFailure(memory != null ? "the PC ran out of memory" : "an error: " + e.Message);
                if (memory != null) throw new OutOfMemoryPause(e);
            }
            finally
            {
                rejoiner.Playing(DateTime.UtcNow);

                if (SpendsEnergy.Contains(task)) energyRegen.Forget();
                if (SpendsStamina.Contains(task)) staminaRegen.Forget();
            }
        }

        static readonly HashSet<string> SpendsEnergy = new HashSet<string> { "jobs", "heists", "tutorial", "skill points", "learn jobs" };
        static readonly HashSet<string> SpendsStamina = new HashSet<string> { "bosses", "fights", "takedown", "give stamina", "tutorial", "skill points" };

        void Later(string task, int minutes)
        {
            var when = DateTime.UtcNow.AddMinutes(minutes);
            switch (task)
            {
                case "operations": nextOps = when; break;
                case "bosses": nextBosses = when; break;
                case "playtime rewards": nextPlaytime = when; break;
                case "contracts": nextContracts = when; break;
                case "briefcases": nextBriefcases = when; break;
                case "events": nextEvents = when; eventsSig = badgeSig; break;
                case "properties": nextProperties = when; break;
                case "safehouse upgrade": nextUpgrade = when; break;
                case "crew": nextCrew = nextCrewSlot = when; break;
                case "rerolls": nextReroll = when; break;
                case "gear back": gearBackHold = when; break;
                case "gear": nextShop = nextCrates = nextEquip = when; break;
                case "read safehouse": nextSafehouse = when; break;
                case "skill points": nextPoints = when; break;
                case "learn jobs": nextLearn = when; break;
                case "heists": nextHeists = when; break;
                case "jobs": nextJobs = when; break;
                case "fights": nextFights = when; break;
                case "bank": nextBank = when; lastBank = when.AddMinutes(-1); break;
                case "takedown": nextTakedown = when; break;
                case "give stamina": nextGive = when; break;
                case "family": nextFamily = when; break;
                case "relist": nextMarket = when; break;
                case "stay in the game": keepAliveHold = when; break;
                case "tutorial": nextTutorialLook = when; tutorialAction = null; break;
            }
        }

        DateTime keepAliveHold = DateTime.MinValue;

        readonly HashSet<string> redone = new HashSet<string>();

        void DueAgain(string task)
        {
            var now = DateTime.UtcNow;
            switch (task)
            {
                case "operations": nextOps = now; break;
                case "bosses": nextBosses = now; break;
                case "playtime rewards": nextPlaytime = now; break;
                case "contracts": nextContracts = now; break;
                case "briefcases": nextBriefcases = now; break;
                case "events": nextEvents = now; break;
                case "properties": nextProperties = now; break;
                case "safehouse upgrade": nextUpgrade = now; break;
                case "crew": nextCrew = now; break;
                case "rerolls": nextReroll = now; break;
                case "gear": nextCrates = nextEquip = now; break;
                case "read safehouse": nextSafehouse = now; break;
                case "skill points": nextPoints = now; break;
                case "learn jobs": nextLearn = now.AddMinutes(5); break;
                case "heists": nextHeists = now; break;
                case "jobs": nextJobs = now; break;
                case "fights": nextFights = now; break;
                case "bank": nextBank = now; break;
                case "takedown": nextTakedown = now; break;
                case "give stamina": nextGive = now; break;
                case "family": nextFamily = now; break;
                case "relist": nextMarket = now; break;
            }
        }

        void RunTaskNow(string task, Header h)
        {
            switch (task)
            {
                case "operations": DoOperations(); break;
                case "bosses": DoBosses(); break;
                case "playtime rewards":
                {
                    int got = game.ClaimPlaytimeRewards();
                    Count.Rewards += Report(got, "Claimed {0} playtime reward(s)");

                    int wait = game.PlaytimeNextSeconds;
                    nextPlaytime = DateTime.UtcNow.AddSeconds(got > 0 || wait == 0 ? 600 : wait > 0 ? Math.Min(wait + 20, 3 * 3600) : wait == -2 ? 3 * 3600 : 1200);
                    break;
                }
                case "contracts":
                {
                    int got = game.ClaimContracts();
                    Count.Rewards += Report(Math.Max(0, got), "Claimed {0} contract task(s)");
                    nextContracts = DateTime.UtcNow.AddMinutes(got > 0 ? 15 : 30);

                    Visited(Tab.Contracts, got < 0 ? (bool?)null : got > 0, got < 0);
                    break;
                }
                case "events": DoEvents(); break;
                case "briefcases": DoBriefcases(); break;
                case "tutorial": DoTutorial(); break;
                case "properties": DoProperties(); break;
                case "safehouse upgrade": DoSafehouseUpgrade(); break;
                case "crew": DoCrew(); break;
                case "rerolls": DoRerolls(false); break;
                case "gear back":
                {

                    game.OpenTab(Tab.Crew);
                    game.ScrollPage(0.62, 40);
                    if (DoEquip() || !crewUnequipped) break;
                    gearBackFails++;
                    int wait = gearBackFails == 1 ? 1 : gearBackFails == 2 ? 5 : 15;
                    gearBackHold = DateTime.UtcNow.AddMinutes(wait);
                    Log("Crew: the gear isn't back on yet - bosses, player fights and the Takedown wait; AUTO EQUIP BEST again in " + wait + " minute" + (wait == 1 ? "" : "s"));
                    break;
                }
                case "gear": DoGear(); break;
                case "read safehouse": ReadSafehouse(); break;
                case "skill points":
                {

                    int ups = game.SpendPoints(S.PointsStat, h.SkillPoints);
                    int stat = game.SpentOn >= 0 ? game.SpentOn : S.PointsStat;
                    Report(ups, "Skill points: " + Game.StatNames[stat] + " +{0}");
                    if (ups > 0 && stat == 5) PropertiesLevelUp();

                    if (ups == 0 && game.SavingFor != null && pointsSaid != Game.StatNames[stat] + game.PointsCost)
                    {
                        pointsSaid = Game.StatNames[stat] + game.PointsCost;
                        Log("Skill points: saving up for " + game.SavingFor);
                    }
                }
                    nextPoints = DateTime.UtcNow.AddMinutes(game.PointsFailed ? 60 : 10);
                    break;
                case "learn jobs": LearnJobs(); break;
                case "heists": DoHeists(); break;
                case "jobs": DoJobs(); break;
                case "fights": DoFights(); break;
                case "bank": DoBank(); break;
                case "stay in the game": game.KeepAlive(); break;
                case "takedown": DoTakedown(); break;
                case "give stamina": DoGiveStamina(); break;
                case "family": DoFamilyWatch(); break;
                case "relist": DoRelist(); break;
            }
        }

        int Report(int n, string format)
        {
            if (n > 0) Log(string.Format(format, n));
            return n;
        }

        DateTime nextMarket;

        void DoRelist()
        {
            var now = DateTime.UtcNow;
            nextMarket = now.AddMinutes(Math.Max(5, Math.Min(60, S.MarketMinutes)));
            if (!PendingBackUp()) return;
            if (!S.MarketRelist) { Neutral(); return; }
            var shop = game.OpenMyShop();
            if (shop == null) return;
            int used = shop.Used;
            if (shop.Rows.Count == 0)
            {
                if (used == 0) SayOnce("market none", "Black Market: there are no listings in your shop - nothing to relist (the bot only puts your own back up)");
                Neutral();
                return;
            }
            var done = new List<string>();
            var turned = new HashSet<string>();
            for (int go = 0; go < Math.Max(1, used) && go < 40; go++)
            {
                if (go > 0) { shop = game.OpenMyShop(); if (shop == null) break; }
                shop = game.MyShopToEnd(shop);
                MarketListing last;
                using (var f = game.Capture()) last = game.ReadMyShop(f, Game.Prices.Last).Rows.LastOrDefault();
                if (last == null) break;
                if (!last.ForSale)
                {
                    SayOnce("market closed", "Black Market: your listings don't say FOR SALE right now (is the shop closed?) - nothing relisted. The bot never pays rent");
                    break;
                }
                if (turned.Contains(Parse.Key(last.Name) + "|" + last.Left + "|" + last.Price)) break;
                if (last.Price <= 0 || last.Left <= 0 || last.Name.Length < 3)
                {
                    Log("Black Market: couldn't read \"" + last.Name + "\" (" + (last.Left > 0 ? last.Left + " left" : "how many are left unread") + ", "
                        + (last.Price > 0 ? last.Price + " each" : "its price unread") + ") - left in your shop as it is");
                    game.Snapshot("market listing unread", 60);
                    break;
                }
                S.MarketPending = last.Name + "|" + last.Left + "|" + last.Price;
                SaveSettings();
                var removed = game.RemoveListing(last, used);
                if (removed == false)
                {
                    S.MarketPending = "";
                    SaveSettings();
                    game.NoteFailure("Black Market: " + last.Name + " wasn't taken out");
                    break;
                }
                if (removed == null)
                {

                    Log("Black Market: couldn't tell whether " + last.Name + " was taken out - looking again in 2 minutes");
                    game.NoteFailure("Black Market: " + last.Name + " unclear");
                    nextMarket = DateTime.UtcNow.AddMinutes(2);
                    break;
                }
                int listed;
                string why = game.ListItem(last.Name, last.Left, last.Price, out listed);
                if (why != null)
                {
                    Log("Black Market: " + last.Name + " is in your inventory again but didn't go back up (" + why + ") - trying again in 2 minutes, no collection deposit meanwhile");
                    game.NoteFailure("Black Market: " + last.Name + " didn't go back up");
                    nextMarket = DateTime.UtcNow.AddMinutes(2);
                    break;
                }
                S.MarketPending = "";
                SaveSettings();
                turned.Add(Parse.Key(last.Name) + "|" + listed + "|" + last.Price);
                done.Add(last.Name + " (" + listed + (listed < last.Left ? " of " + last.Left : "") + " at " + last.Price + ")");
            }
            if (done.Count > 0) Log("Black Market: relisted " + string.Join(", ", done) + " - back on top");
        }

        bool PendingBackUp()
        {
            if (S.MarketPending.Length == 0) return true;
            var p = S.MarketPending.Split('|');
            int qty, price;
            if (p.Length != 3 || !int.TryParse(p[1], out qty) || !int.TryParse(p[2], out price) || qty <= 0 || price <= 0 || p[0].Length < 3)
            {
                S.MarketPending = "";
                SaveSettings();
                return true;
            }
            string name = p[0];

            var shop = game.OpenMyShop();
            if (shop == null) { nextMarket = DateTime.UtcNow.AddMinutes(2); return false; }
            var end = game.MyShopToEnd(shop);
            if (shop.Rows.Concat(end.Rows).Any(r => Game.SameItem(r.Name, name) && r.Left == qty))
            {
                Log("Black Market: " + name + " is up in your shop already");
                S.MarketPending = "";
                SaveSettings();
                return true;
            }
            int listed;
            string why = game.ListItem(name, qty, price, out listed);
            if (why == null)
            {
                Log("Black Market: put " + name + " back up (" + listed + " at " + price + " each)");
                S.MarketPending = "";
                SaveSettings();
                return true;
            }
            if (why == Game.NotTradable)
            {
                Log("Black Market: " + name + " isn't among your tradable items any more - nothing to put back up");
                S.MarketPending = "";
                SaveSettings();
                return true;
            }
            Log("Black Market: " + name + " still didn't go back up (" + why + ") - again in 2 minutes, no collection deposit meanwhile");
            game.NoteFailure("Black Market: " + name + " didn't go back up");
            nextMarket = DateTime.UtcNow.AddMinutes(2);
            return false;
        }

        bool NeedLearn()
        {
            if (DateTime.UtcNow < nextLearn || level <= 0) return false;

            int top = Catalog.Count == 0 ? -1 : Catalog.Max(j => GameData.CityIndex(j.City));
            if (top + 1 < GameData.CityLevels.Length && level >= GameData.CityLevels[top + 1]) return true;
            if (learnedThisRun) return false;

            if (catalogBuiltIn) return true;

            if (S.JobMode == JobMode.Mastery && !JobsReadRecently()) return true;
            return Catalog.Count == 0;
        }

        bool JobsReadRecently()
        {
            DateTime at;
            if (!DateTime.TryParse(S.JobsReadAt, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out at)) return false;
            var age = DateTime.UtcNow - at;
            return age >= TimeSpan.Zero && age < TimeSpan.FromHours(12);
        }

        void LearnJobs()
        {
            nextLearn = DateTime.UtcNow.AddMinutes(30);
            if (!game.OpenTab(Tab.Jobs)) { nextLearn = DateTime.UtcNow.AddMinutes(5); return; }
            Log("Reading every job and its mastery (takes a moment)...");
            int before = Catalog.Count;
            int seen = game.LearnJobs(Catalog, level, true);
            GameData.Renumber(Catalog);
            SaveCatalog();
            learnedThisRun = true;

            if (seen > 0) { S.JobsReadAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture); catalogBuiltIn = false; }
            Log(string.Format("Read {0} jobs ({1} new).", seen, Catalog.Count - before));
            ReportTarget();
        }

        void SaveCatalog()
        {
            CatalogView = Catalog.Select(j => j.Copy()).ToList();

            try
            {
                GameData.SaveCatalog(catalogFile, Catalog);
                if (S.Account.Length > 0) { Directory.CreateDirectory(Accounts.Folder(dir, S.Account)); GameData.SaveCatalog(Accounts.Jobs(dir, S.Account), Catalog); }
            }
            catch (Exception e) { if (firstSeen.Add("jobs.txt not saved")) Log("Couldn't save jobs.txt: " + e.Message); }
            var h = CatalogChanged;
            if (h != null) h();
        }

        public JobInfo PickJob(Header h) { return PickJob(h, Catalog); }

        public JobInfo PickJob(Header h, List<JobInfo> catalog)
        {
            if (S.JobMode == JobMode.OneJob)
            {

                JobInfo one;
                return OneJobWhy(h, catalog, out one) == null && !JobAside(one) ? one : null;
            }
            int lv = level > 0 ? level : 1;
            int maxE = h != null && h.EnergyMax > 0 ? h.EnergyMax : int.MaxValue;

            var open = catalog.Where(j => j.TierLevel <= lv && j.Cost > 0 && j.Cost <= maxE && j.Unseen < 2 && !JobAside(j)).ToList();
            if (S.JobMode == JobMode.Mastery)
            {
                var next = open.OrderBy(j => j.Order).FirstOrDefault(j => j.MasteryRank < MasteryGold);
                if (next != null || open.Count == 0) return next;

            }
            return open.OrderByDescending(j => j.XpPerEnergy).ThenByDescending(j => j.Xp).FirstOrDefault();
        }

        public bool MasteryDone(JobInfo picked) { return S.JobMode == JobMode.Mastery && picked != null && picked.MasteryRank >= MasteryGold; }

        const int MasteryGold = 3;

        string oneJobSaid;

        public string OneJobWhy(Header h, List<JobInfo> catalog, out JobInfo job)
        {

            int lv = level > 0 ? level : h != null && h.Level > 0 ? h.Level : 1;
            return OneJobWhy(S.OneJob, catalog, lv, h != null && h.EnergyMax > 0 ? h.EnergyMax : 0, out job);
        }

        internal static string OneJobWhy(string name, List<JobInfo> catalog, int level, int energyMax, out JobInfo job)
        {
            job = null;
            name = (name ?? "").Trim();
            if (name.Length == 0) return OneJobNone;
            job = catalog.FirstOrDefault(j => j.Name == name) ?? catalog.FirstOrDefault(j => string.Equals(j.Name, name, StringComparison.OrdinalIgnoreCase));
            if (job == null || job.Cost <= 0) return "isn't in the job list";
            if (job.Unseen >= 2) return "wasn't in the game's job list";
            if (job.TierLevel > Math.Max(1, level)) return "opens at level " + job.TierLevel;
            if (energyMax > 0 && job.Cost > energyMax) return "needs " + job.Cost + " energy and your bar holds " + energyMax;
            return null;
        }

        public const string OneJobNone = "no job picked";

        public DateTime JobAsideUntil(string name)
        {
            DateTime until;
            lock (jobAside) return name != null && jobAside.TryGetValue(name, out until) && DateTime.UtcNow < until ? until : DateTime.MinValue;
        }

        void NoteOneJob(Header h)
        {
            JobInfo job;
            string why = OneJobWhy(h, Catalog, out job);
            string name = (S.OneJob ?? "").Trim();
            string said = why == null ? null : name + "|" + why;
            if (said == oneJobSaid) return;
            if (why == OneJobNone) Log("Jobs: no job picked - no jobs until you click one (One job, Energy page)");
            else if (why != null)
                Log("Jobs: \"" + name + "\" " + why + " - no jobs" + (why.StartsWith("opens at") ? " until then" : why.StartsWith("needs") ? " until it holds more" : "")
                    + " (One job, Energy page)");

            else if (oneJobSaid != null && oneJobSaid.StartsWith(name + "|")) Log("Jobs: \"" + job.Name + "\" can be done now - doing only that job (One job)");
            oneJobSaid = said;
            Info("job", why ?? job.Name);
        }

        readonly Dictionary<string, DateTime> jobAside = new Dictionary<string, DateTime>();
        readonly Dictionary<string, int> jobMisses = new Dictionary<string, int>();

        bool JobAside(JobInfo j)
        {
            DateTime until;
            lock (jobAside) return jobAside.TryGetValue(j.Name, out until) && DateTime.UtcNow < until;
        }

        void JobMissed(JobInfo job, string why)
        {
            int n;
            jobMisses.TryGetValue(job.Name, out n);
            jobMisses[job.Name] = ++n;
            game.NoteFailure("\"" + job.Name + "\" " + why);
            if (n < 2) return;
            jobMisses.Remove(job.Name);
            lock (jobAside) jobAside[job.Name] = DateTime.UtcNow.AddHours(1);
            Log("Jobs: \"" + job.Name + "\" " + why + " twice in a row - it waits an hour, "
                + (S.JobMode == JobMode.OneJob ? "no other job meanwhile (One job)" : "the next best job meanwhile"));
            ReportTarget();
            nextJobs = DateTime.UtcNow;
        }

        bool JobReady(Header h)
        {
            var job = PickJob(h);
            if (job == null || h.Energy < job.Cost) return false;

            if (h.EnergyMax <= 0) return true;
            if (h.Energy >= h.EnergyMax - 2 * job.Cost) return true;
            return h.XpNext > 0 && h.Xp >= 0 && job.Xp > 0 && (long)(h.Energy / job.Cost) * job.Xp >= h.XpNext - h.Xp;
        }

        void ReportTarget()
        {

            if (S.Jobs && S.JobMode == JobMode.OneJob) NoteOneJob(last);
            var job = PickJob(last);
            Info("job", job == null ? (S.JobMode == JobMode.Mastery ? "all jobs have the chosen mastery" : S.JobMode == JobMode.OneJob ? "one job: not now" : "none") :
                string.Format("{0} ({1} energy, {2} XP, mastery {3}{4})", job.Name, job.Cost, job.Xp, job.MasteryLabel, MasteryDone(job) ? ", the best XP job now that every job is Gold" : ""));
        }

        void DoJobs()
        {
            var job = PickJob(last);
            if (job == null) { Neutral(); return; }

            if (last != null && last.Energy >= 0 && last.Energy < job.Cost) { Neutral(); return; }
            if (MasteryDone(job) && firstSeen.Add("mastery done " + job.Name))
                Log("Jobs: every job open has Gold mastery - doing the best XP job, \"" + job.Name + "\", until a new one opens");
            if (!game.OpenTab(Tab.Jobs)) { nextJobs = DateTime.UtcNow.AddMinutes(2); return; }
            var card = game.FindJob(job, Catalog);
            if (card == null)
            {

                int ci = GameData.CityIndex(job.City);
                if (ci >= 0 && game.ExpandCity(ci)) card = game.FindJob(job, Catalog);
            }
            if (card == null)
            {

                var saw = game.JobsSeen.Take(12).ToList();
                Log("Couldn't find \"" + job.Name + "\" in the job list - trying again in 5 minutes"
                    + (saw.Count > 0 ? " (it showed: " + string.Join(", ", saw) + (game.JobsSeen.Count > 12 ? "..." : "") + ")" : ""));
                game.Snapshot("job not found " + job.Name, 60);
                nextJobs = DateTime.UtcNow.AddMinutes(5);
                JobMissed(job, "wasn't found in the job list");
                return;
            }

            var lm = Regex.Match(card.ButtonText ?? "", @"LEVEL\s*([0-9OIlS]+)", RegexOptions.IgnoreCase);
            int opensAt;
            if (lm.Success && int.TryParse(Parse.Digits(lm.Groups[1].Value), out opensAt) && opensAt > job.TierLevel && GameData.InCityLevels(job.City, opensAt))
            {

                if (S.JobMode != JobMode.OneJob) Log(string.Format("\"{0}\" opens at level {1} - picking another job", job.Name, opensAt));
                job.TierLevel = opensAt;
                SaveCatalog();
                ReportTarget();
                nextJobs = DateTime.UtcNow;
                return;
            }

            if (card.Button == ButtonState.Locked)
            {
                nextJobs = DateTime.UtcNow.AddMinutes(5);
                JobMissed(job, "shows \"" + (card.ButtonText ?? "").Trim() + "\" where DO JOB should be");
                return;
            }
            int energy = last != null ? last.Energy : job.Cost;
            int times = Math.Max(1, Math.Min(30, energy / Math.Max(1, job.Cost)));
            if (S.JobMode == JobMode.Mastery && job.MasteryRank >= 0 && job.MasteryRank < MasteryGold && job.MasteryCur >= 0)
            {

                int left = 100 - job.MasteryCur;
                if (left > 0) times = Math.Min(times, left);
            }
            int rankBefore = job.MasteryRank;
            int done = game.DoJob(card, times, Catalog);
            Count.Jobs += done;

            if (done > 0) jobMisses.Remove(job.Name);
            else if (card.Button != ButtonState.Dim) JobMissed(job, "couldn't be done (its DO JOB didn't take)");

            if (done > 0 && last != null && last.Energy >= 0) last.Energy = Math.Max(0, last.Energy - done * job.Cost);
            if (done > 0) energyRegen.Forget();
            string before = job.MasteryLabel, label = null;
            for (int look = 0; look < 3; look++)
            {

                if (look > 0) Sleep(700);
                using (var f = game.Capture())
                {

                    var fresh = game.ReadJobCards(f, Catalog, false).Where(c => c.Info == job).OrderBy(c => Math.Abs(c.NameY - card.NameY)).FirstOrDefault();
                    if (fresh != null) card = fresh;
                    label = Game.ReadMasteryText(f, card);
                    Game.ApplyMastery(job, label, f, card);
                }
                if (done == 0 || job.MasteryLabel != before || job.MasteryRank >= 3) break;
            }
            if (done > 0)
            {
                Log(string.Format("Did \"{0}\" x{1}, mastery now {2}", job.Name, done, job.MasteryLabel));

                if (job.MasteryLabel == before && job.MasteryRank < 3)
                {
                    Log("  (mastery label read as \"" + label + "\")");
                    if (!Game.IsMasteryLabel(label)) SnapshotOnce("mastery label " + Game.MasteryWording(label));
                }
            }

            if (S.JobMode == JobMode.Mastery && job.MasteryRank >= MasteryGold && rankBefore < MasteryGold) Log("Finished the mastery of \"" + job.Name + "\" - moving to the next job");
            SaveCatalog();
            ReportTarget();
            nextJobs = DateTime.UtcNow.AddSeconds(done > 0 ? 2 : 30);
        }

        void DoHeists()
        {
            nextHeists = DateTime.UtcNow.AddMinutes(3);
            if (!game.OpenTab(Tab.Heists)) return;
            game.HeistsTop();
            if (S.StartHeists && DateTime.UtcNow >= nextHeistStart) StartHeist();
            bool lookedAgain = false;
            int energyLeft = int.MaxValue, favorsLeft = int.MaxValue;
            for (int i = 0; i < 30; i++)
            {

                var h = ReadBarsSettled(5000, x => x.Energy >= 0);
                if (h.Energy < 0)
                {

                    if (energyLeft != int.MaxValue) h.Energy = energyLeft;
                    else { Log("Couldn't read energy - no heist helps this round"); game.NoteFailure("the energy bar didn't read"); SnapshotOnce("heists energy unreadable"); return; }
                }
                if (Math.Min(h.Energy, energyLeft) < helpEnergy) return;
                int favors;
                List<HeistRow> rows;
                using (var f = game.Capture()) { favors = game.ReadFavors(f); rows = game.ReadHeists(f); }
                if (favors < 0) using (var f = game.Capture()) favors = game.ReadFavors(f);

                if (favors > 0 && favorsLeft == int.MaxValue)
                {
                    int again;
                    using (var f = game.Capture()) again = game.ReadFavors(f, 1);
                    if (again < 0) using (var f = game.Capture()) again = game.ReadFavors(f);
                    if (again != favors) favors = -1;
                }

                int cost = rows.Where(r => r.Energy > 0).Select(r => r.Energy).DefaultIfEmpty(-1).Max();
                if (cost > 0) helpEnergy = Math.Max(HelpEnergy, cost);

                else if (h.EnergyMax > 0) helpEnergy = Math.Max(helpEnergy, (int)Math.Ceiling(h.EnergyMax / 20.0));
                if (Math.Min(h.Energy, energyLeft) < helpEnergy) return;
                if (favors >= 0) { favors = Math.Min(favors, favorsLeft); Info("favors", favors.ToString()); }
                if (favors == 0) { Log("No heist favors left - checking again in an hour"); nextHeists = DateTime.UtcNow.AddHours(1); return; }
                if (favors < 0)
                {

                    Log("Couldn't read how many favors are left - not helping this time");
                    game.NoteFailure("the favors left didn't read");
                    game.Snapshot("heist favors unread", 60);
                    nextHeists = DateTime.UtcNow.AddMinutes(15);
                    return;
                }

                double bar = JobXpFor5();
                var best = rows.Where(r => r.CanHelp && S.WouldHelp(r.YourFamily, r.XpPerHelp, bar))
                               .OrderByDescending(r => r.YourFamily).ThenByDescending(r => r.XpPerHelp).FirstOrDefault();
                if (best == null)
                {

                    if (i == 0) { nextHeists = DateTime.UtcNow.AddMinutes(10); return; }
                    if (lookedAgain) return;
                    lookedAgain = true;
                    game.Wait(1500);
                    game.HeistsTop();
                    continue;
                }
                lookedAgain = false;
                if (Math.Min(h.Energy, energyLeft) < Math.Max(helpEnergy, best.Energy)) return;
                game.HelpHeist(best);

                string toast = game.ReadToast(900, t => Game.Refused(t) || Parse.Has(t, "HEIST") || Parse.Has(t, "HELP") || Parse.Has(t, "FAVOR"));
                if (Game.Refused(toast))
                {
                    Log("Heist help refused: " + toast);
                    nextHeists = DateTime.UtcNow.AddMinutes(10);
                    return;
                }
                if (toast != null) SnapshotOnce("heist help notification");
                energyLeft = Math.Min(h.Energy, energyLeft) - Math.Max(helpEnergy, best.Energy);

                if (last != null && last.Energy >= 0) last.Energy = Math.Max(0, Math.Min(last.Energy, energyLeft));
                favorsLeft = favors - 1;
                Count.Helps++;
                Log(string.Format("Helped \"{0}\" ({1}){2}", best.Name, best.XpPerHelp >= 0 ? "+" + best.XpPerHelp + " XP" : "XP not read",
                    best.YourFamily ? " - a heist of your family" : ""));
                nextHeists = DateTime.UtcNow.AddSeconds(30);
            }
        }

        DateTime nextHeistStart;

        const int HelpEnergy = 6;
        int helpEnergy = HelpEnergy;
        int heistEnergy;

        DateTime heistDay;
        int heistsToday;
        const int HeistsPerDay = 2;

        bool HeistEnergyHeld(Header h)
        {
            return S.StartHeists && heistEnergy > 0 && h.Energy >= 0 && h.Energy < heistEnergy && (h.EnergyMax <= 0 || heistEnergy <= h.EnergyMax);
        }

        void StartHeist()
        {
            nextHeistStart = DateTime.UtcNow.AddMinutes(10);
            if (heistDay != DateTime.Today) { heistDay = DateTime.Today; heistsToday = 0; }
            if (heistsToday >= HeistsPerDay)
            {

                heistEnergy = 0;
                nextHeistStart = DateTime.Today.AddDays(1).AddMinutes(2).ToUniversalTime();
                return;
            }
            OcrLine button;
            using (var f = game.Capture()) button = game.NoActiveHeist(f);
            if (button == null) { heistEnergy = 0; return; }
            game.PressStartAHeist(button);
            List<HeistOffer> offers = null;
            OcrLine close = null;
            HeistOffer pick = null;
            for (int look = 0; look < 6 && pick == null; look++)
            {
                if (look > 0) { game.ScrollPage(0.55, -2); game.Wait(500); }
                using (var f = game.Capture()) offers = game.ReadHeistOffers(f, out close);
                if (offers == null) { game.Wait(700); continue; }
                pick = offers.FirstOrDefault(o => Game.SameHeist(o.Name, S.StartHeist) && o.Start != null);

                if (pick == null && offers.Any(o => Game.SameHeist(o.Name, S.StartHeist) && o.Start == null))
                    pick = offers.Where(o => o.Start != null).OrderByDescending(o => o.Level).FirstOrDefault();
            }
            if (offers == null)
            {
                Log("Heists: the START A HEIST window didn't show");
                game.Snapshot("heist start window not read", 60);
                nextHeistStart = DateTime.UtcNow.AddMinutes(60);
                return;
            }
            if (pick == null)
            {
                SayOnce("heist pick " + S.StartHeist, "Heists: \"" + S.StartHeist + "\" wasn't found in the START A HEIST window - no heist started (looking again in an hour)");
                SnapshotOnce("heist pick not found");
                game.CloseHeistOffers(close);
                nextHeistStart = DateTime.UtcNow.AddMinutes(60);
                return;
            }
            if (!Game.SameHeist(pick.Name, S.StartHeist)) SayOnce("heist lower " + pick.Name, "Heists: " + S.StartHeist + " is above your level - starting " + pick.Name + " instead");
            var h = ReadBarsSettled(5000, x => x.Energy >= 0 && x.Cash >= 0);
            if (pick.Energy > 0 && (h.Energy < 0 || h.Energy < pick.Energy))
            {

                heistEnergy = pick.Energy;
                SayOnce("heist energy " + pick.Name, "Heists: saving " + pick.Energy + " energy to start " + pick + " - the jobs wait for it");
                game.CloseHeistOffers(close);
                nextHeistStart = DateTime.UtcNow.AddMinutes(2);
                return;
            }
            bool took = false;
            try
            {
                if (pick.Stake > 0 && (h.Cash < 0 || h.Cash < pick.Stake))
                {
                    took = WithdrawFor(pick.Stake, false);
                    double cash = CashNow();
                    if (cash < pick.Stake)
                    {
                        SayOnce("heist cash " + pick.Name, "Heists: not enough cash on hand for " + pick + "'s stake - looking again in 30 minutes");
                        game.CloseHeistOffers(close);
                        nextHeistStart = DateTime.UtcNow.AddMinutes(30);
                        return;
                    }

                    if (!game.OpenTab(Tab.Heists)) return;
                    game.HeistsTop();
                    using (var f = game.Capture()) button = game.NoActiveHeist(f);
                    if (button == null) return;
                    game.PressStartAHeist(button);
                    pick = null;
                    for (int look = 0; look < 6 && pick == null; look++)
                    {
                        if (look > 0) { game.ScrollPage(0.55, -2); game.Wait(500); }
                        using (var f = game.Capture()) offers = game.ReadHeistOffers(f, out close);
                        if (offers != null) pick = offers.FirstOrDefault(o => o.Start != null && Game.SameHeist(o.Name, S.StartHeist))
                                                   ?? offers.Where(o => o.Start != null && o.Level <= Math.Max(1, level)).OrderByDescending(o => o.Level).FirstOrDefault();
                    }
                    if (pick == null) { game.CloseHeistOffers(close); return; }
                }

                using (var f = game.Capture())
                {
                    var again = game.ReadHeistOffers(f, out close);
                    var row = again == null ? null : again.FirstOrDefault(o => o.Start != null && Game.SameHeist(o.Name, pick.Name));
                    if (row == null) { Log("Heists: " + pick.Name + "'s START moved before it was pressed - again in 10 minutes"); game.CloseHeistOffers(close); return; }
                    pick = row;
                }
                game.PressHeistStart(pick);
                game.Wait(1500);
                List<HeistOffer> still;
                using (var f = game.Capture()) still = game.ReadHeistOffers(f, out close);
                OcrLine none;
                using (var f = game.Capture()) none = game.NoActiveHeist(f);
                if (still == null && none == null)
                {
                    heistEnergy = 0;
                    heistsToday++;
                    Count.HeistsStarted++;
                    Log("Heists: started " + pick);
                    SnapshotOnce("heist started");
                    nextHeistStart = DateTime.UtcNow.AddMinutes(Math.Max(10, pick.Minutes > 0 ? pick.Minutes : 30));
                    return;
                }

                heistEnergy = 0;
                Log("Heists: START on " + pick.Name + " didn't start it" + (still != null ? " (the window is still open - the day's 2 heists used?)" : "") + " - again in an hour");
                game.Snapshot("heist start did nothing", 60);
                if (still != null) game.CloseHeistOffers(close);
                nextHeistStart = DateTime.UtcNow.AddMinutes(60);
            }
            finally { if (took) DepositBack(); }
        }

        public double JobXpFor5() { return JobXpFor5(last, Catalog); }

        public double JobXpFor5(Header h, List<JobInfo> catalog)
        {
            if (S.JobMode == JobMode.OneJob)
            {

                JobInfo one;
                return OneJobWhy(h, catalog, out one) == null ? one.XpPerEnergy * 5 : 0;
            }
            int lv = level > 0 ? level : 1;
            int maxE = h != null && h.EnergyMax > 0 ? h.EnergyMax : int.MaxValue;
            double best = 0;
            foreach (var j in catalog)
                if (j.TierLevel <= lv && j.Cost > 0 && j.Cost <= maxE && j.Xp > 0) best = Math.Max(best, j.XpPerEnergy);
            return best * 5;
        }

        void DoFights()
        {
            if (!FamilyTakes()) { DoFights(100, AboveKept); return; }
            if (DoFights(100, -1) > 0) givenSinceFights = 0;
        }

        const int AboveKept = -2;

        int DoFights(int max, int reserve)
        {
            nextFights = DateTime.UtcNow.AddSeconds(30);
            if (!game.OpenTab(Tab.Fight)) return 0;

            if (game.ProfileShowing() && !game.CloseProfile()) { Log("Fights: a player's profile is open and didn't close - fights wait 5 minutes"); nextFights = DateTime.UtcNow.AddMinutes(5); return 0; }
            int fought = 0, staminaLeft = -1;
            bool missed = false;
            bool rolledUp = false;
            bool refreshed = false;

            for (int i = 0; i < max; i++)
            {

                var h = ReadBarsSettled(i == 0 ? 6000 : 5000);
                List<FightRow> rows;
                bool hospital = false, war;
                using (var f = game.Capture()) { rows = game.ReadFightRows(f, out war); if (i == 0) hospital = game.Hospitalized(f); }
                if (hospital) { Hospital(); return fought; }

                if (rows.Count == 0 && game.ProfileShowing())
                {
                    if (!game.CloseProfile()) { Log("Fights: a player's profile is open and didn't close - fights wait 5 minutes"); nextFights = DateTime.UtcNow.AddMinutes(5); return fought; }
                    using (var f = game.Capture()) rows = game.ReadFightRows(f, out war);
                }

                if (war && S.WarTargetsOnly && !rows.Any(r => r.War) && !rolledUp)
                {
                    rolledUp = true;
                    game.ScrollPage(0.5, 10);
                    game.Wait(500);
                    using (var f = game.Capture()) rows = game.ReadFightRows(f, out war);
                }
                SayWar(war, rows.Count);

                if (war && S.WarTargetsOnly) rows = rows.Where(r => r.War).ToList();

                if (h.Stamina < 0 && staminaLeft >= 0) h.Stamina = staminaLeft;

                if (h.Stamina < 0) { Log("Couldn't read stamina - no attacks this round"); game.NoteFailure("the stamina bar didn't read"); break; }
                if (reserve == AboveKept) reserve = Kept(h.StaminaMax);
                else if (reserve < 0) reserve = Math.Max(0, h.Stamina - Kept(h.StaminaMax));
                if (h.Stamina <= reserve) break;
                int spare = h.Stamina - reserve;

                if (S.ScoutFights && (ownAttack < 0 || DateTime.UtcNow - ownPowerAt > TimeSpan.FromMinutes(30)))
                {
                    var own = game.ReadOwnProfile();
                    if (own == null || game.ProfileShowing())
                    {
                        SayOnce("own profile", "Fights: couldn't read your own attack and defense (your profile) - fights wait 5 minutes (\"Scout players first\" is on)");
                        nextFights = DateTime.UtcNow.AddMinutes(5);
                        return fought;
                    }
                    ownAttack = own.Attack; ownDefense = own.Defense; ownPowerAt = DateTime.UtcNow;
                }
                var now = DateTime.UtcNow;
                var ready = rows.Where(r => r.Ready && r.StaminaCost <= spare && !(fightSkip.ContainsKey(FightKey(r)) && fightSkip[FightKey(r)] > now)
                                            && !(S.ScoutFights && TooStrong(ScoutedNow(r))) && !LeftAlone(r, now)).ToList();

                var notHit = ready.Where(r => r.War || !HitOnList(r)).ToList();
                if (notHit.Count == 0 && rows.Count > 0 && !refreshed && !(war && S.WarTargetsOnly))
                {
                    refreshed = true;
                    if (NewFightList(rows)) { i--; continue; }
                }

                if (notHit.Count > 0) ready = notHit;
                if (ready.Count == 0)
                {

                    if (fought == 0 && S.ScoutFights && rows.Any(r => r.Ready) && rows.Where(r => r.Ready).All(r => TooStrong(ScoutedNow(r))))
                    {
                        nextFights = now.AddMinutes(15);
                        SayOnce("all stronger", "Fights: every player on the list is stronger than you (scouted) - looking again in 15 minutes");
                        break;
                    }

                    if (fought == 0)
                    {
                        var free = rows.Select(r => FightKey(r)).Where(k => fightSkip.ContainsKey(k) && fightSkip[k] > now).Select(k => fightSkip[k]).ToList();
                        var wait = rows.Count == 0 ? TimeSpan.FromMinutes(2) : free.Count > 0 ? free.Min() - now : TimeSpan.FromMinutes(5);
                        nextFights = now + TimeSpan.FromSeconds(Math.Max(30, Math.Min(300, wait.TotalSeconds)));
                        SayOnce("no player to attack", "Fights: no player to attack right now - looking again in a few minutes");
                    }
                    break;
                }
                var cheap = ready.Where(r => r.StaminaCost == 1).ToList();
                if (cheap.Count > 0) ready = cheap;

                var target = ready.OrderByDescending(r => BigPayer(r, now)).ThenBy(r => S.ScoutFights && ScoutedNow(r) != null ? 0 : 1).ThenBy(r => r.Level < 0 ? 999 : r.Level).First();
                if (BigPayer(target, now) > 0) Log("Fights: " + target.Name + " paid " + M(BigPayer(target, now)) + " in the last day (" + memory.Record(target.Name) + ") - attacked first");
                if (S.ScoutFights && ScoutedNow(target) == null)
                {

                    bool may = spare >= target.StaminaCost + 1, paid;
                    string why;
                    var p = game.ScoutPlayer(target, may, out paid, out why);
                    if (paid) staminaLeft = h.Stamina - 1;
                    if (game.ProfileShowing() && !game.CloseProfile())
                    {

                        Log("Fights: a player's profile stayed open - fights wait 5 minutes");
                        nextFights = DateTime.UtcNow.AddMinutes(5);
                        return fought;
                    }
                    if (p == null)
                    {

                        if (!may && !paid && why == Game.NoStaminaToScout) { if (fought == 0) nextFights = DateTime.UtcNow.AddMinutes(1); break; }
                        fightSkip[FightKey(target)] = DateTime.UtcNow.AddMinutes(why == Game.NoProfile ? 6 * 60 : 30);
                        Log("Fights: couldn't scout a " + (target.Level > 0 ? "level " + target.Level + " " : "") + "player (" + why + ") - another one");
                        continue;
                    }
                    var s = scouted[FightKey(target)] = new Scouted { Attack = p.Attack, Defense = p.Defense, At = DateTime.UtcNow };
                    Log(string.Format(CultureInfo.InvariantCulture, "Fights: scouted a {0}player{1} - attack {2:N0}, defense {3:N0} (yours {4:N0} and {5:N0}): {6}",
                                      target.Level > 0 ? "level " + target.Level + " " : "", paid ? "" : " (scouted before)", s.Attack, s.Defense, ownAttack, ownDefense,
                                      TooStrong(s) ? "stronger, left alone" : "attacking"));
                    continue;
                }
                game.Attack(target);
                string result = game.FinishFight(15000);
                if (result == null)
                {
                    bool inHospital;
                    using (var f = game.Capture()) inHospital = game.Hospitalized(f);
                    if (inHospital) { Hospital(); return fought; }

                    string why = game.LastFightRefusal;
                    if (why != null && Regex.IsMatch(why, @"HOSPITALI[SZ]ED|MUST\s+RECOVER", RegexOptions.IgnoreCase))
                    {
                        fightSkip[FightKey(target)] = DateTime.UtcNow.AddMinutes(10);
                        SayOnce("target in hospital", "Fights: a player you beat is in the hospital - the bot attacks another one");
                        continue;
                    }

                    why = why ?? game.LastFightToast;
                    if (!missed)
                    {
                        missed = true;
                        fightSkip[FightKey(target)] = DateTime.UtcNow.AddMinutes(2);
                        continue;
                    }
                    Log("The attack didn't start twice - fights paused for 5 minutes" + (why != null ? " (the game said: " + why + ")" : ""));
                    game.Snapshot("attack did not start", 60);
                    nextFights = DateTime.UtcNow.AddMinutes(5);
                    return fought;
                }
                fought++;
                missed = false;
                refreshed = false;
                hitOnList.Add(FightKey(target));
                staminaLeft = h.Stamina - target.StaminaCost;
                Count.Fights++;
                if (result.StartsWith("VICTORY")) Count.Wins++;
                else if (result.StartsWith("DEFEAT")) Count.Losses++;
                if (target.War) Log(string.Format("Fight vs war target {0}: {1}", target.Name.Length > 0 ? target.Name : "#" + (target.Index + 1), result));
                else Log(string.Format("Fight vs level {0} player: {1}", target.Level, result));

                bool won = result.StartsWith("VICTORY");
                double took = won && target.Name.Length > 0 && (S.WhaleHunt || S.RememberPlayers) ? WinTook(h.Cash) : -1;
                Remember(target, won, result, took);

                if (S.WhaleHunt && won && target.Name.Length > 0 && max > 1 && took >= S.WhaleCash) fought += HitAgain(target, took, reserve, ref staminaLeft);
            }
            return fought;
        }

        double WinTook(double cashBefore)
        {
            var after = ReadBarsSettled(2000, x => x.Cash >= 0);
            return cashBefore >= 0 && after.Cash >= 0 ? Math.Max(0, after.Cash - cashBefore) : -1;
        }

        int HitAgain(FightRow target, double took, int reserve, ref int staminaLeft)
        {
            string name = target.Name;
            Log("Fights: the win on " + name + " took " + M(took) + " - banking it and hitting again (up to 5 hits, while each takes " + M(S.WhaleCash) + " or more)");
            int more = 0;
            for (int hit = 2; hit <= 5; hit++)
            {
                if (S.Bank && Can(Tab.Bank)) DoBank();
                if (!game.OpenTab(Tab.Fight)) return more;
                Game.AttackEntry e = null;
                int end = Environment.TickCount + 60000;
                while (true)
                {
                    using (var f = game.Capture()) e = game.ReadAttackHistory(f).FirstOrDefault(x => Game.SamePlayer(x.Name, name));
                    if (e == null || e.Again != null || end - Environment.TickCount <= 0) break;
                    game.Wait(2000);
                }
                if (e == null || e.Again == null) { Log("Fights: no ATTACK AGAIN for " + name + " within a minute - on with the list"); break; }
                var h = ReadBarsSettled(5000);
                if (h.Stamina < 0 || h.Stamina - reserve < target.StaminaCost) break;
                game.AttackAgain(e);
                string result = game.FinishFight(15000);
                if (result == null)
                {
                    bool inHospital;
                    using (var f = game.Capture()) inHospital = game.Hospitalized(f);
                    if (inHospital) { Hospital(); break; }
                    Log("Fights: hitting " + name + " again didn't start" + (game.LastFightRefusal != null ? " (the game said: " + game.LastFightRefusal + ")" : "") + " - on with the list");
                    break;
                }
                more++;
                staminaLeft = h.Stamina - target.StaminaCost;
                Count.Fights++;
                if (result.StartsWith("VICTORY")) Count.Wins++;
                else if (result.StartsWith("DEFEAT")) Count.Losses++;
                double got = result.StartsWith("VICTORY") ? WinTook(h.Cash) : -1;
                Remember(target, result.StartsWith("VICTORY"), result, got);
                Log(string.Format("Fight vs {0} again (hit {1} of 5): {2}{3}", name, hit, result, got >= 0 ? " - took " + M(got) : ""));
                if (got < S.WhaleCash) break;
            }

            if (S.Bank && Can(Tab.Bank)) DoBank();
            game.OpenTab(Tab.Fight);
            return more;
        }

        FightMemory memory;

        void Remember(FightRow r, bool won, string result, double took)
        {
            if (!S.RememberPlayers || r.Name.Length < 3 || r.War || !(won || result.StartsWith("DEFEAT"))) return;
            memory.Add(r.Name, won, took, DateTime.UtcNow);
        }

        double BigPayer(FightRow r, DateTime now)
        {
            if (!S.RememberPlayers || r.Name.Length < 3 || r.War) return -1;
            double big = memory.BigWin(r.Name, now);
            return big >= S.WhaleCash ? big : -1;
        }

        bool LeftAlone(FightRow r, DateTime now)
        {
            if (!S.RememberPlayers || r.Name.Length < 3 || r.War) return false;
            var until = memory.LeftAloneUntil(r.Name, now);
            if (until <= now) return false;
            SayOnce("left alone " + FightKey(r) + until.ToString("yyyyMMddHH"), "Fights: " + r.Name + " beat you twice in a row - left alone until "
                    + until.ToLocalTime().ToString("ddd HH:mm", CultureInfo.InvariantCulture) + ", then tried again");
            return true;
        }

        bool atWar;

        void SayWar(bool war, int rows)
        {
            if (war == atWar || (!war && rows == 0)) return;
            atWar = war;
            if (!war) { Log("Fights: no war on the Fight page any more - the whole list again"); return; }
            Log(S.WarTargetsOnly ? "Fights: your family is at war - only war targets are attacked"
                                 : "Fights: your family is at war (\"Only attack war targets\" is off: the whole list)");
            SnapshotOnce("fights at war");
        }

        readonly Dictionary<string, DateTime> fightSkip = new Dictionary<string, DateTime>();

        readonly HashSet<string> hitOnList = new HashSet<string>();
        DateTime refreshBrokenUntil = DateTime.MinValue;

        bool HitOnList(FightRow r) { return HitBefore(hitOnList, r); }

        internal static bool HitBefore(ICollection<string> hit, FightRow r)
        {
            string k = FightKey(r);
            if (hit.Contains(k)) return true;
            return r.Name.Length > 0 && k.Length >= 6 && hit.Any(x => !x.StartsWith("#") && x.Length >= 6 && View.Distance(x, k) <= 1);
        }

        bool NewFightList(List<FightRow> before)
        {
            if (DateTime.UtcNow < refreshBrokenUntil) return false;
            var was = new HashSet<string>(before.Where(r => r.Name.Length > 0).Select(FightKey));
            if (!game.RefreshFightList())
            {
                SayOnce("refresh not found", "Fights: couldn't find the list's round arrow (refresh) - the same players are attacked again for now");
                game.Snapshot("fight refresh not found", 60);
                refreshBrokenUntil = DateTime.UtcNow.AddMinutes(30);
                return false;
            }
            for (int look = 0; look < 4; look++)
            {
                if (look > 0) game.Wait(800);
                List<FightRow> rows;
                using (var f = game.Capture()) rows = game.ReadFightRows(f);
                var named = rows.Where(r => r.Name.Length > 0).ToList();
                int fresh = named.Count(r => !was.Contains(FightKey(r)));
                if (named.Count > 0 && fresh * 2 >= named.Count)
                {
                    hitOnList.Clear();
                    Log("Fights: everyone on the list had a hit - pressed refresh, " + fresh + " new players");
                    SnapshotOnce("fight list refreshed");
                    return true;
                }
            }
            SayOnce("refresh changed nothing", "Fights: refresh brought no new players - the same ones are attacked again for now");
            game.Snapshot("fight refresh changed nothing", 60);
            refreshBrokenUntil = DateTime.UtcNow.AddMinutes(30);
            return false;
        }

        sealed class Scouted { public int Attack, Defense; public DateTime At; }
        readonly Dictionary<string, Scouted> scouted = new Dictionary<string, Scouted>();
        int ownAttack = -1, ownDefense = -1;
        DateTime ownPowerAt = DateTime.MinValue;

        Scouted ScoutedNow(FightRow r)
        {
            Scouted s;
            return scouted.TryGetValue(FightKey(r), out s) && DateTime.UtcNow - s.At < TimeSpan.FromHours(1) ? s : null;
        }

        bool TooStrong(Scouted s)
        {
            return s != null && Stronger(s.Attack, s.Defense, ownAttack, ownDefense);
        }

        internal static bool Stronger(int attack, int defense, int ownAttack, int ownDefense)
        {
            return ownAttack >= 0 && ownDefense >= 0 && (attack > ownDefense || defense > ownAttack);
        }

        static string FightKey(FightRow r) { return r.Name.Length > 0 ? Parse.Key(r.Name) : "#" + r.Index + " L" + r.Level; }

        void Hospital()
        {

            if (DateTime.UtcNow - hospitalSaid > TimeSpan.FromMinutes(30)) Log("You're in the hospital - player fights wait until your health is back");
            hospitalSaid = DateTime.UtcNow;
            nextFights = DateTime.UtcNow.AddMinutes(15);
        }

        DateTime hospitalSaid = DateTime.MinValue;
        string pointsSaid;

        bool feeSaid;
        bool bankHiddenSaid;
        int bankFee = -1;
        string feeLookedUp;

        int BankFee()
        {
            if (bankFee >= 0) return bankFee;
            string name = S.Account.Length > 0 ? S.Account : last != null ? (last.Name ?? "").Trim() : "";
            if (name.Length == 0 || feeLookedUp == name) return bankFee;
            feeLookedUp = name;
            var p = Profile.Load(Profile.FileFor(dir, name));
            if (p != null && p.BankFee >= 0) bankFee = p.BankFee;
            return bankFee;
        }

        bool CanWithdraw() { return S.Bank && Can(Tab.Bank); }

        double Spendable(Header h) { return h.Cash < 0 ? h.Cash : h.Cash + (CanWithdraw() && h.Banked > 0 ? h.Banked : 0); }

        bool cashOut;

        bool WithdrawFor(double need, bool bigBuy)
        {
            if (!CanWithdraw() || need <= 0) return false;
            if (!bigBuy && BankFee() != 0) return false;
            double cash = CashNow();
            if (cash < 0 || cash >= need) return false;

            double banked;
            using (var f = game.Capture()) banked = game.ReadHeader(f).Banked;
            if (banked < 0 || cash + banked < need) return false;
            nextBank = DateTime.UtcNow;
            string msg = game.WithdrawAll();
            if (msg == null) return false;
            if (msg == Game.BankUnchanged)
            {
                Log("Bank: WITHDRAW ALL pressed, but the top bar shows no cash coming out");
                game.Snapshot("bank withdraw did nothing", 60);
                return false;
            }

            Log("Bank: took the cash out for a purchase (" + msg.Trim() + ")");
            SnapshotOnce("bank withdraw");
            cashOut = true;
            return true;
        }

        void DepositBack()
        {
            if (BankFee() != 0) { nextBank = DateTime.UtcNow; return; }
            DoBank();
        }

        static bool BigBuy(string task) { return task == "properties" || task == "safehouse upgrade"; }

        void BankNext(List<string> due, int i)
        {
            if (!cashOut || !S.Bank || !Can(Tab.Bank)) return;
            if (i + 1 < due.Count && (due[i + 1] == "bank" || (BankFee() != 0 && BigBuy(due[i + 1])))) return;
            int at = due.IndexOf("bank", i + 1);
            if (at < 0 && due.IndexOf("bank") >= 0) return;
            if (at >= 0) due.RemoveAt(at);
            due.Insert(i + 1, "bank");
        }

        const int BankMinutes = 5;

        void DoBank()
        {
            int fee;
            string msg = game.DepositAll(out fee);
            if (fee >= 0) bankFee = fee;
            else if (msg != null && Game.FeeIn(msg) >= 0) bankFee = Game.FeeIn(msg);
            lastBank = DateTime.UtcNow;
            nextBank = DateTime.UtcNow.AddMinutes(BankMinutes);
            if (fee > 0 && !feeSaid)
            {

                Log("Bank: each deposit costs " + fee + "% without the No Bank Fees pass");
                feeSaid = true;
            }

            if (msg == null) { if (game.DepositCashBefore == 0) cashOut = false; return; }

            if (msg == Game.BankHidden)
            {
                cashOut = false;
                if (!bankHiddenSaid) Log("Bank: DEPOSIT ALL pressed " + msg);
                bankHiddenSaid = true;
                Neutral();
                return;
            }
            if (msg == Game.BankUnchanged || msg == Game.BankUnread)
            {

                Log("Bank: DEPOSIT ALL pressed " + msg);
                game.Snapshot("bank deposit unconfirmed", 60);
                return;
            }
            cashOut = false;
            Count.Deposits++;
            string line = msg.StartsWith("the notification didn't show") ? "deposited (" + msg + ")" : TidyDeposit(msg);

            if (!line.Contains("$") && game.DepositCashBefore > 0) line += " - about " + M(game.DepositCashBefore) + " (the cash on hand before it)";
            Log("Bank: " + line);
            NoteBankFee(msg);
        }

        public static string TidyDeposit(string msg)
        {
            var m = Regex.Match(msg, @"\$\s?[\d,.]+\s?[KMBT]?\b", RegexOptions.IgnoreCase);
            var fee = Regex.Match(msg, @"\(\s*(no\s*fee|\$\s?[\d,.]+\s?[KMBT]?\s*fee)\s*\)", RegexOptions.IgnoreCase);

            if (!m.Success) return Regex.IsMatch(msg, @"dep\w*s\w*ted", RegexOptions.IgnoreCase) ? "deposited" + (fee.Success ? " (" + Regex.Replace(fee.Groups[1].Value, @"\s+", " ").ToLowerInvariant() + ")" : "") : msg.Trim();
            return "deposited " + m.Value.Replace(" ", "") + (fee.Success ? " (" + Regex.Replace(fee.Groups[1].Value, @"\s+", " ").ToLowerInvariant().Replace("$ ", "$") + ")" : "");
        }

        string feeNotedFor;

        void NoteBankFee(string msg)
        {
            string name = last != null ? (last.Name ?? "").Trim() : "";
            int fee = Game.FeeIn(msg);
            if (name.Length == 0 || fee < 0 || feeNotedFor == name) return;
            feeNotedFor = name;
            string file = Profile.FileFor(dir, name);
            var p = Profile.Load(file);
            if (p == null || p.BankFee >= 0) return;
            p.BankFee = fee;
            p.BankFeeFrom = "a deposit";
            p.BankLine = msg.Trim();
            try { p.Save(file); } catch (Exception) { return; }
            Log(fee == 0 ? "(The deposit says there's no bank fee: noted in your game check)" : "(The deposit says each one costs " + fee + "%: noted in your game check)");
            var h = ProfileChanged;
            if (h != null) h(p);
        }

        public event Action<string> AccountChanged;
        string accountSeen;
        int accountVotes;
        DateTime accountFirstVote;
        readonly object accountLock = new object();

        bool NoteAccount(string name)
        {
            if (!Accounts.Plausible(name)) return false;
            name = name.Trim();
            lock (accountLock)
            {

                if (!ownerLooked && S.Account.Length == 0) { ownerLooked = true; ownerHint = Accounts.OwnerHint(dir); }
                string whose = S.Account.Length > 0 ? S.Account : ownerHint ?? "";

                bool own = whose.Length > 0 && !string.Equals(Profile.Key(name), Profile.Key(whose), StringComparison.OrdinalIgnoreCase)
                           && Directory.Exists(Accounts.Folder(dir, name));
                if (whose.Length == 0 || (!own && Accounts.Same(name, whose)))
                {
                    accountSeen = null; accountVotes = 0;
                    if (S.Account.Length == 0) Adopt(name);
                    return false;
                }

                var now = DateTime.UtcNow;
                if (accountSeen == null || !Accounts.Same(name, accountSeen)) { accountSeen = name; accountVotes = 1; accountFirstVote = now; return true; }
                accountVotes++;
                bool known = Directory.Exists(Accounts.Folder(dir, name));
                if (accountVotes < (known ? 3 : 5) || now - accountFirstVote < TimeSpan.FromSeconds(known ? 10 : 30)) return true;
                accountSeen = null; accountVotes = 0;
                if (S.Account.Length == 0) S.Account = whose;
                SwitchAccount(name);
                return true;
            }
        }

        string ownerHint;
        bool ownerLooked;

        void Adopt(string name)
        {
            string ini = Accounts.Ini(dir, name), jobs = Accounts.Jobs(dir, name);
            bool back = File.Exists(ini);
            if (back)
            {
                var o = Settings.Load(ini);
                S.TakeAccountFields(o);
                if (o.Unreadable != null) S.Unreadable = o.Unreadable;
                level = S.LastLevel;
                crewUnequipped = S.CrewGearOff;
            }
            if (catalogBuiltIn && File.Exists(jobs))
            {

                catalogBuiltIn = false;
                Catalog = GameData.LoadCatalog(jobs);
                CatalogView = Catalog.Select(j => j.Copy()).ToList();
                var cc = CatalogChanged;
                if (cc != null) cc();
            }
            S.Account = name;
            SaveAccount();
            Log(back ? "Account: " + name + ". Its own settings are back (accounts\\" + Profile.Key(name) + ")."
                     : "Account: " + name + ". Its settings and job list are kept for it from now on (accounts\\" + Profile.Key(name) + ").");
            var h = AccountChanged;
            if (h != null) h(name);
        }

        void SaveAccount()
        {
            if (S.Account.Length == 0) return;
            try
            {
                Directory.CreateDirectory(Accounts.Folder(dir, S.Account));
                S.Save(Accounts.Ini(dir, S.Account), true);
                S.Save(Path.Combine(dir, "IdleMafiaBot.ini"));
                GameData.SaveCatalog(Accounts.Jobs(dir, S.Account), Catalog);
                GameData.SaveCatalog(catalogFile, Catalog);
            }
            catch (Exception e) { Log("Couldn't save " + S.Account + "'s settings: " + e.Message); }
        }

        void SwitchAccount(string name)
        {
            string before = S.Account;
            SaveAccount();
            string ini = Accounts.Ini(dir, name), jobs = Accounts.Jobs(dir, name);
            bool known = File.Exists(ini);
            var theirs = known ? Settings.Load(ini) : new Settings();
            S.TakeAccountFields(theirs);
            if (theirs.Unreadable != null)
            {

                S.Unreadable = theirs.Unreadable;
                Log("Couldn't read " + name + "'s settings (" + theirs.Unreadable + "). It plays with a new player's settings and nothing is saved this time. Start the bot again to get them back.");
            }
            S.Account = name;
            catalogBuiltIn = !File.Exists(jobs);
            Catalog = GameData.LoadCatalog(jobs);
            CatalogView = Catalog.Select(j => j.Copy()).ToList();
            level = S.LastLevel;
            ResetAccountState();
            SaveAccount();
            Log(known ? "Account: " + name + " is playing now. Its own settings and job list are back (" + before + "'s are kept)."
                      : "Account: " + name + " is new here. It gets a new player's settings and the game's job list; " + before + "'s are kept for when it's back.");
            var cc = CatalogChanged;
            if (cc != null) cc();
            var h = AccountChanged;
            if (h != null) h(name);
        }

        void ResetAccountState()
        {
            DateTime now = DateTime.UtcNow;
            nextOps = nextBosses = nextPlaytime = nextContracts = nextCrates = nextHeists = nextFights = nextBank = nextPoints = nextJobs = nextLearn = now;
            ResetMenuNumbers();
            nextEquip = nextShop = nextSafehouse = nextProperties = nextTrophies = nextTutorialLook = nextMarket = now;
            nextBriefcases = nextEvents = now; badgeSig = eventsSig = null; eventsOn.Clear(); briefcasesSaid = null; briefcasesStopped = false;
            ResetFamily(now);
            lastBank = DateTime.MinValue;
            learnedThisRun = false;
            last = null;
            propPrice = 0; propLotsOpen = -1; propSaid = ""; propReplace = false; replaceSaid = null;
            upgradePrice = -1; nextUpgrade = now; upgradeSaid = ""; upgradeNeedsLevel = -1;
            nextCrew = nextCrewSlot = now; crewSlotPrice = -1; crewSaid = ""; bankFee = -1; feeLookedUp = null; crewInfo = null;
            nextReroll = now; rerollCrew = null; rerollEmpty = false; emptiedRarity = -1; emptiedByStats = false; rerollCash = -1; rerollSaid = null; rerollShort = false;
            listShortFails = 0; hiredRead = -1; rerollSkip.Clear(); sortStuck.Clear(); sortStuckRounds = 0;
            lock (jobAside) jobAside.Clear(); jobMisses.Clear();
            ownStats.Clear(); ownReadAt = DateTime.MinValue; crewUnequipped = S.CrewGearOff; gearBackFails = 0; gearBackHold = DateTime.MinValue; ownUnknownLeft = -1; ownJustRead = false;
            energyRegen.Forget(); staminaRegen.Forget();
            crewHirePrice = new double[] { 10000, 10e6, 10e9 };
            tutorialHint = tutorialItem = tutorialAction = tutorialLogged = null;
            tutorialOver = tutorialFought = false;
            feeSaid = saidBestGear = saidDeposit = false;
            saidOnce.Clear();
            levelSaid = levelSeen = levelSaidXp = -1; summaryLevel = -1;
            streaks.Clear(); ShowStuck();
            if (game != null) game.ResetAccount();
        }

        bool TutorialOn { get { return tutorialHint != null; } }
        string tutorialHint, tutorialItem, tutorialAction;
        DateTime tutorialSince, nextTutorialLook;
        bool tutorialOver, tutorialFought;

        void LookForTutorial()
        {
            if (tutorialOver || game == null || (level > 8 && tutorialHint == null) || DateTime.UtcNow < nextTutorialLook) return;
            nextTutorialLook = DateTime.UtcNow.AddSeconds(15);
            Game.TutorialBox box;
            using (var f = game.Capture()) box = game.ReadTutorial(f);
            if (box == null)
            {
                if (tutorialHint != null && tutorialFought) { tutorialOver = true; Log("Tutorial: finished"); }
                tutorialHint = null; tutorialAction = null; tutorialItem = null;
                return;
            }
            bool changed = box.Hint != tutorialHint;
            tutorialHint = box.Hint;
            if (changed && box.Hint != tutorialLogged)
            {
                tutorialLogged = box.Hint;
                tutorialSince = DateTime.UtcNow;
                Log("Tutorial: \"" + box.Hint + "\"");
            }

            bool stuck = DateTime.UtcNow - tutorialSince > TimeSpan.FromMinutes(30);
            if (box.Done != null) { tutorialAction = "done"; return; }
            if (stuck && box.Skip != null) { tutorialAction = "skip"; return; }

            string u = box.Hint.ToUpperInvariant();
            var buy = Regex.Match(box.Hint, @"Buy the (.+?)\s*[.!]", RegexOptions.IgnoreCase);
            bool fightStep = !buy.Success && !u.Contains("PROPERT") && (u.Contains("FIGHT PAGE") || u.Contains("REAL PLAYER") || u.Contains("TO ATTACK"));
            if (fightStep) { tutorialAction = tutorialFought ? null : "fight"; return; }
            if (!changed) return;
            tutorialItem = buy.Success ? buy.Groups[1].Value.Trim() : null;
            tutorialAction = null;
            if (u.Contains("PROPERT")) { propPrice = 0; nextProperties = DateTime.UtcNow; }
            else if (tutorialItem != null || u.Contains("SHOP")) { nextShop = DateTime.UtcNow; game.MenuChanged(Tab.Shop); }
            else if (u.Contains("EQUIP") || u.Contains("CREW")) nextEquip = DateTime.UtcNow;
            else if (!u.Contains("JOB") && !u.Contains("EXP")) SnapshotOnce("tutorial step " + Regex.Replace(box.Hint, "[^A-Za-z ]", "").Trim());
        }

        string tutorialLogged;

        void DoTutorial()
        {
            string what = tutorialAction;
            tutorialAction = null;
            nextTutorialLook = DateTime.UtcNow;
            if (what == "fight")
            {

                int n = DoFights(1, 0);
                tutorialFought = n > 0;
                if (!tutorialFought) nextTutorialLook = DateTime.UtcNow.AddMinutes(2);
                return;
            }
            Game.TutorialBox box;
            using (var f = game.Capture()) box = game.ReadTutorial(f);
            if (box == null) return;
            if (what == "done" && box.Done != null) { game.PressTutorial(box.Done); Log("Tutorial: DONE pressed"); }
            else if (what == "skip" && box.Skip != null) { game.PressTutorial(box.Skip); Log("Tutorial: skipped \"" + box.Hint + "\" (stuck on it for 30 minutes)"); }
        }

        DateTime nextProperties;
        double propPrice;
        int propLotsOpen = -1;
        string propSaid = "";

        void DoProperties()
        {
            nextProperties = DateTime.UtcNow.AddMinutes(2);
            propRecheck = false;
            propReplace = false;
            bool took = false;
            try
            {
                var cards = game.ReadBuildCards();
                if (cards == null || cards.Count == 0)
                {
                    SayReplace("the BUILD page's cards didn't read. The bot looks again in 30 minutes.");
                    propPrice = 0; propRecheck = true;
                    nextProperties = DateTime.UtcNow.AddMinutes(30);
                    return;
                }
                int open;
                var lots = game.ReadBlock(cards.Where(c => c.Gold > 0).ToList(), out open);
                if (lots == null || open < 0)
                {
                    SayReplace("YOUR BLOCK didn't read. The bot looks again in 30 minutes.");
                    game.Snapshot("property lots not read", 60);
                    propPrice = 0; propRecheck = true;
                    nextProperties = DateTime.UtcNow.AddMinutes(30);
                    return;
                }
                propLotsOpen = open;
                double spend = SpendNow();
                var plan = PlanProperty(cards, lots, open, S.GoldLots, () => game.GoldLotsOwned(lots), spend);
                if (plan.Target == null)
                {

                    propPrice = 0; propRecheck = true;
                    nextProperties = DateTime.UtcNow.AddMinutes(plan.Why != null && plan.Why.Contains("didn't read") ? 30 : 360);
                    SayReplace(plan.Why ?? "every lot holds the best property you can build. The bot looks again in 6 hours or after a level-up.");
                    return;
                }
                propPrice = plan.Target.Price;
                propReplace = plan.Replace != null;
                string what = plan.Replace != null ? plan.Target + " in place of a " + plan.Replace : plan.Target + " on a free lot";
                if (spend < 0 || spend < plan.Target.Price * 1.01)
                {
                    SayReplace("saving up for " + what);
                    return;
                }

                double need = plan.Target.Price * 1.01;
                took = WithdrawFor(need, true);
                double cash = plan.Replace != null ? CashSure() : CashNow();
                if (cash < 0 || cash < need) { SayReplace("saving up for " + what); return; }
                if (plan.Replace != null && !Replace(plan.Replace, plan.Target, lots)) return;
                string why;
                if (!game.BuildCard(plan.Target, out why))
                {
                    if (plan.Replace != null) { Log("Properties: the lot is free but nothing was built yet (" + why + ") - the next look builds on it"); propLotsOpen = Math.Max(1, propLotsOpen); }
                    else Log("Properties: didn't build " + plan.Target + " - " + why);
                    nextProperties = DateTime.UtcNow.AddMinutes(plan.Replace != null ? 1 : 10);
                    return;
                }
                PropertyBuilt(plan.Target, plan.Replace);
                if (plan.Replace == null && propLotsOpen > 0) propLotsOpen--;
                replaceSaid = null;

                propRecheck = true;
                nextProperties = DateTime.UtcNow;
            }
            finally { if (took) DepositBack(); }
        }

        double SpendNow()
        {
            double cash = CashNow();
            if (cash < 0) return -1;
            if (!CanWithdraw()) return cash;
            double banked;
            using (var f = game.Capture()) banked = game.ReadHeader(f).Banked;
            return cash + Math.Max(0, banked);
        }

        internal sealed class PropertyPlan
        {
            public Game.PropertyCard Target;
            public Game.BlockLot Replace;
            public string Why;
        }

        internal static PropertyPlan PlanProperty(List<Game.PropertyCard> cards, List<Game.BlockLot> lots, int lotsOpen, int goldLots, Func<int> goldOwned, double spend)
        {
            var cash = cards.Where(c => c.Income > 0 && c.Gold <= 0 && c.Price > 0).ToList();
            var gold = cards.Where(c => c.Gold > 0 && c.Price > 0).ToList();
            Func<Game.PropertyCard, bool> covered = c => spend >= 0 && c.Price * 1.01 <= spend;
            Func<List<Game.PropertyCard>, Func<Game.PropertyCard, double>, Game.PropertyCard> pick = (from, worth) =>
                from.Where(covered).OrderByDescending(worth).ThenBy(c => c.Price).FirstOrDefault() ?? from.OrderBy(c => c.Price).FirstOrDefault();
            var worstCash = Game.WorstLot(lots);
            var better = worstCash == null ? new List<Game.PropertyCard>() : cash.Where(c => c.Income > worstCash.Income).ToList();

            bool cashDone = cash.Count == 0 || (worstCash != null && better.Count == 0);
            int owned = int.MinValue;
            Func<int> ownedNow = () => { if (owned == int.MinValue) owned = goldLots > 0 && gold.Count > 0 ? goldOwned() : 0; return owned; };

            if (lotsOpen > 0)
            {
                if (cashDone && goldLots > 0 && gold.Count > 0)
                {
                    int n = ownedNow();
                    if (n < 0) return new PropertyPlan { Why = "the gold lots' count didn't read. The bot looks again in 30 minutes." };
                    if (n < goldLots) return new PropertyPlan { Target = pick(gold, c => c.Gold) };
                }
                if (cash.Count == 0) return new PropertyPlan { Why = "a lot is free, but no property to build on it read. The bot looks again in 30 minutes (didn't read)." };
                return new PropertyPlan { Target = pick(cash, c => c.Income) };
            }
            if (better.Count > 0) return new PropertyPlan { Target = pick(better, c => c.Income), Replace = worstCash };
            if (worstCash == null && !lots.Any(l => l.Gold > 0))
                return new PropertyPlan { Why = "every lot is built on, and YOUR BLOCK's lots didn't read. The bot looks again in 30 minutes." };
            if (goldLots > 0 && gold.Count > 0)
            {
                int n = ownedNow();
                if (n < 0) return new PropertyPlan { Why = "the gold lots' count didn't read. The bot looks again in 30 minutes." };
                if (n < goldLots && worstCash != null) return new PropertyPlan { Target = pick(gold, c => c.Gold), Replace = worstCash };
            }

            var worstGold = Game.WorstGoldLot(lots);
            if (worstGold != null && goldLots > 0)
            {
                var more = gold.Where(c => c.Gold > worstGold.Gold).ToList();
                if (more.Count > 0) return new PropertyPlan { Target = pick(more, c => c.Gold), Replace = worstGold };
            }
            return new PropertyPlan();
        }

        void PropertiesLevelUp() { nextProperties = DateTime.UtcNow; propPrice = 0; propLotsOpen = -1; propReplace = false; propRecheck = false; }

        bool propReplace;
        bool propRecheck;

        string replaceSaid;

        bool Replace(Game.BlockLot worst, Game.PropertyCard target, List<Game.BlockLot> lots)
        {

            if (!game.BuildReady(target))
            {
                Log("Properties: didn't replace a " + worst + " - " + target.Name + "'s BUILD isn't showing gold yet");
                nextProperties = DateTime.UtcNow.AddMinutes(10);
                return false;
            }

            int open;
            var again = game.ReadBlock(null, out open, 2);
            if (again == null || again.Count == 0)
            {
                Log("Properties: didn't replace a " + worst + " - YOUR BLOCK didn't read a second time");
                nextProperties = DateTime.UtcNow.AddMinutes(30);
                return false;
            }
            if (worst.Gold <= 0)
            {
                var lower = Game.WorstLot(lots, again);
                if (lower != null && lower.Income < worst.Income)
                {
                    Log("Properties: the second read of YOUR BLOCK found a " + lower + ", worse than the " + worst + " the first read picked - that one goes");
                    worst = lower;
                }
            }
            string why;
            if (!game.DemolishLot(worst, out why))
            {
                Log("Properties: didn't replace a " + worst + " - " + why);
                nextProperties = DateTime.UtcNow.AddMinutes(30);
                return false;
            }
            Log("Properties: demolished a " + worst + " to build " + target);
            propLotsOpen = Math.Max(1, propLotsOpen);
            return true;
        }

        void PropertyBuilt(object p, object instead)
        {
            Log("Properties: built " + p);
            Count.Properties++;
            Discord.Post(Ping.Property, "\U0001F3E0 New property", "Built " + p + (instead != null ? " in place of a " + instead : ""), Shot, Discord.Green);
        }

        void SayReplace(string what)
        {
            if (replaceSaid == what) return;
            replaceSaid = what;
            Log("Properties: " + what);
        }

        double PropertyKeep() { return S.Properties && propLotsOpen > 0 && propPrice > 0 && !TutorialOn ? propPrice : 0; }

        DateTime nextUpgrade;
        double upgradePrice = -1;
        int upgradeNeedsLevel = -1;
        string upgradeSaid = "";

        void DoSafehouseUpgrade()
        {

            bool took = upgradePrice > 0 && BankFee() == 0 && WithdrawFor(upgradePrice + PropertyKeep(), true);
            string why = DoSafehouseUpgradeHere(!took);

            if (!took && why == "saving up" && upgradePrice > 0 && WithdrawFor(upgradePrice + PropertyKeep(), true))
            {
                took = true;
                DoSafehouseUpgradeHere(true);
            }
            if (took) DepositBack();
        }

        string DoSafehouseUpgradeHere(bool sayWaiting)
        {
            nextUpgrade = DateTime.UtcNow.AddMinutes(10);
            var r = game.UpgradeSafehouse(PropertyKeep());
            if (r.Upgraded)
            {
                var a = r.After;
                upgradePrice = a == null ? -1 : a.Level > 0 && a.Level >= a.MaxLevel ? 0 : a.Price;
                Log("Safehouse: upgraded to " + r.Next + (a != null ? " (level " + a.Level + " of " + a.MaxLevel + ")" : "") + " for " + SafehouseInfo.Money(r.Price)
                    + (upgradePrice > 0 && a.Next != null ? ". Next: " + a.Next + " for " + SafehouseInfo.Money(upgradePrice) : upgradePrice == 0 ? ". That's the top level" : ""));
                SnapshotOnce("safehouse upgraded");
                nextUpgrade = nextSafehouse = DateTime.UtcNow;
                return null;
            }
            if (r.Why == "top level") { upgradePrice = 0; nextUpgrade = DateTime.UtcNow.AddHours(24); return r.Why; }
            if (r.NeedLevel > 0)
            {

                upgradePrice = -1;

                upgradeNeedsLevel = last != null && last.Level >= r.NeedLevel ? -1 : r.NeedLevel;
                nextUpgrade = DateTime.UtcNow.AddHours(6);
                if (upgradeSaid != "level" + r.NeedLevel) { upgradeSaid = "level" + r.NeedLevel; Log("Safehouse: the next one" + (r.Next != null ? ", " + r.Next + "," : "") + " needs level " + r.NeedLevel + ". The bot upgrades it once you get there."); }
                return r.Why;
            }
            upgradeNeedsLevel = -1;
            if (r.Price > 0) upgradePrice = r.Price;
            if (r.Why == "saving up")
            {

                bool bankCovers = r.Price > 0 && last != null && Spendable(last) >= r.Price + PropertyKeep() && CanWithdraw();
                if (sayWaiting && !bankCovers && upgradeSaid != r.Next + r.Price) { upgradeSaid = r.Next + r.Price; Log("Safehouse: saving up for " + r.Next + " (" + SafehouseInfo.Money(r.Price) + ")"); }
                return r.Why;
            }
            if (r.Why != null) { nextUpgrade = DateTime.UtcNow.AddHours(1); Log("Safehouse: can't upgrade right now - " + r.Why); return r.Why; }

            Log("Safehouse: UPGRADE pressed but the level didn't change" + (r.Toast != null ? " (\"" + r.Toast.Trim() + "\")" : "")
                + (!string.IsNullOrEmpty(r.Window) ? ". On screen: \"" + r.Window + "\"" : ""));
            game.Snapshot("safehouse upgrade did nothing", 60);
            nextUpgrade = DateTime.UtcNow.AddHours(6);
            upgradePrice = -1;
            return null;
        }

        DateTime nextCrew, nextCrewSlot;
        bool crewQuiet;
        double crewSlotPrice = -1;
        double[] crewHirePrice = { 10000, 10e6, 10e9 };
        string crewSaid = "";

        double CashNow()
        {
            for (int i = 0; i < 3; i++)
            {
                if (i > 0) game.Wait(400);
                double c;
                using (var f = game.Capture()) c = game.ReadCash(f);
                if (c >= 0) return c;
            }
            return -1;
        }

        double CashFor(double need)
        {
            double cash = CashNow();
            if (cash >= need) return cash;
            game.Wait(500);
            double again;
            using (var f = game.Capture()) again = game.ReadCash(f, true);
            return Math.Max(cash, again);
        }

        double CashSure()
        {
            double a = CashNow();
            if (a < 0) return -1;
            game.Wait(500);
            double b;
            using (var f = game.Capture()) b = game.ReadCash(f, true);
            return b < 0 ? -1 : Math.Min(a, b);
        }

        static string M(double v) { return SafehouseInfo.Money(v); }

        public event Action<CrewInfo> CrewRead;
        CrewInfo crewInfo;

        void DoCrew()
        {
            var info = new CrewInfo { At = DateTime.Now, SlotPrice = crewSlotPrice };
            List<Game.CrewRow> crew = null;
            rerollCrew = null;
            try { DoCrewHere(info, out crew); }
            finally { ShowCrewInfo(info, crew); }
        }

        void ShowCrewInfo(CrewInfo info, List<Game.CrewRow> crew)
        {
            if (crew == null) return;
            foreach (var r in crew) if (r.Rarity >= 0 && r.Rarity < 8) info.ByRarity[r.Rarity]++;
            var worst = crew.Where(r => r.Rarity >= 0).OrderBy(r => r.Rarity).ThenBy(r => r.Power).FirstOrDefault();
            info.Worst = worst != null ? worst.ToString() : "";
            if (info.Henchmen < 0) info.Henchmen = crew.Count;
            crewInfo = info;
            var h = CrewRead;
            if (h != null) h(info);
        }

        void DoCrewHere(CrewInfo info, out List<Game.CrewRow> crew)
        {
            crew = null;
            nextCrew = DateTime.UtcNow.AddMinutes(30);
            nextCrewSlot = DateTime.UtcNow.AddMinutes(5);
            Game.CrewPage page;
            var rows = game.ReadWholeCrew(out page);
            if (page == null || !page.Page) { Log("Crew: couldn't read the crew page"); game.Snapshot("crew not read", 60); return; }
            crew = rows.Where(r => !r.Empty && !r.Locked && r.Rarity >= 0).ToList();
            info.Henchmen = page.Henchmen; info.Slots = page.Slots; info.Empty = rows.Count(r => r.Empty);
            if (game.CrewTotalsSeen != null && game.CrewTotalsSeen.Read) { info.Attack = game.CrewTotalsSeen.Attack; info.Defense = game.CrewTotalsSeen.Defense; }
            foreach (var e in rows.Where(r => r.Empty)) for (int t = 0; t < 3; t++) if (e.HirePrice[t] > 0) crewHirePrice[t] = e.HirePrice[t];
            bool changed = false, more = false;
            double keep = PropertyKeep();

            if (S.CrewSlots)
            {
                double price = -1, cash = CashNow();
                double paid = cash >= 0 ? game.UnlockCrewSlot(cash, keep, out price) : -1;
                if (price > 0) crewSlotPrice = info.SlotPrice = price;

                if (paid == 0 && price > 0 && WithdrawFor(price + keep, true))
                {
                    if (game.OpenTab(Tab.Crew)) { cash = CashNow(); paid = cash >= 0 ? game.UnlockCrewSlot(cash, keep, out price) : -1; }
                    DepositBack();
                    if (!game.OpenTab(Tab.Crew)) return;
                }
                if (paid > 0)
                {
                    Log("Crew: bought a new slot for " + M(paid));
                    changed = true; crewSlotPrice = info.SlotPrice = -1; nextCrewSlot = slotBoughtAt = DateTime.UtcNow;
                    if (info.Slots > 0) info.Slots++;
                    info.Status = "Bought a new slot.";
                }
                else if (paid == 0 && price > 0)
                {
                    info.Status = "Saving up for the next slot (" + M(price) + ").";
                    if (crewSaid != "slot" + price) { crewSaid = "slot" + price; Log("Crew: saving up for the next slot (" + M(price) + ")"); }
                }
            }

            if (S.CrewFill)
                for (int n = 0; n < 5; n++)
                {
                    Game.CrewPage p;
                    var empty = game.FindCrewRow(r => r.Empty, out p);
                    if (empty == null) break;
                    for (int t = 0; t < 3; t++) if (empty.HirePrice[t] > 0) crewHirePrice[t] = empty.HirePrice[t];
                    CrewRoll used;
                    double paid;

                    var got = game.HireInto(empty, S.CrewRollType, true, CashFor(empty.HirePrice[(int)S.CrewRollType] > 0 ? empty.HirePrice[(int)S.CrewRollType] : 0), 0, out used, out paid);
                    if (got == null)
                    {
                        if (paid > 0) { Log("Crew: a " + used + " hire showed no recruit"); game.Snapshot("crew hire no recruit", 60); }
                        else if (crewSaid != "hire") { crewSaid = "hire"; Log("Crew: an empty slot waits - no hire the cash covers yet"); }
                        break;
                    }
                    Count.Hired++;
                    Count.CrewCash += paid;
                    changed = true;
                    crew.Add(got);
                    Game.NoteOwn(ownStats, got);
                    Log("Crew: hired " + got + " with a " + used + " hire (" + M(paid) + ")");
                    PingHenchman(got, -1, false);
                    SnapshotOnce("crew hired");
                }

            if (changed && S.EquipBest) DoEquip();

            bool sorted;
            SortCrew(40, out sorted);
            if (!sorted) more = true;
            if (more) nextCrew = DateTime.UtcNow.AddMinutes(1);

            if (sortWasStuck) { sortStuckRounds++; nextCrew = DateTime.UtcNow.AddMinutes(sortStuckRounds == 1 ? 1 : sortStuckRounds == 2 ? 5 : 30); }
            else if (sorted) sortStuckRounds = 0;

            crewQuiet = !S.CrewSlots && !S.CrewFill && !S.CrewReroll && !S.TrainCrew && !more && !sortWasStuck;
            if (crewQuiet) nextCrew = DateTime.UtcNow.AddHours(6);

            if (S.TrainCrew && S.TrainAtOnce > 0) TrainCrew(crew, keep, info);
        }

        void TrainCrew(List<Game.CrewRow> crew, double keep, CrewInfo info)
        {

            int busy = crew.Count(r => r.Training && r.TrainSecondsLeft > 0);
            int soonest = crew.Where(r => r.Training && r.TrainSecondsLeft > 0).Select(r => r.TrainSecondsLeft).DefaultIfEmpty(-1).Min();
            int best = Game.BestRoll(S.CrewRollType);
            var picks = crew.Where(r => !(r.Training && r.TrainSecondsLeft > 0) && !r.Stationed && r.Rarity >= (int)Rarity.Epic && r.TrainLevel < 10 && (r.Train == null || r.TrainGold) && !RerollWouldReplace(r, best))
                            .OrderByDescending(r => r.Rarity).ThenByDescending(r => { var o = Game.OwnOf(r, ownStats); return o != null ? o.Sum : r.Power; }).ToList();
            var noTimer = crew.Where(r => r.Training && r.TrainSecondsLeft <= 0).Select(r => r.Name).ToList();
            string nt = noTimer.Count > 0 ? string.Join(", ", noTimer) : null;
            if (nt != trainNoTimerSaid) { trainNoTimerSaid = nt; if (nt != null) Log("Crew: read as training but no time left on their row: " + nt + " - not counted, training goes on"); }
            int started = 0;

            string why = null;
            if (picks.Count == 0 && busy < Math.Min(S.TrainAtOnce, Settings.MaxTraining))
            {
                int epic = crew.Count(r => r.Rarity >= (int)Rarity.Epic);
                why = "nobody to train (" + epic + " Epic or better: " + crew.Count(r => r.Rarity >= (int)Rarity.Epic && r.TrainLevel >= 10) + " at +10, "
                      + crew.Count(r => r.Rarity >= (int)Rarity.Epic && r.Rarity < best && S.CrewReroll) + " the rerolls would replace, "
                      + crew.Count(r => r.Rarity >= (int)Rarity.Epic && r.Train != null && !r.TrainGold) + " with a TRAIN that isn't gold, "
                      + crew.Count(r => r.Rarity >= (int)Rarity.Epic && r.Train == null && !r.Training) + " with no TRAIN read)";
            }

            if (busy >= Math.Min(S.TrainAtOnce, Settings.MaxTraining))
                why = busy + " training already, the most at once (" + string.Join(", ", crew.Where(r => r.Training && r.TrainSecondsLeft > 0).Select(r => r.Name + " " + r.RarityName
                      + (r.TrainSecondsLeft > 0 ? ", " + Parse.Short(r.TrainSecondsLeft) + " left" : ", no time read"))) + ")";

            double tooDear = double.MaxValue;
            string waiting = null;
            int opened = 0;
            try
            {
                foreach (var pick in picks)
                {
                    if (busy >= Math.Min(S.TrainAtOnce, Settings.MaxTraining) || started >= 5) break;
                    double known;
                    if (pick.TrainLevel > 0 && trainPrice.TryGetValue(pick.TrainLevel + 1, out known) && known >= tooDear) continue;
                    if (opened >= 4) break;
                    Game.CrewPage p;
                    var row = game.FindCrewRow(r => Game.NamedLike(r, pick), out p, true);
                    if (row == null) { why = why ?? "couldn't find " + pick.Name + " in the list"; continue; }
                    if (row.Training && row.TrainSecondsLeft > 0) { why = why ?? pick.Name + " reads as training on a second look"; continue; }
                    double cash = CashNow();
                    if (cash < 0) { why = "the cash on hand didn't read"; break; }
                    Game.TrainWindow offer;
                    opened++;
                    int got = game.TrainHenchman(row, pick, cash, keep, out offer);
                    if (got == 0 && offer != null && offer.Price > 0 && offer.Names(pick.Name) && WithdrawFor(offer.Price + keep, false))
                    {

                        if (game.OpenTab(Tab.Crew))
                        {
                            row = game.FindCrewRow(r => Game.NamedLike(r, pick), out p, true);
                            if (row != null && !(row.Training && row.TrainSecondsLeft > 0)) got = game.TrainHenchman(row, pick, CashNow(), keep, out offer);
                        }
                        DepositBack();
                        game.OpenTab(Tab.Crew);
                    }
                    if (offer != null && offer.Price > 0 && offer.ToLevel > 0 && offer.Names(pick.Name)) trainPrice[offer.ToLevel] = offer.Price;
                    if (got == 1)
                    {
                        busy++; started++;
                        Count.CrewCash += offer.Price;
                        string mins = offer.Minutes > 0 ? ", off duty for " + Parse.Short(offer.Minutes * 60) : "";
                        Log("Crew: training " + row.Name + " (" + row.RarityName + ") to +" + offer.ToLevel + " for " + M(offer.Price) + mins);
                        info.Status = "Training " + row.Name + " to +" + offer.ToLevel + ".";
                        if (offer.Minutes > 0 && (soonest < 0 || offer.Minutes * 60 < soonest)) soonest = offer.Minutes * 60;
                        continue;
                    }
                    string who = pick.Name + " (" + pick.RarityName + (pick.TrainLevel > 0 ? ", +" + pick.TrainLevel : "") + ")";
                    if (got == 0 && offer != null && offer.Price > 0 && offer.Names(pick.Name))
                    {

                        double onHand = CashNow();
                        string dear = who + " to +" + offer.ToLevel + " (" + M(offer.Price) + ", " + (onHand >= 0 ? M(onHand) : "?") + " on hand"
                                      + (keep > 0 ? ", " + M(keep) + " kept for the next property" : "") + ")";
                        if (waiting == null) waiting = dear;
                        why = "saving up to train " + waiting;
                        info.Status = "Saving up to train " + row.Name + " (" + M(offer.Price) + ").";
                        tooDear = Math.Min(tooDear, offer.Price);
                        continue;
                    }

                    else if (got == 0) why = who + ": the training window " + (offer == null ? "didn't read" : offer.Price <= 0 ? "showed no price the bot could read (\"" + offer.Words + "\")" : "named " + offer.Name) + " - CANCEL pressed";
                    else why = who + ": " + (game.TrainWhy ?? "nothing pressed");
                    break;
                }
            }
            finally
            {

                if (started > 0 && waiting != null)
                {
                    string key = Regex.Replace(waiting, @"[^ (]+ on hand", "");
                    if (key != trainSkipSaid) { trainSkipSaid = key; Log("Crew: " + waiting + " waits for the cash - the next best were trained meanwhile"); }
                }
                if (started > 0) trainSaid = null;
                else if (why != null)
                {

                    string key = Regex.Replace(Regex.Replace(why, @"[0-9]+[hms ]*[0-9]*[hms]* left", ""), @"[^ (]+ on hand", "");
                    if (key != trainSaid) { trainSaid = key; Log("Crew: nobody trained - " + why); }
                }
                if (soonest > 0)
                {
                    var at = DateTime.UtcNow.AddSeconds(soonest + 20);
                    if (at < nextCrew) nextCrew = at;
                }
            }
        }

        string trainSaid;
        string trainSkipSaid;
        readonly Dictionary<int, double> trainPrice = new Dictionary<int, double>();
        string trainNoTimerSaid;

        bool RerollWouldReplace(Game.CrewRow r, int best)
        {
            return S.CrewReroll && r.Rarity < best;
        }

        int dismissFails;
        string dismissFailsOn;
        int findFails;
        string findFailsOn;

        readonly List<Game.OwnStats> ownStats = new List<Game.OwnStats>();
        DateTime ownReadAt = DateTime.MinValue, gearBackHold = DateTime.MinValue;
        bool crewUnequipped;
        int gearBackFails;
        int ownUnknownLeft = -1;
        bool ownJustRead;

        bool CrewUnequipped
        {
            get { return crewUnequipped; }
            set
            {
                crewUnequipped = value;
                if (!value) gearBackFails = 0;
                if (S.CrewGearOff == value) return;
                S.CrewGearOff = value;
                SaveSettings();
            }
        }

        void SaveSettings()
        {
            try
            {
                S.Save(Path.Combine(dir, "IdleMafiaBot.ini"));
                if (S.Account.Length > 0) { Directory.CreateDirectory(Accounts.Folder(dir, S.Account)); S.Save(Accounts.Ini(dir, S.Account), true); }
            }
            catch (Exception e) { if (firstSeen.Add("settings not saved")) Log("Couldn't save the settings: " + e.Message); }
        }

        int ReadOwnStats()
        {
            ownReadAt = DateTime.UtcNow;
            bool pressed;
            string why;
            int read = 0;
            CrewUnequipped = true;
            bool off = game.UnequipAll(out pressed, out why);
            if (!pressed) CrewUnequipped = false;
            if (off)
            {
                Game.CrewPage page;
                foreach (var r in game.ReadWholeCrew(out page))
                    if (!r.Empty && !r.Locked && r.Attack >= 0 && r.Defense >= 0) { Game.NoteOwn(ownStats, r); read++; }
            }
            if (pressed) { game.ScrollPage(0.62, 40); DoEquip(); }
            if (read > 0) Log("Crew: read " + read + " henchmen's own stats with their gear off, then the best gear on again");
            else
            {
                Log("Crew: " + (pressed ? "UNEQUIP ALL pressed, but " + (why ?? "no stats read") : "UNEQUIP ALL not pressed: " + why) + " - own stats looked at again in an hour");
                ownReadAt = DateTime.UtcNow.AddHours(-5);
            }
            return read;
        }

        bool OwnReadDue(int unknown) { return OwnReadDue(unknown, ownUnknownLeft, DateTime.UtcNow - ownReadAt); }

        internal static bool OwnReadDue(int unknown, int unknownLeft, TimeSpan sinceRead)
        {
            if (unknown <= 0) return false;
            return sinceRead > TimeSpan.FromHours(6) || (unknown > unknownLeft && sinceRead > TimeSpan.FromHours(1));
        }

        DateTime nextReroll;
        List<Game.CrewRow> rerollCrew;
        DateTime rerollCrewAt;
        bool rerollEmpty;
        DateTime emptyFootSaidAt;
        int emptiedRarity = -1;
        bool emptiedByStats;
        double rerollCash = -1;
        string pausedIdle;
        DateTime pausedIdleAt;
        bool rerollShort;
        DateTime rerollShortAt;
        string rerollSaid;

        bool RerollCashOk(Header h, DateTime now)
        {
            if (!rerollShort) return true;
            double need = crewHirePrice[(int)S.CrewRollType] + (rerollEmpty ? 0 : PropertyKeep());
            return h != null && h.Cash >= 0 ? h.Cash >= need : now >= rerollShortAt.AddMinutes(1);
        }

        public volatile bool RerollOnly;

        public void StartRerollOnly() { onlyFails = 0; onlyProgressAt = DateTime.UtcNow; nextReroll = DateTime.UtcNow; RerollOnly = true; if (!Running) Begin(false); }

        public void StopRerollOnly() { if (!RerollOnly) return; RerollOnly = false; stop = true; }

        void EndRerollOnly(string why, bool finished)
        {
            if (!RerollOnly) return;
            Log("Only rerolling: " + why + " - the bot stops");
            RerollOnly = false;
            stop = true;
            stopWhy = (finished ? "Only rerolling is over: " : "Only rerolling gave up: ") + why;
        }

        void DoRerolls(bool only)
        {
            var info = new CrewInfo { At = DateTime.Now, SlotPrice = crewSlotPrice };
            List<Game.CrewRow> crew = null;
            try { RerollsHere(info, only, out crew); }
            finally { ShowCrewInfo(info, crew); }
        }

        List<Game.CrewRow> ReadCrewForRerolls(CrewInfo info, int step = 4)
        {
            Game.CrewPage page;
            var rows = game.ReadWholeCrew(out page, step);
            if (page == null || !page.Page) { Log("Crew: couldn't read the crew page"); game.Snapshot("crew not read", 60); return null; }

            info.Henchmen = page.Henchmen >= 0 ? page.Henchmen : game.CrewCountSeen;
            if (info.Henchmen < 0 && hiredRead >= 0 && hiredReadAt > slotBoughtAt) info.Henchmen = hiredRead;
            info.Slots = page.Slots; info.Empty = rows.Count(r => r.Empty);
            if (game.CrewTotalsSeen != null && game.CrewTotalsSeen.Read) { info.Attack = game.CrewTotalsSeen.Attack; info.Defense = game.CrewTotalsSeen.Defense; }
            foreach (var e in rows.Where(r => r.Empty)) for (int t = 0; t < 3; t++) if (e.HirePrice[t] > 0) crewHirePrice[t] = e.HirePrice[t];
            rerollEmpty = rows.Any(r => r.Empty);
            return rows.Where(r => !r.Empty && !r.Locked && r.Rarity >= 0).ToList();
        }

        List<Game.CrewRow> ReadAllHenchmen(CrewInfo info, out string why)
        {
            why = null;

            for (int look = 0; look < 3; look++)
            {

                int step = 4 - (listShortFails * 2 + look) % 3;
                var rows = ReadCrewForRerolls(info, step);
                if (rows == null) { why = "couldn't read the crew page"; return null; }

                int want = info.Henchmen - 1;
                if (info.Henchmen > 0 && rows.Count == want) { ListReadWhole(); return rows; }
                if (info.Henchmen > 0 && rows.Count > want)
                {

                    var merged = Game.MergeRepeats(rows, rows.Count - want);
                    if (merged != null)
                    {
                        Log("Crew: read " + rows.Count + " of " + want + " henchmen - " + (rows.Count - want == 1 ? "one was" : (rows.Count - want) + " were")
                            + " read twice where two pages meet (the same rarity, attack and defense), taken once");
                        ListReadWhole();
                        return merged;
                    }
                }
                why = info.Henchmen <= 0 ? "couldn't read how many henchmen there are" : "read " + rows.Count + " of " + want + " henchmen";

                if (look == 2 && listShortFails == 0) Log("Crew: the list read as " + string.Join(", ", rows.Select(r => r.ToString())));
            }
            if (listShortFails++ == 0) listShortSince = DateTime.UtcNow;
            game.Snapshot("crew list short", 60);
            game.NoteFailure(why);
            return null;
        }

        int listShortFails;
        DateTime listShortSince;
        int hiredRead = -1;
        DateTime hiredReadAt = DateTime.MinValue, slotBoughtAt = DateTime.MinValue;

        bool SlotStandsEmpty(out string count)
        {
            count = null;
            Func<Game.CrewPage, bool> open = p => p != null && p.Henchmen >= 0 && p.Slots > 0 && p.Henchmen < p.Slots;
            if (!open(game.CrewPageSeen)) return false;
            var now = game.LookAtCrew();
            if (!open(now)) return false;
            count = now.Henchmen + " of " + now.Slots;
            return true;
        }

        void ListReadWhole()
        {
            if (listShortFails > 0) Log("Crew: the whole list reads right again (it didn't from " + listShortSince.ToLocalTime().ToString("HH:mm") + ")");
            listShortFails = 0;
        }

        void RerollsHere(CrewInfo info, bool only, out List<Game.CrewRow> crew)
        {
            crew = null;
            var start = DateTime.UtcNow;
            nextReroll = start.AddMinutes(1);
            rerollShort = false;

            rerollCash = -1;
            var roll = S.CrewRollType;
            int best = Game.BestRoll(roll);
            double keep = only ? 0 : PropertyKeep();

            if (rerollCrew != null && start - rerollCrewAt < TimeSpan.FromMinutes(10) && game.OpenTab(Tab.Crew))
            {
                crew = rerollCrew;
                if (crewInfo != null) { info.Henchmen = crewInfo.Henchmen; info.Slots = crewInfo.Slots; info.Attack = crewInfo.Attack; info.Defense = crewInfo.Defense; }
            }
            else crew = ReadCrewForRerolls(info);
            rerollCrew = null;
            if (crew == null) { if (only) OnlyGo(false, "couldn't read the crew page", 0, keep); return; }
            int rolls = 0, fails = 0, readAt = Environment.TickCount - 60000, badgesAt = Environment.TickCount;
            int emptyHolds = 0;
            double spent = 0;
            Game.CrewRow lastHired = null;
            var better = new List<string>();
            string stop = null, pausedFor = null, idleFor = null;
            int idleUntil = 0;
            bool done = false, readAgain = false, reread = false, statsWait = false, statsSoon = false, listShort = false, skipWait = false;
            bool counted = false;
            try
            {
                while (true)
                {

                    if (!only && S.Events && EventOn(GameEvent.RecruitLuck) && Environment.TickCount - badgesAt > 30000)
                    {
                        badgesAt = Environment.TickCount;
                        BadgesInRerolls();
                    }
                    if (!only && !(S.Events && EventOn(GameEvent.RecruitLuck)))
                    {

                        var other = OtherTaskDue(ref readAt);
                        if (other != null && other == idleFor && Environment.TickCount < idleUntil) other = null;
                        else if (other != null && rolls == 0 && other == pausedIdle && DateTime.UtcNow - pausedIdleAt < TimeSpan.FromMinutes(1))
                        {
                            SayOnce("rerolls idle " + other, "Crew: the rerolls paused for " + other + " before their first reroll, and the round after didn't find it due - they now go on 30 s before looking again");
                            idleFor = other; idleUntil = Environment.TickCount + 30000; other = null;
                        }
                        if ((pausedFor = other) != null) break;
                    }
                    if (rolls >= 500) break;
                    if (fails >= 3) break;
                    if (readAgain)
                    {

                        readAgain = false;
                        var again = ReadCrewForRerolls(info);
                        if (again == null) { stop = "couldn't read the crew page"; fails = 3; break; }
                        crew = again;
                        counted = false;
                    }

                    double hold = rerollEmpty ? 0 : keep;
                    double cash = CashFor(crewHirePrice[(int)roll] + hold);

                    if (cash < 0) { stop = "couldn't read the cash on hand"; fails++; game.Wait(1000); continue; }
                    rerollCash = cash;
                    if (crewHirePrice[(int)roll] > cash - hold)
                    {
                        stop = hold > 0 && crewHirePrice[(int)roll] <= cash
                            ? "the cash is kept for the next property (" + M(hold) + ")"
                            : "saving up for " + roll + " hires (" + M(crewHirePrice[(int)roll]) + " each, " + M(cash) + " on hand)";

                        rerollShort = true;
                        rerollShortAt = DateTime.UtcNow;
                        nextReroll = DateTime.UtcNow.AddSeconds(15);
                        break;
                    }
                    Game.CrewPage p;
                    CrewRoll used;
                    double paid;
                    Game.CrewRow got;
                    if (rerollEmpty)
                    {

                        var empty = game.FindCrewRow(r => r.Empty, out p, true);
                        if (empty == null)
                        {

                            var seen = game.CrewPageSeen;
                            if (seen != null && seen.Henchmen >= 0 && seen.Slots > 0 && seen.Henchmen >= seen.Slots) { rerollEmpty = false; continue; }
                            stop = "a slot stands empty" + (seen != null && seen.Henchmen >= 0 && seen.Slots > 0 ? " (" + seen.Henchmen + " of " + seen.Slots + ")" : "")
                                 + " but it wasn't found to hire into - nobody is dismissed until it's filled";

                            if (DateTime.UtcNow - emptyFootSaidAt > TimeSpan.FromHours(1))
                            {
                                emptyFootSaidAt = DateTime.UtcNow;
                                Log("Crew: the empty slot wasn't found - the list's foot shows: " + game.CrewFootSeen());
                            }
                            game.Snapshot("crew empty slot not found", 60);
                            fails++; readAgain = true;
                            if (!only) break;
                            continue;
                        }
                        got = HireForReroll(empty, roll, cash, 0, out used, out paid);
                        if (got == null)
                        {
                            stop = paid > 0 ? "a hire showed no recruit" : "the " + roll + " hire isn't gold right now";
                            if (paid > 0) readAgain = true;
                            fails++;
                            if (!only) break;
                            continue;
                        }
                        rerollEmpty = false; fails = 0;
                        Count.Hired++; Count.CrewCash += paid; spent += paid;
                        counted = false;

                        PingHenchman(got, emptiedRarity >= 0 ? emptiedRarity : crew.Count > 0 ? crew.Min(r => r.Rarity) : -1,
                            emptiedByStats && got.Rarity >= best && got.Attack >= Settings.Perfect && got.Defense >= Settings.Perfect);
                        emptiedRarity = -1; emptiedByStats = false;
                        crew.Add(got);
                        Game.NoteOwn(ownStats, got);
                        Log("Crew: hired " + got + " into the empty slot with a " + used + " hire (" + M(paid) + ")");
                        continue;
                    }

                    var order = crew.Where(r => !r.Training && !r.Stationed).OrderBy(r => r.Rarity).ThenBy(r => r.Power).ToList();
                    var worst = order.FirstOrDefault();
                    if (worst == null) { stop = "no henchmen to reroll"; break; }
                    if (Skipped(worst))
                    {
                        var next = order.FirstOrDefault(r => r.Rarity == worst.Rarity && !Skipped(r));
                        if (next == null)
                        {
                            stop = worst.Name + " is left alone until " + SkipUntil(worst).ToLocalTime().ToString("HH:mm") + " (its DISMISS kept failing), and there's no other " + worst.RarityName + " to reroll";
                            skipWait = true;
                            break;
                        }
                        worst = next;
                    }

                    bool byStats = false;
                    Game.OwnStats weakOwn = null;
                    if (worst.Rarity >= best)
                    {
                        string everyone = "everyone is " + Game.RarityTitle(best) + ", the best " + roll + " hires can roll";
                        int min = S.CrewRerollStats > 0 ? Settings.Perfect : 0;
                        if (min <= 0) { stop = everyone; done = true; break; }

                        if (!S.EquipBest) { stop = everyone + " (rerolls by their own stats need \"Equip the best gear\" on)"; done = true; break; }
                        int unknown;
                        var weak = Game.WeakestOwn(crew.Where(r => !r.Training && !r.Stationed && !Skipped(r)).ToList(), ownStats, best, min, out unknown);
                        if (ownJustRead) { ownUnknownLeft = unknown; ownJustRead = false; }

                        if (unknown > 0 && OwnReadDue(unknown))
                        {
                            ReadOwnStats();
                            ownJustRead = true;
                            readAgain = true;
                            continue;
                        }
                        if (weak == null)
                        {
                            if (unknown == 0) { stop = everyone + ", with " + min + "+ attack and defense of their own"; done = true; }
                            else { stop = "couldn't read the own stats of " + unknown + " henchm" + (unknown == 1 ? "an" : "en"); statsWait = true; statsSoon = unknown > ownUnknownLeft; }
                            break;
                        }

                        bool sorted = false;
                        for (int go = 0; go < (only ? 10 : 2) && !sorted; go++) SortCrew(40, out sorted);
                        if (!sorted) { stop = "sorting the crew by their own stats first"; readAgain = true; break; }
                        worst = weak;
                        weakOwn = Game.OwnOf(weak, ownStats);
                        byStats = true;
                        info.Status = "Rerolling the weakest " + Game.RarityTitle(best) + ": " + weak.Name + " (own " + weakOwn + ", under " + min + ").";
                    }
                    else info.Status = "Rerolling the worst: " + worst + ".";

                    if ((byStats || worst.Rarity >= (int)Rarity.Mythic) && !counted)
                    {
                        string why;
                        var all = ReadAllHenchmen(info, out why);
                        if (all == null) { stop = "no Mythic or better dismissed: " + why; listShort = true; break; }
                        crew = all;
                        counted = true;
                        lastHired = null;
                        continue;
                    }

                    var row = !byStats && ReferenceEquals(worst, lastHired) ? worst : game.FindCrewRow(r => Game.NamedLike(r, worst), out p, true);
                    if (row == null)
                    {

                        if (!reread) { reread = readAgain = true; continue; }
                        stop = "couldn't find " + worst + " in the list";
                        game.Snapshot("crew row not found", 60);
                        game.NoteFailure(stop);
                        fails++; readAgain = true;

                        findFails = findFailsOn == worst.Key ? findFails + 1 : 1;
                        findFailsOn = worst.Key;
                        if (findFails >= 3)
                        {
                            rerollSkip.Add(Tuple.Create(worst.Name, worst.Rarity, DateTime.UtcNow.AddHours(3)));
                            Log("Crew: couldn't find " + worst + " in the list 3 times - it's left alone for 3 hours, the next one goes first");
                            findFails = 0; fails = 0;
                            continue;
                        }
                        if (!only) break;
                        continue;
                    }
                    findFails = 0;
                    Game.CrewRow emptied;

                    bool sure = (worst.Rarity < best && crew.All(r => r.Rarity >= worst.Rarity))
                                || (byStats && weakOwn != null && worst.Rarity == best && crew.All(r => r.Rarity >= best));

                    string open;
                    if (SlotStandsEmpty(out open))
                    {

                        if (++emptyHolds > 3) { stop = "a slot stands empty (" + open + ") and couldn't be filled - nobody is dismissed"; game.Snapshot("crew empty slot not filled", 60); fails = 3; break; }
                        Log("Crew: a slot stands empty (" + open + ") - it's filled before anyone else is dismissed");
                        rerollEmpty = true;
                        continue;
                    }
                    if (!game.DismissHenchman(row, worst, sure, out emptied))
                    {

                        game.Wait(1200);
                        var late = game.EmptyRowNear(row.Anchor.CenterY);
                        emptied = late != null && late.Anchor.CenterY >= row.Top && late.Anchor.CenterY <= row.Bottom ? late : null;

                        string nowOpen;
                        if (emptied == null && SlotStandsEmpty(out nowOpen))
                        {
                            rerollEmpty = true;
                            counted = false;
                            if (game.FindCrewRow(r => Game.NamedLike(r, worst), out p) == null)
                            {
                                dismissFails = 0;
                                crew.Remove(worst);
                                if (byStats) ownStats.Remove(weakOwn);
                                Log("Crew: dismissed " + worst + " - its row didn't show the empty slot, but the page counts " + nowOpen + " henchmen and it's gone from the list: the slot is filled next");
                                emptiedRarity = worst.Rarity; emptiedByStats = byStats;
                            }
                            else Log("Crew: a slot stands empty (" + nowOpen + ") - it's filled before anyone else is dismissed");
                            continue;
                        }
                        if (emptied == null)
                        {
                            var again = game.FindCrewRow(r => Game.NamedLike(r, worst), out p);
                            if (again == null || !game.DismissHenchman(again, worst, sure, out emptied))
                            {
                                stop = "DISMISS didn't take on " + worst; game.Snapshot("crew dismiss failed", 60);
                                game.NoteFailure(stop);
                                dismissFails = dismissFailsOn == worst.Key ? dismissFails + 1 : 1;
                                dismissFailsOn = worst.Key;
                                fails++;
                                if (dismissFails >= 3)
                                {

                                    rerollSkip.Add(Tuple.Create(worst.Name, worst.Rarity, DateTime.UtcNow.AddHours(3)));
                                    Log("Crew: DISMISS failed 3 times on " + worst + " - it's left alone for 3 hours, the next one goes first");
                                    dismissFails = 0; fails = 0; readAgain = true;
                                    continue;
                                }
                                if (!only) break;
                                continue;
                            }
                        }
                    }
                    dismissFails = 0;
                    crew.Remove(worst);
                    if (byStats)
                    {
                        ownStats.Remove(weakOwn);
                        Log("Crew: dismissed " + worst.Name + " (" + worst.RarityName + ", own " + weakOwn + ", under " + Settings.Perfect + ") to roll a better one");
                    }
                    rerollEmpty = true;
                    emptiedRarity = worst.Rarity; emptiedByStats = byStats;

                    got = HireForReroll(emptied, roll, cash, keep, out used, out paid);
                    if (got == null)
                    {

                        stop = paid > 0 ? "a hire showed no recruit" : "the " + roll + " hire isn't gold right now";
                        if (paid > 0) { rerollEmpty = false; readAgain = true; emptiedRarity = -1; }
                        fails++;
                        if (!only) break;
                        continue;
                    }
                    rerollEmpty = false; fails = 0; reread = false; emptiedRarity = -1; emptiedByStats = false;
                    rolls++; spent += paid; Count.Rerolls++; Count.CrewCash += paid;
                    counted = false;
                    crew.Add(got);
                    Game.NoteOwn(ownStats, got);
                    lastHired = got;
                    if (got.Rarity > worst.Rarity) better.Add(got.ToString());
                    PingHenchman(got, worst.Rarity, byStats && got.Rarity >= best && got.Attack >= Settings.Perfect && got.Defense >= Settings.Perfect);
                    SnapshotOnce("crew reroll");
                }
            }
            finally
            {

                if (!readAgain && crew != null) { rerollCrew = crew; rerollCrewAt = DateTime.UtcNow; }

                if (rolls > 0 && crew != null)
                    Log(string.Format("Crew: {0} reroll{1} with {2} hires ({3}){4}. The worst now: {5}{6}", rolls, rolls == 1 ? "" : "s", roll, M(spent),
                        better.Count > 0 ? ", better than before: " + string.Join(", ", better) : "", crew.OrderBy(r => r.Rarity).ThenBy(r => r.Power).FirstOrDefault(),
                        pausedFor != null ? " (paused for: " + pausedFor + ")" : ""));
            }

            if (rolls == 0 && (pausedFor != null || rerollShort)) Neutral();
            if (pausedFor != null && rolls == 0) { pausedIdle = pausedFor; pausedIdleAt = DateTime.UtcNow; }
            if (pausedFor != null) { nextReroll = DateTime.UtcNow; info.Status = "Rerolls paused for: " + pausedFor + "."; }
            else if (rolls >= 500) nextReroll = DateTime.UtcNow;
            else if (stop != null)
            {
                info.Status = (done ? "Rerolls done: " : "Rerolls wait: ") + stop + ".";
                if (done) nextReroll = DateTime.UtcNow.AddMinutes(30);
                if (statsWait) nextReroll = ownReadAt.AddHours(statsSoon ? 1 : 6);
                if (skipWait) nextReroll = rerollSkip.Select(x => x.Item3).DefaultIfEmpty(DateTime.UtcNow.AddMinutes(30)).Min();

                if (listShort)
                {
                    nextReroll = DateTime.UtcNow.AddMinutes(listShortFails <= 1 ? 5 : listShortFails == 2 ? 10 : 20);
                    info.Status = "Rerolls stuck since " + listShortSince.ToLocalTime().ToString("HH:mm") + ": " + stop.Replace("no Mythic or better dismissed: ", "") + ".";
                }

                string said = rerollShort ? "saving up " + roll + (keep > 0 ? " and keeping " + M(keep) : "") : stop;
                if (rerollSaid != said) { rerollSaid = said; if (!only) Log("Crew: rerolls " + (done ? "done" : "wait") + " - " + stop); }
            }
            var skipped = rerollSkip.Where(x => x.Item3 > DateTime.UtcNow).ToList();
            if (skipped.Count > 0) info.Status += " Left alone for now (its DISMISS kept failing): " + string.Join(", ", skipped.Select(x => x.Item1 + " until " + x.Item3.ToLocalTime().ToString("HH:mm"))) + ".";
            if (only) OnlyGo(done, stop, rolls, keep);
        }

        readonly List<Tuple<string, int, DateTime>> rerollSkip = new List<Tuple<string, int, DateTime>>();

        bool Skipped(Game.CrewRow r)
        {
            var now = DateTime.UtcNow;
            rerollSkip.RemoveAll(x => x.Item3 <= now);
            return rerollSkip.Any(x => x.Item2 == r.Rarity && Game.WholeNameSame(x.Item1, r.Name));
        }

        DateTime SkipUntil(Game.CrewRow r)
        {
            return rerollSkip.Where(x => x.Item2 == r.Rarity && Game.WholeNameSame(x.Item1, r.Name)).Select(x => x.Item3).DefaultIfEmpty(DateTime.UtcNow).Max();
        }

        int onlyFails;
        DateTime onlyProgressAt;

        void OnlyGo(bool done, string why, int rolls, double keep)
        {
            var now = DateTime.UtcNow;
            if (rolls > 0) { onlyFails = 0; onlyProgressAt = now; }
            if (why == null && rolls > 0) return;
            if (done) { EndRerollOnly(why, true); return; }
            if (rerollShort)
            {
                game.Wait(3000);
                double cash = CashNow();
                if (cash < 0 || cash >= crewHirePrice[(int)S.CrewRollType] + keep) { nextReroll = now; return; }
                EndRerollOnly(why, true);
                return;
            }
            onlyFails++;
            if (onlyFails >= 3 || now - onlyProgressAt > TimeSpan.FromHours(1)) { EndRerollOnly(why + " (" + onlyFails + " tries in a row)", false); return; }
            if (nextReroll < now.AddMinutes(1)) nextReroll = now.AddMinutes(onlyFails == 1 ? 1 : 2);
            Log("Only rerolling: " + (why ?? "nothing rerolled") + " - trying again at " + nextReroll.ToLocalTime().ToString("HH:mm"));
        }

        Game.CrewRow HireForReroll(Game.CrewRow empty, CrewRoll roll, double cash, double keep, out CrewRoll used, out double paid)
        {
            var got = game.HireInto(empty, roll, false, cash, keep, out used, out paid);
            if (got == null && paid <= 0)
            {

                game.Wait(600);
                var again = game.EmptyRowNear(empty.Anchor.CenterY);
                if (again != null) got = game.HireInto(again, roll, false, cash, keep, out used, out paid);
            }
            if (got == null) game.Snapshot(paid > 0 ? "crew hire no recruit" : "crew hire not pressed", 60);
            return got;
        }

        string OtherTaskDue(ref int readAt)
        {
            bool unsure = !energyRegen.HasValue || !staminaRegen.HasValue;
            if (Environment.TickCount - readAt > (unsure ? 10000 : 30000))
            {
                readAt = Environment.TickCount;
                var fresh = unsure ? ReadBarsSettled(4000, x => x.Energy >= 0 && x.Stamina >= 0) : ReadBarsSettled(0, x => true);
                NoteTicks(fresh);
                if (last == null) last = fresh;
                else
                {
                    if (fresh.Level > 0) { last.Level = fresh.Level; last.Xp = fresh.Xp; last.XpNext = fresh.XpNext; }
                    if (fresh.SkillPoints >= 0) last.SkillPoints = fresh.SkillPoints;
                    if (fresh.Health >= 0) { last.Health = fresh.Health; last.HealthMax = fresh.HealthMax; }
                    if (fresh.Cash >= 0) last.Cash = fresh.Cash;
                    if (fresh.Banked >= 0) last.Banked = fresh.Banked;
                }
            }
            if (last == null) return null;
            var now = DateTime.UtcNow;

            int e = energyRegen.Guess(now), st = staminaRegen.Guess(now);
            if (e >= 0) { last.Energy = e; last.EnergyMax = energyRegen.Max; }
            if (st >= 0) { last.Stamina = st; last.StaminaMax = staminaRegen.Max; }
            if (rerollCash >= 0) last.Cash = rerollCash;
            var due = WhatIsDue(last, false);
            return due.Count > 0 ? string.Join(", ", due) : null;
        }

        int SortCrew(int maxMoves, out bool done)
        {
            done = false;
            sortWasStuck = false;
            int moves = 0, noArrow = 0;
            var movedUp = new List<string>();

            bool autoTried = ByOwnStats, restart = false;
            try
            {
                for (int pass = 0; pass < 6 && moves < maxMoves; pass++)
                {
                    int before = moves;
                    string lastSig = null;
                    Game.CrewRow lastMover = null;
                    bool top = false;
                    restart = false;
                    game.CrewListEnd(-1);
                    for (int look = 0; look < 120; look++)
                    {
                        Game.CrewPage p;
                        using (var f = game.Capture()) p = game.ReadCrew(f);
                        if (!p.Page) return moves;
                        var inSight = p.Rows.Where(r => !r.Empty && !r.Locked && !r.Cut && r.Rarity >= 0).ToList();
                        int missing;

                        var mover = Game.NextToMoveUp(inSight, out missing, ByOwnStats ? ownStats : null, SortSkipped);
                        noArrow += missing;
                        if (mover != null && !autoTried)
                        {
                            autoTried = true;
                            string why;
                            if (game.AutoSortCrew(out why)) { Log("Crew: AUTO SORT CREW pressed (the higher rarities on top)"); restart = true; break; }
                            if (firstSeen.Add("crew auto sort " + why)) Log("Crew: AUTO SORT CREW not pressed (" + why + ") - sorting with the arrows");
                        }
                        if (mover != null)
                        {
                            if (moves >= maxMoves) return moves;

                            if (lastMover != null && Game.SameHenchman(lastMover, mover) && Math.Abs(lastMover.Anchor.CenterY - mover.Anchor.CenterY) < 4)
                            {
                                sortStuck.Add(Tuple.Create(mover.Name, mover.Rarity, DateTime.UtcNow.AddHours(1)));
                                sortWasStuck = true;
                                Log("Crew: an up arrow didn't move " + mover + " - it stays where it is for an hour, the sort goes on without it");
                                SnapshotOnce("crew arrow no move");
                                lastMover = null;
                                continue;
                            }
                            game.CrewMoveUp(mover);
                            string who = (mover.Name.Length > 0 ? mover.Name : "a henchman") + " (" + mover.RarityName + ")";
                            if (!movedUp.Contains(who)) movedUp.Add(who);
                            lastMover = mover;
                            moves++;
                            continue;
                        }
                        string sig = string.Join(",", p.Rows.Select(r => r.Empty ? "empty" : r.Locked ? "locked" : r.Key));
                        if (sig == lastSig) { top = true; break; }
                        lastSig = sig;
                        game.ScrollPage(0.62, game.ListNotches(3));
                    }
                    if (restart) continue;
                    if (!top) break;
                    if (moves == before) { done = true; break; }
                }
            }
            finally
            {
                if (moves > 0)
                    Log("Crew: sorted by rarity" + (ByOwnStats && ownStats.Count > 0 ? " and own stats" : "") + ", " + moves + " move" + (moves == 1 ? "" : "s") + " up: " + string.Join(", ", movedUp.Take(5)) + (movedUp.Count > 5 ? " and " + (movedUp.Count - 5) + " more" : ""));
            }
            if (noArrow > 0 && moves == 0 && firstSeen.Add("crew arrows")) { Log("Crew: couldn't find the up arrows to sort the crew"); SnapshotOnce("crew arrows not found"); }
            return moves;
        }

        bool ByOwnStats { get { return S.CrewReroll && S.CrewRerollStats > 0; } }

        readonly List<Tuple<string, int, DateTime>> sortStuck = new List<Tuple<string, int, DateTime>>();
        bool sortWasStuck;
        int sortStuckRounds;

        bool SortSkipped(Game.CrewRow r)
        {
            var now = DateTime.UtcNow;
            sortStuck.RemoveAll(x => x.Item3 <= now);
            return sortStuck.Any(x => x.Item2 == r.Rarity && Game.WholeNameSame(x.Item1, r.Name));
        }

        DateTime nextBriefcases, nextEvents;
        string badgeSig, eventsSig;
        const int UnreadEventSeconds = 10 * 60;
        readonly Dictionary<GameEvent, DateTime> eventsOn = new Dictionary<GameEvent, DateTime>();
        string briefcasesSaid;

        void NoteBadges(Frame f)
        {
            string sig = Game.BadgeSignature(Game.FindBadges(f));

            noBadges = sig.Length == 0 ? noBadges + 1 : 0;
            if (sig.Length == 0 && noBadges < 3) return;
            badgeSig = sig;
            if (badgeSig.Length == 0 && eventsOn.Count > 0) { eventsOn.Clear(); eventsSig = ""; Log("Events: none running now"); }
        }

        int noBadges;

        bool EventOn(GameEvent e)
        {
            DateTime until;
            return eventsOn.TryGetValue(e, out until) && DateTime.UtcNow < until;
        }

        void BadgesInRerolls()
        {
            List<Game.EventBadge> badges;
            using (var f = game.Capture()) badges = Game.FindBadges(f);
            if (badges.Count == 0)
            {

                game.Wait(1000);
                using (var f = game.Capture()) badges = Game.FindBadges(f);
            }
            string sig = Game.BadgeSignature(badges);
            if (sig == eventsSig) return;
            badgeSig = sig;
            eventsOn.Remove(GameEvent.RecruitLuck);
            Log(Game.LuckBadge(badges) ? "Events: the badges changed - read again before more rerolls"
                                       : "Events: recruitment luck's badge is gone - the other tasks go first again");
        }

        bool BossDamageHold()
        {
            DateTime until;
            if (!S.Events || !S.Bosses || !Can(Tab.Bosses) || !eventsOn.TryGetValue(GameEvent.BossDamage, out until) || DateTime.UtcNow >= until) return false;
            return bossesUp || bossBackAt < until || bossesReadAt == DateTime.MinValue;
        }

        bool CratesHeld(DateTime now)
        {
            if (!S.Events || EventOn(GameEvent.CrateLuck)) return false;
            DateTime at;
            if (!DateTime.TryParse(S.CratesOpenedAt, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out at)) return false;
            return now < at.AddHours(24);
        }

        void DoEvents()
        {
            var now = DateTime.UtcNow;
            nextEvents = now.AddMinutes(30);
            List<Game.EventBadge> badges;
            using (var f = game.Capture()) badges = Game.FindBadges(f);
            string sig = Game.BadgeSignature(badges);
            badgeSig = eventsSig = sig;
            if (badges.Count == 0) { if (eventsOn.Count > 0) { eventsOn.Clear(); Log("Events: none running now"); } return; }
            var read = game.ReadEvents(badges);
            var before = new HashSet<GameEvent>(eventsOn.Where(kv => kv.Value > now).Select(kv => kv.Key));
            eventsOn.Clear();
            foreach (var e in read.Where(e => e.Kind != GameEvent.Other))
            {

                var until = now.AddSeconds(e.SecondsLeft > 0 ? e.SecondsLeft : UnreadEventSeconds);
                DateTime had;
                if (!eventsOn.TryGetValue(e.Kind, out had) || had < until) eventsOn[e.Kind] = until;
            }
            int soonest = read.Select(e => e.SecondsLeft > 0 ? e.SecondsLeft : UnreadEventSeconds).DefaultIfEmpty(30 * 60).Min();
            nextEvents = now.AddSeconds(Math.Min(30 * 60, soonest + 5));
            if (!before.SetEquals(eventsOn.Keys))
                Log("Events: " + (read.Count > 0 ? string.Join(", ", read.Select(e => e + EventPlan(e.Kind))) : badges.Count + " badges, their words didn't read"));

            if (eventsOn.ContainsKey(GameEvent.CrateLuck) && !before.Contains(GameEvent.CrateLuck)) nextCrates = now;
            if (eventsOn.ContainsKey(GameEvent.BossDamage) && !before.Contains(GameEvent.BossDamage)) nextBosses = now;
            if (eventsOn.ContainsKey(GameEvent.RecruitLuck) && !before.Contains(GameEvent.RecruitLuck)) nextReroll = now;
            if (eventsOn.ContainsKey(GameEvent.OpsSpeed) && !before.Contains(GameEvent.OpsSpeed)) nextOps = now;
            if (read.Count < badges.Count) SnapshotOnce("event badge words not read");
            foreach (var e in read.Where(e => e.Kind == GameEvent.Other)) SnapshotOnce("event " + e.Title);
        }

        string EventPlan(GameEvent e)
        {
            switch (e)
            {
                case GameEvent.RecruitLuck: return S.CrewReroll ? ": the rerolls go first while it runs" : "";
                case GameEvent.CrateLuck: return S.Crates ? ": the crates are opened now" : "";
                case GameEvent.BossDamage: return S.Bosses ? ": stamina goes to the bosses, no gifts while one is up" : "";
                case GameEvent.OpsSpeed: return S.Operations ? ": operations looked at twice as often" : "";
                default: return "";
            }
        }

        void DoBriefcases()
        {
            nextBriefcases = DateTime.UtcNow.AddMinutes(20);
            if (!game.OpenBriefcases())
            {
                nextBriefcases = DateTime.UtcNow.AddHours(1);
                briefcasesStopped = false;
                if (briefcasesSaid != "off")
                {
                    Log(briefcasesSaid == "on" ? "Lucky Briefcases: no longer in the game's menu - the event is over. The bot looks again every hour"
                                               : "Lucky Briefcases: not in the game's menu (no event right now). The bot looks again every hour");
                    briefcasesSaid = "off";
                }
                return;
            }
            if (briefcasesSaid != "on") { briefcasesSaid = "on"; Log("Lucky Briefcases: the event is on - the bot picks a briefcase with each free pick (one every 20 minutes online), never with keys"); }

            if (briefcasesStopped) { nextBriefcases = DateTime.UtcNow.AddHours(6); return; }
            for (int pick = 0; pick < 6; pick++)
            {

                var page = ReadBriefcasesSteady();
                if (page == null || !page.Page)
                {
                    if (page == null) SayBriefcases("the free picks didn't read the same twice - not picking now, looked at again in 10 minutes");
                    nextBriefcases = DateTime.UtcNow.AddMinutes(10);
                    break;
                }
                if (page.ButtonFree <= 0)
                {

                    nextBriefcases = DateTime.UtcNow.AddSeconds(page.NextSeconds > 0 ? Math.Min(page.NextSeconds + 15, 3600) : 20 * 60);
                    SayBriefcases(page.ButtonFree < 0 ? "the pick button didn't read - looked at again in 20 minutes"
                                  : "no free pick right now - the next one in " + (page.NextSeconds > 0 ? Parse.Short(page.NextSeconds) : "? (its time didn't read; looked at again in 20 minutes)"));
                    break;
                }
                if (page.Cases.Count == 0)
                {
                    Log("Lucky Briefcases: a free pick, but no closed briefcase in sight - looked at again in an hour");
                    SnapshotOnce("briefcases none closed");
                    nextBriefcases = DateTime.UtcNow.AddHours(1);
                    break;
                }
                Game.BriefcasePage after;
                string paid = game.PickBriefcase(page, out after);
                if (page.Keys >= 0 && after != null && after.Page && after.Keys >= 0 && after.Keys < page.Keys)
                {

                    briefcasesStopped = true;
                    Log("Lucky Briefcases: the keys went down (" + page.Keys + " -> " + after.Keys + ") after a pick - no more picks while this event runs");
                    game.Snapshot("briefcase keys went down");
                    nextBriefcases = DateTime.UtcNow.AddHours(6);
                    return;
                }
                if (paid == null) { nextBriefcases = DateTime.UtcNow.AddMinutes(10); break; }
                Count.Rewards++;
                Log("Lucky Briefcases: a free pick paid " + (paid.Length > 0 ? CultureInfo.InvariantCulture.TextInfo.ToTitleCase(paid.ToLowerInvariant()) : "something (its words didn't read)"));
            }
        }

        bool briefcasesStopped;
        DateTime briefcasesSaidAt = DateTime.MinValue;

        void SayBriefcases(string what)
        {
            if (DateTime.UtcNow < briefcasesSaidAt) return;
            briefcasesSaidAt = DateTime.UtcNow.AddHours(1);
            Log("Lucky Briefcases: " + what);
        }

        Game.BriefcasePage ReadBriefcasesSteady()
        {
            Game.BriefcasePage a, b;
            using (var f = game.Capture()) a = game.ReadBriefcases(f);
            if (!a.Page) return a;
            game.Wait(1000);
            using (var f = game.Capture()) b = game.ReadBriefcases(f);
            if (!b.Page) return b;
            return a.ButtonFree == b.ButtonFree ? b : null;
        }

        DateTime nextEquip, nextShop, nextSafehouse, nextTrophies;
        bool saidBestGear, saidDeposit;

        public event Action<SafehouseInfo> SafehouseRead;

        void DoGear()
        {

            var now = DateTime.UtcNow;
            bool gearDue = now >= nextEquip;
            nextEquip = now.AddHours(6);
            int bought = 0, opened = 0;
            if (ShopOn() && now >= nextShop && Can(Tab.Shop)) bought = BuyFromShop();
            if (S.Crates && now >= nextCrates && Can(Tab.Inventory) && !CratesHeld(now))
            {
                nextCrates = now.AddMinutes(20);
                bool done;
                opened = Report(game.OpenCrates(out done), "Opened {0} crate(s)" + (S.Events && EventOn(GameEvent.CrateLuck) ? " during crate luck" : ""));
                Count.Crates += opened;

                if (done) S.CratesOpenedAt = now.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
            }

            if (now >= nextTrophies && Can(Tab.Inventory)) { nextTrophies = DateTime.UtcNow.AddHours(1); Count.Rewards += game.ClaimTrophies(); }
            if (bought > 0 || opened > 0 || gearDue) EquipThenDeposit();
        }

        string shopLeftSaid;

        string shopRaritySaid;
        double shopKeepSaid;

        bool ShopOn() { return S.ShopBuy && (S.ShopAnyOn() || TutorialOn); }

        double ShopKeep(bool all)
        {
            if (TutorialOn) return 0;
            double keep = PropertyKeep();
            if (keep <= 0 && S.SafehouseUpgrade && upgradePrice > 0 && Can(Tab.Safehouse)) keep = upgradePrice;
            if (keep <= 0 || all) return Math.Max(0, keep);
            double banked = CanWithdraw() && last != null && last.Banked > 0 ? last.Banked : 0;
            return Math.Max(0, keep - banked);
        }

        int BuyFromShop()
        {
            int stock;

            double keep = ShopKeep(false);
            if (keep > 0 && keep != shopKeepSaid)
                Log("Shop: keeps " + M(keep) + " of the cash on hand for " + (PropertyKeep() > 0 ? "the next property" : "the next safehouse upgrade"));
            shopKeepSaid = keep;
            var dear = new List<Game.ShopItem>();

            var left = new HashSet<string>();
            Func<Game.ShopItem, bool> want = i =>
            {
                if (S.ShopWants(i.Rarity) || (TutorialOn && tutorialItem != null && i.Name != null && Parse.Key(i.Name) == Parse.Key(tutorialItem))) return true;
                left.Add((i.Name ?? "an item") + (i.Rarity < 0 ? " (its rarity didn't read)" : " (" + Game.Rarities[i.Rarity].ToLowerInvariant() + ")"));
                return false;
            };
            var bought = game.BuyFromShop(out stock, keep, TutorialOn ? tutorialItem : null, dear, 0, want);

            if (S.Events && EventOn(GameEvent.ShopSale)) SnapshotOnce("shop sale page");

            nextShop = DateTime.UtcNow.AddSeconds(stock >= 0 ? Math.Max(120, stock + 5) : 15 * 60);

            bool bankPays = S.ShopWithdraw && S.Bank && BankFee() == 0 && (S.ShopBankMythic || S.ShopBankSecret || S.ShopBankForbidden);
            var unread = bankPays ? dear.Where(i => i.Rarity < 0 && i.Price > 0).ToList() : new List<Game.ShopItem>();
            string unreadSay = unread.Count == 0 ? null : "Shop: left " + string.Join(", ", unread.Select(i => i.ToString())) + " for the cash on hand - its rarity didn't read, and the bank's cash only pays for the rarities picked";
            if (unreadSay != null && unreadSay != shopRaritySaid) { Log(unreadSay); SnapshotOnce("shop rarity not read"); }
            if (unreadSay != null) shopRaritySaid = unreadSay;
            dear.RemoveAll(i => !S.ShopBankFor(i.Rarity));
            if (S.ShopWithdraw && dear.Count > 0)
            {
                double cheapest = dear.Min(i => i.Price), keepAll = ShopKeep(true);
                bool took = WithdrawFor(cheapest + keepAll, false);
                try
                {
                    int again;
                    if (took) bought.AddRange(game.BuyFromShop(out again, keepAll, null, null, cheapest * 0.99, i => want(i) && S.ShopBankFor(i.Rarity)));
                }
                finally { if (took) DepositBack(); }
            }

            string leftSay = left.Count == 0 ? null : "Shop: left in the shop (its rarity is switched off): " + string.Join(", ", left.OrderBy(x => x));
            if (leftSay != null && leftSay != shopLeftSaid) Log(leftSay);
            shopLeftSaid = leftSay;
            if (bought.Count == 0) return 0;
            Count.Bought += bought.Count;
            Log("Shop: bought " + string.Join(", ", bought.Select(i => i.ToString())));
            SnapshotOnce("shop bought");
            return bought.Count;
        }

        void EquipThenDeposit()
        {
            if (!S.EquipBest || !Can(Tab.Crew)) return;
            if (!DoEquip() || !Can(Tab.Collection)) return;

            if (S.MarketPending.Length > 0 && !Settings.MarketRelistShown)
            {

                Log("Black Market: " + S.MarketPending.Split('|')[0] + " was taken out of your shop to relist and is in your inventory - the bot no longer relists (the game asks a captcha now), so put it back up yourself");
                S.MarketPending = "";
                SaveSettings();
            }
            if (S.MarketPending.Length > 0) { Log("Collection: no deposit while " + S.MarketPending.Split('|')[0] + " waits to go back up on the Black Market"); return; }
            DepositCollection();
        }

        bool DoEquip()
        {
            nextEquip = DateTime.UtcNow.AddHours(6);
            rerollCrew = null;
            var r = game.EquipBest();
            if (r == null) return false;
            var a = r.Before;
            var b = r.After ?? new Game.CrewTotals();
            if (r.Window != null) { Log("Crew: a window came up after AUTO EQUIP BEST - left alone: \"" + r.Window + "\""); return false; }
            if (!r.GearOn)
            {

                Log("Crew: AUTO EQUIP BEST pressed " + r.Presses + " time" + (r.Presses == 1 ? "" : "s") + ", but nothing showed it took (" + a + " before, " + b + " after"
                    + (r.Bare ? ", no gear on the crew" : "") + ") - no deposit into the collection this time");
                game.Snapshot("auto equip best not confirmed", 60);
                return false;
            }
            CrewUnequipped = false;
            if (!a.Read || !b.Read) Log("Crew: equipped the best gear (" + a + " before, " + b + " after)");
            else if (a.Attack != b.Attack || a.Defense != b.Defense) Log(string.Format("Crew: best gear on. Attack +{0} -> +{1}, defense +{2} -> +{3}", a.Attack, b.Attack, a.Defense, b.Defense));
            else if (!saidBestGear) Log("Crew: already wearing the best gear (" + b + ")");
            saidBestGear = true;
            SnapshotOnce("crew auto equip best");
            return true;
        }

        void DepositCollection()
        {
            var r = game.DepositCollection();
            if (r == null) return;
            var a = r.Before;
            var b = r.After ?? new Game.CollectionState();
            if (r.Window != null) Log("Collection: a window came up after DEPOSIT ALL - left alone: \"" + r.Window + "\"");
            bool more = a.Deposited >= 0 && b.Deposited > a.Deposited, stronger = a.Attack >= 0 && b.Attack > a.Attack;
            if (more || stronger)
            {

                if (more) Count.Collected += b.Deposited - a.Deposited;
                Log("Collection: " + (more ? string.Format("{0} new item(s) deposited, {1} of {2}", b.Deposited - a.Deposited, b.Deposited, b.Total) : "new items deposited")
                    + (stronger ? string.Format(". Attack +{0} -> +{1}", a.Attack, b.Attack) : ""));
            }

            else if (!saidDeposit) Log("Collection: DEPOSIT ALL pressed (" + b + ")" + (!string.IsNullOrEmpty(r.Toast) ? " - \"" + r.Toast + "\"" : ""));
            saidDeposit = true;
            SnapshotOnce("collection deposit");
        }

        void ReadSafehouse()
        {
            nextSafehouse = DateTime.UtcNow.AddHours(24);
            var info = game.ReadSafehouse();
            if (info == null || info.Found == 0)
            {
                if (info != null) { Log("Couldn't read the Safehouse page"); game.Snapshot("safehouse not read", 60); }
                nextSafehouse = DateTime.UtcNow.AddHours(1);
                return;
            }

            if (info.AttackPower > 0 && S.LastAttackPower > 0 && info.AttackPower * 3 < S.LastAttackPower) info.AttackPower = -1;
            if (info.DefensePower > 0 && S.LastDefensePower > 0 && info.DefensePower * 3 < S.LastDefensePower) info.DefensePower = -1;
            if ((info.AttackPower > 0 && info.AttackPower != S.LastAttackPower) || (info.DefensePower > 0 && info.DefensePower != S.LastDefensePower))
            {
                if (info.AttackPower > 0) S.LastAttackPower = info.AttackPower;
                if (info.DefensePower > 0) S.LastDefensePower = info.DefensePower;
                SaveSettings();
            }
            foreach (var line in info.Lines()) Log(line);
            if (info.Hired >= 0) { hiredRead = info.Hired; hiredReadAt = DateTime.UtcNow; }
            if (info.Level > 0 && info.MaxLevel > 0 && info.Level >= info.MaxLevel) upgradePrice = 0;
            else if (info.UpgradeCost > 0) upgradePrice = info.UpgradeCost;
            if (!info.Complete) SnapshotOnce("safehouse partly read");
            var h = SafehouseRead;
            if (h != null) h(info);
        }

        void DoOperations()
        {
            nextOps = DateTime.UtcNow.AddMinutes(5);
            if (!game.OpenTab(Tab.Operations)) { Visited(Tab.Operations, null, true); return; }
            int collectedBefore = Count.OpsCollected, startedBefore = Count.OpsStarted;
            List<OpSlot> slots;
            using (var f = game.Capture()) slots = game.ReadOpSlots(f);

            for (int i = 0; i < 4; i++)
            {
                var s = slots.FirstOrDefault(x => x.Collect != null);
                if (s == null) break;
                game.CollectOp(s);
                Count.OpsCollected++;
                string toast = game.ReadToast(1500, t => Parse.Has(t, "COLLECTED"));
                Log(toast != null ? "Operation done - " + Regex.Replace(toast, @"\s+[xX×]$", "")
                                                                     : "Collected operation \"" + s.Name + "\"");
                using (var f = game.Capture()) slots = game.ReadOpSlots(f);
            }

            int free = slots.Count(x => !x.Locked && !x.Running && !x.Done && x.Collect == null && x.Name.Length == 0);
            if (slots.Any(x => x.Done && x.Collect == null) && firstSeen.Add("finished operation without collect"))
            {
                Log("An operation is finished but I can't find its collect button - leaving it for you");
                game.Snapshot("operation finished without collect button");
            }
            if (slots.Any(x => !x.Locked && !x.Running && !x.Done && x.Collect == null && x.Name.Length > 0))
                SnapshotOnce("operation slot in an unknown state");
            if (free > 0)
            {
                StartOperations(free);
                game.OpsTop();
            }

            using (var f = game.Capture()) slots = game.ReadOpSlots(f);
            var ops = OpsRead;
            if (ops != null) ops(slots);

            bool allRunning = slots.Count > 0 && slots.Where(x => !x.Locked).All(x => x.Running);
            var left = slots.Where(x => x.Running && x.SecondsLeft > 0).Select(x => x.SecondsLeft).ToList();
            int soonest = -1;
            if (left.Count > 0)
            {
                soonest = left.Min();

                if (S.Events && EventOn(GameEvent.OpsSpeed)) soonest /= 2;
                nextOps = DateTime.UtcNow.AddSeconds(Math.Min(soonest + 8, 30 * 60));
            }

            Visited(Tab.Operations, Count.OpsCollected > collectedBefore || Count.OpsStarted > startedBefore, !allRunning,
                    soonest >= 0 ? DateTime.UtcNow.AddSeconds(soonest + 600) : DateTime.MinValue);
        }

        bool opsNoneSaid;

        void StartOperations(int free)
        {
            game.OpsTop();
            var all = new List<OpRow>();
            string lastSig = null;
            int step = 1;
            for (int page = 0; page < 30; page++)
            {
                List<OpRow> rows;
                using (var f = game.Capture()) rows = game.ReadOpRows(f, page);
                if (page == 0) step = Game.OpsStep(rows);
                foreach (var r in rows)
                    if (!all.Any(a => Parse.Key(a.Name) == Parse.Key(r.Name))) all.Add(r);
                string sig = string.Join(",", rows.Select(r => r.Name));
                if (sig == lastSig) break;
                lastSig = sig;
                game.OpsDown(step);
            }

            OpRow pick = all.Where(r => r.CanStart && r.Seconds > 0 && r.Xp > 0).OrderByDescending(r => (double)r.Xp / r.Seconds).FirstOrDefault();
            if (pick == null)
            {

                if (!opsNoneSaid) Log("A slot is free but no operation can be started (not enough cash on hand?) - looking again every 15 minutes");
                opsNoneSaid = true;
                SnapshotOnce("no operation to start");
                nextOps = DateTime.UtcNow.AddMinutes(15);
                return;
            }
            opsNoneSaid = false;

            game.OpsTop();
            for (int i = 0; i < pick.Page; i++) game.OpsDown(step);
            for (int n = 0; n < free; n++)
            {
                OpRow row;
                using (var f = game.Capture()) row = game.ReadOpRows(f, pick.Page).FirstOrDefault(r => Parse.Key(r.Name) == Parse.Key(pick.Name));
                if (row == null || !row.CanStart) break;
                game.StartOp(row);
                Count.OpsStarted++;
                Log("Started operation \"" + row.Name + "\"");
            }
        }

        static bool IsAttackButton(string text)
        {
            string k = Parse.Key(text);
            return k.Contains("ATTACK") || k == "HIT" || k.StartsWith("HIT") || k.Contains("FIGHT") || k.Contains("STRIKE") || k.Contains("CHAIIENGE");
        }

        enum BossOutcome { Defeated, HitLimit, NoStamina, LowHealth, CantRead, BarsUnreadable }

        int bossFailRounds;

        static bool SameBoss(BossCard a, BossCard b)
        {
            if (Math.Abs(a.ButtonX - b.ButtonX) > 40) return false;
            string ka = Parse.Key(a.Name), kb = Parse.Key(b.Name);
            if (ka.Length >= 3 && kb.Length >= 3) return View.Distance(ka, kb) <= Math.Max(1, Math.Min(ka.Length, kb.Length) / 5);
            return a.Level > 0 && a.Level == b.Level;
        }

        BossCard FindBoss(BossCard seen)
        {
            using (var f = game.Capture()) return game.ReadBosses(f).FirstOrDefault(c => SameBoss(c, seen));
        }

        void DoBosses()
        {
            nextBosses = DateTime.UtcNow.AddMinutes(20);
            if (!game.OpenTab(Tab.Bosses)) return;
            var st = ReadStage();
            if (st != null && st.Stage < 0 && st.StagesTab != null) { game.StagesTab(st); st = ReadStage(); }
            if (st == null || st.Stage < 0)
            {
                Log("Bosses: the stages page didn't read" + (st != null && st.Said.Length > 0 ? " (\"" + Cut(st.Said, 160) + "\")" : ""));
                game.Snapshot("boss stage page not read", 60);
                nextBosses = DateTime.UtcNow.AddMinutes(15);
                return;
            }
            int hits = 0, waits = 0, nexts = 0, stamina = -1, sinceBars = 99, stageHits = 0, stageAt = st.Stage;
            string stageBoss = st.Boss, stop = null;
            var done = new List<string>();
            for (int look = 0; look < 400 && stop == null; look++)
            {
                if (st == null || st.Stage < 0)
                {
                    if (++waits > 8) { stop = "the page stopped reading"; game.Snapshot("boss stage page not read", 60); break; }
                    game.Wait(500);
                    st = ReadStage();
                    continue;
                }
                if (st.Stage != stageAt) { stageAt = st.Stage; stageBoss = st.Boss; stageHits = 0; }
                if (st.Boss.Length > 0) stageBoss = st.Boss;
                if (st.Attack == null)
                {

                    if (st.Cooldown || ((st.DefeatedToday || st.Cleared) && waits < 10)) { waits++; game.Wait(500); st = ReadStage(); continue; }

                    if ((st.Cleared || st.DefeatedToday) && nexts < 2)
                    {
                        nexts++;
                        for (int roll = 0; roll < 6 && st != null && st.Next == null && !st.ListAt.IsEmpty; roll++) { game.StagesListScroll(st, -3); game.Wait(500); st = ReadStage(); }
                        if (st != null && st.Next != null)
                        {
                            game.StageNext(st);
                            waits = 0;
                            st = ReadStage();
                            continue;
                        }
                        if (st == null) continue;
                    }
                    stop = st.Cleared || st.DefeatedToday ? "cleared" : "no attack";
                    break;
                }
                waits = 0;

                if (sinceBars >= 5 || stamina <= 1)
                {
                    var h = ReadBarsSettled(15000, x => x.Stamina >= 0 && x.Health >= 0);
                    if (h.Stamina < 0) { Log("Couldn't read stamina - bosses wait a couple of minutes"); game.Snapshot("stamina unreadable at bosses", 60); stop = "bars"; break; }
                    stamina = h.Stamina;
                    sinceBars = 0;
                    if (h.Health >= 0 && !h.HealthAtLeast(LowHealthPct)) { stop = "health"; break; }
                }
                if (stamina <= 0) { stop = "stamina"; break; }
                game.StageAttack(st);
                hits++; stageHits++; stamina--; sinceBars++;
                Count.BossHits++;

                game.Wait(1100);
                st = ReadStage();
                if (st != null && st.Stage == stageAt && (st.DefeatedToday || st.Health == 0))
                {
                    Count.BossesBeaten++;
                    string who = "stage " + stageAt + (stageBoss.Length > 0 ? " " + stageBoss : "");
                    done.Add(who + (stageHits > 1 ? " (" + stageHits + " hits)" : ""));
                    Discord.Post(Ping.Boss, "\U0001F44A Boss beaten", who + " in " + stageHits + (stageHits == 1 ? " hit" : " hits"), Shot, Discord.Gold);
                    stageHits = 0;
                }
            }
            if (done.Count > 0) Log("Bosses: beat " + string.Join(", ", done) + (st != null && st.Tokens >= 0 ? " - " + st.Tokens.ToString("N0", CultureInfo.InvariantCulture) + " boss tokens" : ""));
            int reset = st != null && st.ResetSeconds >= 0 ? st.ResetSeconds : SecondsToUtcMidnight();
            string where = st != null && st.Stage > 0 ? "stage " + st.Stage + (st.Boss.Length > 0 ? " (" + st.Boss + ")" : "") : "the next stage";
            switch (stop)
            {
                case "cleared":

                    SayOnce("bosses cleared " + DateTime.UtcNow.ToString("yyyyMMdd"), "Bosses: every stage you can reach is beaten today (highest " + (st != null ? st.Highest : -1)
                            + ") - back after the game's reset at 00:00 UTC");
                    nextBosses = DateTime.UtcNow.AddSeconds(reset + 60);
                    break;
                case "no attack":

                    SayOnce("bosses noattack " + (st != null ? st.Stage : -1), "Bosses: " + where + " shows no ATTACK" + (st != null && st.NeedLevel > 0 ? " - it needs level " + st.NeedLevel : " (a higher level needed?)")
                            + " - the bot looks again after a level-up" + (st != null && st.Said.Length > 0 ? " (\"" + Cut(st.Said, 160) + "\")" : ""));
                    SnapshotOnce("boss stage without attack");
                    nextBosses = DateTime.UtcNow.AddHours(3);
                    break;
                case "stamina":
                case "health":
                    if (hits == 0) Log("Bosses: " + where + " waits - " + (stop == "stamina" ? "no stamina left" : "health is low"));
                    nextBosses = DateTime.UtcNow.AddMinutes(10);
                    break;
                case "bars": nextBosses = DateTime.UtcNow.AddMinutes(2); break;
                case null: nextBosses = DateTime.UtcNow.AddMinutes(1); break;
                default:
                    Log("Bosses: stopped at " + where + " - " + stop);
                    nextBosses = DateTime.UtcNow.AddMinutes(15);
                    break;
            }
            bool up = stop != "cleared" && stop != "no attack";
            Info("bossname", st != null && st.Stage > 0 ? "Stage " + st.Stage + (st.Boss.Length > 0 ? " - " + st.Boss : "") : "?");
            if (stop == "cleared")
            {
                var t = TimeSpan.FromSeconds(Math.Max(60, reset));
                Info("bosswhen", string.Format("all beaten - again in {0}h {1:00}m", (int)t.TotalHours, t.Minutes));
            }
            else Info("bosswhen", stop == "no attack" ? "needs a higher level" : "ready to fight");
            NoteBossRound(up ? 1 : 0, up ? int.MaxValue : stop == "cleared" ? reset : 3 * 3600);
        }

        BossStage ReadStage()
        {
            BossStage st = null;
            for (int look = 0; look < 3; look++)
            {
                if (look > 0) game.Wait(400);
                using (var f = game.Capture()) st = game.ReadBossStage(f);
                if (st != null && st.Stage > 0) return st;
            }
            return st;
        }

        static int SecondsToUtcMidnight() { var now = DateTime.UtcNow; return (int)(now.Date.AddDays(1) - now).TotalSeconds; }

        static string Cut(string s, int n) { return s.Length <= n ? s : s.Substring(0, n) + "..."; }

        Header ReadBarsSettled(int maxMs) { return ReadBarsSettled(maxMs, h => h.Stamina >= 0); }

        Header ReadBarsSettled(int maxMs, Func<Header, bool> ok)
        {
            int end = Environment.TickCount + maxMs;
            while (true)
            {
                Header h;
                using (var f = game.Capture()) h = game.ReadHeader(f);
                if (ok(h) || end - Environment.TickCount <= 0) return h;
                game.Wait(400);
            }
        }

        string WaitForBossWindow()
        {
            for (int i = 0; i < 12; i++)
            {
                string text;
                using (var f = game.Capture()) text = game.ReadBossWindow(f);
                if (text != null) return text;
                game.Wait(250);
            }
            return null;
        }

        const int LowHealthPct = 10;

        BossOutcome FightBoss(BossCard c)
        {

            var h = ReadBarsSettled(15000, x => x.Stamina >= 0 && x.Health >= 0);
            if (h.Stamina < 0)
            {
                Log("Couldn't read stamina - bosses wait a couple of minutes");
                game.Snapshot("stamina unreadable at bosses", 60);
                return BossOutcome.BarsUnreadable;
            }
            if (h.Stamina == 0) { Log("Out of stamina for bosses"); return BossOutcome.NoStamina; }
            if (h.Health >= 0 && !h.HealthAtLeast(LowHealthPct)) { Log("Health is low - bosses can wait until it regenerates"); return BossOutcome.LowHealth; }
            game.OpenBoss(c);
            string text = WaitForBossWindow();
            if (text == null)
            {

                bool cleared;
                using (var f = game.Capture()) cleared = game.ClearPopup(f);
                if (cleared) { game.OpenBoss(c); text = WaitForBossWindow(); }
            }
            if (text == null) { Log("The boss window for \"" + c.Name + "\" didn't open"); game.Snapshot("boss window missing", 60); return BossOutcome.CantRead; }

            int stamina = h.Stamina;
            int hits = 0, waits = 0;
            bool interrupted = false, eventClosed = false;
            try
            {

                while (hits < 500)
                {
                    using (var f = game.Capture()) text = game.ReadBossWindow(f);

                    for (int again = 0; again < 2 && text == null; again++)
                    {
                        game.Wait(400);
                        using (var f = game.Capture()) text = game.ReadBossWindow(f);
                    }
                    if (text == null) { Log("The boss window closed by itself"); SnapshotOnce("boss window closed"); return BossOutcome.CantRead; }
                    if (Parse.Has(text, "BOSS DEFEATED"))
                    {
                        int at = text.IndexOf("DEFEATED", StringComparison.OrdinalIgnoreCase);
                        string rewards = TidyRewards(at >= 0 ? text.Substring(at + 8).Replace("LEAVE", "") : "");
                        Log(string.Format("Defeated boss \"{0}\" in {1} {2}: {3}", c.Name, hits, hits == 1 ? "hit" : "hits", rewards));
                        Count.BossesBeaten++;
                        Discord.Post(Ping.Boss, "\U0001F44A Boss beaten", c.Name + " in " + hits + (hits == 1 ? " hit" : " hits") + (rewards.Length > 0 ? ": " + rewards : ""), Shot, Discord.Gold);
                        return BossOutcome.Defeated;
                    }
                    int cur, max;
                    var st = System.Text.RegularExpressions.Regex.Match(text, @"([0-9OIl]+)\s*/\s*([0-9OIl]+)\s*STAMINA", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (st.Success && int.TryParse(Parse.Digits(st.Groups[1].Value), out cur)) stamina = Math.Min(stamina, cur);
                    if (stamina <= 0) { Log("Out of stamina while fighting \"" + c.Name + "\""); return BossOutcome.NoStamina; }

                    if (!Game.BossWindowHealth(text, out cur, out max)) using (var f = game.Capture()) game.ReadHealthBar(f, out cur, out max);
                    if (cur >= 0 && max > 0 && cur * 100 < LowHealthPct * max) { Log("Health is low - stopping the boss fight"); return BossOutcome.LowHealth; }
                    if (!game.BossAttackReady)
                    {

                        if (waits == 6 && !eventClosed) using (var f = game.Capture()) if (game.CloseEventPopup(f)) { eventClosed = true; waits = 0; continue; }
                        if (++waits > 12) { Log("Boss window not responding - leaving"); game.Snapshot("boss window stuck", 60); return BossOutcome.CantRead; }
                        game.Wait(350);
                        continue;
                    }
                    waits = 0;
                    game.BossAttack();
                    hits++;
                    stamina--;
                    Count.BossHits++;
                    game.Wait(900);
                }
                Log(string.Format("Hit \"{0}\" {1} times and stopped (safety limit)", c.Name, hits));
                return BossOutcome.HitLimit;
            }
            catch (Exception)
            {

                interrupted = true;
                throw;
            }
            finally
            {
                if (!interrupted && !game.LeaveBossWindow())
                {
                    game.Snapshot("boss window won't close");
                    throw new NeedUserException("The BOSS FIGHT window won't close - press LEAVE yourself, then start the bot again.");
                }
            }
        }

        static string TidyRewards(string t)
        {

            var parts = new List<string>();
            var cash = System.Text.RegularExpressions.Regex.Match(t, @"\+\s*\$\s?[\d.,oO]+\s*[KMBT]?\b");
            if (cash.Success) parts.Add("+" + System.Text.RegularExpressions.Regex.Replace(cash.Value.Substring(1), @"\s", "").Replace('o', '0').Replace('O', '0'));
            var xp = System.Text.RegularExpressions.Regex.Match(t, @"\+\s*([\d,oO]+)\s*XP\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (xp.Success) parts.Add("+" + xp.Groups[1].Value.Replace('o', '0').Replace('O', '0') + " XP");
            var crate = System.Text.RegularExpressions.Regex.Match(t, @"\+\s*([A-Z][A-Z']*(?:\s+[A-Z']+){0,3}\s+(?:CRATE|STASH|STRONGBOX|CHEST|LOCKBOX|DUFFEL|SATCHEL|TRUNK|BOX))\b");
            if (crate.Success) parts.Add("+ " + crate.Groups[1].Value);
            if (parts.Count > 0) return string.Join(" ", parts);
            t = System.Text.RegularExpressions.Regex.Replace(t, @"(^|\s)[oO0]\s*/\s*[\d,]+", " ");
            t = System.Text.RegularExpressions.Regex.Replace(t, @"[$\d][\d,.oO]*", m => m.Value.Replace('o', '0').Replace('O', '0'));
            t = System.Text.RegularExpressions.Regex.Replace(" " + t, @"\s(?:[^\s+]|\w:)(?=\s+[+\-$])", "");
            return System.Text.RegularExpressions.Regex.Replace(t, @"\s{2,}", " ").Trim();
        }

        Rejoiner rejoiner = new Rejoiner();
        int dialogLookAt = Environment.TickCount - 60000;
        bool dialogUp;
        bool disconnectHint;
        bool robloxUsed;
        DateTime robloxUsedAt;
        string dialogSeen;
        uint stepInput;
        IntPtr focusBack;
        DateTime gameInput;

        DateTime GameInput { get { return game.Win.LastInput > gameInput ? game.Win.LastInput : gameInput; } }

        internal static int LookEvery(bool hint, bool tryUnderWay, bool suspect, bool upLastTime, bool rejoining)
        {
            return hint || tryUnderWay ? 0 : suspect || upLastTime || rejoining ? 10000 : 60000;
        }

        bool LookForDisconnect(Frame f, bool suspect, ref Game.Disconnect dialog)
        {
            int every = LookEvery(disconnectHint, rejoiner.Busy, suspect, dialogUp, rejoiner.Active);
            if (Environment.TickCount - dialogLookAt < every) return false;
            disconnectHint = false;
            dialogLookAt = Environment.TickCount;
            dialog = game.ReadDisconnect(f);

            if (dialog == null && !rejoiner.Active) game.CloseEventPopup(f);
            return true;
        }

        DateTime robloxBarSince = DateTime.MinValue, robloxBarSaid = DateTime.MinValue;

        void NoteRobloxBar(bool showing)
        {
            var now = DateTime.UtcNow;
            if (showing)
            {
                if (robloxBarSince != DateTime.MinValue) return;
                robloxBarSince = now;
                if (now - robloxBarSaid < TimeSpan.FromMinutes(30)) return;
                robloxBarSaid = now;
                Log("Roblox's own title bar is over the game's top bar - the bot reads nothing up there (bars, cash, name) until it goes");
                SnapshotOnce("roblox title bar over the top bar");
            }
            else if (robloxBarSince != DateTime.MinValue)
            {
                if (robloxBarSaid >= robloxBarSince && now - robloxBarSince >= TimeSpan.FromMinutes(1))
                    Log("Roblox's title bar is gone after " + (int)Math.Round((now - robloxBarSince).TotalMinutes) + " min - the top bar reads again");
                robloxBarSince = DateTime.MinValue;
                barSweeps = 0;
                nextBarSweep = DateTime.MinValue;
                bankHiddenSaid = false;
            }
        }

        DateTime nextBarSweep = DateTime.MinValue;
        int barSweeps;

        bool SweepRobloxBar()
        {
            var now = DateTime.UtcNow;
            if (!game.RobloxBar || robloxBarSince == DateTime.MinValue || now - robloxBarSince < TimeSpan.FromSeconds(20) || now < nextBarSweep) return false;
            if (S.WaitWhileBusy && game.Win.UserActiveWithin(S.IdleSeconds * 1000)) return false;
            barSweeps++;
            nextBarSweep = now.AddMinutes(barSweeps < 3 ? 2 : 15);
            bool gone = false;
            State("Moving the mouse over Roblox's title bar so it goes away");
            if (!Act(delegate { gone = game.ClearRobloxBar(); })) return true;
            if (gone) Log("Roblox's title bar went away after the bot moved the mouse up to it and back down");
            else if (barSweeps == 1) Log("Roblox's title bar stays after the bot moved the mouse up to it and back down - trying again in 2 minutes");
            else if (barSweeps == 3) Log("Roblox's title bar still stays - the bot tries every 15 minutes. Moving the mouse to the top of the screen and away, "
                                         + "or playing Roblox in a window instead of fullscreen (F11), clears it");
            return true;
        }

        int emptySaid, copiesSaid;
        DateTime emptySaidAt = DateTime.MinValue;

        void NoteEmptyPictures()
        {
            NoteScreenFirst();
            int empty = RobloxWindow.EmptyPictures, copies = RobloxWindow.ScreenCopies;
            int n = (empty - emptySaid) - (copies - copiesSaid);
            if (n <= 0) { emptySaid = empty; copiesSaid = copies; return; }
            if (DateTime.UtcNow - emptySaidAt < TimeSpan.FromMinutes(30)) return;
            emptySaid = empty; copiesSaid = copies; emptySaidAt = DateTime.UtcNow;
            Log("Windows gave the bot an empty picture of Roblox " + (n == 1 ? "once" : n + " times") + " (a graphics problem on this PC) - "
                + "it reads the game only with nothing over Roblox. If it goes on: restart the PC, update the graphics driver");
        }

        readonly object screenFirstLock = new object();

        void NoteScreenFirst()
        {
            if (!RobloxWindow.ScreenFirst || S.ScreenFirst) return;
            lock (screenFirstLock)
            {
                if (S.ScreenFirst) return;
                S.ScreenFirst = true;
                SaveSettings();
            }
            Log("Windows gave the bot an empty picture of Roblox while the screen showed the game. On this PC that can make Roblox "
                + "flash white, so from now on the bot copies Roblox from the screen while nothing is over it");
        }

        bool DisconnectBeforeRound()
        {
            if (Environment.TickCount - dialogLookAt < 5000) return false;
            Game.Disconnect d;
            using (var f = game.Capture()) d = game.ReadDisconnect(f);
            dialogLookAt = Environment.TickCount;
            if (d == null) return false;
            dialogUp = true;
            if (!Offline(d)) { State(rejoiner.Status ?? Game.Disconnected); WatchRoblox(4000); }
            return true;
        }

        void WatchRoblox(int ms)
        {
            int end = Environment.TickCount + ms;
            do
            {
                if (game.Win.IsForeground && game.Win.UserActiveWithin(1000)) { robloxUsed = true; robloxUsedAt = DateTime.UtcNow; }
                Sleep(Math.Max(1, Math.Min(250, end - Environment.TickCount)));
            } while (!stop && end - Environment.TickCount > 0);
        }

        IntPtr readHandle;
        bool readOk;
        bool rejoinIdleSaid;

        void GameReadable()
        {
            var now = DateTime.UtcNow;
            dialogSeen = null;
            robloxUsed = false;
            disconnectSaid = false;
            rejoinIdleSaid = false;
            readHandle = game.Win.Handle;
            readOk = true;
            rejoiner.Readable(now);
            RejoinNote();
            if (focusBack != IntPtr.Zero)
            {
                if (Native.LastInputTick() == stepInput) game.Win.GiveFocusBack(focusBack);
                focusBack = IntPtr.Zero;
            }
            if (game.Win.IsForeground && game.Win.UserActiveWithin(5000)) gameInput = now;
        }

        bool Offline(Game.Disconnect d)
        {
            var now = DateTime.UtcNow;
            Rejoiner.Step step;
            if (d != null)
            {
                string text = d.ToString();
                if (text != dialogSeen)
                {
                    Log((d.Teleport ? "Roblox shows its Teleport Failed window: " : "Roblox disconnected the game: ") + (text.Length > 0 ? text : "(its message didn't read)"));
                    SnapshotOnce((d.Teleport ? "teleport failed " : "disconnected ") + (d.Code >= 0 ? d.Code.ToString() : "no code"));
                    dialogSeen = text;
                }
                step = rejoiner.OnDialog(now, d, S.Rejoin, robloxUsed);

                if (!disconnectSaid && !robloxUsed && !(d.Teleport && (step == Rejoiner.Step.Dismiss || rejoiner.Busy)))
                {
                    Discord.Problem("disconnected", d.Teleport ? "⚠️ Roblox shows its Teleport Failed window" : "⚠️ Roblox disconnected the game", (text.Length > 0 ? text + "\n" : "") + DisconnectPlan(d), Shot, false, 10);
                    disconnectSaid = offlineSaid = true;
                }
            }
            else
            {
                dialogSeen = null;
                step = rejoiner.OnUnreadable(now, S.Rejoin, robloxUsed);
            }
            return TakeRejoinStep(step);
        }

        bool TakeRejoinStep(Rejoiner.Step step)
        {
            RejoinNote();
            if (step == Rejoiner.Step.Wait) return false;
            if (S.WaitWhileBusy && game.Win.UserActiveWithin(S.IdleSeconds * 1000))
            {

                State("Roblox disconnected the game - getting back in once you stop using the mouse/keyboard");
                if (!rejoinIdleSaid) { Log("Roblox disconnected the game - getting back in once you've been idle for " + S.IdleSeconds + " s (you're using the PC)"); rejoinIdleSaid = true; }
                int end = Environment.TickCount + 30000;
                do WatchRoblox(500);
                while (!stop && !robloxUsed && game.Win.UserActiveWithin(S.IdleSeconds * 1000) && end - Environment.TickCount > 0);
                return true;
            }
            var now = DateTime.UtcNow;
            if (step == Rejoiner.Step.Launch)
            {
                IntPtr front = Native.GetForegroundWindow();
                rejoiner.Took(step, now);
                robloxUsed = false;
                try { RobloxWindow.StartGame(); }
                catch (Exception e)
                {

                    Log("Couldn't start Idle Mafia Game: " + e.Message);
                    rejoiner.Failed(now);
                    RejoinNote();
                    Sleep(4000);
                    return true;
                }
                Log("Starting Idle Mafia Game again");
                stepInput = Native.LastInputTick();
                focusBack = S.GiveFocusBack ? front : IntPtr.Zero;
                game.ForgetView();
                State("Starting Idle Mafia Game in Roblox...");
                WatchRoblox(5000);
                return true;
            }
            bool reconnect = step == Rejoiner.Step.Reconnect, dismiss = step == Rejoiner.Step.Dismiss, pressed = false;
            State(dismiss ? "Roblox's Teleport Failed window - pressing Ok" : reconnect ? "Roblox disconnected the game - pressing Reconnect" : "Roblox disconnected the game - pressing Leave");
            Act(delegate { pressed = dismiss ? game.PressTeleportOk() : game.PressDisconnect(reconnect); });
            if (pressed)
            {
                rejoiner.Took(step, now);
                robloxUsed = false;
                stepInput = Native.LastInputTick();
                Log(dismiss ? "Pressed Ok in Roblox's Teleport Failed window (it only closes it)" : reconnect ? "Pressed Reconnect" : "Pressed Leave - the game is started again once Roblox has closed");
            }
            WatchRoblox(3000);
            return true;
        }

        volatile string stopWhy;
        DateTime awaySince = DateTime.MinValue, nextSummary, nextLevelPing;
        string awaySaid;
        volatile bool offlineSaid;
        bool disconnectSaid, awayYours;

        public bool OutOfGame { get { return offlineSaid; } }
        Counters summaryFrom;
        int summaryLevel = -1, levelSaid = -1, levelSeen = -1, levelSaidXp = -1, rerollsSaid;

        void DiscordStart()
        {
            var now = DateTime.UtcNow;
            stopWhy = null;
            awaySince = DateTime.MinValue; awaySaid = null; offlineSaid = disconnectSaid = false;
            nextSummary = now.AddHours(3); summaryFrom = Count.Copy(); summaryLevel = level;
            levelSaid = levelSeen = levelSaidXp = -1; nextLevelPing = now;
            rerollsSaid = Count.Rerolls;
        }

        string DisconnectPlan(Game.Disconnect d)
        {
            if (!rejoiner.Active) return "It was up before the bot started (or in another game), so the bot leaves it to you.";
            if (d.OtherDevice) return "Your account joined the game on another device, so the bot leaves Roblox alone.";
            if (d.Kicked) return "Roblox says you were kicked. The bot doesn't rejoin after a kick, so start the game yourself.";
            if (!S.Rejoin) return "Rejoining is off in the bot's settings, so it waits.";
            if (d.Foreign) return "Roblox's window isn't in English, so the bot can't rejoin by itself. Set Roblox to English.";
            if (d.Teleport) return "The bot closes it with its Ok and plays on.";
            return "The bot gets back in by itself.";
        }

        byte[] Shot()
        {
            using (var f = game.Win.Capture()) return Discord.Jpg(f.Bitmap);
        }

        void Away(string key, string title, string text)
        {
            var now = DateTime.UtcNow;
            if (awaySince == DateTime.MinValue || awaySaid != null && awaySaid != key || awayLogged != null && awayLogged != key)
            {
                awaySince = now; awaySaid = null; awayLogged = null;

                awayYours = key == "closed" && now - robloxUsedAt < TimeSpan.FromMinutes(1);
            }
            if (robloxUsed) return;
            if (awayLogged == null && now - awaySince >= TimeSpan.FromMinutes(2))
            {
                awayLogged = key;
                string since = awaySince.ToLocalTime().ToString("HH:mm");
                Log(key == "closed" ? "Roblox isn't open since " + since + " - " + text : "Can't read the game since " + since + ": " + text);
                if (key != "closed") SnapshotOnce("game unreadable");
            }
            if (awaySaid != null || now - awaySince < TimeSpan.FromMinutes(5)) return;
            awaySaid = key;
            if (awayYours) return;
            offlineSaid = true;
            Discord.Problem("away " + key, title, text, key == "closed" ? null : (Func<byte[]>)Shot, false, 60);
        }

        string awayLogged;
        bool rejoinBackSaid;

        void BackIn()
        {
            if (awayLogged != null && !rejoinBackSaid) Log("The game reads again, after " + Math.Max(1, (int)Math.Round((DateTime.UtcNow - awaySince).TotalMinutes)) + " minutes");
            rejoinBackSaid = false;
            if (awaySaid != null && offlineSaid) Discord.Problem("back", "✅ The bot can read the game again", "It plays on.", Shot, false, 0, Discord.Green);
            if (awaySaid != null) offlineSaid = false;
            awaySaid = awayLogged = null;
            awaySince = DateTime.MinValue;
        }

        void RejoinNote()
        {
            string n = rejoiner.Note;
            rejoiner.Note = null;
            if (n == null) return;
            Log(n);
            if (n.StartsWith("Back in the game"))
            {
                rejoinBackSaid = true;
                if (offlineSaid) Discord.Problem("back", "✅ " + n, "The bot plays on.", Shot, false, 0, Discord.Green);
                offlineSaid = false;
            }
            else if (n.StartsWith("Couldn't get back")) Discord.Problem("rejoin failed", "⚠️ " + n, null, null, false, 30);
        }

        void LevelPing(int lv, int xpNext)
        {
            if (lv != levelSeen) { levelSeen = lv; return; }
            if (levelSaid < 0) { levelSaid = lv; levelSaidXp = xpNext; return; }
            if (lv == levelSaid && levelSaidXp <= 0) levelSaidXp = xpNext;
            if (lv <= levelSaid) return;
            if (lv - levelSaid > 3 && !(xpNext > 0 && levelSaidXp > 0 && xpNext > levelSaidXp)) return;
            if (!Discord.On(Ping.Level)) { levelSaid = lv; levelSaidXp = xpNext; return; }
            if (DateTime.UtcNow < nextLevelPing) return;
            Discord.Post(Ping.Level, "⬆️ Level " + lv, lv - levelSaid == 1 ? "Up from " + levelSaid + "." : "Up " + (lv - levelSaid) + " levels from " + levelSaid + ".", Shot, Discord.Purple);
            levelSaid = lv;
            levelSaidXp = xpNext;
            nextLevelPing = DateTime.UtcNow.AddMinutes(10);
        }

        void PingHenchman(Game.CrewRow got, int replaced, bool perfect)
        {
            if (got == null || got.Rarity < (int)Rarity.Mythic || got.Rarity >= Discord.RarityColors.Length) return;
            if (got.Rarity <= replaced && !perfect) return;
            int took = Count.Rerolls - rerollsSaid;
            rerollsSaid = Count.Rerolls;
            Discord.Post(Ping.Rarity, "\U0001F3B2 " + (perfect ? "Perfect " : "") + got.RarityName + " henchman!",
                got + (took > 1 ? " after " + took + " rerolls" : ""), Shot, Discord.RarityColors[got.Rarity]);
        }

        void DiscordSummary()
        {
            var now = DateTime.UtcNow;
            if (now < nextSummary) return;
            nextSummary = now.AddHours(3);
            var from = summaryFrom;
            int fromLevel = summaryLevel;
            summaryFrom = Count.Copy();
            summaryLevel = level;
            if (from == null || !Discord.On(Ping.Summary)) return;
            var c = Count;
            var lines = new List<string>();
            if (level > 0) lines.Add("Level " + level + (fromLevel > 0 && level > fromLevel ? " (+" + (level - fromLevel) + ")" : ""));
            if (last != null && last.Cash >= 0) lines.Add("Cash " + M(last.Cash) + (last.Banked >= 0 ? ", " + M(last.Banked) + " in the bank" : ""));
            var did = new List<string>();
            Action<int, string, string> add = (n, one, many) => { if (n > 0) did.Add(n + " " + (n == 1 ? one : many)); };
            add(c.Jobs - from.Jobs, "job", "jobs");
            add(c.Helps - from.Helps, "heist help", "heist helps");
            int fights = c.Fights - from.Fights;
            if (fights > 0) did.Add(fights + (fights == 1 ? " fight" : " fights") + " (" + (c.Wins - from.Wins) + " won)");
            add(c.BossesBeaten - from.BossesBeaten, "boss beaten", "bosses beaten");
            add(c.Properties - from.Properties, "property built", "properties built");
            add(c.Hired - from.Hired, "hire", "hires");
            add(c.Rerolls - from.Rerolls, "reroll", "rerolls");
            add(c.Bought - from.Bought, "shop item", "shop items");
            add(c.Crates - from.Crates, "crate", "crates");
            add(c.OpsCollected - from.OpsCollected, "operation collected", "operations collected");
            add(c.Rewards - from.Rewards, "reward", "rewards");
            add(c.StaminaGiven - from.StaminaGiven, "stamina given to the family", "stamina given to the family");
            lines.Add(did.Count > 0 ? "Last 3 hours: " + string.Join(", ", did) + "." : "Nothing done in the last 3 hours.");
            Discord.Post(Ping.Summary, "\U0001F4CA 3-hour update", string.Join("\n", lines), Shot, Discord.Gold);
        }

        public event Action<FamilyInfo> FamilyRead;
        FamilyInfo family = new FamilyInfo();
        DateTime nextTakedown, nextGive, nextFamily;
        DateTime freeGoneAt = DateTime.MinValue;
        bool familyMissingSaid;

        int takedownHealthCost = 25;
        int takedownNoAttack;
        int familyFails;
        int familyShots;
        string familyLogDay;
        readonly HashSet<string> saidOnce = new HashSet<string>();

        DateTime bossesReadAt = DateTime.MinValue, bossBackAt = DateTime.MaxValue;
        bool bossesUp;
        readonly RegenGuess energyRegen = new RegenGuess(), staminaRegen = new RegenGuess();
        int takedownWants;
        int givenSinceFights;
        DateTime giveBrokenUntil = DateTime.MinValue;
        int giveMissed;

        void GiveMissed(string what)
        {
            int wait = ++giveMissed == 1 ? 10 : 60;
            Log(what + " - tries again in " + wait + " minutes");
            game.NoteFailure(what);
            nextGive = giveBrokenUntil = DateTime.UtcNow.AddMinutes(wait);
        }

        void ResetFamily(DateTime now)
        {
            nextTakedown = nextGive = nextFamily = now;
            familyMissingSaid = false;
            familyFails = 0;
            bossesReadAt = DateTime.MinValue;
            bossBackAt = DateTime.MaxValue;
            bossesUp = false;
            takedownWants = givenSinceFights = giveMissed = 0;
            giveBrokenUntil = DateTime.MinValue;
            saidOnce.Clear();
        }

        void SayOnce(string key, string msg) { if (saidOnce.Add(key)) Log(msg); }

        void FamilyChanged() { var h = FamilyRead; if (h != null) h(family.Copy()); }

        void NoteBossRound(int stillUp, int soonest)
        {
            bossesReadAt = DateTime.UtcNow;
            bossesUp = stillUp > 0;
            bossBackAt = soonest < int.MaxValue ? DateTime.UtcNow.AddSeconds(soonest) : DateTime.MaxValue;
        }

        void NoteTicks(Header h)
        {
            if (h == null) return;
            var now = DateTime.UtcNow;
            energyRegen.Note(h.Energy, h.EnergyMax, h.EnergyTick, now);
            staminaRegen.Note(h.Stamina, h.StaminaMax, h.StaminaTick, now);
        }

        int Kept(int max)
        {
            int k = Math.Max(0, S.FightStaminaReserve);
            if (max <= 0 || k <= max / 2) return k;
            SayOnce("kept half " + max, "Your stamina bar holds " + max + ", so the bot keeps half of it (" + max / 2 + ") for bosses instead of " + k);
            return max / 2;
        }

        static int FightSpare(int max, int kept) { return max > 0 ? Math.Max(1, Math.Min(5, max - kept)) : 5; }

        bool FamilyTakes()
        {
            return S.GiveStamina && Can(Tab.Family) && !family.NotInFamily && DateTime.UtcNow >= giveBrokenUntil;
        }

        bool FightsMayUse(Header h)
        {
            if (h == null || h.Stamina < 0) return false;
            int kept = Kept(h.StaminaMax);
            if (!FamilyTakes())
            {
                int need = FightSpare(h.StaminaMax, kept);
                if (h.Stamina - kept >= need) return true;
                SayOnce("fights wait " + kept + "/" + h.StaminaMax, "Fights wait until the stamina is " + need + " above the " + kept + " kept for bosses"
                        + (h.StaminaMax > 0 ? " (your bar holds " + h.StaminaMax + ")" : ""));
                return false;
            }
            return kept > 0 && h.Stamina >= kept && h.StaminaMax > 0 && givenSinceFights >= Math.Max(5, h.StaminaMax - kept);
        }

        bool FamilyOpen(string sub, ref DateTime retry)
        {
            var nav = game.OpenFamily(sub);
            if (nav == FamilyNav.Open)
            {
                familyFails = 0;
                if (family.NotInFamily) { family.NotInFamily = false; familyMissingSaid = false; FamilyChanged(); }
                return true;
            }

            if (nav == FamilyNav.Failed && game.NotInMenu(Tab.Family)) { retry = DateTime.UtcNow.AddMinutes(30); return false; }
            if (nav == FamilyNav.Failed && familyFails++ == 0) familyShots = game.Shots;
            if (nav == FamilyNav.Failed && familyFails >= 5)
            {

                nextTakedown = nextGive = nextFamily = DateTime.UtcNow.AddHours(3);
                familyFails = 0;
                Log("The Family tab didn't open as expected 5 times in a row - the family tasks wait 3 hours" + (game.Shots > familyShots ? " (pictures are in the problems folder)" : ""));
                return false;
            }
            if (nav == FamilyNav.NotInFamily)
            {
                nextTakedown = nextGive = nextFamily = DateTime.UtcNow.AddHours(6);
                if (!familyMissingSaid)
                {
                    familyMissingSaid = true;
                    Log("You're not in a family, so the family tasks (Takedown attacks, stamina perks, the family watch) wait - the bot looks again every 6 hours");
                    SnapshotOnce("family page without a family");
                }
                family.NotInFamily = true;
                FamilyChanged();
                return false;
            }
            retry = DateTime.UtcNow.AddMinutes(30);
            return false;
        }

        void NoteTakedown(TakedownState t)
        {
            if (t == null || !t.Page) return;
            family.Takedown = t;
            family.TakedownAt = DateTime.Now;
            FamilyChanged();
        }

        void NotePerks(List<PerkRow> rows)
        {
            if (rows == null || rows.Count == 0) return;
            family.Perks = rows;
            family.PerksAt = DateTime.Now;
            FamilyChanged();
        }

        static string TakedownBlock(TakedownState t)
        {
            if (t == null || !t.Page) return "The Takedown page isn't showing (or didn't read) - no attacks this time";
            if (t.Free < 0) return "Couldn't read the Takedown's free attacks - not attacking (without free ones ATTACK uses tickets or Robux)";
            if (t.Free == 0) return "";
            if (t.Segments <= 0) return string.Format("The Takedown's free attacks read as {0}/{1}, but their bar shows none - not attacking", t.Free, t.FreeMax);
            if (t.Attack == null && !t.AttackBelow) return "Couldn't find the Takedown's ATTACK button - not attacking";
            return null;
        }

        int takedownEvery = 60;

        void DoTakedown()
        {
            takedownWants = 0;
            nextTakedown = DateTime.UtcNow.AddMinutes(takedownEvery);
            if (!FamilyOpen("TAKEDOWN", ref nextTakedown)) return;
            var t = game.ReadTakedownSteady();
            NoteTakedown(t);
            if (t.Page && t.Free > 0 && freeGoneAt != DateTime.MinValue)
            {
                Log(string.Format("{0} free Takedown attack(s) came back in {1}", t.Free, SpanText((int)(DateTime.UtcNow - freeGoneAt).TotalSeconds)));
                if (t.FreeMax > 0 && t.Free >= t.FreeMax && takedownEvery > 15)
                {

                    takedownEvery = Math.Max(15, takedownEvery / 2);
                    Log("(They were all back, so the bot now looks every " + takedownEvery + " minutes)");
                }
            }
            if (t.Page && t.Free > 0) freeGoneAt = DateTime.MinValue;
            if (t.Page && t.Free >= 0 && t.Segments >= 0 && t.Segments != t.Free && saidOnce.Add("takedown bar"))
            {
                Log(string.Format("(The Takedown's free attacks read {0}/{1} while their bar shows {2} gold - keeping a picture)", t.Free, t.FreeMax, t.Segments));
                SnapshotOnce("takedown count and bar differ");
            }

            var start = t;
            int done = 0, hpBefore = -1;
            bool waitShort = false;
            for (int i = 0; i < 20; i++)
            {
                string why = TakedownBlock(t);
                if (why != null)
                {

                    bool noAttack = t.Page && t.Free > 0 && t.Attack == null && !t.AttackBelow;
                    takedownNoAttack = noAttack ? takedownNoAttack + 1 : 0;
                    if (why.Length > 0) SayOnce("takedown: " + why, why);
                    if (noAttack && takedownNoAttack >= 3 && t.NewSeconds > 0)
                    {
                        nextTakedown = DateTime.UtcNow.AddSeconds(t.NewSeconds + 120);
                        takedownNoAttack = 0;
                        SayOnce("takedown finished", "Takedown: still no ATTACK after three looks - taken as finished, looked at again when the new one starts (in " + SpanText(t.NewSeconds) + ")");
                    }
                    else if (why.Length > 0 && (!noAttack || takedownNoAttack == 1)) game.Snapshot("takedown " + why, 60);
                    break;
                }
                takedownNoAttack = 0;

                var bars = ReadBarsSettled(3000, x => x.Health >= 0 && x.Stamina >= 0);
                int hp = t.Health >= 0 ? t.Health : bars.Health, hpMax = t.HealthMax > 0 ? t.HealthMax : bars.HealthMax;

                if (hpBefore >= 0 && hp >= 0 && hpBefore > hp && hpMax > 0 && (hpBefore - hp) * 10 <= hpMax * 4) takedownHealthCost = hpBefore - hp;
                bool costly = hp >= 0 && hpMax > 0 && (hp - takedownHealthCost) * 100 < LowHealthPct * hpMax;
                if (hp >= 0 && hpMax > 0 && (hp * 100 < LowHealthPct * hpMax || costly))
                {
                    Log("Health is low" + (costly ? " (" + hp + ", an attack takes about " + takedownHealthCost + ")" : "") + " - Takedown attacks wait until it comes back");
                    nextTakedown = DateTime.UtcNow.AddMinutes(20);
                    waitShort = true;
                    break;
                }

                if (bars.Stamina < 0 || bars.Stamina <= Kept(bars.StaminaMax))
                {

                    if (bars.Stamina >= 0) takedownWants = Math.Max(1, t.Free);
                    SayOnce("takedown waits for stamina", "Takedown: " + t.Free + " free attack(s) wait for stamina above the " + Kept(bars.StaminaMax) + " kept for bosses and fights - they go before the stamina gifts");
                    nextTakedown = DateTime.UtcNow.AddMinutes(20);
                    waitShort = true;
                    break;
                }
                hpBefore = hp;
                int free = t.Free;
                if (!game.TakedownAttack(t))
                {

                    const string notFound = "Couldn't find the Takedown's ATTACK button below the window's edge - not attacking";
                    SayOnce("takedown: " + notFound, notFound);
                    game.Snapshot("takedown attack not found below", 60);
                    break;
                }
                done++;
                Count.TakedownAttacks++;
                family.Attacks++;
                if (done == 1) { game.Wait(600); SnapshotOnce("takedown right after an attack"); }
                var after = game.WaitTakedownResult(free, text => { Log("A window came up over the Takedown: " + text); SnapshotOnce("takedown window"); });
                if (!after.Page || after.Free < 0 || after.Free >= free)
                {
                    Log(string.Format("The Takedown's free attacks didn't go down after ATTACK ({0} before, {1} now) - no more attacks this time",
                        free, !after.Page ? "page not read" : after.Free < 0 ? "unreadable" : after.Free.ToString()));
                    game.Snapshot("takedown attack not counted", 60);
                    NoteTakedown(after);
                    t = after;
                    break;
                }

                t = game.ReadTakedownSteady();
                if (t.Page && t.Free > 0 && t.Attack == null && !t.AttackBelow && game.CloseTakedownWindow(text => { Log("A window stayed over the Takedown: " + text); SnapshotOnce("takedown window"); }))
                    t = game.ReadTakedownSteady();
                NoteTakedown(t);
            }
            if (done > 0)
            {

                var parts = new List<string> { string.Format("{0} free attack{1} used", done, done == 1 ? "" : "s") };
                if (t.Page && t.Free >= 0) parts.Add(t.Free == 0 ? "none left" : t.Free + " of " + t.FreeMax + " left");
                if (start.Damage >= 0 && t.Damage > start.Damage)
                    parts.Add(string.Format(CultureInfo.InvariantCulture, "+{0:N0} damage ({1:N0} this week{2})", t.Damage - start.Damage, t.Damage, t.Place.Length > 0 ? ", " + t.Place : ""));
                if (start.Stage > 0 && t.Stage > start.Stage) parts.Add("now at stage " + t.Stage + (t.StageName.Length > 0 ? " (" + t.StageName + ")" : ""));
                if (start.Health >= 0 && t.Health >= 0) parts.Add("health " + start.Health + " -> " + t.Health);
                Log("Takedown: " + string.Join(", ", parts));
                if (t.Page && t.Free == 0) freeGoneAt = DateTime.UtcNow;
            }
            if (!t.Page || waitShort) return;

            var now = DateTime.UtcNow;
            if (t.Free == 0 && t.NextFreeSeconds > 0) nextTakedown = now.AddSeconds(Math.Max(120, Math.Min(3 * 3600, t.NextFreeSeconds + 30)));
            if (t.Free == 0 && t.NextFreeSeconds < 0 && saidOnce.Add("takedown refill text"))
            {
                Log("(No free Takedown attacks left. Under the bar it says \"" + t.Under + "\". Looking again in an hour.)");
                SnapshotOnce("takedown without free attacks");
            }
            if (t.NewSeconds > 0 && now.AddSeconds(t.NewSeconds + 120) < nextTakedown) nextTakedown = now.AddSeconds(t.NewSeconds + 120);
        }

        static string SpanText(int seconds)
        {
            var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
            return t.TotalHours >= 1 ? string.Format("{0}h {1:00}m", (int)t.TotalHours, t.Minutes) : string.Format("{0} min", Math.Max(1, t.Minutes));
        }

        int GiveReserve(int max) { return Kept(max); }

        internal static int GiveAtLeast(int max, int kept) { return Math.Max(5, Math.Min(15, (max - kept - 5) / 5 * 5)); }

        string WhyNotGive(Header h)
        {
            if (h == null || h.Stamina < 0 || h.StaminaMax <= 0) return "stamina unreadable";
            int kept = GiveReserve(h.StaminaMax);
            int least = GiveAtLeast(h.StaminaMax, kept);
            if (h.Stamina - kept < least) return "gives once stamina is " + least + " above the " + kept + " kept for bosses and fights (" + h.Stamina + " now)";
            if (S.Takedown && takedownWants > 0 && Can(Tab.Family)) return "the Takedown's free attacks come first";
            return null;
        }

        string giveWhySaid;

        void NoteGiveWhy(Header h)
        {
            string why = !S.GiveStamina ? "" : family.NotInFamily ? "you're not in a family"
                       : DateTime.UtcNow < giveBrokenUntil ? "giving didn't work, tries again at " + giveBrokenUntil.ToLocalTime().ToString("HH:mm")
                       : WhyNotGive(h) ?? (BossDamageHold() ? "kept for the bosses during 2X boss damage"
                       : DateTime.UtcNow < nextGive ? "gave just now, gives again at " + nextGive.ToLocalTime().ToString("HH:mm") : "gives this round");
            if (why == giveWhySaid) return;
            giveWhySaid = why;
            Info("givewhy", why);
        }

        PerkRow PickPerk(List<PerkRow> rows)
        {
            if (S.StaminaPerk.Trim().Length > 0)
            {
                var r = rows.FirstOrDefault(x => Game.SamePerk(x.Name, S.StaminaPerk));
                if (r == null) SayOnce("perk missing", "The stamina perk \"" + S.StaminaPerk + "\" isn't on the Perks page - no stamina given (pick another on the Stamina page)");
                else if (r.Maxed) { SayOnce("perk maxed", r.Name + " is at its top level - pick another stamina perk on the Stamina page"); r = null; }
                return r;
            }

            var pick = rows.Where(x => x.Left > 0 && !x.Maxed && x.Level >= 0 && x.Name.Length > 0).OrderByDescending(x => x.GiveReady).ThenBy(x => x.Left).FirstOrDefault();
            if (pick == null) SayOnce("perk none", "Couldn't read how far any stamina perk is from its next level - no stamina given");
            return pick;
        }

        static string Progress(PerkRow r)
        {
            return r == null || r.Have < 0 ? "?" : string.Format(CultureInfo.InvariantCulture, "{0:N0} of {1:N0}", r.Have, r.Need);
        }

        void DoGiveStamina()
        {
            try { GiveStaminaHere(); }
            finally
            {

                if (nextGive > DateTime.UtcNow.AddMinutes(20)) giveBrokenUntil = nextGive;
            }
        }

        void GiveStaminaHere()
        {
            nextGive = DateTime.UtcNow.AddMinutes(5);
            var h = ReadBarsSettled(3000);
            bool open = false;
            if (h.Stamina < 0)
            {

                if (!FamilyOpen("PERKS", ref nextGive)) return;
                open = true;
                h = ReadBarsSettled(6000);
                if (h.Stamina < 0)
                {
                    SayOnce("give stamina covered", "Give stamina: the stamina bar stayed covered by notifications - it tries again in 2 minutes");
                    SnapshotOnce("stamina bar covered at give stamina");
                    nextGive = DateTime.UtcNow.AddMinutes(2);
                    return;
                }
            }
            if (WhyNotGive(h) != null) { Neutral(); return; }
            if (!open && !FamilyOpen("PERKS", ref nextGive)) return;
            var rows = game.WalkStaminaPerks();
            if (rows.Count == 0)
            {

                game.Wait(1500);
                if (!FamilyOpen("PERKS", ref nextGive)) return;
                rows = game.WalkStaminaPerks();
            }
            NotePerks(rows);
            if (rows.Count == 0)
            {
                game.Snapshot("stamina perks unreadable", 60);
                GiveMissed("Couldn't read the stamina perks - no stamina given");
                return;
            }
            var pick = PickPerk(rows);
            if (pick == null) { nextGive = DateTime.UtcNow.AddMinutes(60); return; }
            int reserve = GiveReserve(h.StaminaMax), stamina = h.Stamina, given = 0;
            PerkRow row = null;
            while (stamina - 5 >= reserve && given < 500)
            {
                row = (row != null ? game.PerkRowInSight(pick.Name) : null) ?? game.FindPerkRow(pick.Name);
                if (row == null)
                {
                    game.Snapshot("stamina perk button not found", 60);
                    GiveMissed("Couldn't find the GIVE 5 button of " + pick.Name + " - no more stamina given this time");
                    break;
                }
                if (row.Maxed) break;
                long haveBefore = row.Have;
                game.GiveStamina(row);

                PerkRow seen = null;
                bool counted = false;
                for (int look = 0; look < 3 && !counted; look++)
                {
                    if (look > 0) game.Wait(500);
                    var r = game.PerkRowInSight(pick.Name, true);
                    if (r == null) continue;
                    seen = r;
                    counted = haveBefore >= 0 && r.Have >= haveBefore + 4 && r.Have <= haveBefore + 6;

                    if (!counted && ((row.Level > 0 && r.Level > row.Level) || (row.Need > 0 && r.Need > row.Need))) counted = true;
                }
                if (!counted)
                {

                    string toast = game.ReadToast(800, t => Parse.Has(t, "GAVE") && Parse.Has(t, "STAMINA"));
                    counted = toast != null;
                }
                int left = stamina - 5;
                if (!counted)
                {

                    var after = ReadBarsSettled(6000, x => x.Stamina >= 0 && x.Stamina <= stamina - 4);
                    if (after.Stamina < 0)
                    {

                        Log("Gave stamina to " + pick.Name + " but couldn't see the 5 go (the stamina bar was covered) - giving again in 5 minutes");
                        break;
                    }
                    if (after.Stamina > stamina - 4)
                    {
                        game.Snapshot("give stamina not counted", 60);
                        GiveMissed(string.Format("GIVE 5 on {0} didn't take stamina ({1} before, {2} after)", pick.Name, stamina, after.Stamina));
                        break;
                    }
                    left = after.Stamina;
                }
                if (seen != null) row = seen;
                if (given == 0) SnapshotOnce("stamina perk after GIVE 5");
                giveMissed = 0;
                given += 5;
                stamina = left;
                Count.StaminaGiven += 5;
                family.StaminaGiven += 5;
                givenSinceFights += 5;
            }
            if (given == 0) { FamilyChanged(); return; }
            var now = game.PerkRowInSight(pick.Name) ?? row;
            Log(string.Format("Gave {0} stamina to {1} ({2} -> {3}), stamina now {4}/{5}", given, pick.Name, Progress(pick), Progress(now), stamina, h.StaminaMax));
            if (now != null) NotePerks(rows.Select(x => Game.SamePerk(x.Name, pick.Name) ? now : x).ToList());

            if (nextGive < DateTime.UtcNow.AddMinutes(30)) { nextGive = DateTime.UtcNow.AddMinutes(5); giveBrokenUntil = DateTime.MinValue; }
        }

        void DoFamilyWatch()
        {
            nextFamily = DateTime.UtcNow.AddHours(3);
            if (!FamilyOpen("OVERVIEW", ref nextFamily)) return;
            var o = game.ReadFamilyOverviewAll();
            if (o.Level < 0 && o.Name.Length == 0 && o.Xp < 0) { Log("Couldn't read the family's overview"); game.Snapshot("family overview unreadable", 60); }
            else
            {
                var old = family.Overview;
                if (old != null && old.Xp >= 0 && o.Xp >= 0 && old.XpNext == o.XpNext && o.Xp != old.Xp)
                    Log(string.Format(CultureInfo.InvariantCulture, "Family XP {0:+#,0;-#,0} since {1:HH:mm} ({2:N0} of {3:N0})", o.Xp - old.Xp, family.OverviewAt, o.Xp, o.XpNext));
                family.Overview = o;
                family.OverviewAt = DateTime.Now;
                FamilyChanged();
            }

            var stale = DateTime.Now.AddHours(-6);
            DateTime ignore = DateTime.MinValue;
            if (family.TakedownAt < stale && FamilyOpen("TAKEDOWN", ref ignore)) NoteTakedown(game.ReadTakedownSteady());
            if (family.PerksAt < stale && FamilyOpen("PERKS", ref ignore)) NotePerks(game.WalkStaminaPerks());
            string day = DateTime.Now.ToString("yyyy-MM-dd");
            if (familyLogDay != day) { familyLogDay = day; Log(FamilyLine()); }
        }

        string FamilyLine()
        {
            var ic = CultureInfo.InvariantCulture;
            var parts = new List<string>();
            var o = family.Overview;
            if (o != null)
            {

                var p = new List<string> { (o.Name.Length > 0 ? o.Name : "your family") + (o.Tag.Length > 0 ? " [" + o.Tag + "]" : "") };
                if (o.Level > 0) p.Add("level " + o.Level);
                if (o.Xp >= 0) p.Add(string.Format(ic, "family XP {0:N0} of {1:N0}", o.Xp, o.XpNext));
                if (o.Members >= 0) p.Add(o.Members + " of " + o.MembersMax + " members");
                if (o.Respect >= 0) p.Add(o.Respect.ToString("N0", ic) + " respect");
                if (o.PerkLevels >= 0) p.Add(o.PerkLevels + " of " + o.PerkLevelsMax + " perk levels");
                if (o.Territories >= 0) p.Add(o.Territories + " of " + o.TerritoriesMax + " territories");
                if (o.Vault >= 0) p.Add("vault " + CashText(o.Vault) + (o.Gold >= 0 ? " and " + o.Gold + " gold bars" : ""));
                parts.Add(string.Join(", ", p));
            }
            var t = family.Takedown;
            if (t != null)
            {
                var p = new List<string> { "Takedown" + (t.Name.Length > 0 ? " " + t.Name : "") };
                if (t.Damage >= 0) p.Add(string.Format(ic, "your damage this week {0:N0}{1}", t.Damage, t.Place.Length > 0 ? " (" + t.Place + ")" : ""));
                if (t.Free >= 0) p.Add(t.Free + " of " + t.FreeMax + " free attacks");
                if (t.Stage > 0) p.Add(string.Format(ic, "stage {0}{1}", t.Stage, t.StageHpMax > 0 ? string.Format(ic, " ({0:N0} of {1:N0} health left)", t.StageHp, t.StageHpMax) : ""));
                parts.Add(string.Join(", ", p));
            }
            if (family.Perks != null && family.Perks.Count > 0)
                parts.Add("stamina perks: " + string.Join(", ", family.Perks.Select(r => r.Name + (r.Level > 0 ? " " + r.Level : "") + (r.Have >= 0 ? " (" + Progress(r) + ")" : ""))));
            if (family.Attacks > 0 || family.StaminaGiven > 0) parts.Add(string.Format("this session: {0} Takedown attacks, {1} stamina given", family.Attacks, family.StaminaGiven));
            return "Family: " + (parts.Count > 0 ? string.Join(" | ", parts) : "nothing read yet");
        }

        static string CashText(double v)
        {
            var ic = CultureInfo.InvariantCulture;
            if (v >= 1e12) return "$" + (v / 1e12).ToString("0.##", ic) + "T";
            if (v >= 1e9) return "$" + (v / 1e9).ToString("0.##", ic) + "B";
            if (v >= 1e6) return "$" + (v / 1e6).ToString("0.##", ic) + "M";
            return "$" + v.ToString("N0", ic);
        }

        void ScanFamily()
        {
            DateTime ignore = DateTime.MinValue;
            if (!FamilyOpen("OVERVIEW", ref ignore)) { if (family.NotInFamily) Log("Family: you're not in a family"); return; }
            family.Overview = game.ReadFamilyOverviewAll();
            family.OverviewAt = DateTime.Now;
            FamilyChanged();
            if (FamilyOpen("TAKEDOWN", ref ignore))
            {
                var t = game.ReadTakedownSteady();
                NoteTakedown(t);
                string why = TakedownBlock(t);
                Log(string.Format("  Takedown: free attacks {0} of {1} (bar {2} gold, under it \"{3}\"), tickets {4}, health {5} of {6} - {7}", t.Free, t.FreeMax, t.Segments, t.Under,
                    t.Tickets, t.Health, t.HealthMax, why == null ? (S.Takedown ? "would attack" : "would attack, but that's switched off") : why.Length == 0 ? "none left" : why));
            }
            if (FamilyOpen("PERKS", ref ignore))
            {
                var rows = game.WalkStaminaPerks();
                NotePerks(rows);
                foreach (var r in rows)
                    Log(string.Format("  stamina perk {0}: level {1} of {2}, {3}{4}", r.Name, r.Level, r.LevelMax, Progress(r), r.Left > 0 ? ", " + r.Left + " to go" : ""));
                var pick = rows.Count > 0 ? PickPerk(rows) : null;
                string why = WhyNotGive(last);
                Log("  spare stamina: " + (pick == null ? "no perk to give to" : "would go to " + pick.Name) + " - " + (!S.GiveStamina ? "switched off" : why == null ? "would give now" : "not now: " + why));
            }
            Log(FamilyLine());
        }

        public event Action<Profile> ProfileRead;

        public event Action<Profile> ProfileChanged;

        void CheckGame()
        {
            Log("--- Checking your game: opening its tabs and reading them (it only opens tabs and rolls lists) ---");
            State("Checking your game - about a minute");
            var p = new Profile { Checked = DateTime.Now, Version = Program.Version };
            var h = ReadBarsSettled(4000, x => x.Level > 0 && !string.IsNullOrWhiteSpace(x.Name) && x.Stamina >= 0);
            p.Name = (h.Name ?? "").Trim();

            if (S.Account.Length > 0 && (p.Name.Length == 0 || Accounts.Same(p.Name, S.Account))) p.Name = S.Account;
            p.Level = h.Level > 0 ? h.Level : level;
            p.Xp = h.Xp; p.XpNext = h.XpNext; p.SkillPoints = h.SkillPoints;
            p.EnergyMax = h.EnergyMax; p.StaminaMax = h.StaminaMax; p.HealthMax = h.HealthMax;
            p.Cash = h.Cash; p.Banked = h.Banked;
            using (var f = game.Capture()) p.Window = f.Width + "x" + f.Height;
            if (p.Name.Length == 0) p.Note("your name (the top bar)");
            game.OnlyNavigate = true;
            try
            {
                CheckPart(p, "the game's menu", () =>
                {
                    var m = game.ReadWholeMenu();
                    if (m == null) { p.Note("the game's menu"); return; }
                    p.Tabs = string.Join(",", m.Seen);
                    p.MenuWhole = m.Whole && m.Unread == 0 ? 1 : 0;
                    if (m.Unread > 0) p.Note(m.Unread + " entries of the game's menu");
                });
                CheckPart(p, "the Safehouse", () =>
                {
                    var s = game.ReadSafehouse();
                    if (s == null || s.Found == 0) { p.Note("the Safehouse"); return; }
                    p.SafeLevel = s.Level; p.SafeMax = s.MaxLevel; p.SafeBonus = s.Bonus;
                    p.Respect = s.Respect; p.AttackPower = s.AttackPower; p.DefensePower = s.DefensePower; p.Income = s.Income;
                    p.Hired = s.Hired; p.HireMax = s.HireMax; p.Gear = s.Gear; p.GearMax = s.GearMax; p.Capacity = s.Capacity;
                    if (s.Family != null) { p.Family = s.Family; p.InFamily = 1; }
                    p.Rank = Profile.NormalRank(s.Rank);
                    if (s.Members >= 0) { p.Members = s.Members; p.MembersMax = s.MembersMax; }
                    var sh = SafehouseRead;
                    if (sh != null) sh(s);
                });
                if (Open(p, Tab.Bank, "the Bank"))
                    CheckPart(p, "the Bank's fee", () =>
                    {
                        string line;
                        using (var f = game.Capture()) p.BankFee = game.ReadBankFee(f, out line);
                        p.BankLine = line;
                        if (p.BankFee >= 0) p.BankFeeFrom = "the Bank page";
                    });
                if (Open(p, Tab.Operations, "Operations"))
                    CheckPart(p, "Operations", () =>
                    {
                        List<OpSlot> slots;
                        using (var f = game.Capture()) { slots = game.ReadOpSlots(f); p.OfflineOpsOffer = game.OffersOfflineOps(f) ? 1 : 0; }
                        p.OpsSlots = slots.Count;
                        p.OpsOpen = slots.Count(s => !s.Locked);
                        p.OpsPass = slots.Count(s => s.Pass);
                        p.OpsRunning = slots.Count(s => s.Running || s.Done || s.Collect != null);
                        var ops = OpsRead;
                        if (ops != null) ops(slots);
                    });
                if (Open(p, Tab.Jobs, "Jobs"))
                    CheckPart(p, "the Jobs list", () =>
                    {
                        var j = game.ReadJobsList(Catalog);
                        p.CitiesSeen = string.Join(",", j.Cities.Select(i => GameData.Cities[i]));
                        p.CitiesFolded = string.Join(",", j.Folded.Select(i => GameData.Cities[i]));
                        p.JobCash = j.Cash;
                    });
                if (Open(p, Tab.Crew, "the Crew"))
                    CheckPart(p, "the Crew", () =>
                    {
                        Game.CrewTotals c;
                        using (var f = game.Capture()) c = game.ReadCrewTotals(f);
                        p.Henchmen = c.Henchmen; p.HenchSlots = c.Slots; p.CrewAttack = c.Attack; p.CrewDefense = c.Defense;
                        if (p.Henchmen < 0 && p.Hired >= 0) { p.Henchmen = p.Hired; p.HenchSlots = p.HireMax; }
                    });
                if (p.Lacks(Tab.Family)) { if (p.InFamily < 0) p.InFamily = 0; }
                else
                    CheckPart(p, "your family", () =>
                    {
                        var nav = game.OpenFamily("OVERVIEW");
                        if (nav == FamilyNav.NotInFamily || (nav == FamilyNav.Failed && game.NotInMenu(Tab.Family) && p.InFamily != 1))
                        {
                            p.InFamily = 0; p.Family = ""; p.Rank = "";
                            return;
                        }
                        if (nav != FamilyNav.Open) { p.Note("your family"); return; }
                        var o = game.ReadFamilyOverviewAll();
                        p.InFamily = 1;
                        if (o.Name.Length > 0) p.Family = o.Name + (o.Tag.Length > 0 ? " [" + o.Tag + "]" : "");
                        p.FamilyLevel = o.Level;
                        if (o.Members >= 0) { p.Members = o.Members; p.MembersMax = o.MembersMax; }
                        family.Overview = o;
                        family.OverviewAt = DateTime.Now;
                        FamilyChanged();
                    });
                if (Open(p, Tab.Bosses, "Bosses"))
                    CheckPart(p, "the Bosses list", () =>
                    {
                        var b = game.ReadBossList(p.Level);
                        p.Bosses = b.Total; p.BossesOpen = b.Open; p.BossesReady = b.Ready; p.BossLowest = b.Lowest; p.BossNext = b.Next; p.BossesUnsure = b.Unsure;
                    });
                if (Open(p, Tab.Heists, "Heists"))
                    CheckPart(p, "Heists", () =>
                    {
                        game.HeistsTop();
                        using (var f = game.Capture()) { p.Favors = game.ReadFavors(f); p.FamilyHeists = game.ReadHeists(f).Count(r => r.YourFamily); }
                        if (p.Favors >= 0) Info("favors", p.Favors.ToString());
                    });
            }
            finally { game.OnlyNavigate = false; }

            string file = Profile.FileFor(dir, p.Name);
            try { p.Save(file); }
            catch (Exception e) { Log("Couldn't save what the check found: " + e.Message); }
            feeNotedFor = null;
            foreach (var row in Setup.Found(p)) Log("Check - " + row[0] + ": " + row[1]);
            Log("--- Check finished (saved in " + System.IO.Path.GetFileName(file) + "). The window shows what it suggests. Nothing changes until you say so ---");
            var ev = ProfileRead;
            if (ev != null) ev(p);
        }

        bool Open(Profile p, Tab t, string what)
        {
            if (p.Lacks(t)) return false;
            bool ok = false;
            CheckPart(p, what, () => { ok = game.OpenTab(t); if (!ok && !game.NotInMenu(t)) p.Note(what + " (the tab didn't open)"); });
            return ok;
        }

        void CheckPart(Profile p, string what, Action read)
        {
            try { read(); }
            catch (StopException) { throw; }
            catch (UserBusyException) { throw; }
            catch (WindowChangedException) { throw; }
            catch (NeedUserException) { throw; }
            catch (Exception e)
            {
                p.Note(what + " (" + e.Message + ")");

                if (MemoryShort(e)) { Log("Checking your game: the PC ran out of memory (" + what + ")"); throw new OutOfMemoryPause(e); }
                SnapshotOnce("check " + what);
            }
        }

        void Scan()
        {
            Log("--- Scan: reading every screen, no actions ---");
            if (last != null)
                Log(string.Format("Level {0}, XP {1}/{2}, energy {3}/{4}, stamina {5}/{6}, health {7}/{8}, skill points {9}",
                    last.Level, last.Xp, last.XpNext, last.Energy, last.EnergyMax, last.Stamina, last.StaminaMax, last.Health, last.HealthMax, last.SkillPoints));
            ReportTarget();
            var job = PickJob(last);
            if (job != null) Log("Job it would do now: " + job.Name + " (" + job.Cost + " energy)");

            if (game.OpenTab(Tab.Heists))
            {
                game.HeistsTop();
                using (var f = game.Capture())
                {
                    Log("Heist favors left: " + game.ReadFavors(f));
                    foreach (var r in game.ReadHeists(f))
                        Log(string.Format("  heist \"{0}\": {1}, +{2} XP per help{3}{4}", r.Name, r.HelpText, r.XpPerHelp, r.YourFamily ? " (your family)" : "",
                            r.CanHelp && S.WouldHelp(r.YourFamily, r.XpPerHelp, JobXpFor5()) ? "  <- would help" : ""));
                }
            }
            if (game.OpenTab(Tab.Fight))
                using (var f = game.Capture())
                {
                    bool war;
                    var rows = game.ReadFightRows(f, out war);
                    Log("Fight list" + (war ? " (at war)" : "") + ": " + string.Join("  ", rows.Select(r => string.Format("#{0} {1} steal {2}% {3} stam{4}", r.Index + 1,
                        r.War ? "war target" : "lvl " + r.Level, r.Steal, r.StaminaCost, r.Ready ? "" : " (not ready)"))));
                }
            if (game.OpenTab(Tab.Operations))
                using (var f = game.Capture())
                {
                    var slotsSeen = game.ReadOpSlots(f);
                    var ops = OpsRead;
                    if (ops != null) ops(slotsSeen);
                    foreach (var s in slotsSeen)
                        Log(string.Format("  operation slot {0}: {1}", s.Index + 1, s.Locked ? "locked" : s.Collect != null || s.Done ? "READY to collect: " + s.Name
                            : s.Running ? s.Name + ", " + (s.SecondsLeft >= 0 ? TimeSpan.FromSeconds(s.SecondsLeft).ToString() + " left" : "running") : "free"));
                }
            if (game.OpenTab(Tab.Bosses))
            {
                game.BossesTop();
                string lastSig = null;
                for (int page = 0; page < 12; page++)
                {
                    List<BossCard> cards;
                    using (var f = game.Capture()) cards = game.ReadBosses(f);
                    foreach (var c in cards.Where(c => level <= 0 || c.Level <= level))
                        Log(string.Format("  boss \"{0}\" (level {1}): {2}", c.Name, c.Level, c.RespawnSeconds >= 0 ? "respawns in " + TimeSpan.FromSeconds(c.RespawnSeconds) : c.ButtonText));
                    string sig = string.Join(",", cards.Select(c => c.Name + c.Level));
                    if (sig == lastSig) break;
                    lastSig = sig;
                    game.BossesDown();
                }
            }
            ScanFamily();
            if (game.OpenTab(Tab.Crew))
                using (var f = game.Capture())
                {
                    System.Drawing.Rectangle box;
                    string why;
                    var auto = game.FindAutoEquip(f, out box, out why);
                    Log("Crew: " + game.ReadCrewTotals(f) + "; AUTO EQUIP BEST " + (auto != null ? "found at " + (box.X + box.Width / 2) + "," + (box.Y + box.Height / 2) : "not pressable: " + why));
                }
            var info = game.ReadSafehouse();
            if (info != null)
            {
                foreach (var line in info.Lines()) Log(line);
                var h = SafehouseRead;
                if (h != null && info.Found > 0) h(info);
            }
            Log("--- Scan finished ---");
        }
    }
}
