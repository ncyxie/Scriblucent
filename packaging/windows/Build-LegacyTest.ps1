[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$CompilerPath,
    [Parameter(Mandatory)][ValidateSet('x64','arm64')][string]$Architecture,
    [Parameter(Mandatory)][string]$PublishedApp,
    [Parameter(Mandatory)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$baseline = '838330dc66feb6048e55bdee39cf8d489791bb17'
$output = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path "$output/payload" -Force | Out-Null
# Compile the actual previous per-user setup code with a small representative payload.
git cat-file -e "${baseline}:packaging/windows/Scriblucent.iss" 2>$null
if ($LASTEXITCODE -ne 0) {
    git fetch --no-tags --depth=1 origin $baseline
    if ($LASTEXITCODE -ne 0) { throw 'Cannot fetch the pinned per-user installer baseline.' }
}
foreach ($name in @('VersionComparison.iss','ShortcutCleanup.iss','Scriblucent.iss')) {
    $source = @(git show "${baseline}:packaging/windows/$name") -join "`r`n"
    if ($LASTEXITCODE -ne 0) { throw 'Cannot read the pinned installer baseline.' }
    if ($name -eq 'Scriblucent.iss') { $source = $source.Replace('[Setup]', "[Setup]`r`nSourceDir=$PSScriptRoot") }
    [IO.File]::WriteAllText((Join-Path $output $name), $source, [Text.UTF8Encoding]::new($false))
}
Copy-Item -LiteralPath $PublishedApp -Destination "$output/payload/Scriblucent.exe"
& $CompilerPath '/Qp' '/DAppVersion=1.0.0-beta.3' '/DNumericVersion=1.0.0.0' "/DTargetArch=$Architecture" `
    "/DPublishDir=$output/payload" "/DOutputDir=$output" (Join-Path $output 'Scriblucent.iss')
if ($LASTEXITCODE -ne 0) { throw 'Per-user compatibility installer did not compile.' }
