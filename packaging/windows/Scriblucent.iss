#if VER != EncodeVer(6,7,3)
  #error Use Inno Setup 6.7.3 to build this installer
#endif
#ifndef AppVersion
  #error AppVersion must be supplied by Build-Installer.ps1
#endif
#ifndef PublishDir
  #error PublishDir must be supplied by Build-Installer.ps1
#endif
#ifndef OutputDir
  #error OutputDir must be supplied by Build-Installer.ps1
#endif
#if TargetArch == "x64"
  #define AllowedArch "x64compatible and not arm64"
  #define InstallArch "x64compatible"
#elif TargetArch == "arm64"
  #define AllowedArch "arm64"
  #define InstallArch "arm64"
#else
  #error Unsupported TargetArch
#endif

[Setup]
AppId={{2768A225-1F0D-43A6-9AF9-C6A8F8CAAA72}
AppName=Scriblucent
AppVersion={#AppVersion}
AppVerName=Scriblucent {#AppVersion}
AppPublisher=ncyxie
AppCopyright=Copyright (c) 2026 ncyxie
AppPublisherURL=https://github.com/ncyxie/Scriblucent
AppSupportURL=https://github.com/ncyxie/Scriblucent/issues
AppUpdatesURL=https://github.com/ncyxie/Scriblucent/releases
VersionInfoVersion={#NumericVersion}
DefaultDirName={code:InstallDirectory}
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed={#AllowedArch}
ArchitecturesInstallIn64BitMode={#InstallArch}
MinVersion=10.0.17763
WizardStyle=modern dynamic
WizardSmallImageFile=wizard-logo.bmp
WizardSmallImageFileDynamicDark=wizard-logo-dark.bmp
SetupIconFile=..\..\Scriblucent\logo.ico
UninstallDisplayIcon={app}\Scriblucent.exe
UninstallDisplayName=Scriblucent
LicenseFile=..\..\LICENSE
OutputDir={#OutputDir}
#ifdef RecoveryTests
OutputBaseFilename=RecoveryTestSetup
#else
OutputBaseFilename=Scriblucent-{#AppVersion}-win-{#TargetArch}-Setup
#endif
Compression=lzma2
SolidCompression=yes
CloseApplications=no
AppMutex=Scriblucent.Running
SetupMutex=Global\Scriblucent.Setup
ChangesAssociations=yes
UsePreviousTasks=yes
UsePreviousAppDir=no
AllowNetworkDrive=no
AllowUNCPath=no
ShowLanguageDialog=yes
LanguageDetectionMethod=uilanguage
RestartApplications=no
SetupLogging=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "lithuanian"; MessagesFile: "Languages/Lithuanian.isl"

#include "Messages.iss"

[Tasks]
Name: "desktopicon"; Description: "{cm:DesktopOption}"; GroupDescription: "{cm:ShortcutGroup}"; Flags: unchecked
Name: "fileassociations"; Description: "{cm:AssociationsOption}"; GroupDescription: "{cm:FileGroup}"; Flags: unchecked

[Files]
Source: "{#HelperPath}"; Flags: dontcopy
Source: "{#ManifestPath}"; Flags: dontcopy
Source: "{#ManifestPath}"; DestDir: "{app}"; DestName: "InstallManifest.sha256"; Flags: ignoreversion
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Scriblucent"; Filename: "{app}\Scriblucent.exe"
Name: "{autodesktop}\Scriblucent"; Filename: "{app}\Scriblucent.exe"; Tasks: desktopicon

[Run]
Filename: "{code:UserHelperPath}"; Parameters: "launch-user ""{code:UserResultPath}"""; Description: "{cm:OpenApplication}"; Flags: postinstall skipifsilent runasoriginaluser; Check: CanLaunchApplication; AfterInstall: ApplicationLaunchFinished

[Code]
#include "VersionComparison.iss"
#include "ShortcutCleanup.iss"
#include "RunningGuard.iss"

const
  UninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{2768A225-1F0D-43A6-9AF9-C6A8F8CAAA72}_is1';

var
  Helper, Manifest, BridgeDirectory, UserState, LegacyPath: String;
  BackupStarted, InstallationCommitted, ConversionIncomplete: Boolean;

function CreateGUID(var Guid: TGUID): Integer;
  external 'CoCreateGuid@ole32.dll stdcall';
function GuidToBuffer(var Guid: TGUID; Buffer: String; Size: Integer): Integer;
  external 'StringFromGUID2@ole32.dll stdcall';
#ifdef RecoveryTests
procedure TerminateTestSetup(Code: LongWord);
  external 'ExitProcess@kernel32.dll stdcall';

procedure CurInstallProgressChanged(Current, Maximum: Integer);
begin
  if (Current > Maximum div 3) and (ExpandConstant('{param:RecoveryTestFault|}') = 'crash') then
    TerminateTestSetup(77);
  if (Current > Maximum div 3) and (ExpandConstant('{param:RecoveryTestFault|}') = 'cancel') then Abort;
end;
#endif

function InstallDirectory(Param: String): String;
begin
  if not RegQueryStringValue(HKLM64, UninstallKey, 'InstallLocation', Result) then
    Result := ExpandConstant('{commonpf64}\Scriblucent');
end;

function RunHelper(Arguments: String): Boolean;
var
  Code: Integer;
begin
  Result := ExecAndLogOutput(Helper, Arguments, '', SW_HIDE, ewWaitUntilTerminated, Code, nil);
  Result := Result and (Code = 0);
  if not Result then Log('Setup helper failed: ' + Arguments + ' (exit ' + IntToStr(Code) + ')');
end;

function RunUserHelper(Operation: String): Boolean;
var
  Code: Integer;
begin
  Result := ExecAsOriginalUser(BridgeDirectory + '\SetupHelper.exe', Operation + ' "' + UserState + '"', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Result := Result and (Code = 0) and (GetIniString('Setup', 'Result', '', UserState) = '0');
  if not Result then begin
    Log('Original-user setup operation failed: ' + Operation + ' (exit ' + IntToStr(Code) + ')');
    Log(GetIniString('Setup', 'Error', 'No user-helper diagnostic available', UserState));
  end;
end;

function UserHelperPath(Param: String): String;
begin
  Result := BridgeDirectory + '\SetupHelper.exe';
end;

function UserResultPath(Param: String): String;
begin
  Result := UserState;
end;

function CanLaunchApplication: Boolean;
begin
  Result := InstallationCommitted and not ConversionIncomplete;
end;

procedure ApplicationLaunchFinished;
begin
  if GetIniString('Setup', 'Result', '', UserState) <> '0' then
    SuppressibleMsgBox(CustomMessage('LaunchFailed'), mbError, MB_OK, IDOK);
end;

procedure CheckVersion(Version: String);
begin
  if (Version <> '') and (CompareSemanticVersions(Version, '{#AppVersion}') > 0) then
    RaiseException(FmtMessage(CustomMessage('NewerVersion'), [Version]));
end;

procedure InitializeWizard;
var
  Version, Tasks, BridgeName: String;
  Note: TNewStaticText;
  BridgeId: TGUID;
begin
  ExtractTemporaryFile('SetupHelper.exe');
  ExtractTemporaryFile('payload.sha256');
  Helper := ExpandConstant('{tmp}\SetupHelper.exe');
  Manifest := ExpandConstant('{tmp}\payload.sha256');
  if not RunHelper('recover') then RaiseException(CustomMessage('RecoveryFailed'));
#ifdef RecoveryTests
  if ExpandConstant('{param:RecoveryTestFault|}') = 'after-recovery' then
    RaiseException('Recovery test stopped before beginning another installation');
#endif
  if RegQueryStringValue(HKLM64, UninstallKey, 'DisplayVersion', Version) then CheckVersion(Version);
  if CreateGUID(BridgeId) <> 0 then RaiseException(CustomMessage('PreparationFailed'));
  SetLength(BridgeName, 39);
  if GuidToBuffer(BridgeId, BridgeName, 39) <> 39 then RaiseException(CustomMessage('PreparationFailed'));
  SetLength(BridgeName, 38);
  BridgeDirectory := ExpandConstant('{commonappdata}\Scriblucent\SetupBridge\') + BridgeName;
  if not RunHelper('prepare-bridge "' + BridgeDirectory + '"') then RaiseException(CustomMessage('PreparationFailed'));
  UserState := BridgeDirectory + '\user.ini';
  if not RunUserHelper('probe-user') then RaiseException(CustomMessage('OriginalUserRequired'));
  LegacyPath := GetIniString('Setup', 'LegacyPath', '', UserState);
  CheckVersion(GetIniString('Setup', 'LegacyVersion', '', UserState));
  if (LegacyPath <> '') and not RegKeyExists(HKLM64, UninstallKey) and (ExpandConstant('{param:TASKS|unspecified}') = 'unspecified') then begin
    Tasks := GetIniString('Setup', 'Tasks', '', UserState);
    WizardSelectTasks(Tasks);
  end;
  Note := TNewStaticText.Create(WizardForm);
  Note.Parent := WizardForm.SelectTasksPage;
  Note.Left := WizardForm.TasksList.Left;
  Note.Top := WizardForm.TasksList.Top + WizardForm.TasksList.Height - ScaleY(62);
  Note.Width := WizardForm.TasksList.Width;
  Note.Height := ScaleY(62);
  Note.AutoSize := False;
  Note.WordWrap := True;
  Note.Caption := CustomMessage('OptionsNote');
  WizardForm.TasksList.Height := WizardForm.TasksList.Height - ScaleY(74);
end;

procedure CurPageChanged(Page: Integer);
begin
  if Page = wpFinished then begin
    if ConversionIncomplete then begin
      WizardForm.FinishedLabel.Caption := CustomMessage('ConversionIncomplete');
      WizardForm.RunList.Enabled := False;
      if WizardForm.RunList.Items.Count > 0 then WizardForm.RunList.Checked[0] := False;
    end else
      WizardForm.FinishedLabel.Caption := WizardForm.FinishedLabel.Caption + #13#10#13#10 + CustomMessage('RetainedData');
  end;
end;

function UpdateReadyMemo(Space, NewLine, UserInfo, DirectoryInfo, TypeInfo, ComponentsInfo, GroupInfo, TasksInfo: String): String;
begin
  Result := DirectoryInfo;
  if TasksInfo <> '' then Result := Result + NewLine + NewLine + TasksInfo;
  if LegacyPath <> '' then
    Result := Result + NewLine + NewLine + FmtMessage(CustomMessage('MigrationNote'), [LegacyPath]);
  Result := Result + NewLine + NewLine + CustomMessage('RetainedData');
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if ScriblucentRunning(ExpandConstant('{app}')) or ScriblucentRunning(LegacyPath) then begin
    Result := CustomMessage('CloseApplication'); exit;
  end;
  if not BackupStarted then begin
    if not RunHelper('begin "' + ExpandConstant('{app}') + '"') then begin
      Result := CustomMessage('BackupFailed'); exit;
    end;
    BackupStarted := True;
  end;
end;
procedure RemoveAssociation(Extension, ProgId: String);
var
  Existing, Command: String;
begin
  { Remove only registrations whose command still points at this installation. }
  if RegQueryStringValue(HKLM64, 'Software\Classes\' + ProgId + '\shell\open\command', '', Command) then
    if Command = '"' + ExpandConstant('{app}\Scriblucent.exe') + '" --import "%1"' then begin
      RegDeleteValue(HKLM64, 'Software\Classes\' + Extension + '\OpenWithProgids', ProgId);
      if RegQueryStringValue(HKLM64, 'Software\Classes\' + Extension, '', Existing) then
        if Existing = ProgId then RegDeleteValue(HKLM64, 'Software\Classes\' + Extension, '');
      RegDeleteKeyIncludingSubkeys(HKLM64, 'Software\Classes\' + ProgId);
      RegDeleteKeyIfEmpty(HKLM64, 'Software\Classes\' + Extension + '\OpenWithProgids');
      RegDeleteKeyIfEmpty(HKLM64, 'Software\Classes\' + Extension);
    end;
end;

procedure ConfigureAssociation(Extension, ProgId, Description: String; Enabled: Boolean);
var
  Key, Existing: String;
begin
  if not Enabled then begin
    RemoveAssociation(Extension, ProgId);
    exit;
  end;
  Key := 'Software\Classes\' + ProgId;
  if not RegWriteStringValue(HKLM64, Key, '', Description) or
    not RegWriteStringValue(HKLM64, Key + '\DefaultIcon', '', '"' + ExpandConstant('{app}\Scriblucent.exe') + '",0') or
    not RegWriteStringValue(HKLM64, Key + '\shell\open\command', '', '"' + ExpandConstant('{app}\Scriblucent.exe') + '" --import "%1"') or
    not RegWriteStringValue(HKLM64, 'Software\Classes\' + Extension + '\OpenWithProgids', ProgId, '') then
      RaiseException(CustomMessage('AssociationFailed'));
  if not RegQueryStringValue(HKLM64, 'Software\Classes\' + Extension, '', Existing) then
    if not RegWriteStringValue(HKLM64, 'Software\Classes\' + Extension, '', ProgId) then
      RaiseException(CustomMessage('AssociationFailed'));
end;

procedure ConfigureAssociations(Enabled: Boolean);
begin
  ConfigureAssociation('.scribnote', 'Scriblucent.Note', CustomMessage('NoteType'), Enabled);
  ConfigureAssociation('.scribflash', 'Scriblucent.Flashcards', CustomMessage('FlashcardsType'), Enabled);
  ConfigureAssociation('.scribmap', 'Scriblucent.MindMap', CustomMessage('MindMapType'), Enabled);
  ConfigureAssociation('.scribquiz', 'Scriblucent.Quiz', CustomMessage('QuizType'), Enabled);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then begin
    if not RunHelper('verify "' + ExpandConstant('{app}') + '" "' + Manifest + '"') then
      RaiseException(CustomMessage('VerificationFailed'));
    if not RunHelper('prune "' + ExpandConstant('{app}') + '" "' + Manifest + '"') then
      RaiseException(CustomMessage('CommitFailed'));
    ConfigureAssociations(WizardIsTaskSelected('fileassociations'));
    if not WizardIsTaskSelected('desktopicon') then
      RemoveOwnedShortcut(ExpandConstant('{autodesktop}\Scriblucent.lnk'), ExpandConstant('{app}\Scriblucent.exe'));
#ifdef RecoveryTests
    if ExpandConstant('{param:RecoveryTestFault|}') = 'failure' then
      RaiseException('Injected replacement failure');
#endif
    if not RunHelper('commit') then RaiseException(CustomMessage('CommitFailed'));
    InstallationCommitted := True;
    if (LegacyPath <> '') and not RunUserHelper('remove-user') then begin
      ConversionIncomplete := True;
      WizardForm.FinishedLabel.Caption := CustomMessage('ConversionIncomplete');
      SuppressibleMsgBox(CustomMessage('ConversionIncomplete'), mbError, MB_OK, IDOK);
    end;
  end;
end;

function GetCustomSetupExitCode: Integer;
begin
  if ConversionIncomplete then Result := 20 else Result := 0;
end;

procedure DeinitializeSetup;
begin
  if (Helper <> '') and not InstallationCommitted then
    if not RunHelper('restore') then
      SuppressibleMsgBox(CustomMessage('RecoveryFailed'), mbError, MB_OK, IDOK);
  if BridgeDirectory <> '' then DelTree(BridgeDirectory, True, True, True);
  RemoveDir(ExpandConstant('{commonappdata}\Scriblucent\SetupBridge'));
  RemoveDir(ExpandConstant('{commonappdata}\Scriblucent'));
end;

function InitializeUninstall: Boolean;
begin
  Result := not ScriblucentRunning(ExpandConstant('{app}'));
  if not Result then SuppressibleMsgBox(CustomMessage('CloseApplication'), mbError, MB_OK, IDOK);
end;

procedure CurUninstallStepChanged(CurStep: TUninstallStep);
begin
  if CurStep = usUninstall then ConfigureAssociations(False);
end;
