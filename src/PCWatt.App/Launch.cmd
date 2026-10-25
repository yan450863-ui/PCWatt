@echo off
setlocal
set "PCWATT_APP_DIRECTORY=%~dp0"
set "PCWATT_DOTNET_HOST=%ProgramFiles%\dotnet\dotnet.exe"
set "PCWATT_START_ARGUMENTS="
if /i "%~1"=="--background" set "PCWATT_START_ARGUMENTS= --background"
if not exist "%PCWATT_APP_DIRECTORY%PCWatt.App.dll" goto missing
if exist "%PCWATT_APP_DIRECTORY%coreclr.dll" goto standalone
if not exist "%PCWATT_DOTNET_HOST%" goto runtime

"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoProfile -NonInteractive -WindowStyle Hidden -Command "$ErrorActionPreference = 'Stop'; $taskStart = New-Object System.Diagnostics.ProcessStartInfo; $taskStart.FileName = $env:PCWATT_DOTNET_HOST; $taskStart.Arguments = [char]34 + (Join-Path $env:PCWATT_APP_DIRECTORY 'PCWatt.App.dll') + [char]34 + $env:PCWATT_START_ARGUMENTS; $taskStart.WorkingDirectory = $env:PCWATT_APP_DIRECTORY; $taskStart.UseShellExecute = $false; $taskStart.CreateNoWindow = $true; [void][System.Diagnostics.Process]::Start($taskStart)"
if errorlevel 1 goto failed
exit /b 0

:standalone
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoProfile -NonInteractive -WindowStyle Hidden -Command "$ErrorActionPreference = 'Stop'; $taskStart = New-Object System.Diagnostics.ProcessStartInfo; $taskStart.FileName = Join-Path $env:PCWATT_APP_DIRECTORY 'PCWatt.App.exe'; $taskStart.Arguments = $env:PCWATT_START_ARGUMENTS; $taskStart.WorkingDirectory = $env:PCWATT_APP_DIRECTORY; $taskStart.UseShellExecute = $false; $taskStart.CreateNoWindow = $true; [void][System.Diagnostics.Process]::Start($taskStart)"
if errorlevel 1 goto failed
exit /b 0

:missing
echo PC Watt application files were not found. Keep the complete package together.
goto failed
:runtime
echo Install the x64 .NET 10 Windows Desktop Runtime before starting PC Watt.
:failed
pause
exit /b 1
