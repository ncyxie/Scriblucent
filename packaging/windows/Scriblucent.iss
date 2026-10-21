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
DefaultDirName={localappdata}\Programs\Scriblucent
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
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
OutputBaseFilename=Scriblucent-{#AppVersion}-win-{#TargetArch}-Setup
Compression=lzma2
SolidCompression=yes
CloseApplications=yes
AppMutex=Scriblucent.Running
SetupMutex=Scriblucent.Setup
ChangesAssociations=yes
UsePreviousTasks=yes
RestartApplications=no
SetupLogging=yes

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked
Name: "fileassociations"; Description: "Offer Scriblucent for .scribnote, .scribflash, .scribmap and .scribquiz files"; GroupDescription: "File opening:"; Flags: unchecked

[InstallDelete]
; Replace shipped binaries on upgrade without touching user data in LocalAppData\Scriblucent.
Type: filesandordirs; Name: "{app}\AIWorker"
Type: filesandordirs; Name: "{app}\runtimes"

Type: files; Name: "{app}\cs\*.resources.dll"
Type: dirifempty; Name: "{app}\cs"
Type: files; Name: "{app}\de\*.resources.dll"
Type: dirifempty; Name: "{app}\de"
Type: files; Name: "{app}\es\*.resources.dll"
Type: dirifempty; Name: "{app}\es"
Type: files; Name: "{app}\fr\*.resources.dll"
Type: dirifempty; Name: "{app}\fr"
Type: files; Name: "{app}\it\*.resources.dll"
Type: dirifempty; Name: "{app}\it"
Type: files; Name: "{app}\ja\*.resources.dll"
Type: dirifempty; Name: "{app}\ja"
Type: files; Name: "{app}\ko\*.resources.dll"
Type: dirifempty; Name: "{app}\ko"
Type: files; Name: "{app}\pl\*.resources.dll"
Type: dirifempty; Name: "{app}\pl"
Type: files; Name: "{app}\pt-BR\*.resources.dll"
Type: dirifempty; Name: "{app}\pt-BR"
Type: files; Name: "{app}\ru\*.resources.dll"
Type: dirifempty; Name: "{app}\ru"
Type: files; Name: "{app}\tr\*.resources.dll"
Type: dirifempty; Name: "{app}\tr"
Type: files; Name: "{app}\zh-Hans\*.resources.dll"
Type: dirifempty; Name: "{app}\zh-Hans"
Type: files; Name: "{app}\zh-Hant\*.resources.dll"
Type: dirifempty; Name: "{app}\zh-Hant"

Type: files; Name: "{app}\concrt140.dll"
Type: files; Name: "{app}\msvcp140.dll"
Type: files; Name: "{app}\msvcp140_1.dll"
Type: files; Name: "{app}\msvcp140_2.dll"
Type: files; Name: "{app}\msvcp140_atomic_wait.dll"
Type: files; Name: "{app}\msvcp140_codecvt_ids.dll"
Type: files; Name: "{app}\vcruntime140.dll"
Type: files; Name: "{app}\vcruntime140_1.dll"
Type: files; Name: "{app}\vcomp140.dll"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Scriblucent"; Filename: "{app}\Scriblucent.exe"
Name: "{autodesktop}\Scriblucent"; Filename: "{app}\Scriblucent.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Scriblucent.exe"; Description: "Open Scriblucent"; Flags: nowait postinstall skipifsilent

[Code]
#include "VersionComparison.iss"

const
  UninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{2768A225-1F0D-43A6-9AF9-C6A8F8CAAA72}_is1';

function InitializeSetup: Boolean;
var
  InstalledVersion: String;
begin
  Result := True;
  if RegQueryStringValue(HKCU64, UninstallKey, 'DisplayVersion', InstalledVersion) then
    if CompareSemanticVersions(InstalledVersion, '{#AppVersion}') > 0 then begin
      SuppressibleMsgBox('A newer version of Scriblucent (' + InstalledVersion + ') is already installed. Uninstall it first to install an older version. Your library will be retained.', mbError, MB_OK, IDOK);
      Result := False;
    end;
end;

procedure RemoveAssociation(Extension, ProgId: String);
var
  Existing, Command: String;
begin
  { Remove only registrations whose command still points at this installation. }
  if RegQueryStringValue(HKCU64, 'Software\Classes\' + ProgId + '\shell\open\command', '', Command) then
    if Command = '"' + ExpandConstant('{app}\Scriblucent.exe') + '" --import "%1"' then begin
      RegDeleteValue(HKCU64, 'Software\Classes\' + Extension + '\OpenWithProgids', ProgId);
      if RegQueryStringValue(HKCU64, 'Software\Classes\' + Extension, '', Existing) then
        if Existing = ProgId then RegDeleteValue(HKCU64, 'Software\Classes\' + Extension, '');
      RegDeleteKeyIncludingSubkeys(HKCU64, 'Software\Classes\' + ProgId);
      RegDeleteKeyIfEmpty(HKCU64, 'Software\Classes\' + Extension + '\OpenWithProgids');
      RegDeleteKeyIfEmpty(HKCU64, 'Software\Classes\' + Extension);
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
  RegWriteStringValue(HKCU64, Key, '', Description);
  RegWriteStringValue(HKCU64, Key + '\DefaultIcon', '', '"' + ExpandConstant('{app}\Scriblucent.exe') + '",0');
  RegWriteStringValue(HKCU64, Key + '\shell\open\command', '', '"' + ExpandConstant('{app}\Scriblucent.exe') + '" --import "%1"');
  RegWriteStringValue(HKCU64, 'Software\Classes\' + Extension + '\OpenWithProgids', ProgId, '');
  if not RegQueryStringValue(HKCU64, 'Software\Classes\' + Extension, '', Existing) then
    RegWriteStringValue(HKCU64, 'Software\Classes\' + Extension, '', ProgId);
end;

procedure ConfigureAssociations(Enabled: Boolean);
begin
  ConfigureAssociation('.scribnote', 'Scriblucent.Note', 'Scriblucent note', Enabled);
  ConfigureAssociation('.scribflash', 'Scriblucent.Flashcards', 'Scriblucent flashcard set', Enabled);
  ConfigureAssociation('.scribmap', 'Scriblucent.MindMap', 'Scriblucent mind map', Enabled);
  ConfigureAssociation('.scribquiz', 'Scriblucent.Quiz', 'Scriblucent quiz', Enabled);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Shell, Shortcut: Variant;
  ShortcutPath: String;
begin
  if CurStep = ssPostInstall then begin
    ConfigureAssociations(WizardIsTaskSelected('fileassociations'));
    if not WizardIsTaskSelected('desktopicon') then begin
      ShortcutPath := ExpandConstant('{autodesktop}\Scriblucent.lnk');
      if FileExists(ShortcutPath) then begin
        Shell := CreateOleObject('WScript.Shell');
        Shortcut := Shell.CreateShortcut(ShortcutPath);
        if CompareText(Shortcut.TargetPath, ExpandConstant('{app}\Scriblucent.exe')) = 0 then
          DeleteFile(ShortcutPath);
      end;
    end;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then ConfigureAssociations(False);
end;
