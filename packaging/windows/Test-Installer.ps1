[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][ValidateSet('x64','arm64')][string]$Architecture
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
# Lifecycle tests run only on disposable CI users; never replace a developer's installation or library.
if ($env:GITHUB_ACTIONS -ne 'true') { throw 'Run installer lifecycle tests on a disposable GitHub Actions runner.' }
$data = Join-Path $env:LOCALAPPDATA 'Scriblucent'
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{2768A225-1F0D-43A6-9AF9-C6A8F8CAAA72}_is1'
if ((Test-Path $data) -or (Test-Path $uninstallKey)) { throw 'The runner already contains Scriblucent user data or an installation.' }
$installer = (Resolve-Path "artifacts/installers/$Version/win-$Architecture/setup/*-Setup.exe").Path
$destination = Join-Path $env:RUNNER_TEMP 'Scriblucent setup Žinių biblioteka'
$logs = Join-Path $env:RUNNER_TEMP 'Scriblucent-setup-tests'
New-Item -ItemType Directory -Path $logs -Force | Out-Null
$script:sequence = 0

function Invoke-Setup([string]$File, [string]$Options = '', [bool]$ExpectSuccess = $true) {
    $script:sequence++
    $log = Join-Path $logs "setup-$script:sequence.log"
    $process = Start-Process -FilePath $File -ArgumentList "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LOG=`"$log`" $Options" -WindowStyle Hidden -PassThru
    if (!$process.WaitForExit(120000)) {
        Stop-Process -Id $process.Id -Force
        throw "Setup did not complete within two minutes: $File"
    }
    if (($process.ExitCode -eq 0) -ne $ExpectSuccess) {
        Get-Content $log
        throw "Unexpected setup exit code $($process.ExitCode) for $File"
    }
}

function Assert-Condition([bool]$Condition, [string]$Message) {
    if (!$Condition) { throw $Message }
}

function Wait-For([scriptblock]$Condition, [string]$Description) {
    $deadline = [DateTime]::UtcNow.AddSeconds(45)
    while ([DateTime]::UtcNow -lt $deadline) {
        try { if (& $Condition) { return } }
        catch [IO.IOException] { }
        Start-Sleep -Milliseconds 200
    }
    throw "Timed out waiting for $Description"
}

function Assert-DataPreserved {
    foreach ($entry in $script:dataHashes.GetEnumerator()) {
        Assert-Condition ((Get-FileHash -LiteralPath $entry.Key -Algorithm SHA256).Hash -eq $entry.Value) "User data changed: $($entry.Key)"
    }
}

Invoke-Setup $installer "/DIR=`"$destination`" /TASKS=`"`""
$startMenu = Join-Path ([Environment]::GetFolderPath('Programs')) 'Scriblucent.lnk'
$desktop = Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) 'Scriblucent.lnk'
Assert-Condition (Test-Path $startMenu) 'Start menu shortcut is missing.'
Assert-Condition (!(Test-Path $desktop)) 'Desktop shortcut should be opt-in.'
Assert-Condition (!(Test-Path 'HKCU:\Software\Classes\Scriblucent.Note')) 'File associations should be opt-in.'
Assert-Condition (Test-Path "$destination/AIWorker/coreclr.dll") 'Worker is not self-contained.'
$env:DOTNET_ROOT = Join-Path $env:RUNNER_TEMP 'no-global-dotnet'
& "$destination/AIWorker/Scriblucent.AI.Worker.exe" --self-test --backend cpu
if ($LASTEXITCODE -ne 0) { throw 'Installed CPU native backend failed.' }

# Exercise shell import at startup, then hand off a different format to the running instance.
$noteFile = Join-Path $logs 'Import Žinios.scribnote'
$mapFile = Join-Path $logs 'Import mind map.scribmap'
$flashFile = Join-Path $logs 'Import flashcards.scribflash'
$quizFile = Join-Path $logs 'Import quiz.scribquiz'
@{ FormatVersion=1; Document=@{Id=[guid]::NewGuid().ToString();Title='Installer import note';Content=''} } | ConvertTo-Json -Depth 5 | Set-Content $noteFile -Encoding utf8
@{ FormatVersion=1; Document=@{Id=[guid]::NewGuid().ToString();Title='Installer import map';Root=@{Id=[guid]::NewGuid().ToString();Text='Planning';Children=@()}} } | ConvertTo-Json -Depth 6 | Set-Content $mapFile -Encoding utf8
$flashManifest = @{ FormatVersion=1; Document=@{Id=[guid]::NewGuid().ToString();Title='Installer import flashcards';Cards=@(@{Id=[guid]::NewGuid().ToString();Question='Question';Answer='Answer'})} } | ConvertTo-Json -Depth 8
$archive = [IO.Compression.ZipFile]::Open($flashFile, [IO.Compression.ZipArchiveMode]::Create)
try {
    $writer = [IO.StreamWriter]::new($archive.CreateEntry('manifest.json').Open())
    try { $writer.Write($flashManifest) } finally { $writer.Dispose() }
} finally { $archive.Dispose() }
@{ FormatVersion=1; Document=@{Id=[guid]::NewGuid().ToString();Title='Installer import quiz';Questions=@(@{Id=[guid]::NewGuid().ToString();Type=3;Question='Question';PlainTextAnswer='Answer'})} } | ConvertTo-Json -Depth 8 | Set-Content $quizFile -Encoding utf8
$sourceHashes = @{}
foreach ($file in @($noteFile,$mapFile,$flashFile,$quizFile)) { $sourceHashes[$file] = (Get-FileHash $file).Hash }
$app = Start-Process -FilePath "$destination/Scriblucent.exe" -ArgumentList "--import `"$noteFile`"" -WindowStyle Hidden -PassThru
try {
    Assert-Condition ($app.WaitForInputIdle(30000) -and !$app.HasExited) 'Installed application failed to start.'
    Wait-For { (Test-Path "$data/notes.json") -and ([IO.File]::ReadAllText("$data/notes.json").Contains('Installer import note')) } 'startup note import'
    $handoff = Start-Process -FilePath "$destination/Scriblucent.exe" -ArgumentList "--import `"$mapFile`" `"$flashFile`" `"$quizFile`"" -WindowStyle Hidden -PassThru
    Assert-Condition ($handoff.WaitForExit(20000) -and $handoff.ExitCode -eq 0) 'Second launch did not hand off to the library owner.'
    Wait-For { (Test-Path "$data/mind-maps.json") -and ([IO.File]::ReadAllText("$data/mind-maps.json").Contains('Installer import map')) } 'running-instance mind map import'
    Wait-For { (Test-Path "$data/flashcard-sets.json") -and ([IO.File]::ReadAllText("$data/flashcard-sets.json").Contains('Installer import flashcards')) } 'queued flashcard import'
    Wait-For { (Test-Path "$data/quizzes.json") -and ([IO.File]::ReadAllText("$data/quizzes.json").Contains('Installer import quiz')) } 'queued quiz import'
    Invoke-Setup $installer '' $false
    Invoke-Setup "$destination/unins000.exe" '' $false
    Assert-Condition (!$app.HasExited) 'Setup terminated the running application.'
    Assert-Condition ($app.CloseMainWindow()) 'Could not request a graceful application close.'
    Assert-Condition ($app.WaitForExit(20000)) 'Application did not close gracefully.'
} finally {
    if (!$app.HasExited) { Stop-Process -Id $app.Id -Force }
}
foreach ($entry in $sourceHashes.GetEnumerator()) { Assert-Condition ((Get-FileHash $entry.Key).Hash -eq $entry.Value) 'Shell import modified an exported source file.' }

# Representative persistent media and model data must survive every subsequent installer operation.
New-Item -ItemType Directory -Path "$data/Models", "$data/NotesMedia", "$data/FlashcardMedia" -Force | Out-Null
[IO.File]::WriteAllBytes("$data/NotesMedia/test.png", [Convert]::FromBase64String('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a+1cAAAAASUVORK5CYII='))
[IO.File]::WriteAllBytes("$data/FlashcardMedia/test.wav", [Text.Encoding]::ASCII.GetBytes('RIFF-preservation-fixture'))
[IO.File]::WriteAllText("$data/Models/test.gguf", 'Model preservation fixture; not an inference model.')
$script:dataHashes = @{}
Get-ChildItem $data -Recurse -File | ForEach-Object { $script:dataHashes[$_.FullName] = (Get-FileHash $_.FullName -Algorithm SHA256).Hash }

# Reinstall with optional integrations, preserving a different application's existing default.
New-Item 'HKCU:\Software\Classes\.scribnote' -Force | Out-Null
Set-Item 'HKCU:\Software\Classes\.scribnote' -Value 'AnotherApplication.Note'
Invoke-Setup $installer '/TASKS="desktopicon,fileassociations"'
Assert-Condition (Test-Path $desktop) 'Requested desktop shortcut is missing.'
$associationCommand = (Get-Item 'HKCU:\Software\Classes\Scriblucent.Note\shell\open\command').GetValue('')
Assert-Condition ($associationCommand -ceq "`"$destination\Scriblucent.exe`" --import `"%1`"") 'Association command is not correctly quoted.'
Assert-Condition ((Get-Item 'HKCU:\Software\Classes\.scribnote').GetValue('') -eq 'AnotherApplication.Note') 'Setup replaced another application default.'
foreach ($type in @('Flashcards','MindMap','Quiz')) { Assert-Condition (Test-Path "HKCU:\Software\Classes\Scriblucent.$type\shell\open\command") "Missing association: $type" }
Assert-DataPreserved

# Simulate an installed stable version to exercise the new installer's semantic downgrade guard.
Set-ItemProperty $uninstallKey -Name DisplayVersion -Value '99.0.0'
try { Invoke-Setup $installer '' $false }
finally { Set-ItemProperty $uninstallKey -Name DisplayVersion -Value $Version }
Invoke-Setup $installer
Assert-Condition (Test-Path $desktop) 'Upgrade did not retain the desktop shortcut choice.'
Assert-Condition (Test-Path 'HKCU:\Software\Classes\Scriblucent.Note') 'Upgrade did not retain association choice.'
Invoke-Setup $installer '/TASKS=""'
Assert-Condition (!(Test-Path 'HKCU:\Software\Classes\Scriblucent.Note')) 'Disabling associations did not remove owned registrations.'
Assert-Condition (!(Test-Path $desktop)) 'Disabling the desktop shortcut did not remove it.'
Invoke-Setup "$destination/unins000.exe"
Assert-Condition (!(Test-Path $startMenu)) 'Uninstall retained its Start menu shortcut.'
Assert-Condition ((Get-Item 'HKCU:\Software\Classes\.scribnote').GetValue('') -eq 'AnotherApplication.Note') 'Uninstall removed another application registration.'
Assert-DataPreserved

# Verify upgrade from the actual published beta.2, with no changes to that public release.
$previous = Join-Path $logs 'previous'
New-Item -ItemType Directory -Path $previous | Out-Null
& gh release download v1.0.0-beta.2 --repo ncyxie/Scriblucent --pattern "*-win-$Architecture-Setup.exe*" --dir $previous
if ($LASTEXITCODE -ne 0) { throw 'Could not download the published upgrade baseline.' }
$oldInstaller = (Get-ChildItem $previous -Filter '*-Setup.exe').FullName
$expected = ([IO.File]::ReadAllText("$oldInstaller.sha256")).Split(' ')[0]
Assert-Condition ((Get-FileHash $oldInstaller -Algorithm SHA256).Hash.ToLowerInvariant() -eq $expected) 'Baseline checksum failed.'
Invoke-Setup $oldInstaller "/DIR=`"$destination`" /TASKS=`"`""
Invoke-Setup $installer
Assert-Condition ((Get-ItemProperty $uninstallKey).InstallLocation.TrimEnd('\') -eq $destination.TrimEnd('\')) 'Upgrade changed the installation directory.'
Assert-Condition (!(Test-Path "$destination/cs/PresentationFramework.resources.dll")) 'Upgrade retained obsolete language resources.'
Assert-DataPreserved
Invoke-Setup "$destination/unins000.exe"
Assert-DataPreserved
Write-Host 'Passed: install, native CPU loading, shell imports, instance handoff, running-app guards, reinstall, upgrade, downgrade rejection, optional integrations, and user-data preservation.'
Write-Host 'These are hosted-runner checks, not a clean-Windows virtual machine certification.'
