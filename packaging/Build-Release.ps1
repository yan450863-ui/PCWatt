param(
    [string]$Compiler = '',
    [string]$OutputDirectory = ''
)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
[xml]$taskProps = Get-Content -LiteralPath (Join-Path $taskRoot 'Directory.Build.props')
$taskVersion = [string]$taskProps.Project.PropertyGroup.Version
if ($taskVersion -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid release version.' }
if (!$OutputDirectory) { $OutputDirectory = Join-Path $taskRoot "artifacts\releases\$taskVersion" }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $OutputDirectory) { throw "Preserve existing release: $OutputDirectory" }
if (!$Compiler) { $Compiler = Join-Path $taskRoot 'artifacts\tools\inno-7.1.0\ISCC.exe' }
if (!(Test-Path -LiteralPath $Compiler)) { throw 'Inno Setup 7.1.0 compiler required. See packaging/README.md.' }
New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
$taskPayload = Join-Path $OutputDirectory 'app'
Push-Location $taskRoot
try {
    & dotnet publish src/PCWatt.App/PCWatt.App.csproj -c Release -r win-x64 --self-contained true -p:RuntimeFrameworkVersion=10.0.12 -p:PublishTrimmed=false -p:PublishSingleFile=false -p:DebugSymbols=false -p:DebugType=None -o $taskPayload
    if ($LASTEXITCODE -ne 0) { throw 'Application publish failed.' }
    Copy-Item -LiteralPath 'docs\INSTALLATION.txt' -Destination (Join-Path $taskPayload '使用说明.txt')
    & $Compiler ("/DAppVersion=$taskVersion") ("/DPayloadDir=$taskPayload") ("/DReleaseDir=$OutputDirectory") 'packaging\PCWatt.iss'
    if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
    $taskZip = Join-Path $OutputDirectory "PCWatt-$taskVersion-win-x64.zip"
    Compress-Archive -Path (Join-Path $taskPayload '*') -DestinationPath $taskZip -CompressionLevel Optimal
    $taskFiles = Get-ChildItem -LiteralPath $OutputDirectory -File | Where-Object Extension -In '.exe','.zip'
    $taskHashes = foreach ($taskFile in $taskFiles) { (Get-FileHash -LiteralPath $taskFile.FullName -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + $taskFile.Name }
    [IO.File]::WriteAllLines((Join-Path $OutputDirectory 'SHA256SUMS.txt'), $taskHashes, [Text.UTF8Encoding]::new($false))
    Copy-Item -LiteralPath 'docs\INSTALLATION.txt' -Destination (Join-Path $OutputDirectory '使用说明.txt')
    Write-Output "Release built: $OutputDirectory"
} finally { Pop-Location }
