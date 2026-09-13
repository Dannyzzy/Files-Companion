@echo off
setlocal enabledelayedexpansion
rem ===============================================================
rem  Files Companion - one-click installer
rem
rem  Downloads the newest release and starts it. Some networks block
rem  github.com outright, so this tries the direct download first and
rem  the ghfast.top mirror second, and only then gives up - telling
rem  you exactly what to do by hand.
rem
rem  No administrator rights are needed, and nothing is installed
rem  until you confirm it in the installer window.
rem ===============================================================

set "REPO=Dannyzzy/Files-Companion"
set "ASSET=FilesCompanionSetup.exe"
set "DIRECT=https://github.com/%REPO%/releases/latest/download/%ASSET%"
set "MIRROR=https://ghfast.top/https://github.com/%REPO%/releases/latest/download/%ASSET%"
set "OUT=%TEMP%\%ASSET%"

echo.
echo ===============================================
echo   Files Companion - one-click install
echo ===============================================
echo.

if exist "%OUT%" del /f /q "%OUT%" >nul 2>&1

call :fetch "github.com" "%DIRECT%"
if not defined OK call :fetch "the ghfast.top mirror" "%MIRROR%"

if not defined OK (
  echo.
  echo [x] Both download channels failed.
  echo       %DIRECT%
  echo       %MIRROR%
  echo.
  echo     What to do:
  echo       1. Run this script again - the mirror is sometimes slow.
  echo       2. Open this page in a browser and download the setup by hand:
  echo          https://ghfast.top/https://github.com/%REPO%/releases/latest
  echo.
  pause
  exit /b 1
)

for %%A in ("%OUT%") do set "SIZE=%%~zA"
echo.
echo [ok] Saved %ASSET% ^(%SIZE% bytes^) to
echo      %OUT%
echo.
echo   Starting the installer now. When it finishes it shows what was
echo   detected on this machine - untick anything you do not want first.
echo.
pause
start "" "%OUT%"
exit /b 0

rem ---------------------------------------------------------------
rem  :fetch <label> <url>
rem  Sets OK=1 on success. A failed transfer can still leave a stub
rem  file behind, so the size is checked as well as the exit code.
rem ---------------------------------------------------------------
:fetch
echo [..] Downloading from %~1 ...
curl.exe -L --fail --silent --show-error --retry 2 --retry-delay 2 --connect-timeout 15 --max-time 300 -o "%OUT%" "%~2"
if errorlevel 1 (
  echo [x]  %~1 failed.
  if exist "%OUT%" del /f /q "%OUT%" >nul 2>&1
  exit /b 1
)
set "SZ=0"
for %%A in ("%OUT%") do set "SZ=%%~zA"
if !SZ! LSS 100000 (
  echo [x]  %~1 returned only !SZ! bytes - not the real installer.
  del /f /q "%OUT%" >nul 2>&1
  exit /b 1
)
echo [ok] %~1 succeeded ^(!SZ! bytes^).
set "OK=1"
exit /b 0