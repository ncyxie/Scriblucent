[CmdletBinding()]
param([Parameter(Mandatory)][string]$InstallationDirectory, [Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($env:GITHUB_ACTIONS -ne 'true') { throw 'Run account-isolation checks only on disposable CI runners.' }
$name = 'ScribSetup' + [guid]::NewGuid().ToString('N').Substring(0,8)
$password = ConvertTo-SecureString ([guid]::NewGuid().ToString('N') + 'aA1!') -AsPlainText -Force
$account = New-LocalUser -Name $name -Password $password -Description 'Disposable installer verification account'
$credential = [pscredential]::new("$env:COMPUTERNAME\$name", $password)
$shared = Join-Path ([Environment]::GetFolderPath('CommonDocuments')) $name
New-Item -ItemType Directory -Path $shared, $OutputDirectory -Force | Out-Null
$sid = $account.SID.Value
& icacls.exe $shared /grant "*${sid}:(OI)(CI)M" | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Cannot prepare standard-user test workspace.' }
$script = Join-Path $shared 'Verify-Installation.ps1'
$body = @'
param([string]$InstallationDirectory, [string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$app = $null
try {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if ($principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'The test account is elevated.' }
    # This account has never signed in through Explorer. Initialize its Windows known folder first.
    $localData = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData, [Environment+SpecialFolderOption]::Create)
    $profile = [Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)
    if (!$localData -or !$profile -or !$localData.StartsWith($profile.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Windows did not initialize a separate Local AppData folder for the test account.'
    }
    $denied = $false
    try { $stream = [IO.File]::Open((Join-Path $InstallationDirectory 'LICENSE'), [IO.FileMode]::Open, [IO.FileAccess]::Write, [IO.FileShare]::ReadWrite); $stream.Dispose() }
    catch [UnauthorizedAccessException] { $denied = $true }
    if (!$denied) { throw 'A standard account can modify installed program files.' }
    $env:DOTNET_ROOT = Join-Path $OutputDirectory 'no-global-dotnet'
    & "$InstallationDirectory/AIWorker/Scriblucent.AI.Worker.exe" --self-test --backend cpu > "$OutputDirectory/native.log" 2>&1
    if ($LASTEXITCODE -ne 0) { throw 'The AI CPU backend failed without administrator rights.' }
    $note = Join-Path $OutputDirectory 'Standard user import.scribnote'
    @{FormatVersion=1;Document=@{Id=[guid]::NewGuid().ToString();Title='Standard user library';Content=[Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes('{\rtf1\ansi Standard user note}'))}} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $note -Encoding utf8
    $app = Start-Process -FilePath "$InstallationDirectory/Scriblucent.exe" -ArgumentList "--import `"$note`"" -WindowStyle Hidden -PassThru
    if (!$app.WaitForInputIdle(45000)) { throw 'The standard-user application did not start.' }
    $library = Join-Path $localData 'Scriblucent/notes.json'
    $deadline = [DateTime]::UtcNow.AddSeconds(60)
    while ([DateTime]::UtcNow -lt $deadline) {
        if ((Test-Path -LiteralPath $library) -and [IO.File]::ReadAllText($library).Contains('Standard user library')) { break }
        if ($app.HasExited) { throw 'The standard-user application exited before import.' }
        Start-Sleep -Milliseconds 200
    }
    if (!(Test-Path -LiteralPath $library) -or ![IO.File]::ReadAllText($library).Contains('Standard user library')) { throw 'The app did not create a library in its own account.' }
    Add-Type @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class StandardUserWindows {
 public delegate bool EnumProc(IntPtr handle, IntPtr parameter);
 [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc callback, IntPtr parameter);
 [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr handle,StringBuilder text,int capacity);
 [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr handle,uint message,IntPtr wParam,IntPtr lParam);
}
"@
    $script:dashboard = [IntPtr]::Zero
    [StandardUserWindows+EnumProc]$find = {
        param($handle,$parameter)
        [uint32]$owner = 0
        [void][StandardUserWindows]::GetWindowThreadProcessId($handle,[ref]$owner)
        if ($owner -eq $app.Id) {
            $title = [Text.StringBuilder]::new(256)
            [void][StandardUserWindows]::GetWindowText($handle,$title,$title.Capacity)
            if ($title.ToString() -ceq 'Scriblucent') { $script:dashboard = $handle }
        }
        return $true
    }
    [void][StandardUserWindows]::EnumWindows($find,[IntPtr]::Zero)
    if ($script:dashboard -eq [IntPtr]::Zero) { throw 'Could not find the standard-user dashboard.' }
    [void][StandardUserWindows]::PostMessage($script:dashboard,0x10,[IntPtr]::Zero,[IntPtr]::Zero)
    if (!$app.WaitForExit(30000)) { throw 'The standard-user application did not close cleanly.' }
    [IO.File]::WriteAllText((Join-Path $OutputDirectory 'passed.txt'), 'Passed: non-administrator app launch, separate library, AI CPU backend and read-only installation directory.')
} catch {
    $_ | Out-String | Set-Content -LiteralPath (Join-Path $OutputDirectory 'failure.log')
    exit 1
} finally {
    if ($app -and !$app.HasExited) { Stop-Process -Id $app.Id -Force }
}
'@
[IO.File]::WriteAllText($script, $body, [Text.UTF8Encoding]::new($true))
try {
    $process = Start-Process -FilePath "$env:WINDIR/System32/WindowsPowerShell/v1.0/powershell.exe" -Credential $credential -LoadUserProfile `
        -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$script`" -InstallationDirectory `"$InstallationDirectory`" -OutputDirectory `"$shared`"" -WindowStyle Hidden -PassThru
    if (!$process.WaitForExit(180000)) { Stop-Process -Id $process.Id -Force; throw 'Standard-user verification timed out.' }
    foreach ($file in Get-ChildItem $shared -File | Where-Object Extension -eq '.log') { Copy-Item -LiteralPath $file.FullName -Destination $OutputDirectory }
    if ($process.ExitCode -ne 0 -or !(Test-Path "$shared/passed.txt")) {
        if (Test-Path "$OutputDirectory/failure.log") { Get-Content "$OutputDirectory/failure.log" }
        throw 'Standard-user installation verification failed.'
    }
    Get-Content "$shared/passed.txt"
} finally {
    Remove-LocalUser -Name $name
    $resolved = (Resolve-Path -LiteralPath $shared).Path
    $allowed = [IO.Path]::GetFullPath((Join-Path ([Environment]::GetFolderPath('CommonDocuments')) $name))
    if ($resolved -cne $allowed) { throw 'Unexpected account-test cleanup path.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
