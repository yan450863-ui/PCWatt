@echo off
setlocal
set "PCWATT_DIR=%~dp0src\PCWatt.App\bin\Release\net10.0-windows"
if not exist "%PCWATT_DIR%\Launch.cmd" goto build
call "%PCWATT_DIR%\Launch.cmd" %*
exit /b %errorlevel%

:build
where dotnet >nul 2>nul
if errorlevel 1 goto missing
pushd "%~dp0"
dotnet build PCWatt.sln -c Release
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
