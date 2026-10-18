@echo off
rem Starts the bot keeping a picture of every player fight in problems\fights.
rem The pictures are there to check the odds the bot works out against real
rem fights: the two rolls and the result are in them. Delete that folder when
rem they are not wanted any more.
start "" "%~dp0IdleMafiaBot.exe" --fightshots
