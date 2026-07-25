@echo off
setlocal EnableExtensions EnableDelayedExpansion
rem ===========================================================================
rem  ToPlay installer build pipeline.
rem
rem  Produces a single, self-contained  dist\ToPlaySetup.exe  that any Windows
rem  PC can download and run to install ToPlay (no .NET required on the target).
rem
rem  Steps:
rem    1. fetch ffmpeg ONCE and cache it (so it ships inside the installer and
rem       the target PC never has to download it)
rem    2. publish the streaming host   (ToPlay.Host.exe, self-contained)
rem    3. publish the GUI launcher     (ToPlay.exe,      self-contained)
rem       -> both land in dist\app so ToPlay.exe finds ToPlay.Host.exe beside it
rem    4. zip dist\app  ->  src\ToPlay.Installer\payload\app.zip (embedded payload)
rem    5. publish the installer        (ToPlaySetup.exe, single-file self-contained)
rem
rem  Run from anywhere:   scripts\build-installer.cmd
rem
rem  Set  TOPLAY_NO_FFMPEG=1  to build a smaller installer WITHOUT ffmpeg bundled
rem  (the target PC will then download ffmpeg during install instead).
rem ===========================================================================


rem --- locate the .NET 8 SDK (per-user installs preferred) -------------------
rem  Check both common per-user install dirs: the dotnet-install.ps1 default
rem  (%USERPROFILE%\.dotnet) and the winget / manual LocalAppData location
rem  (%LOCALAPPDATA%\Microsoft\dotnet). Fall back to whatever "dotnet" is on PATH.
set "DOTNET="
if exist "%USERPROFILE%\.dotnet\dotnet.exe" (
  set "DOTNET=%USERPROFILE%\.dotnet\dotnet.exe"
  set "DOTNET_ROOT=%USERPROFILE%\.dotnet"
) else if exist "%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe" (
  set "DOTNET=%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe"
  set "DOTNET_ROOT=%LOCALAPPDATA%\Microsoft\dotnet"
) else (
  set "DOTNET=dotnet"
)

rem  Disable the reusable build/compiler server processes. When the SDK lives in
rem  a non-standard per-user location, spawning those helper processes can fail
rem  with "The system cannot find the path specified"; running in-process is
rem  a touch slower but always works.
set "NOSRV=--disable-build-servers"

set "ROOT=%~dp0.."
pushd "%ROOT%" || (echo Could not enter repo root & exit /b 1)

set "RID=win-x64"
set "DIST=%ROOT%\dist"
set "APPDIR=%DIST%\app"
set "PAYLOAD=%ROOT%\src\ToPlay.Installer\payload"

echo(
echo === ToPlay installer build =================================================
echo   SDK : %DOTNET%
echo   RID : %RID%
echo   out : %DIST%\ToPlaySetup.exe
echo ===========================================================================

echo [1/6] Cleaning previous output...
if exist "%APPDIR%" rmdir /s /q "%APPDIR%"
if exist "%PAYLOAD%" rmdir /s /q "%PAYLOAD%"
mkdir "%APPDIR%" 2>nul
mkdir "%PAYLOAD%" 2>nul

echo [2/6] Ensuring ffmpeg is bundled (downloaded once, then cached)...
set "FFMPEG=%ROOT%\src\ToPlay.Host\tools\ffmpeg.exe"
if exist "%FFMPEG%" (
  echo   ffmpeg already cached -^> %FFMPEG%
) else if /I "%TOPLAY_NO_FFMPEG%"=="1" (
  echo   TOPLAY_NO_FFMPEG=1 set - skipping bundle; target PC will download it during install.
) else (
  echo   Fetching ffmpeg once so it can ship inside the installer...
  powershell -NoProfile -ExecutionPolicy Bypass -File "%ROOT%\scripts\get-ffmpeg.ps1"
  if not exist "%FFMPEG%" echo   WARNING: ffmpeg was not fetched; target PC will download it during install.
)

echo [3/6] Publishing ToPlay.Host (self-contained)...
"%DOTNET%" publish "src\ToPlay.Host\ToPlay.Host.csproj" -c Release -r %RID% %NOSRV% --self-contained true -o "%APPDIR%" --nologo
if errorlevel 1 goto :fail

echo [4/6] Publishing ToPlay.App -> ToPlay.exe (self-contained)...
"%DOTNET%" publish "src\ToPlay.App\ToPlay.App.csproj" -c Release -r %RID% %NOSRV% --self-contained true -o "%APPDIR%" --nologo
if errorlevel 1 goto :fail

rem ship the icon file too, so Start Menu / Desktop shortcuts can point at it
if exist "src\ToPlay.App\app.ico" copy /y "src\ToPlay.App\app.ico" "%APPDIR%\app.ico" >nul

if exist "%APPDIR%\tools\ffmpeg.exe" (
  echo   ffmpeg bundled into payload - target PC will NOT need to download it.
) else (
  echo   NOTE: ffmpeg is NOT bundled - target PC will download it during install.
)

echo [5/6] Packing payload zip...
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "Compress-Archive -Path (Join-Path '%APPDIR%' '*') -DestinationPath (Join-Path '%PAYLOAD%' 'app.zip') -Force"
if errorlevel 1 goto :fail
if not exist "%PAYLOAD%\app.zip" (echo Payload zip was not created. & goto :fail)

echo [6/6] Publishing installer to ToPlaySetup.exe (single-file)...

"%DOTNET%" publish "src\ToPlay.Installer\ToPlay.Installer.csproj" -c Release -r %RID% %NOSRV% ^
  --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true ^
  -o "%DIST%" --nologo
if errorlevel 1 goto :fail

rem  Publish this next to the .exe on the GitHub release. ToPlay's built-in
rem  "Check for updates" downloads it and refuses to install anything whose
rem  SHA-256 does not match, byte for byte.
echo   Writing SHA-256 checksum sidecar...
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$h = (Get-FileHash -Algorithm SHA256 -Path (Join-Path '%DIST%' 'ToPlaySetup.exe')).Hash.ToLower(); Set-Content -Path (Join-Path '%DIST%' 'ToPlaySetup.exe.sha256') -Value $h -NoNewline -Encoding ascii; Write-Host ('   SHA-256: ' + $h)"

echo(
echo === DONE ===================================================================
echo   Installer: %DIST%\ToPlaySetup.exe
echo   Checksum : %DIST%\ToPlaySetup.exe.sha256  (upload with the release)
echo   Share this single file; users double-click it to install ToPlay.
echo ===========================================================================

popd
endlocal
exit /b 0

:fail
echo(
echo *** BUILD FAILED ***
popd
endlocal
exit /b 1
