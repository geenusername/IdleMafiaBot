# Bug registry

Working branch: `fix/bugs`. Everything gets fixed and committed here.

## States

- `new` - reported, not reviewed
- `reproducing` - confirming the symptom
- `fixing` - fix in progress
- `verifying` - fix applied, checking it
- `done` - solved, clean build, verified
- `dropped` - not a bug / can't reproduce

## Table

| ID | Module | Symptom | State | Commit |
|----|--------|---------|-------|--------|
| #1 | Fights | Only fighting: after analyzing the player, it can't find the profile's X and gets stuck (fights wait 5 min) | done | this commit |
| #2 | Fights | Fights aren't guaranteed enough: it attacked without scouting (ScoutFights default off) and only asked for 85% (about 20% more attack) before the analysis of the fight rolls proved 90% needs a 1.25x attack | done | this commit |
| #3 | Remote | `/remote start` from Discord starts the macro even with Roblox closed; it should say Roblox isn't open and not start | verifying | |
| #4 | Fights | The fight result's reward lines read corrupted ("VICTORY (-ucife ealth -21 Q evect)" instead of "+21 Respect"), so the fight is remembered with 0 respect even though the game paid +15/+19 | fixing | |
| #5 | Fights | After SCOUT the profile's numbers reload and the 15 looks (~5 s) gave up too soon, so a scouted player was wrongly "unreadable" | verifying | |
| #6 | Takedown/Bank/Give stamina | The game's notification toasts cover the top bars: "stamina unreadable (3 tries)" stuck the Takedown for 30+ min, give stamina waited, and a withdraw for $10.2B fell back to WITHDRAW ALL that took the whole $3.42T out | fixing | |
| #7 | Bosses | Twice in a row it read "every stage beaten today (highest 2)" when the highest was 28 (a fresh list misread) - bosses skipped for two rounds | new | |
| #8 | Crew | One look at a half-loaded crew page reading the AUTO buttons greyed turned Auto Roll off "until the bot is started again", though the pass was on (the game's own auto roll landed a Secret) | verifying | |

### #1 - Doesn't close the player's profile when fighting (only-fight mode)
- Module: Fights (player profile)
- File(s): `src/Game.cs` - `ReadPlayerProfile`, `CloseProfile`, `SkillWindowX`
- Symptom: in "only fighting", after opening and reading the rival's profile (e.g. PLAYER PROFILE: QEMQEP), the bot can't find the X to close the window; `CloseProfile` retries, returns false, and fights get parked for 5 minutes.
- Steps to reproduce: turn on "Scout players first" / only fighting for respect; let the bot open a rival's profile that shows the SCOUT (1 STAMINA) button; the bot never clicks the X and gets stuck.
- Evidence: QEMQEP profile screenshot (grey X at the top right, orange gift icon at its left).
- Cause: the X isn't read as text (`^[xX×]$` doesn't match) and the fallbacks fail: `SkillWindowX` cuts its search at the first gold pixel to the right of the title (the gift icon is gold and sits before the X), and `CloseProfile`'s blind retry clicks just `4*h` pixels from the title, which lands in the middle of the window (the dialog is wide and the X sits far away).
- Fix: look for the grey cross itself (grouping grey pixels into blobs to the right of the title, ignoring the gold) as the last fallback of `ReadPlayerProfile`.
- Verification: done with the built 1.8.1 (log 2026-10-19 00:22-00:26): 5 players scouted, 4 fights won, no "profile stayed open" message.
- Commit: this one.

### #2 - Guarantee at least a 90% chance to win every fight
- Module: Fights
- File(s): `src/Settings.cs`, `src/ui/MainWindow.xaml`
- Symptom: without "Scout players first" it attacked unknown players, and the default odds were 85% (about 20% more attack than their defense). Defeats cost about as much respect as a win gains, so anything below 90% eats into the respect.
- Evidence: fight windows (5 points) confirmed the game's formula: every side rolls uniform x0.8 to x1.2 of its stat, higher roll wins; solving the overlap gives 90% at exactly a 1.25x attack, 100% at 1.5x.
- Fix: `ScoutFights` and `FightWinChance` now default to on and 90, and the settings hint says 90 is about 25% more attack while 100 only attacks players whose best roll can't beat the worst one.
- Verification: done (user ran the bot with the new defaults: it scouted first and only attacked at the 90% odds).
- Commit: this one.

### #3 - Remote start works with Roblox closed
- Module: Remote (Discord control)
- File(s): `src/UiWindow.cs` - `RemoteDo`, `src/Bot.cs`
- Symptom: `start` from Discord answers "Started. It plays now." even when Roblox isn't open; the macro runs blind (and with Rejoin on it tries to launch Roblox by itself). The user expects a clear refusal.
- Steps to reproduce: close Roblox, leave the bot's window open, send `/remote start` in the Discord channel.
- Cause: the remote `start` only checks `bot.Running` and the window being covered, never whether the Roblox window is there.
- Fix: new `Bot.RobloxOpen()` (`game.Win.Find()`); the remote `start` refuses with "Roblox isn't open..." on Discord, and `status` says "Stopped (Roblox isn't open)".
- Verification: pending.
- Commit: pending.

### #6 - Notification toasts cover the top bars
- Module: Takedown / Bank / Give stamina (top bar)
- File(s): `src/TerritoryToast.cs`, `src/Bot.cs`, `src/Game.cs`
- Symptom (account Lordbane, 1.8.1): three "stamina unreadable" at the Takedown ("stuck since 00:42"), "Give stamina: the stamina bar stayed covered by notifications", and `Bank: WITHDRAW pressed for $10.2B, but the top bar shows no cash coming out - WITHDRAW ALL instead` right after taking $36.2B out → WITHDRAW ALL lifted the whole $3.42T (unprotected for seconds).
- Evidence: report zip 2026-10-19 01:06 (system.txt says "Roblox isn't open" because it was made after closing).
- Cause: the toasts ("Hired ...", "Deposited 23 items ...", "SHIPMENT SEIZED") stack over the bars and persist; only the shipment/territory ones are closed by name.
- Fix: `ToastX`/`CloseNotifications()` (grey cross at the strip's right, only with a long line of words beside it) pressed in GiveStaminaHere, the Takedown's stamina wait, and `WithdrawAmount` before "no cash coming out" counts (it re-reads the bars after closing).
- Verification: pending.
- Commit: pending.

### #7 - Bosses misread a fresh reset as "highest 2"
- Module: Bosses
- File(s): likely `src/Bot.cs` (bosses round)
- Symptom: at 00:19 and 00:25 of the same report it said "every stage you can reach is beaten today (highest 2)" while the game's list showed highest stage 28 (and the 00:17 check had read 28); by 00:38 it read 28 again. Bosses were skipped for two rounds.
- Evidence: same zip.
- Cause: not dug into yet; possibly a mid-render list read (the reset had just happened).
- Next: watch for it again, then dig.

### #8 - One greyed-AUTO look turns Auto Roll off for the restart
- Module: Crew (Auto Roll)
- File(s): `src/Bot.cs` - `StartRollInto`, `src/AutoRoll.cs`, `src/Crew.cs`
- Symptom: Lordbane's Auto Roll pass is on, but a single reading of the crew page (11 s after a bank round, page half-loading, "HIRE -25% ?" on the screen) read every row's AUTO greyed, and `StartRollInto` set `autoRollOff = true`: "off until the bot is started again". Later the game itself showed "Auto Roll landed Zofia 'Big Hat' Kuznetsov (Secret) after 76 hires for $570M", proving the pass works.
- Cause: `row.AutoActive` comes from one `LightInk` reading of the button; a mid-load page reads grey, and the punishment isn't repeated or delayed.
- Fix: three no-pass looks in a row (10 minutes apart) disable it; a successful start or a plain reading error resets the count.
- Verification: pending.
- Commit: pending.

## Commit convention

`Module: lowercase description` in the imperative. One bug = one commit.

## Cycle per bug

1. Reproduce and note it (log + screenshot).
2. Locate the module/file.
3. Minimal, local fix.
4. `build.ps1` without errors.
5. Check the symptom (real game; else review `IdleMafiaBot.log`).
6. Commit with the module prefix.

## Regression (don't break)

jobs, crew, fights, heists, bank, operations, safehouse, family and the window (UI).
