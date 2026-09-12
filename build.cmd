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
if not exist "%ROOT%vendor\RecycleBin\RecycleBin.exe" (
  echo [x] vendor\RecycleBin is missing. See vendor\README.md.
  exit /b 1
)

echo.
echo [1/4] Compiling the launch shim...
if not exist "%ROOT%dist" mkdir "%ROOT%dist"
"%CSC%" /nologo /target:winexe /platform:x64 /optimize+ ^
  /out:"%ROOT%dist\FilesOpen.exe" ^
  /reference:System.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll ^
  "%ROOT%src\FilesOpen.cs"
if errorlevel 1 ( echo [x] shim compile failed & exit /b 1 )

echo [2/4] Packing the payloads...
set "STAGE=%ROOT%build\payload"
if exist "%ROOT%build" rd /s /q "%ROOT%build%"
mkdir "%STAGE%\shim"
mkdir "%STAGE%\rb"
copy /y "%ROOT%dist\FilesOpen.exe" "%STAGE%\shim\" >nul
xcopy /y /e /i /q "%ROOT%vendor\RecycleBin\*" "%STAGE%\rb\" >nul
powershell -NoProfile -Command "Compress-Archive -Path '%STAGE%\shim\*' -DestinationPath '%ROOT%build\payload-shim.zip' -Force; Compress-Archive -Path '%STAGE%\rb\*' -DestinationPath '%ROOT%build\payload-rb.zip' -Force"
if errorlevel 1 ( echo [x] packing failed & exit /b 1 )

echo [3/4] Building the all-in-one installer...
rem csc does not split "file,ID" when the whole argument is quoted, so run from
rem the build folder and pass bare file names.
pushd "%ROOT%build"
"%CSC%" /nologo /target:winexe /platform:x64 /optimize+ ^
  /out:"%ROOT%dist\FilesCompanionSetup.exe" ^
  /reference:System.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll ^
  /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll ^
  /resource:payload-shim.zip,PAYLOAD_SHIM ^
  /resource:payload-rb.zip,PAYLOAD_RB ^
  "%ROOT%installer\Setup.cs"
set "RC=%errorlevel%"
popd
if not "%RC%"=="0" ( echo [x] installer build failed & exit /b 1 )

echo [4/4] Done.
echo.
echo [OK] Build finished:
echo      dist\FilesOpen.exe             the launch shim (standalone)
echo      dist\FilesCompanionSetup.exe   all-in-one installer (both components)
echo.
endlocal