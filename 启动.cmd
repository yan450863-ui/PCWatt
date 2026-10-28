@echo off
setlocal
set "PCWATT_DIR=%~dp0artifacts\build-lean-backup\bin\PCWatt.App\release"
if not exist "%PCWATT_DIR%\Launch.cmd" goto build
call "%PCWATT_DIR%\Launch.cmd" %*
exit /b %errorlevel%

:build
where dotnet >nul 2>nul
if errorlevel 1 goto missing
pushd "%~dp0"
dotnet build PCWatt.sln -c Release --artifacts-path "%~dp0artifacts\build-lean-backup"
if errorlevel 1 goto failed
popd
call "%PCWATT_DIR%\Launch.cmd" %*
exit /b %errorlevel%

:failed
popd
:missing
echo PC Watt source launcher requires the .NET 10 SDK.
echo For normal use, download the EXE installer from:
echo https://github.com/yan450863-ui/PCWatt/releases
pause
exit /b 1
