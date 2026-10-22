[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('x64','arm64')][string]$Architecture,
    [Parameter(Mandatory)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$output = [IO.Path]::GetFullPath($OutputDirectory)
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
if (!(Test-Path -LiteralPath $vswhere)) { throw 'Visual Studio C++ tools are required to build the setup helper.' }
$installation = @(& $vswhere -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath) | Select-Object -First 1
if (!$installation) { throw 'Install the Visual Studio C++ desktop tools to build the setup helper.' }
$hostArchitecture = if ([Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture -eq 'Arm64') { 'arm64' } else { 'x64' }
$developerCommand = Join-Path $installation 'Common7/Tools/VsDevCmd.bat'
$source = Join-Path $PSScriptRoot 'SetupHelper.cpp'
$command = '""{0}" -no_logo -arch={1} -host_arch={2} && cl.exe /nologo /std:c++17 /MT /O1 /W4 /EHsc /utf-8 /D_WIN32_WINNT=0x0A00 /Fo"{3}\SetupHelper.obj" /Fe"{3}\SetupHelper.exe" "{4}" /link /DYNAMICBASE /NXCOMPAT"' -f $developerCommand,$Architecture,$hostArchitecture,$output,$source
& cmd.exe /d /s /c $command
if ($LASTEXITCODE -ne 0) { throw 'Native setup helper compilation failed.' }
