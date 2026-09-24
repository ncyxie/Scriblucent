param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$project = Join-Path $PSScriptRoot "Scriblucent\Scriblucent.csproj"
$outputRoot = Join-Path $PSScriptRoot "artifacts\portable"

Write-Host "==> Cleaning old portable artifacts" -ForegroundColor Cyan
if (Test-Path $outputRoot) {
    Remove-Item -Recurse -Force $outputRoot
}

$runtimeIdentifiers = @("win-x64", "win-arm64")

foreach ($runtimeIdentifier in $runtimeIdentifiers) {
    $output = Join-Path $outputRoot $runtimeIdentifier
    Write-Host "==> Publishing runtime: $runtimeIdentifier" -ForegroundColor Cyan
    dotnet publish $project -c $Configuration -r $runtimeIdentifier --self-contained false -o $output
}

Write-Host "==> Portable builds ready:" -ForegroundColor Green
Write-Host "    $outputRoot"
