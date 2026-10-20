[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('x64', 'arm64')][string]$Architecture,
    [Parameter(Mandatory)][string]$Version,
    [string]$CompilerPath,
    [string]$VcRedistPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Keep tags, assembly versions, installer metadata and filenames in agreement.
if ($Version -cnotmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-((?:[0-9A-Za-z-]+\.)*[0-9A-Za-z-]+))?$') {
    throw 'Use a version such as 1.0.0 or 1.0.0-beta.2, without a v prefix.'
}
$versionParts = $Version.Split('-')[0].Split('.')
if (@($versionParts | Where-Object { [long]$_ -gt 65534 }).Count) {
    throw 'Version components must be at most 65534 for Windows version resources.'
}
if ($Version.Contains('-')) {
    foreach ($identifier in $Version.Substring($Version.IndexOf('-') + 1).Split('.')) {
        if ($identifier -match '^0[0-9]+$') { throw 'Numeric prerelease identifiers cannot have leading zeroes.' }
    }
}

$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$buildRoot = Join-Path $repository "artifacts/installers/$Version/win-$Architecture"
$publishDir = Join-Path $buildRoot 'app'
$outputDir = Join-Path $buildRoot 'setup'

# Clean only this architecture's generated staging directory.
if (Test-Path -LiteralPath $buildRoot) {
    $resolved = (Resolve-Path -LiteralPath $buildRoot).Path
    $allowed = [IO.Path]::GetFullPath((Join-Path $repository 'artifacts/installers')) + [IO.Path]::DirectorySeparatorChar
    if (!$resolved.StartsWith($allowed, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid installer staging path.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
New-Item -ItemType Directory -Path $publishDir, $outputDir -Force | Out-Null

if (!$CompilerPath) {
    $compiler = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($compiler) { $CompilerPath = $compiler.Source }
    else { $CompilerPath = Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6/ISCC.exe' }
}
if (!(Test-Path -LiteralPath $CompilerPath -PathType Leaf)) { throw 'Install Inno Setup 6 or pass -CompilerPath.' }

if (!$VcRedistPath) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
    if (!(Test-Path -LiteralPath $vswhere)) { throw 'Visual Studio C++ redistributable files are required.' }
    $installations = @(& $vswhere -products '*' -property installationPath)
    foreach ($installation in $installations) {
        $redistRoot = Join-Path $installation 'VC/Redist/MSVC'
        if (!(Test-Path -LiteralPath $redistRoot)) { continue }
        $candidates = Get-ChildItem -LiteralPath $redistRoot -Directory |
            Where-Object Name -Match '^\d+\.\d+\.\d+$' |
            Sort-Object { [version]$_.Name } -Descending
        foreach ($candidate in $candidates) {
            $archRoot = Join-Path $candidate.FullName $Architecture
            if (!(Test-Path -LiteralPath $archRoot)) { continue }
            if (@(Get-ChildItem -LiteralPath $archRoot -Directory -Filter '*.CRT').Count -and
                @(Get-ChildItem -LiteralPath $archRoot -Directory -Filter '*.OpenMP').Count) {
                $VcRedistPath = $archRoot
                break
            }
        }
        if ($VcRedistPath) { break }
    }
}
if (!$VcRedistPath) { throw "Install the Visual Studio C++ $Architecture redistributable components, or pass -VcRedistPath." }

& dotnet publish (Join-Path $repository 'Scriblucent/Scriblucent.csproj') -c Release -r "win-$Architecture" `
    --self-contained true -p:PublishSingleFile=false -p:PublishTrimmed=false "-p:Version=$Version" -o $publishDir
if ($LASTEXITCODE -ne 0) { throw 'Application publish failed.' }

# App-local deployment keeps setup per-user and offline, including the worker's OpenMP dependency.
$crt = @(Get-ChildItem -LiteralPath $VcRedistPath -Directory -Filter '*.CRT')
$openMp = @(Get-ChildItem -LiteralPath $VcRedistPath -Directory -Filter '*.OpenMP')
if ($crt.Count -ne 1 -or $openMp.Count -ne 1) { throw 'Expected one CRT and one OpenMP redistributable directory.' }
foreach ($destination in @($publishDir, (Join-Path $publishDir 'AIWorker'))) {
    Get-ChildItem -LiteralPath $crt[0].FullName -Filter '*.dll' | Copy-Item -Destination $destination -Force
    Get-ChildItem -LiteralPath $openMp[0].FullName -Filter '*.dll' | Copy-Item -Destination $destination -Force
}

$required = @(
    'Scriblucent.exe', 'Scriblucent.dll', 'Scriblucent.deps.json', 'Scriblucent.runtimeconfig.json',
    'coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll', 'PresentationFramework.dll',
    'libSkiaSharp.dll', 'libHarfBuzzSharp.dll', 'msvcp140.dll', 'vcruntime140.dll',
    'LICENSE', 'LICENSE-VirtualizingWrapPanel', 'THIRD-PARTY-NOTICES.md',
    'AIWorker/Scriblucent.AI.Worker.exe', 'AIWorker/Scriblucent.AI.Worker.dll',
    'AIWorker/Scriblucent.AI.Worker.deps.json', 'AIWorker/Scriblucent.AI.Worker.runtimeconfig.json',
    'AIWorker/coreclr.dll', 'AIWorker/hostfxr.dll', 'AIWorker/hostpolicy.dll',
    'AIWorker/msvcp140.dll', 'AIWorker/vcruntime140.dll', 'AIWorker/vcomp140.dll'
)
if ($Architecture -eq 'x64') {
    $required += 'AIWorker/vcruntime140_1.dll', 'AIWorker/runtimes/win-x64/native/vulkan/llama.dll'
} else {
    $required += 'AIWorker/runtimes/win-arm64/native/llama.dll'
    if (Test-Path (Join-Path $publishDir 'AIWorker/runtimes/win-x64')) { throw 'ARM64 package contains x64 worker natives.' }
}
foreach ($relative in $required) {
    if (!(Test-Path -LiteralPath (Join-Path $publishDir $relative) -PathType Leaf)) { throw "Missing packaged dependency: $relative" }
}
foreach ($relative in @('Scriblucent.runtimeconfig.json', 'AIWorker/Scriblucent.AI.Worker.runtimeconfig.json')) {
    $config = Get-Content -LiteralPath (Join-Path $publishDir $relative) -Raw | ConvertFrom-Json
    if (!$config.runtimeOptions.PSObject.Properties['includedFrameworks'] -or
        $config.runtimeOptions.PSObject.Properties['framework'] -or $config.runtimeOptions.PSObject.Properties['frameworks']) {
        throw "Package is not self-contained: $relative"
    }
}

$packageRootOutput = & dotnet nuget locals global-packages --list --force-english-output
if ($LASTEXITCODE -ne 0) { throw 'Cannot locate runtime license files.' }
$packageRoot = ($packageRootOutput -replace '^global-packages:\s*', '').Trim()
$appRuntime = Get-Content -LiteralPath (Join-Path $publishDir 'Scriblucent.runtimeconfig.json') -Raw | ConvertFrom-Json
$licensesDir = Join-Path $publishDir 'Licenses'
New-Item -ItemType Directory -Path $licensesDir -Force | Out-Null
foreach ($framework in $appRuntime.runtimeOptions.includedFrameworks) {
    $package = Join-Path $packageRoot "$($framework.name.ToLowerInvariant()).runtime.win-$Architecture/$($framework.version)"
    $license = @(Get-ChildItem -LiteralPath $package -File | Where-Object Name -Match '^LICENSE(?:\.TXT)?$')
    if ($license.Count -ne 1) { throw "Missing runtime license: $($framework.name)" }
    Copy-Item -LiteralPath $license[0].FullName -Destination (Join-Path $licensesDir "$($framework.name)-LICENSE.txt")
    $notice = Join-Path $package 'THIRD-PARTY-NOTICES.TXT'
    if (Test-Path -LiteralPath $notice) {
        Copy-Item -LiteralPath $notice -Destination (Join-Path $licensesDir "$($framework.name)-THIRD-PARTY-NOTICES.txt")
    }
}

# Verify native architecture, including redistributables, before creating a distributable installer.
$expectedMachine = if ($Architecture -eq 'x64') { 0x8664 } else { 0xAA64 }
foreach ($relative in @('Scriblucent.exe', 'coreclr.dll', 'libSkiaSharp.dll', 'AIWorker/Scriblucent.AI.Worker.exe', 'AIWorker/coreclr.dll', 'AIWorker/msvcp140.dll', 'AIWorker/vcomp140.dll')) {
    $bytes = [IO.File]::ReadAllBytes((Join-Path $publishDir $relative))
    $peOffset = [BitConverter]::ToInt32($bytes, 0x3c)
    if ([BitConverter]::ToUInt16($bytes, $peOffset + 4) -ne $expectedMachine) { throw "Incorrect architecture: $relative" }
}
$appInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $publishDir 'Scriblucent.dll'))
$actualVersion = $appInfo.ProductVersion.Split('+')[0]
if ($actualVersion -cne $Version) { throw "Packaged app version $actualVersion does not match $Version." }
if ($appInfo.CompanyName -cne 'ncyxie') { throw 'The packaged app has incorrect publisher metadata.' }
$unwanted = @(Get-ChildItem -LiteralPath $publishDir -Recurse -File | Where-Object {
    $_.Extension -in '.pdb', '.gguf', '.db', '.ps1', '.cs', '.csproj' -or
    $_.Name -match '^(settings\.json|appsettings\.json|\.env)$'
})
if ($unwanted.Count) { throw "Unexpected files in installer: $($unwanted.Name -join ', ')" }

& $CompilerPath "/DAppVersion=$Version" "/DNumericVersion=$($versionParts -join '.').0" `
    "/DTargetArch=$Architecture" "/DPublishDir=$publishDir" "/DOutputDir=$outputDir" (Join-Path $PSScriptRoot 'Scriblucent.iss')
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
$installer = Join-Path $outputDir "Scriblucent-$Version-win-$Architecture-Setup.exe"
if (!(Test-Path -LiteralPath $installer)) { throw 'Installer output is missing.' }
$setupInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($installer)
# Inno Setup reserves 60 characters for this resource and pads the value with spaces.
if ($setupInfo.CompanyName.Trim() -cne 'ncyxie') { throw 'The installer has incorrect publisher metadata.' }
$hash = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $([IO.Path]::GetFileName($installer))" | Set-Content -LiteralPath "$installer.sha256" -Encoding ascii
Write-Host "Installer ready: $installer"
