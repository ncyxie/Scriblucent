[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PublishDirectory,
    [Parameter(Mandatory)][ValidateSet('x64','arm64')][string]$Architecture,
    [switch]$PruneUnusedRedistributables
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = (Resolve-Path -LiteralPath $PublishDirectory).Path
$expectedMachine = if ($Architecture -eq 'x64') { 0x8664 } else { 0xAA64 }

function Get-RawOffset([byte[]]$Bytes, [int]$Pe, [uint32]$Rva) {
    $sections = [BitConverter]::ToUInt16($Bytes, $Pe + 6)
    $table = $Pe + 24 + [BitConverter]::ToUInt16($Bytes, $Pe + 20)
    for ($i = 0; $i -lt $sections; $i++) {
        $entry = $table + 40 * $i
        $start = [BitConverter]::ToUInt32($Bytes, $entry + 12)
        $length = [Math]::Max([BitConverter]::ToUInt32($Bytes, $entry + 8), [BitConverter]::ToUInt32($Bytes, $entry + 16))
        if ($Rva -ge $start -and $Rva -lt $start + $length) {
            return [int]([BitConverter]::ToUInt32($Bytes, $entry + 20) + $Rva - $start)
        }
    }
    throw "Unmapped PE address: $Rva"
}

function Read-AsciiZ([byte[]]$Bytes, [int]$Offset) {
    $end = $Offset
    while ($Bytes[$end] -ne 0) { $end++ }
    return [Text.Encoding]::ASCII.GetString($Bytes, $Offset, $end - $Offset)
}

$checked = 0
$importGraph = @{}
$crtPattern = '^(msvcp140(?:_[0-9]+|_atomic_wait|_codecvt_ids)?|vcruntime140(?:_1)?|concrt140|vcomp140)\.dll$'
foreach ($file in Get-ChildItem -LiteralPath $root -Recurse -File | Where-Object Extension -In '.dll','.exe') {
    $bytes = [IO.File]::ReadAllBytes($file.FullName)
    if ($bytes.Length -lt 64 -or $bytes[0] -ne 77 -or $bytes[1] -ne 90) { throw "Invalid Windows binary: $($file.FullName)" }
    $pe = [BitConverter]::ToInt32($bytes, 60)
    $machine = [BitConverter]::ToUInt16($bytes, $pe + 4)
    $magic = [BitConverter]::ToUInt16($bytes, $pe + 24)
    $directories = $pe + 24 + $(if ($magic -eq 0x20b) {112} else {96})
    $cliRva = [BitConverter]::ToUInt32($bytes, $directories + 14 * 8)
    $ilOnly = $false
    if ($cliRva) {
        $cliOffset = Get-RawOffset $bytes $pe $cliRva
        $ilOnly = ([BitConverter]::ToUInt32($bytes, $cliOffset + 16) -band 1) -ne 0
    }
    if ($machine -ne $expectedMachine -and !($machine -eq 0x14c -and $ilOnly)) {
        throw "Incorrect PE architecture: $($file.FullName)"
    }
    # Pure managed assemblies use the CLR loader; check native and mixed/R2R images below.
    if ($ilOnly -and $machine -eq 0x14c) { continue }
    $processRoot = if ($file.FullName.StartsWith((Join-Path $root 'AIWorker') + '\', [StringComparison]::OrdinalIgnoreCase)) { Join-Path $root 'AIWorker' } else { $root }
    $imports = @()
    foreach ($directoryIndex in @(1,13)) {
        $rva = [BitConverter]::ToUInt32($bytes, $directories + $directoryIndex * 8)
        if (!$rva) { continue }
        $offset = Get-RawOffset $bytes $pe $rva
        $stride = if ($directoryIndex -eq 1) {20} else {32}
        $nameField = if ($directoryIndex -eq 1) {12} else {4}
        while ([BitConverter]::ToUInt32($bytes, $offset + $nameField)) {
            $nameRva = [BitConverter]::ToUInt32($bytes, $offset + $nameField)
            $imports += Read-AsciiZ $bytes (Get-RawOffset $bytes $pe $nameRva)
            $offset += $stride
        }
    }
    foreach ($dependency in $imports | Sort-Object -Unique) {
        if ((Test-Path -LiteralPath (Join-Path $file.DirectoryName $dependency)) -or
            (Test-Path -LiteralPath (Join-Path $processRoot $dependency))) { continue }
        if ($dependency -match '^(api-ms-win-|ext-ms-win-)') { continue }
        # LLamaSharp explicitly preloads the detected CPU variant before the Vulkan ggml image.
        if ($dependency -eq 'ggml-cpu.dll' -and $file.FullName -match '[\\/]vulkan[\\/]') {
            foreach ($variant in @('noavx','avx','avx2','avx512')) {
                if (!(Test-Path -LiteralPath (Join-Path $processRoot "runtimes/win-x64/native/$variant/ggml-cpu.dll"))) {
                    throw "Missing preloaded CPU backend: $variant"
                }
            }
            continue
        }
        if ($dependency -eq 'vulkan-1.dll' -and $file.FullName -match '[\\/]vulkan[\\/]') { continue }
        # A developer's globally installed VC runtime must never conceal a missing packaged DLL.
        if ($dependency -match '^(msvcp|vcruntime|concrt|vcomp)\d') {
            throw "Missing app-local dependency $dependency imported by $($file.FullName)"
        }
        $systemFile = Join-Path ([Environment]::SystemDirectory) $dependency
        if (!(Test-Path -LiteralPath $systemFile)) { throw "Unresolved native dependency $dependency imported by $($file.FullName)" }
        $info = [Diagnostics.FileVersionInfo]::GetVersionInfo($systemFile)
        if ($info.CompanyName -notmatch 'Microsoft' -or $info.ProductName -notmatch 'Windows') {
            throw "Non-Windows global dependency $dependency imported by $($file.FullName)"
        }
    }
    $importGraph[$file.FullName] = @($imports)
    $checked++
}
if ($PruneUnusedRedistributables) {
    $removed = 0
    foreach ($processRoot in @($root, (Join-Path $root 'AIWorker'))) {
        $candidates = @{}
        Get-ChildItem -LiteralPath $processRoot -File | Where-Object Name -Match $crtPattern | ForEach-Object { $candidates[$_.Name] = $_.FullName }
        $required = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        $pending = [Collections.Generic.Queue[string]]::new()
        foreach ($entry in $importGraph.GetEnumerator()) {
            $belongs = $entry.Key.StartsWith($processRoot + '\', [StringComparison]::OrdinalIgnoreCase)
            if ($processRoot -eq $root -and $entry.Key.StartsWith((Join-Path $root 'AIWorker') + '\', [StringComparison]::OrdinalIgnoreCase)) { $belongs = $false }
            if (!$belongs -or $candidates.ContainsValue($entry.Key)) { continue }
            foreach ($dependency in $entry.Value) { if ($required.Add($dependency)) { $pending.Enqueue($dependency) } }
        }
        while ($pending.Count) {
            $name = $pending.Dequeue()
            if (!$candidates.ContainsKey($name)) { continue }
            foreach ($dependency in $importGraph[$candidates[$name]]) {
                if ($required.Add($dependency)) { $pending.Enqueue($dependency) }
            }
        }
        foreach ($entry in $candidates.GetEnumerator()) {
            if (!$required.Contains($entry.Key)) {
                # Only this explicit VC redistributable set inside the verified publish root is eligible.
                Remove-Item -LiteralPath $entry.Value
                $removed++
            }
        }
    }
    Write-Host "Removed $removed unreferenced app-local VC redistributable copies."
}
Write-Host "Verified architecture and native imports for $checked binaries ($Architecture). Vulkan requires a compatible installed graphics driver."
