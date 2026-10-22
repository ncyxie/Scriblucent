[CmdletBinding()]
param([Parameter(Mandatory)][string]$HelperPath, [Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $root) { throw 'Use a fresh native-helper test directory.' }
New-Item -ItemType Directory -Path "$root/payload/Žinių biblioteka" -Force | Out-Null
$file = Join-Path $root 'payload/Žinių biblioteka/notes.txt'
[IO.File]::WriteAllText($file, 'Native payload verification fixture', [Text.UTF8Encoding]::new($false))
$hash = (Get-FileHash -LiteralPath $file).Hash.ToLowerInvariant()
$manifest = Join-Path $root 'payload.sha256'
[IO.File]::WriteAllText($manifest, $hash + '|Žinių biblioteka\notes.txt', [Text.UTF8Encoding]::new($false))
& $HelperPath verify "$root/payload" $manifest
if ($LASTEXITCODE -ne 0) { throw 'Unicode payload verification failed.' }
[IO.File]::WriteAllText($file, 'Tampered fixture')
& $HelperPath verify "$root/payload" $manifest
if ($LASTEXITCODE -eq 0) { throw 'Payload verification accepted a modified file.' }
[IO.File]::WriteAllText($manifest, $hash + '|..\outside.txt', [Text.UTF8Encoding]::new($false))
& $HelperPath verify "$root/payload" $manifest
if ($LASTEXITCODE -eq 0) { throw 'Payload verification accepted path traversal.' }
[IO.File]::WriteAllText($manifest, '', [Text.UTF8Encoding]::new($false))
& $HelperPath verify "$root/payload" $manifest
if ($LASTEXITCODE -eq 0) { throw 'Payload verification accepted an empty manifest.' }
$global:LASTEXITCODE = 0
Write-Host 'Passed native helper checks: Unicode paths, SHA-256 verification, corruption, traversal and empty manifests.'
