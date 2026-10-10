# Builds IdleMafiaBot.exe with the C# compiler that comes with Windows (.NET Framework 4.x).
# Needs the Windows 10/11 SDK for Windows.winmd (the built-in Windows OCR).
# Usage: powershell -ExecutionPolicy Bypass -File build.ps1 [-Out file.exe]
param([string]$Out = 'IdleMafiaBot.exe')
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
$fx = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"
$csc = "$fx\csc.exe"
$gac = "$env:WINDIR\Microsoft.NET\assembly\GAC_MSIL"
$winmd = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\UnionMetadata\*\Windows.winmd" -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -notmatch '\\Facade\\' } | Sort-Object FullName -Descending | Select-Object -First 1
if (-not $winmd) { throw 'Windows SDK metadata (Windows.winmd) not found - install the Windows 10/11 SDK.' }
$refs = @(
  'System.dll', 'System.Core.dll', 'System.Drawing.dll', 'System.Xaml.dll', 'System.Security.dll',
  "$fx\WPF\PresentationFramework.dll", "$fx\WPF\PresentationCore.dll", "$fx\WPF\WindowsBase.dll",
  "$fx\System.IO.Compression.dll", "$fx\System.IO.Compression.FileSystem.dll",
  $winmd.FullName,
  "$gac\System.Runtime\v4.0_4.0.0.0__b03f5f7f11d50a3a\System.Runtime.dll",
  "$gac\System.Runtime.WindowsRuntime\v4.0_4.0.0.0__b77a5c561934e089\System.Runtime.WindowsRuntime.dll",
  "$gac\System.Runtime.InteropServices.WindowsRuntime\v4.0_4.0.0.0__b03f5f7f11d50a3a\System.Runtime.InteropServices.WindowsRuntime.dll",
  "$gac\System.Threading.Tasks\v4.0_4.0.0.0__b03f5f7f11d50a3a\System.Threading.Tasks.dll"
) | ForEach-Object { "/r:$_" }
# the window (WPF): src\ui\*.xaml are embedded in the exe and loaded at startup
$resources = @(Get-ChildItem src\ui\*.xaml | ForEach-Object { "/resource:$($_.FullName),IdleMafiaBot.ui.$($_.Name)" })
$icon = @('/win32icon:src\ui\app.ico', "/resource:$((Resolve-Path src\ui\app.ico).Path),IdleMafiaBot.ui.app.ico")
$sources = @(Get-ChildItem src\*.cs | ForEach-Object { $_.FullName })
& $csc /nologo /optimize+ /codepage:65001 /target:winexe "/out:$Out" @icon @refs @resources @sources
if ($LASTEXITCODE -ne 0) { throw "compile failed ($LASTEXITCODE)" }
"built $Out"
