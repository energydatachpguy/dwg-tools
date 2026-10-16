@echo off
rem Builds the DWG Tools AutoCAD plugin with the C# compiler that ships with
rem Windows -- no Visual Studio needed.
rem
rem EVERY BUILD GETS ITS OWN NAME: bin\DwgTools_<date-time>.dll with its own
rem command DWGT<date-time>. A running Plant 3D can never be stuck on an old
rem build: the app loads the newest one next to any old ones and calls its
rem own command. Old builds are never overwritten or deleted.
setlocal
cd /d "%~dp0"
for /f %%t in ('powershell -NoProfile -Command "Get-Date -Format yyyyMMddHHmmss"') do set STAMP=%%t
echo DWG Tools plugin build %STAMP%  (%DATE% %TIME%) > build.log
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
set FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319
if not exist "%CSC%" (
  echo C# compiler not found at %CSC% (needs .NET Framework 4.7 or newer^) >> build.log
  echo BUILD FAILED >> build.log
  type build.log
  if "%1"=="" pause
  exit /b 1
)
set ACAD=
set ACADYEAR=
set ACADKIND=NONE
for /f "tokens=1-3 delims=|" %%a in ('powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0find_acad.ps1"') do (
  set "ACADKIND=%%a"
  set "ACAD=%%b"
  set "ACADYEAR=%%c"
)
rem Fallback when PowerShell scripts are blocked on this PC: the AutoCAD 2020-2024 folders
if "%ACAD%"=="" for %%y in (2020 2021 2022 2023 2024) do if exist "C:\Program Files\Autodesk\AutoCAD %%y\acdbmgd.dll" (
  set "ACAD=C:\Program Files\Autodesk\AutoCAD %%y"
  set "ACADYEAR=%%y"
  set "ACADKIND=OK"
)
if "%ACADKIND%"=="NONE" (
  echo Could not find AutoCAD or Plant 3D ^(a folder with acdbmgd.dll under C:\Program Files\Autodesk^). >> build.log
  echo Put the full path of acad.exe in settings.json as "acad_exe" and run again. >> build.log
  echo BUILD FAILED >> build.log
  type build.log
  if "%1"=="" pause
  exit /b 1
)
if "%ACADKIND%"=="NET8" goto net8
echo Building against AutoCAD %ACADYEAR% in "%ACAD%" >> build.log
rem WindowsBase.dll (for the Esc key that clears the highlight): the framework's WPF folder, else the reference assemblies
set WB=%FW%\WPF\WindowsBase.dll
if not exist "%WB%" for /d %%r in ("%ProgramFiles(x86)%\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.*") do if exist "%%~r\WindowsBase.dll" set "WB=%%~r\WindowsBase.dll"
if not exist "%WB%" (
  echo Could not find WindowsBase.dll (looked in %FW%\WPF and Reference Assemblies^) >> build.log
  echo BUILD FAILED >> build.log
  type build.log
  if "%1"=="" pause
  exit /b 1
)
if not exist bin mkdir bin
if not exist obj mkdir obj
> obj\BuildInfo.cs (
  echo namespace DwgFlat { public static class BuildInfo { public const string Stamp = "%STAMP%"; public const string Command = "DWGT%STAMP%"; } }
)
"%CSC%" /nologo /target:library /platform:x64 /optimize+ /nowarn:618,414,649 ^
  /out:bin\DwgTools_%STAMP%.dll ^
  /r:"%ACAD%\acdbmgd.dll" /r:"%ACAD%\accoremgd.dll" ^
  /r:"%FW%\System.Web.Extensions.dll" /r:"%FW%\System.Drawing.dll" /r:"%WB%" ^
  src\*.cs obj\BuildInfo.cs >> build.log 2>&1
if errorlevel 1 (
  echo BUILD FAILED >> build.log
  type build.log
  if "%1"=="" pause
  exit /b 1
)
> bin\current.json (
  echo {"dll": "DwgTools_%STAMP%.dll", "command": "DWGT%STAMP%", "stamp": "%STAMP%"}
)
echo Built %~dp0bin\DwgTools_%STAMP%.dll  (command DWGT%STAMP%) >> build.log
type build.log
if "%1"=="" pause
endlocal
exit /b 0

:net8
rem AutoCAD 2025 and newer: .NET 8, built with the .NET SDK (dotnet build)
echo Building against AutoCAD %ACADYEAR% in "%ACAD%" (.NET 8) >> build.log
set "DOTNET="
for %%p in ("%ProgramFiles%\dotnet\dotnet.exe" "%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe") do if not defined DOTNET if exist "%%~p" set "DOTNET=%%~p"
if not defined DOTNET for /f "delims=" %%p in ('where dotnet 2^>nul') do if not defined DOTNET set "DOTNET=%%p"
set "HAVESDK="
if not exist obj mkdir obj
if defined DOTNET "%DOTNET%" --list-sdks > obj\sdks.txt 2>nul
if defined DOTNET for /f "usebackq tokens=1 delims=." %%v in ("obj\sdks.txt") do if %%v GEQ 8 set "HAVESDK=1"
if not defined HAVESDK (
  echo AutoCAD %ACADYEAR% runs on .NET 8, so this computer needs the free .NET 8 SDK to build the plugin. >> build.log
  echo Install it with:  winget install Microsoft.DotNet.SDK.8 >> build.log
  echo or download ".NET 8 SDK x64" from https://dotnet.microsoft.com/download/dotnet/8.0 >> build.log
  echo then run setup.bat again. >> build.log
  echo BUILD FAILED >> build.log
  type build.log
  if "%1"=="" pause
  exit /b 1
)
if not exist bin mkdir bin
if not exist obj mkdir obj
> obj\BuildInfo.cs (
  echo namespace DwgFlat { public static class BuildInfo { public const string Stamp = "%STAMP%"; public const string Command = "DWGT%STAMP%"; } }
)
"%DOTNET%" build "%~dp0DwgTools.net8.csproj" -c Release -nologo -v:minimal -p:Stamp=%STAMP% -p:AcadDir="%ACAD%" >> build.log 2>&1
if errorlevel 1 (
  echo BUILD FAILED >> build.log
  type build.log
  if "%1"=="" pause
  exit /b 1
)
if not exist "bin\DwgTools_%STAMP%.dll" (
  echo dotnet build finished but bin\DwgTools_%STAMP%.dll is missing >> build.log
  echo BUILD FAILED >> build.log
  type build.log
  if "%1"=="" pause
  exit /b 1
)
> bin\current.json (
  echo {"dll": "DwgTools_%STAMP%.dll", "command": "DWGT%STAMP%", "stamp": "%STAMP%", "net": "8"}
)
echo Built %~dp0bin\DwgTools_%STAMP%.dll  (command DWGT%STAMP%, .NET 8) >> build.log
type build.log
if "%1"=="" pause
endlocal
