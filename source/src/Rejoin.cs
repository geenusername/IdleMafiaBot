using System;

namespace IdleMafiaBot
{

    sealed class Rejoiner
    {

        public enum Step { Wait, Reconnect, Leave, Launch, Dismiss }

        enum Phase { None, Pending, Reconnected, Left, Launched, Retry, HandsOff, Dismissed }

        public static readonly TimeSpan OursWithin = TimeSpan.FromMinutes(10);
        public static readonly TimeSpan Settle = TimeSpan.FromSeconds(15);
        public static readonly TimeSpan ReconnectWait = TimeSpan.FromMinutes(3);
        public static readonly TimeSpan LeaveWait = TimeSpan.FromSeconds(20);
        public static readonly TimeSpan LaunchWait = TimeSpan.FromMinutes(4);
        public static readonly TimeSpan Pause = TimeSpan.FromMinutes(30);
        static readonly int[] BackoffMinutes = { 1, 3, 10 };
        public const int MaxTries = 4, ReconnectTries = 2;

        public const int KeepAliveMinutes = 15;

        Phase phase;
        DateTime at, next = DateTime.MinValue, since = DateTime.MinValue, lastGood = DateTime.MinValue;
        int tries;
        bool windowUp;
        bool mayRelaunch;
        string held, offStatus;

        public string Note;

        public string Status { get; private set; }

        public bool Busy { get { return phase == Phase.Reconnected || phase == Phase.Left || phase == Phase.Launched || phase == Phase.Dismissed; } }

        public bool Active { get { return phase != Phase.None; } }

        public static bool KeepAliveDue(DateTime now, DateTime lastInput)
        {
            return now - lastInput >= TimeSpan.FromMinutes(KeepAliveMinutes);
        }

        public void Playing(DateTime now)
        {
            if (phase == Phase.None) lastGood = now;
        }

        public void Readable(DateTime now)
        {
            windowUp = true;
            lastGood = now;
            mayRelaunch = true;
            Status = null;
            held = null;
            if (phase == Phase.None) return;
            if (phase == Phase.Dismissed) Note = "Roblox's window is closed - the game plays on";
            else if (phase != Phase.HandsOff)
                Note = "Back in the game" + (since != DateTime.MinValue ? " after " + Minutes(now - since) : "");
            Reset();
        }

        public Step OnDialog(DateTime now, Game.Disconnect d, bool rejoinOn, bool robloxUsed = false)
        {
            windowUp = true;
            if (phase == Phase.HandsOff) { Status = offStatus; return Step.Wait; }
            if (robloxUsed) return Yours();
            if (phase == Phase.None)
            {
                if (now - lastGood > OursWithin)
                    return d.Teleport ? Hold("Roblox shows its Teleport Failed window - press Ok in Roblox",
                                             "Roblox's Teleport Failed window wasn't the bot's (up before it started, or in another game) - leaving it to you")
                                      : Hold("Roblox shows its Disconnected window - press Reconnect or Leave in Roblox",
                                             "The Disconnected window wasn't the bot's (up before it started, or in another game) - leaving it to you");
                phase = Phase.Pending;
            }
            if (d.OtherDevice) return Off("Your account joined the game on another device - leaving Roblox alone",
                                          "Not rejoining: your account joined the game on another device");
            if (d.Kicked) return Off("Roblox says you were kicked - press Leave, then start the game yourself",
                                     "Not rejoining after a kick - leaving it to you");
            if (SwitchedOff(rejoinOn)) return Hold("Roblox disconnected the game - rejoining is switched off (Settings)", "Rejoining is switched off - leaving it to you");

            if (d.Foreign) return Hold("Roblox shows a window the bot can't read - set Roblox's language to English so it can rejoin by itself",
                                       "Roblox shows a window the bot can't read (not in English) - set Roblox to English so it can rejoin by itself");
            if (since == DateTime.MinValue) since = now;
            if (phase == Phase.Reconnected || phase == Phase.Left || phase == Phase.Launched || phase == Phase.Dismissed)
            {
                if (now - at < Settle) return Hold(phase == Phase.Dismissed ? "Closing Roblox's Teleport Failed window..." : "Roblox disconnected the game - getting back in...", null);
                Failed(now);
            }
            if (now < next) return Hold((d.Teleport ? "Roblox's Teleport Failed window - " : "Roblox disconnected the game - ") + NextTry(), null);
            held = null;

            if (d.Teleport && d.Ok != null) return Step.Dismiss;
            if (d.Reconnect != null && tries < ReconnectTries) return Step.Reconnect;
            if (d.Leave != null) return Step.Leave;
            if (d.Reconnect != null) return Step.Reconnect;
            return Hold("Roblox disconnected the game - can't read its Reconnect or Leave button", null);
        }

        public Step OnUnreadable(DateTime now, bool rejoinOn, bool robloxUsed = false)
        {
            windowUp = true;
            if (robloxUsed && phase != Phase.None && phase != Phase.HandsOff) Yours();
            if (phase == Phase.None || phase == Phase.Pending) { Status = null; return Step.Wait; }
            if (phase == Phase.HandsOff) return Step.Wait;
            if (SwitchedOff(rejoinOn)) return Step.Wait;
            switch (phase)
            {
                case Phase.Reconnected:
                case Phase.Dismissed:
                    if (now - at < ReconnectWait) return Hold("Roblox disconnected the game - reconnecting...", null);
                    Failed(now);
                    break;
                case Phase.Left:

                    if (now - at < LeaveWait) return Hold("Roblox disconnected the game - leaving it, to start it again...", null);
                    return Step.Launch;
                case Phase.Launched:
                    if (now - at < LaunchWait) return Hold("Starting Idle Mafia Game in Roblox...", null);
                    Failed(now);
                    break;
            }
            if (now < next) return Hold("Roblox disconnected the game - " + NextTry(), null);
            return Step.Launch;
        }

        public Step OnClosed(DateTime now, bool userTouched, bool rejoinOn, bool robloxUsed = false)
        {
            bool wasUp = windowUp;
            windowUp = false;
            if (phase == Phase.None)
            {
                Status = null;

                if (rejoinOn && mayRelaunch && wasUp && !userTouched && !robloxUsed && now - lastGood <= OursWithin)
                {
                    phase = Phase.Retry;
                    since = now;
                    next = now;
                    Note = "Roblox closed by itself - starting the game again";
                    return Step.Launch;
                }
                return Step.Wait;
            }

            if (phase == Phase.Pending || phase == Phase.HandsOff) { Reset(); mayRelaunch = false; return Step.Wait; }
            if (robloxUsed) { Yours(); Status = null; return Step.Wait; }
            if (SwitchedOff(rejoinOn)) return Step.Wait;

            if (wasUp && userTouched && phase != Phase.Launched && (phase != Phase.Left || now - at > LeaveWait))
            {
                Reset();
                mayRelaunch = false;
                Note = "Roblox was closed while you were at the PC - the bot won't start it again";
                return Step.Wait;
            }
            switch (phase)
            {
                case Phase.Left:
                    return Step.Launch;
                case Phase.Reconnected:
                case Phase.Dismissed:
                    Failed(now);
                    break;
                case Phase.Launched:
                    if (now - at < LaunchWait) return Hold("Starting Idle Mafia Game in Roblox...", null);
                    Failed(now);
                    break;
            }
            if (now < next) return Hold("Roblox disconnected the game - " + NextTry(), null);
            return Step.Launch;
        }

        public void Took(Step step, DateTime now)
        {
            if (step == Step.Wait) return;
            if (step != Step.Launch || phase != Phase.Left) tries++;
            phase = step == Step.Reconnect ? Phase.Reconnected : step == Step.Leave ? Phase.Left : step == Step.Dismiss ? Phase.Dismissed : Phase.Launched;
            at = now;
            held = null;
            if (since == DateTime.MinValue) since = now;
        }

        public void Failed(DateTime now)
        {
            if (tries >= MaxTries)
            {
                Note = "Couldn't get back into the game after " + tries + " tries - trying again in " + (int)Pause.TotalMinutes + " minutes";
                tries = 0;
                next = now + Pause;
            }
            else
            {
                next = now + TimeSpan.FromMinutes(BackoffMinutes[Math.Max(0, Math.Min(BackoffMinutes.Length - 1, tries - 1))]);
                Note = (phase == Phase.Dismissed ? "Roblox's Teleport Failed window is still up - " : "Not back in the game yet - ") + NextTry();
            }
            phase = Phase.Retry;
        }

        void Reset()
        {
            phase = Phase.None;
            tries = 0;
            next = since = DateTime.MinValue;
            Status = null;
            held = null;
        }

        Step Yours()
        {
            mayRelaunch = false;
            if (phase != Phase.None && phase != Phase.HandsOff) Reset();
            return Hold("Roblox disconnected the game - you're using Roblox, so it's left to you", "You're using Roblox - leaving it to you");
        }

        Step Hold(string status, string note)
        {
            Status = status;
            if (note != null && held != note) Note = note;
            held = note ?? held;
            return Step.Wait;
        }

        Step Off(string status, string note)
        {
            Reset();
            mayRelaunch = false;
            phase = Phase.HandsOff;
            Status = offStatus = status;
            Note = note;
            return Step.Wait;
        }

        bool SwitchedOff(bool rejoinOn)
        {
            if (rejoinOn) return false;
            if (phase != Phase.None && phase != Phase.Pending && phase != Phase.HandsOff)
            {
                Reset();
                phase = Phase.Pending;
                Note = "Rejoining was switched off - leaving Roblox as it is";
            }
            return true;
        }

        string NextTry() { return "next try at " + next.ToLocalTime().ToString("HH:mm"); }

        static string Minutes(TimeSpan t) { return t.TotalMinutes < 1.5 ? "a minute" : (int)Math.Round(t.TotalMinutes) + " minutes"; }
    }
}
