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
rem AutoCAD 2025 and newer: modern .NET, built with the .NET SDK (dotnet build).
rem Which .NET: the one AutoCAD's own acdbmgd.dll was built for (8 at release; 10 after
rem Autodesk's later updates). Read from the DLL; if that guess is wrong the compiler
rem says which version it needs and the build is retried once with that one.
set NETV=8
set RETRIED=
for /f %%v in ('powershell -NoProfile -Command "$t=[Text.Encoding]::GetEncoding(28591).GetString([IO.File]::ReadAllBytes('%ACAD%\acdbmgd.dll')); $m=[regex]::Match($t,'\.NETCoreApp,Version=v(\d+)'); if($m.Success){$m.Groups[1].Value}"') do set NETV=%%v
set "DOTNET="
for %%p in ("%ProgramFiles%\dotnet\dotnet.exe" "%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe") do if not defined DOTNET if exist "%%~p" set "DOTNET=%%~p"
if not defined DOTNET for /f "delims=" %%p in ('where dotnet 2^>nul') do if not defined DOTNET set "DOTNET=%%p"
if not exist bin mkdir bin
if not exist obj mkdir obj
> obj\BuildInfo.cs (
  echo namespace DwgFlat { public static class BuildInfo { public const string Stamp = "%STAMP%"; public const string Command = "DWGT%STAMP%"; } }
)
set DOTNET_CLI_TELEMETRY_OPTOUT=1
set DOTNET_NOLOGO=1

:netbuild
echo Building against AutoCAD %ACADYEAR% in "%ACAD%" (AutoCAD uses .NET %NETV%) >> build.log
set "HAVESDK="
type nul > obj\sdks.txt
if defined DOTNET "%DOTNET%" --list-sdks > obj\sdks.txt 2>nul
for /f "usebackq tokens=1 delims=." %%v in ("obj\sdks.txt") do if %%v GEQ %NETV% set "HAVESDK=1"
if not defined HAVESDK (
  echo AutoCAD %ACADYEAR% on this computer runs on .NET %NETV%, so building the plugin needs the free .NET %NETV% SDK. >> build.log
  if exist obj\sdks.txt type obj\sdks.txt >> build.log
  echo Install it with:  winget install Microsoft.DotNet.SDK.%NETV% >> build.log
  echo or download ".NET %NETV%.0 SDK, Windows x64" from https://dotnet.microsoft.com/download/dotnet/%NETV%.0 >> build.log
  echo then close this window, open a new Command Prompt, and run setup.bat again. >> build.log
  echo BUILD FAILED >> build.log
  type build.log
  if "%1"=="" pause
  exit /b 1
)
"%DOTNET%" build "%~dp0DwgTools.netcore.csproj" -c Release -nologo -v:minimal -clp:ErrorsOnly -p:NetMajor=%NETV% -p:Stamp=%STAMP% -p:AcadDir="%ACAD%" > obj\dotnet.log 2>&1
set BUILDERR=%ERRORLEVEL%
type obj\dotnet.log >> build.log
if not "%BUILDERR%"=="0" (
  if not defined RETRIED for /f %%v in ('powershell -NoProfile -Command "$m=[regex]::Match((Get-Content -Raw 'obj\dotnet.log'),'uses .System\.Runtime, Version=(\d+)'); if($m.Success){$m.Groups[1].Value}"') do if not "%%v"=="%NETV%" (
    set NETV=%%v
    set RETRIED=1
  )
)
if not "%BUILDERR%"=="0" if "%RETRIED%"=="1" (
  set RETRIED=2
  echo AutoCAD's files need .NET %NETV%; building again for that. >> build.log
  goto netbuild
)
if not "%BUILDERR%"=="0" (
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
  echo {"dll": "DwgTools_%STAMP%.dll", "command": "DWGT%STAMP%", "stamp": "%STAMP%", "net": "%NETV%"}
)
echo Built %~dp0bin\DwgTools_%STAMP%.dll  (command DWGT%STAMP%, .NET %NETV%) >> build.log
type build.log
if "%1"=="" pause
endlocal
