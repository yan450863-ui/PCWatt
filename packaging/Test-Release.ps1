param([string]$Directory = '')
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
if (!$Directory) { $Directory = Join-Path $taskRoot 'artifacts\releases\0.1.0' }
$Directory = [IO.Path]::GetFullPath($Directory)
$taskDiagnostics = Join-Path $taskRoot 'artifacts\diagnostics'
New-Item -ItemType Directory -Path $taskDiagnostics -Force | Out-Null
$taskUninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{2D48DE33-FB4A-4C21-89D2-93A600C89D91}_is1'
if (Test-Path -LiteralPath $taskUninstallKey) { throw 'Existing PC Watt installation detected; preserve it instead of running an isolated install test.' }
$taskRunKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$taskRunBefore = (Get-ItemProperty -LiteralPath $taskRunKey -ErrorAction SilentlyContinue).'PC Watt'
$taskSettings = Join-Path $env:LOCALAPPDATA 'PCWatt\settings.json'
$taskSettingsBefore = if (Test-Path -LiteralPath $taskSettings) { (Get-FileHash -LiteralPath $taskSettings).Hash } else { '' }
$taskId = [Guid]::NewGuid().ToString('N')
$taskInstallDirectory = Join-Path ([IO.Path]::GetTempPath()) ('PCWatt-InstallSmoke-' + $taskId)
$taskSetup = Join-Path ([IO.Path]::GetTempPath()) ('PCWatt-SetupSmoke-' + $taskId + '.exe')
# Ordinary fresh file creation, as with a browser download. No ACL/integrity changes.
[IO.File]::WriteAllBytes($taskSetup, [IO.File]::ReadAllBytes((Join-Path $Directory 'PCWatt-0.1.0-Setup-win-x64.exe')))
$taskUninstalled = $false
try {
    $taskInstallLog = Join-Path $taskDiagnostics 'installer-smoke-install.log'
    $taskProcess = Start-Process -FilePath $taskSetup -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/NOICONS','/TASKS=',('/DIR="' + $taskInstallDirectory + '"'),('/LOG="' + $taskInstallLog + '"')) -WindowStyle Hidden -Wait -PassThru
    if ($taskProcess.ExitCode -ne 0) { throw "Installer failed: $($taskProcess.ExitCode). See $taskInstallLog" }
    if (!(Test-Path -LiteralPath $taskUninstallKey)) { throw 'Per-user uninstall registration is missing.' }
    foreach ($taskName in @('PCWatt.App.exe','PCWatt.App.dll','PCWatt.Core.dll','PCWatt.Infrastructure.dll','LibreHardwareMonitorLib.dll','coreclr.dll','hostfxr.dll','e_sqlite3.dll')) {
        if ((Get-FileHash -LiteralPath (Join-Path $taskInstallDirectory $taskName)).Hash -ne (Get-FileHash -LiteralPath (Join-Path $Directory ('app\' + $taskName))).Hash) { throw "Installed file mismatch: $taskName" }
    }
    $taskReport = Join-Path $taskDiagnostics 'installer-smoke-app.json'
    $taskStart = [Diagnostics.ProcessStartInfo]::new((Join-Path $taskInstallDirectory 'PCWatt.App.exe'))
    $taskStart.UseShellExecute = $false; $taskStart.CreateNoWindow = $true
    $taskStart.Environment['DOTNET_ROOT'] = Join-Path $taskInstallDirectory 'no-system-dotnet'
    $taskStart.Environment['DOTNET_ROOT_X64'] = $taskStart.Environment['DOTNET_ROOT']
    $taskStart.Environment['PATH'] = [Environment]::GetFolderPath('System')
    $taskStart.ArgumentList.Add('--package-check'); $taskStart.ArgumentList.Add($taskReport)
    $taskApp = [Diagnostics.Process]::Start($taskStart)
    if (!$taskApp.WaitForExit(30000)) { throw 'Installed package check exceeded 30 seconds.' }
    if ($taskApp.ExitCode -ne 0) { throw "Installed app check failed: $($taskApp.ExitCode)" }
    $taskCheck = Get-Content -LiteralPath $taskReport -Raw | ConvertFrom-Json
    if (!$taskCheck.selfContained -or !$taskCheck.runtimeFromAppDirectory -or !$taskCheck.sqlite -or $taskCheck.userDataTouched -or $taskCheck.version -ne '0.1.0.0') { throw 'Self-contained runtime / SQLite / version check failed.' }
    $taskUninstallLog = Join-Path $taskDiagnostics 'installer-smoke-uninstall.log'
    $taskUninstall = Start-Process -FilePath (Join-Path $taskInstallDirectory 'unins000.exe') -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',('/LOG="' + $taskUninstallLog + '"')) -WindowStyle Hidden -Wait -PassThru
    if ($taskUninstall.ExitCode -ne 0 -or (Test-Path -LiteralPath $taskUninstallKey) -or (Test-Path -LiteralPath (Join-Path $taskInstallDirectory 'PCWatt.App.exe'))) { throw 'Uninstall did not finish cleanly.' }
    $taskUninstalled = $true
    if ((Get-ItemProperty -LiteralPath $taskRunKey -ErrorAction SilentlyContinue).'PC Watt' -ne $taskRunBefore) { throw 'Existing autostart setting changed.' }
    $taskSettingsAfter = if (Test-Path -LiteralPath $taskSettings) { (Get-FileHash -LiteralPath $taskSettings).Hash } else { '' }
    if ($taskSettingsAfter -ne $taskSettingsBefore) { throw 'Existing user settings changed.' }
    $taskResult = @{ installed = $true; nativeSqlite = $true; bundledRuntime = $true; uninstalled = $true; existingSettingsPreserved = $true; existingAutostartPreserved = $true }
    $taskResult | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $taskDiagnostics 'installer-smoke-result.json') -Encoding utf8
    $taskResult | ConvertTo-Json
} finally {
    if (!$taskUninstalled -and (Test-Path -LiteralPath (Join-Path $taskInstallDirectory 'unins000.exe'))) {
        Start-Process -FilePath (Join-Path $taskInstallDirectory 'unins000.exe') -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART') -WindowStyle Hidden -Wait | Out-Null
    }
    if (Test-Path -LiteralPath $taskSetup) { Remove-Item -LiteralPath $taskSetup }
}
