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
| #11 | Top bar / Fights / Takedown | Roblox's own title bar covers the game's top bar (energy/cash/name row): every stamina read returns "the bar measured nothing", so fights log "Couldn't read stamina - no attacks this round" and takedowns sit stuck ("stamina unreadable at the takedown") for hours, even in only-fight mode | done | this commit |
| #12 | Operations | Operations ready but not collected/started; the Home tile shows "Operations 1m left" and stays; during the 2X OPERATION SPEED event the timer doesn't update; bot looked at takedowns/donations wasted the stamina | done | a2d21e9 |
| #13 | Crew (Auto Roll) | "Bot thinks I don't have auto roll gamepass": one greyed-AUTO look turned Auto Roll off while the user owns the pass (forum report #2, no zip) | dropped | 1ec2383 |

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
- Verification: done (build.ps1 passes; confirmed on the session where the title bar was showing).
- Commit: this commit.
- User workaround until 1.8.2: press F11 in Roblox (fullscreen has no title bar).

### #12 - Operations stop being collected and the tile's timer freezes
- ID: #12 (bug report #3, @rocketcrock)
- Module: Operations / Home tile
- File(s): `src/Bot.cs` - `DoOperations` (~5306), `src/Game.cs` - `ReadOpSlots` (~3404), `src/UiWindow.cs` - `TileLine` "Operations" (~1051), `ShowOps` (~2349)
- Symptom (account ox688, reported 2026-10-19 11:54): the operations had been ready but the bot didn't collect or start new ones; the Home tile showed "Operations 1m left" and stayed; during the 2X OPERATION SPEED event the time doesn't update.
- Evidence: report zip 20261019-115412.zip (log read): the last collect/start round was 2026-10-19 01:38 ("Tax the Silk Road", 4 slots). Rounds at 08:44, 10:02, 10:13, 11:24 still included the operations task, but no collect or start after 01:38 - with 10h ops they were ready ~11:38, the user stopped the bot at 11:50, and no collect happened between that.
- Cause: the +600 grace in `DoOperations` (Bot.cs ~5356): `nextOps` is set from the soonest display read (`SecLeft + 8`), but the scheduler's gate (`NumberSays`, Bot.cs ~936) also requires `now >= safety` where safety was `soonest + 600` - so every finished timer waited 10 extra minutes before the tab was revisited. In the report: last visit 11:24 with `soonest≈17 min` → collect scheduled ~11:51; the user stopped at 11:50, so the ready ops were never collected. The Home tile freeze is cosmetic: `TileLine` "Operations" (UiWindow.cs ~1051) prints the `Left` string fixed at the last read.
- Fix: the grace is trimmed from +600 to +90 s, so a finished timer gets the tab re-read about 2.5 min after the display said it would end; a stale display ("1m left" that stays) just reschedules itself instead of parking ready slots 10 minutes. The tile string is still frozen between reads (cosmetic; only updated on a real tab visit).
- State: done (fix in a2d21e9). Still to watch in real sessions: a collect landing within ~3 min of ready, and the 2X-speed scheduling.

### #13 - Auto Roll disabled although the user has the pass
- ID: #13 (forum report #2, no zip, one screenshot; reported 2026-10-19 11:46)
- Module: Crew (Auto Roll)
- Duplicate of #8. The reporter's screenshot shows the pre-1ec2383 message ("off until the bot is started again"), so a build without the #8 fix; the one greyed-AUTO look on a half-loaded crew page turned the pass off even though the account owns it.
- Covered by the #8 fix in 1ec2383: three no-pass looks in a row (10 minutes apart) disable it; a successful start or a plain reading error resets the count.
- State: dropped as a duplicate; verify with the same criteria as #8.

## Feature requests pending

- \@tbear25201 (idea #1): "Auto bank at X amount" - a setting so the macro banks whenever the cash on hand reaches a user-set amount. The bot only banks after each round, and while fighting the cash piles up to huge numbers between rounds.
  - Done: `BankAt` setting with a mode picker on the Bank tile's options - "After every round" (the old behavior, BankAt=0) or an amount (preset or typed) that only banks when the top bar's cash reads at or over it; the fights session banks right there between attacks (a fights run lasts long without a scheduler pass), then goes back to the fight list. Withdrawals for purchases and the whale-hunt safety banking work in both modes.
- \@tbear25201 (idea #4): "Stamina flexibility" - the kept stamina groups gifts, player fights and takedowns into one budget; the user wants player fights' kept stamina as a separate value, so it can keep enough to fight while still donating gifts to the family perks.
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
