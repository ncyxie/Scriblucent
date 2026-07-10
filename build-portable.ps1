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

$profiles = @("Portable-win-x64", "Portable-win-arm64")

foreach ($profile in $profiles) {
    Write-Host "==> Publishing profile: $profile" -ForegroundColor Cyan
    dotnet publish $project -c $Configuration /p:PublishProfile=$profile
}

Write-Host "==> Portable builds ready:" -ForegroundColor Green
Write-Host "    $outputRoot"
