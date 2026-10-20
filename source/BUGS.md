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
