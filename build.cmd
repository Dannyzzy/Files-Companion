@echo off
setlocal enabledelayedexpansion
rem ---------------------------------------------------------------
rem  Files Companion - one-command build
rem  Needs only the .NET Framework compiler shipped with Windows.
rem  Keep this file ASCII with CRLF line endings: cmd.exe parses
rem  batch files as ANSI and breaks on LF-only files.
rem ---------------------------------------------------------------

set "ROOT=%~dp0"
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo [x] csc.exe not found - .NET Framework 4.x is required.
  exit /b 1
)

echo.
echo [1/3] Compiling the launch shim...
if not exist "%ROOT%dist" mkdir "%ROOT%dist"
"%CSC%" /nologo /target:winexe /platform:x64 /optimize+ ^
  /out:"%ROOT%dist\FilesOpen.exe" ^
  /reference:System.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll ^
  "%ROOT%src\FilesOpen.cs"
if errorlevel 1 ( echo [x] shim compile failed & exit /b 1 )

echo [2/3] Packing the payload...
set "STAGE=%ROOT%build\payload"
if exist "%ROOT%build" rd /s /q "%ROOT%build%"
mkdir "%STAGE%"
copy /y "%ROOT%dist\FilesOpen.exe" "%STAGE%\" >nul
powershell -NoProfile -Command "Compress-Archive -Path '%STAGE%\*' -DestinationPath '%ROOT%build\payload.zip' -Force"
if errorlevel 1 ( echo [x] packing failed & exit /b 1 )

echo [3/3] Building the single-file installer...
pushd "%ROOT%build"
"%CSC%" /nologo /target:winexe /platform:x64 /optimize+ ^
  /out:"%ROOT%dist\FilesCompanionSetup.exe" ^
  /reference:System.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll ^
  /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll ^
  /resource:payload.zip,PAYLOAD ^
  "%ROOT%installer\Setup.cs"
set "RC=%errorlevel%"
popd
if not "%RC%"=="0" ( echo [x] installer build failed & exit /b 1 )

echo.
echo [OK] Build finished:
echo      dist\FilesOpen.exe             the launch shim
echo      dist\FilesCompanionSetup.exe   single-file installer
echo.
endlocal
