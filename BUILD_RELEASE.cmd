@echo off
setlocal EnableExtensions
cd /d "%~dp0"
if errorlevel 1 goto fail

echo.
echo =======================================================
echo   WindowPilot - Windows 11 Release Builder
 echo =======================================================
echo.

where dotnet >nul 2>&1
if errorlevel 1 (
  echo ERROR: .NET 10 SDK was not found.
  echo The SDK is only required on the development computer.
  echo https://dotnet.microsoft.com/download/dotnet/10.0
  goto fail
)

set "ROOT=%CD%"
set "DIST=%ROOT%\dist"
set "PUBLISH=%DIST%\publish"
set "PROJECT=%ROOT%\src\WindowPilot.App\WindowPilot.App.csproj"
set "PORTABLE=%DIST%\WindowPilot-Portable-win-x64.zip"
set /p APP_VERSION=<"%ROOT%\VERSION.txt"
if not defined APP_VERSION (
  echo ERROR: VERSION.txt is empty.
  goto fail
)

if not exist "%PROJECT%" (
  echo ERROR: Project file not found: %PROJECT%
  goto fail
)

if not exist "%DIST%" mkdir "%DIST%"
if exist "%PUBLISH%" rmdir /s /q "%PUBLISH%"
if exist "%PORTABLE%" del /q "%PORTABLE%"

echo [1/3] Publishing release with .NET Desktop runtime...
dotnet publish "%PROJECT%" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:PublishTrimmed=false -p:DebugType=none -p:DebugSymbols=false -p:Version=%APP_VERSION% -o "%PUBLISH%"
if errorlevel 1 goto fail
if not exist "%PUBLISH%\WindowPilot.App.exe" (
  echo ERROR: Published executable was not found.
  goto fail
)

echo.
echo Copying GitHub update configuration...
copy /y "%ROOT%\distribution\github-repository.txt" "%PUBLISH%\github-repository.txt" >nul
if errorlevel 1 goto fail

echo.
echo [2/3] Creating portable ZIP...
where tar.exe >nul 2>&1
if errorlevel 1 (
  echo NOTE: tar.exe is unavailable. dist\publish can still be run directly.
) else (
  pushd "%PUBLISH%"
  tar.exe -a -cf "%PORTABLE%" *
  if errorlevel 1 (
    popd
    goto fail
  )
  popd
  echo Portable ZIP: "%PORTABLE%"
)

echo.
echo [3/3] Creating installer EXE...
set "ISCC="
if exist "%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe" set "ISCC=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"
if not defined ISCC if exist "%ProgramFiles%\Inno Setup 6\ISCC.exe" set "ISCC=%ProgramFiles%\Inno Setup 6\ISCC.exe"
if not defined ISCC if exist "%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe" set "ISCC=%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe"
if not defined ISCC (
  where ISCC.exe >nul 2>&1
  if not errorlevel 1 set "ISCC=ISCC.exe"
)
if defined ISCC (
  "%ISCC%" "/DAppVersion=%APP_VERSION%" "/DSourceDir=%PUBLISH%" "/O%DIST%" "%ROOT%\distribution\WindowPilot.iss"
  if errorlevel 1 goto fail
  echo Installer: "%DIST%\WindowPilot-Setup-win-x64.exe"
  powershell -NoProfile -Command "(Get-FileHash -Algorithm SHA256 -LiteralPath '%DIST%\WindowPilot-Setup-win-x64.exe').Hash + '  WindowPilot-Setup-win-x64.exe' | Set-Content -Encoding ascii -LiteralPath '%DIST%\SHA256SUMS.txt'"
) else (
  echo NOTE: Inno Setup is not installed.
  echo To build the installer: https://jrsoftware.org/isinfo.php
  echo Run BUILD_RELEASE.cmd again after installation.
)

echo.
echo DONE. Output directory:
echo "%DIST%"
echo.
echo Note: An unsigned app may trigger a SmartScreen warning.
echo Do not disable Microsoft Defender.
echo.
pause
exit /b 0

:fail
echo.
echo Release build failed.
pause
exit /b 1
