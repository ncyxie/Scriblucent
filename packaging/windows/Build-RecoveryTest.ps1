[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$CompilerPath,
    [Parameter(Mandatory)][string]$HelperPath,
    [Parameter(Mandatory)][ValidateSet('x64','arm64')][string]$Architecture,
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$output = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path "$output/payload" -Force | Out-Null
$fixture = Join-Path $output 'payload/Scriblucent.exe'
[IO.File]::WriteAllText($fixture, 'Disposable recovery fixture; never distributed.')
$manifest = Join-Path $output 'payload.sha256'
[IO.File]::WriteAllText($manifest, (Get-FileHash $fixture).Hash.ToLowerInvariant() + '|Scriblucent.exe', [Text.UTF8Encoding]::new($false))
$numericVersion = $Version.Split('-')[0] + '.0'
& $CompilerPath '/Qp' "/DAppVersion=$Version" "/DNumericVersion=$numericVersion" "/DTargetArch=$Architecture" `
    "/DPublishDir=$output/payload" "/DOutputDir=$output" "/DHelperPath=$HelperPath" "/DManifestPath=$manifest" (Join-Path $PSScriptRoot 'Test-UpgradeRecovery.iss')
if ($LASTEXITCODE -ne 0) { throw 'Recovery verification installer did not compile.' }
