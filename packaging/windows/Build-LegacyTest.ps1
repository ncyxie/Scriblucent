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
$assetArchive = Join-Path $output 'branding-baseline.zip'
git archive --format=zip "--output=$assetArchive" $baseline -- packaging/windows/wizard-logo.bmp packaging/windows/wizard-logo-dark.bmp
if ($LASTEXITCODE -ne 0) { throw 'Cannot read the pinned installer branding.' }
$legacyAssets = Join-Path $output 'branding-baseline'
Expand-Archive -LiteralPath $assetArchive -DestinationPath $legacyAssets -Force
foreach ($name in @('VersionComparison.iss','ShortcutCleanup.iss','Scriblucent.iss')) {
    $source = @(git show "${baseline}:packaging/windows/$name") -join "`r`n"
    if ($LASTEXITCODE -ne 0) { throw 'Cannot read the pinned installer baseline.' }
    if ($name -eq 'Scriblucent.iss') {
        $source = $source.Replace('[Setup]', "[Setup]`r`nSourceDir=$PSScriptRoot")
        $source = $source.Replace('WizardSmallImageFile=wizard-logo.bmp', "WizardSmallImageFile=$legacyAssets/packaging/windows/wizard-logo.bmp")
        $source = $source.Replace('WizardSmallImageFileDynamicDark=wizard-logo-dark.bmp', "WizardSmallImageFileDynamicDark=$legacyAssets/packaging/windows/wizard-logo-dark.bmp")
    }
    [IO.File]::WriteAllText((Join-Path $output $name), $source, [Text.UTF8Encoding]::new($false))
}
Copy-Item -LiteralPath $PublishedApp -Destination "$output/payload/Scriblucent.exe"
& $CompilerPath '/Qp' '/DAppVersion=1.0.0-beta.3' '/DNumericVersion=1.0.0.0' "/DTargetArch=$Architecture" `
    "/DPublishDir=$output/payload" "/DOutputDir=$output" (Join-Path $output 'Scriblucent.iss')
if ($LASTEXITCODE -ne 0) { throw 'Per-user compatibility installer did not compile.' }
