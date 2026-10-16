@echo off
title DWG Tools
cd /d "%~dp0"
echo.
echo   DWG Tools  (Reference Tree + Flatten)
echo   =====================================

REM --- first run: Python environment ---------------------------------------
if exist "venv\Scripts\python.exe" goto havevenv
set "PY="
py -3 --version >nul 2>&1 && set "PY=py -3"
if not defined PY python --version >nul 2>&1 && set "PY=python"
if not defined PY (
    echo   Python 3 was not found. Install it from python.org, then run this again.
    pause
    exit /b 1
)
echo   First run: creating the Python environment...
%PY% -m venv venv
if errorlevel 1 ( echo   Could not create venv. & pause & exit /b 1 )
"venv\Scripts\python.exe" -m pip install --upgrade pip --quiet
"venv\Scripts\python.exe" -m pip install -r requirements.txt --quiet
if errorlevel 1 ( echo   pip install failed. & pause & exit /b 1 )
:havevenv
if not exist "settings.json" copy /y settings.example.json settings.json >nul

REM --- build the AutoCAD plugin if missing or older than its source --------
set "NEEDBUILD="
if not exist "plugin\bin\current.json" set "NEEDBUILD=1"
if not defined NEEDBUILD for /f %%i in ('powershell -NoProfile -Command "$d=(Get-Item 'plugin\bin\current.json').LastWriteTime; if (@(Get-ChildItem 'plugin\src\*.cs' | Where-Object { $_.LastWriteTime -gt $d }).Count) { 'yes' }"') do set "NEEDBUILD=1"
if defined NEEDBUILD (
    echo   Building the AutoCAD plugin...
    call plugin\build.bat nopause >nul
    if not exist logs mkdir logs
    type plugin\build.log >> logs\build-history.log
    echo.>> logs\build-history.log
)
findstr /c:"BUILD FAILED" plugin\build.log >nul 2>&1
if not errorlevel 1 (
    echo.
    echo   *** THE AUTOCAD PLUGIN DID NOT BUILD ***
    echo   The page will keep using the last build that worked, which does not have the newest changes.
    echo   Details from plugin\build.log:
    type plugin\build.log
    echo.
    pause
)

REM --- file-safety audit: refuse to start if any code could delete/overwrite
REM     a file without going through the guard (SafeFs.cs / safe_fs.py) -------
"venv\Scripts\python.exe" tools\safety_audit.py
if errorlevel 1 (
    echo.
    echo   DWG Tools will not start until the safety audit passes.
    pause
    exit /b 1
)

echo.
echo   Starting DWG Tools at http://localhost:5014 (this computer only).
echo   Keep this window open while you use it. Close it to stop.
echo   To let other computers on the network open it, use run_shared.bat instead.
echo.
start "" powershell -NoProfile -WindowStyle Hidden -Command "Start-Sleep -Seconds 3; Start-Process 'http://127.0.0.1:5014'"
"venv\Scripts\python.exe" webapp\app.py %*
echo.
echo   Stopped.
pause
