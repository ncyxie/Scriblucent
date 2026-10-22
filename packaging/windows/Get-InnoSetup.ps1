[CmdletBinding()]
param([Parameter(Mandatory)][string]$Destination)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$version = '6.7.3'
$digest = '9c73c3bae7ed48d44112a0f48e66742c00090bdb5bef71d9d3c056c66e97b732'
$destinationPath = [IO.Path]::GetFullPath($Destination)
$compiler = Join-Path $destinationPath 'ISCC.exe'
if (Test-Path -LiteralPath $compiler) {
    $receipt = Join-Path $destinationPath 'compiler.sha256'
    if (!(Test-Path -LiteralPath $receipt) -or [IO.File]::ReadAllText($receipt) -cne $digest) { throw 'Unexpected installer compiler version.' }
    return $compiler
}
New-Item -ItemType Directory -Path $destinationPath -Force | Out-Null
$download = Join-Path $destinationPath 'inno-setup.exe'
Invoke-WebRequest -Uri "https://github.com/jrsoftware/issrc/releases/download/is-6_7_3/innosetup-$version.exe" -OutFile $download
if ((Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash.ToLowerInvariant() -cne $digest) { throw 'Installer compiler checksum failed.' }
$signature = Get-AuthenticodeSignature -LiteralPath $download
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'CN=Pyrsys B.V.') { throw 'Installer compiler signature failed.' }
$process = Start-Process -FilePath $download -ArgumentList "/CURRENTUSER /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /NOICONS /TASKS=`"`" /DIR=`"$destinationPath`"" -WindowStyle Hidden -Wait -PassThru
if ($process.ExitCode -ne 0 -or !(Test-Path -LiteralPath $compiler)) { throw 'Installer compiler preparation failed.' }
[IO.File]::WriteAllText((Join-Path $destinationPath 'compiler.sha256'), $digest)
return $compiler
