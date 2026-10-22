[CmdletBinding()]
param([Parameter(Mandatory)][string]$HelperPath)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($env:GITHUB_ACTIONS -ne 'true') { throw 'Run elevated user-context checks on disposable CI runners.' }
$parent = Join-Path $env:ProgramData 'Scriblucent/SetupBridge'
$bridge = Join-Path $parent ([guid]::NewGuid().ToString('B'))
try {
    & $HelperPath prepare-bridge $bridge
    if ($LASTEXITCODE -ne 0) { throw 'Cannot prepare original-user test bridge.' }
    & "$bridge/SetupHelper.exe" probe-user "$bridge/user.ini"
    if ($LASTEXITCODE -ne 0) {
        if (Test-Path "$bridge/user.ini") { Get-Content "$bridge/user.ini" }
        throw 'Native original-user handoff failed.'
    }
    $state = [IO.File]::ReadAllText("$bridge/user.ini")
    if (!$state.Contains('Context=Unelevated')) { throw 'The original-user test did not obtain an unelevated context.' }
    Write-Host 'Passed: setup helper uses an unelevated account with read-only access to its executable.'
} finally {
    if (Test-Path -LiteralPath $bridge) {
        $resolved = (Resolve-Path -LiteralPath $bridge).Path
        if (!$resolved.StartsWith([IO.Path]::GetFullPath($parent) + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid user-context test cleanup path.' }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
