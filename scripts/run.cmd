@echo off
setlocal EnableExtensions EnableDelayedExpansion
rem ---------------------------------------------------------------------------
rem  ToPlay dev launcher: builds and runs the GUI (ToPlay.exe) from source with
rem  the .NET 8 SDK. End users don't need this — they run the installer
rem  (dist\ToPlaySetup.exe) and launch ToPlay from the Start Menu / Desktop.
rem ---------------------------------------------------------------------------

rem ToPlay injects input into games (which ignore input from lower-privilege
rem processes), so it must run elevated. ToPlay.exe's manifest requests admin,
rem but a requireAdministrator exe can't be started from a non-elevated console,
rem so we elevate this script first and launch the built exe as our child.
net session >nul 2>&1
if %errorlevel%==0 goto :elevated

echo [ToPlay] Requesting Administrator rights (approve the UAC prompt)...
powershell -NoProfile -Command "try { Start-Process -FilePath '%~f0' -Verb RunAs -ErrorAction Stop; exit 0 } catch { exit 1 }"
if %errorlevel%==0 exit /b
echo [ToPlay] UAC declined. Cannot start elevated; games may ignore input.
echo          Re-run and approve the prompt for full control.
pause
exit /b 1

:elevated
rem --- locate the .NET 8 SDK (per-user install preferred) --------------------
set "DOTNET=%USERPROFILE%\.dotnet\dotnet.exe"
if not exist "%DOTNET%" set "DOTNET=dotnet"
if /I not "%DOTNET%"=="dotnet" set "DOTNET_ROOT=%USERPROFILE%\.dotnet"

set "ROOT=%~dp0.."
pushd "%ROOT%" || (echo Could not enter repo root & pause & exit /b 1)

echo [ToPlay] Building the control panel (ToPlay.exe)...
"%DOTNET%" build "src\ToPlay.App\ToPlay.App.csproj" -c Release --nologo
if errorlevel 1 (echo [ToPlay] Build failed. & popd & pause & exit /b 1)

rem Locate the freshly built exe. The project targets net8.0-windows, so the
rem output lands in bin\Release\net8.0-windows\ — but search for it rather than
rem hard-coding the framework folder, so a future TFM bump can't break launch.
rem NOTE: `for /r` with a literal (no-wildcard) name yields a phantom path for
rem every subdir regardless of existence, so the `if exist` guard is required to
rem pick the first file that actually exists.
set "APPEXE="
for /r "src\ToPlay.App\bin\Release" %%F in (ToPlay.exe) do if exist "%%F" if not defined APPEXE set "APPEXE=%%F"
if not defined APPEXE (echo [ToPlay] Could not find ToPlay.exe under src\ToPlay.App\bin\Release & popd & pause & exit /b 1)

echo [ToPlay] Opening the control panel...
start "" "%APPEXE%"

popd
endlocal
exit /b 0
