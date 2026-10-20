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
| #3 | Remote | `/remote start` from Discord starts the macro even with Roblox closed; it should say Roblox isn't open and not start | verifying | |
| #6 | Takedown/Bank/Give stamina | The game's notification toasts cover the top bars: "stamina unreadable (3 tries)" stuck the Takedown for 30+ min, give stamina waited, and a withdraw for $10.2B fell back to WITHDRAW ALL that took the whole $3.42T out | verifying | |
| #11 | Top bar / Fights / Takedown | Roblox's own title bar covers the game's top bar (energy/cash/name row): every stamina read returns "the bar measured nothing", so fights log "Couldn't read stamina - no attacks this round" and takedowns sit stuck ("stamina unreadable at the takedown") for hours, even in only-fight mode | verifying | |
| #12 | Operations | Operations ready but not collected/started; the Home tile shows "Operations 1m left" and stays; during the 2X OPERATION SPEED event the timer doesn't update; bot looked at takedowns/donations wasted the stamina | reproducing | |

### #3 - Remote start works with Roblox closed
- ID: #3 (remote control)
- Module: Remote (Discord control)
- File(s): `src/UiWindow.cs` - `RemoteDo`, `src/Bot.cs`
- Symptom: `start` from Discord answers "Started. It plays now." even when Roblox isn't open; the macro runs blind (and with Rejoin on it tries to launch Roblox by itself). The user expects a clear refusal.
- Steps to reproduce: close Roblox, leave the bot's window open, send `/remote start` in the Discord channel.
- Cause: the remote `start` only checks `bot.Running` and the window being covered, never whether the Roblox window is there.
- Fix: `Bot.RobloxOpen()` (`game.Win.Find()`, already committed); the remote `start` refuses with "Roblox isn't open..." on Discord, and `status` says "Stopped (Roblox isn't open)".
- Verification: pending.
- Commit: pending.

### #6 - Notification toasts cover the top bars
- ID: #6 (report zip 2026-10-19 01:06)
- Module: Takedown / Bank / Give stamina (top bar)
- File(s): `src/TerritoryToast.cs` - `ToastX`, `CloseNotifications`, `src/Bot.cs`, `src/Game.cs`
- Symptom (account Lordbane, 1.8.1): three "stamina unreadable" at the Takedown ("stuck since 00:42"), "Give stamina: the stamina bar stayed covered by notifications", and `Bank: WITHDRAW pressed for $10.2B, but the top bar shows no cash coming out - WITHDRAW ALL instead` right after taking $36.2B out → WITHDRAW ALL lifted the whole $3.42T (unprotected for seconds).
- Evidence: report zip 2026-10-19 01:06 (system.txt says "Roblox isn't open" because it was made after closing).
- Cause: the toasts ("Hired ...", "Deposited 23 items ...", "SHIPMENT SEIZED") stack over the bars and persist; only the shipment/territory ones are closed by name.
- Fix: `ToastX`/`CloseNotifications()` (grey cross at the strip's right, only with a long line of words beside it) pressed in GiveStaminaHere, the Takedown's stamina wait, and `WithdrawAmount` before "no cash coming out" counts (it re-reads the bars after closing). Note: the call sites were committed in 1.8.1 pointing at a `CloseNotifications` that only existed uncommitted - this commit brings the implementation that makes the tree build.
- Verification: build.ps1 passes; in-game pending.
- Commit: pending.

### #11 - Roblox's title bar covers the game's top bar
- ID: #11 (forum report #5, @JAS24J)
- Module: Top bar / Fights / Takedown
- File(s): `src/Bot.cs` - `NoteRobloxBar`, `SweepRobloxBar`, the fights and takedown stamina checks, `src/Game.cs` - `RobloxBarOver`, `ClearRobloxBar`, new `ToggleFullscreenClearBar`, `src/Native.cs` - new `RobloxWindow.PressKey`
- Symptom: from 2026-10-19 ~01:20, no attacks at all ("even in only mode, no take downs or fights"). Log repeats "Couldn't read stamina (the bar measured nothing, its max known 36) - N free Takedown attack(s) wait", "Couldn't read stamina - no attacks this round", "Fights: stuck since 13:29 - the stamina bar didn't read (3 tries)", plus "Couldn't read YOUR STATS from the game's menu".
- Evidence: report zip 20261019-133340.zip. Key screenshot 20261019-013249 (the "Roblox" window chrome with minimize/close over the top bar, hiding the energy/cash/name row; stamina/health one row down still visible). Fights worked normally Oct 13-18 with the same settings, so not a config regression. Persisted after the 1.8.0 -> 1.8.1 update at 12:33.
- Cause: Roblox's window chrome (drawn in fullscreen-ish windows too) sits over the top bar, so `ReadHeader` returns nothing for the bars. The old remediation (mouse sweep to the top, every 2/15 min) didn't clear it, and the fight/takedown paths gave up for minutes-hours without triggering it.
- Fix (three parts):
  1. `FixRobloxBarNow()` (Bot.cs): when a bar read fails and `game.RobloxBar` is set, the sweep runs immediately instead of waiting - used by the fights loop (before "no attacks this round") and the takedown loop (before the 20 min wait), capped at 2 tries per pass.
  2. `ToggleFullscreenClearBar()` (Game.cs) + `RobloxWindow.PressKey()` (Native.cs): escalation after a failed mouse sweep - press F11 (twice if needed), verifying with a fresh capture after each press; only if neither the mouse nor F11 clears it does the bot tell the user to do it by hand.
  3. Verification before "stuck": the counters reset when the bar goes away, and the give-up message now reflects that both automatic actions were tried.
- Verification: pending (build.ps1 passes; needs a session with the title bar showing).
- Commit: pending.
- User workaround until 1.8.2: press F11 in Roblox (fullscreen has no title bar).

### #12 - Operations stop being collected and the tile's timer freezes
- ID: #12 (bug report #3, @rocketcrock)
- Module: Operations / Home tile
- File(s): `src/Bot.cs` - `DoOperations` (~5306), `src/Game.cs` - `ReadOpSlots` (~3404), `src/UiWindow.cs` - `TileLine` "Operations" (~1051), `ShowOps` (~2349)
- Symptom (account ox688, reported 2026-10-19 11:54): the operations had been ready but the bot didn't collect or start new ones; the Home tile showed "Operations 1m left" and stayed; during the 2X OPERATION SPEED event the time doesn't update.
- Evidence: report zip 20261019-115412.zip (log read): the last collect/start round was 2026-10-19 01:38 ("Tax the Silk Road", 4 slots). Rounds at 08:44, 10:02, 10:13, 11:24 still included the operations task, but no collect or start after 01:38 - with 10h ops they were ready ~11:38, the user stopped the bot at 11:50, and no collect happened between that.
- Cause: to dig into - likely the wait loop: `nextOps` is set from the soonest display read (`SecLeft + 8`), then `Visited(Tab.Operations, ..., due = soonest + 600)` (Bot.cs ~5351-5356) — the +600 parks the operations task 10 min past the read, so ready slots wait in a queue the bot doesn't revisit; the OpsSpeed `/2` split and the tile parse both feed from that same soonest.
- Next: reproduce with the events off/on, log `nextOps` when operations are skipped, then decide between trimming the +600 grace (for overdue timers) and re-reading the page when it's overdue.
- State: reproducing, not fixed.

## Feature requests pending

- \@rocketcrock (report #3): when a level-up is close, the macro should spend the remaining stamina/energy first (donate to the family perks, do a job) instead of rolling straight into the next level-up - "doing this allows less wasting". Priority-order tweak for the round scheduler, not a bug.

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
