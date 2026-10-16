@echo off
title DWG Tools - setup
cd /d "%~dp0"
echo.
echo   DWG Tools - setup
echo   =================
echo   Safe to run again any time (after a git pull, or if something is broken).
echo.

REM --- 1. Python ----------------------------------------------------------
set "PY="
py -3 --version >nul 2>&1 && set "PY=py -3"
if not defined PY python --version >nul 2>&1 && set "PY=python"
if not defined PY (
    echo   Python 3 was not found.
    echo   Install Python 3.10 or newer from https://www.python.org/downloads/
    echo   and tick "Add python.exe to PATH" on the first screen. Then run setup.bat again.
    pause
    exit /b 1
)
echo   [1/5] Python:
%PY% --version

REM --- 2. Virtual environment + packages -----------------------------------
if not exist "venv\Scripts\python.exe" (
    echo   [2/5] Creating the Python environment ^(venv^)...
    %PY% -m venv venv
    if errorlevel 1 ( echo   Could not create venv. & pause & exit /b 1 )
) else (
    echo   [2/5] Python environment already there.
)
echo         Installing packages...
"venv\Scripts\python.exe" -m pip install --upgrade pip --quiet
"venv\Scripts\python.exe" -m pip install -r requirements.txt --quiet
if errorlevel 1 ( echo   pip install failed - check the internet connection. & pause & exit /b 1 )

REM --- 3. Per-machine settings (never in git) -------------------------------
if not exist "settings.json" (
    copy /y settings.example.json settings.json >nul
    echo   [3/5] Created settings.json ^(this machine only^).
) else (
    echo   [3/5] settings.json already there - left as is.
)

REM --- 4. Build the AutoCAD plugin against THIS machine's AutoCAD ------------
echo   [4/5] Building the AutoCAD plugin...
call plugin\build.bat nopause >nul
if not exist logs mkdir logs
type plugin\build.log >> logs\build-history.log
findstr /c:"BUILD FAILED" plugin\build.log >nul 2>&1
if not errorlevel 1 (
    echo.
    echo   *** THE AUTOCAD PLUGIN DID NOT BUILD ***
    type plugin\build.log
    echo.
) else (
    findstr /c:"Building against" /c:"Built " plugin\build.log
)

REM --- 5. Check everything --------------------------------------------------
echo   [5/5] Checking the setup...
"venv\Scripts\python.exe" tools\safety_audit.py
"venv\Scripts\python.exe" tools\check_setup.py
echo.
echo   Start the app with run.bat  (http://localhost:5014, this computer only).
echo.
pause
