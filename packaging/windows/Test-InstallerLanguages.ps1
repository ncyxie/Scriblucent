[CmdletBinding()]
param([Parameter(Mandatory)][string]$EnglishCatalog)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
function Read-Catalog([string]$Path) {
    $result = @{}
    foreach ($line in [IO.File]::ReadAllLines($Path)) {
        if ($line -match '^([^;\[=]+)=(.*)$') {
            if ($result.ContainsKey($matches[1])) { throw "Duplicate language key: $($matches[1])" }
            $result[$matches[1]] = $matches[2]
        }
    }
    return $result
}
$english = Read-Catalog $EnglishCatalog
$lithuanian = Read-Catalog (Join-Path $PSScriptRoot 'Languages/Lithuanian.isl')
foreach ($key in $english.Keys) {
    if (!$lithuanian.ContainsKey($key)) { throw "Missing Lithuanian translation: $key" }
    $original = @([regex]::Matches($english[$key], '%[1-9][0-9]*|\[(?:name|name/ver|gb|mb)\]') | ForEach-Object Value | Sort-Object)
    $translated = @([regex]::Matches($lithuanian[$key], '%[1-9][0-9]*|\[(?:name|name/ver|gb|mb)\]') | ForEach-Object Value | Sort-Object)
    if (($original -join '|') -cne ($translated -join '|')) { throw "Language placeholders differ: $key" }
}
$custom = Read-Catalog (Join-Path $PSScriptRoot 'Messages.iss')
foreach ($key in @($custom.Keys)) {
    if ($key.StartsWith('english.')) {
        $translatedKey = $key.Replace('english.', 'lithuanian.')
        if (!$custom.ContainsKey($translatedKey) -or !$custom[$translatedKey]) { throw "Missing custom translation: $translatedKey" }
        $original = @([regex]::Matches($custom[$key], '%[1-9][0-9]*') | ForEach-Object Value | Sort-Object)
        $translated = @([regex]::Matches($custom[$translatedKey], '%[1-9][0-9]*') | ForEach-Object Value | Sort-Object)
        if (($original -join '|') -cne ($translated -join '|')) { throw "Custom language placeholders differ: $key" }
    }
}
Write-Host "Verified complete installer translations ($($english.Count) stock messages and options)."
