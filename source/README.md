# Idle Mafia Bot - source code

The source code of Idle Mafia Bot, a macro for the Roblox game Idle Mafia Game. It's here so anyone can check what the
bot does.

**To play, download the bot from [Releases](https://github.com/geenusername/IdleMafiaBot/releases/latest)**, not this
folder. A copy from anywhere else may not be the real one.

## How it works

It's a macro, not an injection or exploit:

- **It reads the screen.** It takes a picture of the Roblox window (`PrintWindow`, or a copy of the screen) and reads the
  text with Windows' own OCR (`Windows.Media.Ocr`), plus a few pixel colours.
- **It moves the mouse.** It clicks and scrolls where a player would (`SetCursorPos`, `mouse_event`), and presses a few
  keys like a player would: typing a search word or a price, Alt to bring Roblox to the front (`keybd_event`).
- **It never touches Roblox.** No code injected, no reading or writing the game's memory, no executor, no Lua scripts.
  You won't find `OpenProcess`, `ReadProcessMemory`, `WriteProcessMemory` or `CreateRemoteThread` anywhere in this code:
  every Windows call it makes is listed in the `DllImport` lines.
- **It never spends Robux** and never asks for a password.

## What goes online

Nothing by itself. Only three things you switch on yourself, all in `Discord.cs`, `Stats.cs` and `Updater.cs`
(the only files that make a network connection):

- **Check for updates** (Settings): only when you press it, only GitHub. It installs only an update signed with the
  release key.
- **Discord messages**: only to a webhook link you paste yourself.
- **Stats in our Discord**: only if you paste the link the server's bot gives you; game numbers only, never your Roblox
  name or a picture.

## The files

| File | What it does |
| --- | --- |
| `Program.cs` | Start-up |
| `Native.cs` | Windows calls: finding the Roblox window, taking its picture, the mouse |
| `Vision.cs` | Pictures, the OCR and reading numbers from text |
| `Game.cs` | Reading each game screen and pressing its buttons |
| `Bot.cs` | The work rounds: jobs, heists, fights, bosses, bank, operations, rewards |
| `Crew.cs`, `Family.cs`, `BlackMarket.cs`, `Events.cs`, `HeistStart.cs`, `Rejoin.cs` | Their own parts of the game |
| `UiWindow.cs`, `View.cs`, `ui\*.xaml` | The bot's window |
| `Settings.cs`, `Accounts.cs`, `Profile.cs` | Settings and the account's own files |
| `ProblemReport.cs` | "Report a problem": a zip on your Desktop, never sent anywhere |
| `Updater.cs`, `Discord.cs`, `Stats.cs` | The three online parts above |

## Building it yourself

You need Windows 10 or 11 and the Windows 10/11 SDK (for `Windows.winmd`). Then:

```
powershell -ExecutionPolicy Bypass -File build.ps1
```

That makes `IdleMafiaBot.exe` with the C# compiler that comes with Windows. Builds on the official page are signed and
have their names scrambled, so they won't match a build of this code byte for byte.

## License

All rights reserved: see [LICENSE](../LICENSE). You may read this code and build it for your own use. You may not share,
change and share, or sell it.

Questions or problems: our Discord, https://discord.gg/xw56YcDFyg
