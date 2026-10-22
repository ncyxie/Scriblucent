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
$response = '/nologo /std:c++17 /MT /O1 /W4 /EHsc /utf-8 /D_WIN32_WINNT=0x0A00 /Fo"{0}\SetupHelper.obj" /Fe"{0}\SetupHelper.exe" "{1}" /link /DYNAMICBASE /NXCOMPAT' -f $output,$source
[IO.File]::WriteAllText((Join-Path $output 'helper.rsp'), $response, [Text.UTF8Encoding]::new($true))
$batch = @(
    '@echo off',
    'setlocal DisableDelayedExpansion',
    ('call "{0}" -no_logo -arch={1} -host_arch={2}' -f $developerCommand.Replace('%','%%'),$Architecture,$hostArchitecture),
    'if errorlevel 1 exit /b %errorlevel%',
    'cl.exe @"%~dp0helper.rsp"',
    'exit /b %errorlevel%'
) -join "`r`n"
$buildScript = Join-Path $output 'compile-helper.cmd'
[IO.File]::WriteAllText($buildScript, $batch, [Text.UTF8Encoding]::new($false))
& cmd.exe /d /c $buildScript
if ($LASTEXITCODE -ne 0) { throw 'Native setup helper compilation failed.' }
