[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$CompilerPath,
    [Parameter(Mandatory)][string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$resourceImage = Join-Path (Split-Path ([IO.Path]::GetFullPath($CompilerPath))) 'SetupCustomStyle.e32'
# The style layout belongs to the same pinned compiler as Scriblucent.iss.
if ((Get-FileHash -LiteralPath $resourceImage -Algorithm SHA256).Hash -cne '9675A2A5C78C66B691CC80270031DA42BEBAD5725FBF60853CB4E5F90251BD5E') {
    throw 'Unexpected Inno Setup control-style resources.'
}
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
if (!('Scriblucent.Packaging.ControlStylePalette' -as [type])) {
    Add-Type -Path (Join-Path $PSScriptRoot 'ControlStylePalette.cs')
}
[Scriblucent.Packaging.ControlStylePalette]::Build($resourceImage, [IO.Path]::GetFullPath($OutputDirectory))
Write-Host 'Built light and dark installer control palettes.'
