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
AppPublisher=Scriblucent
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
WizardStyle=modern
SetupIconFile=..\..\Scriblucent\logo.ico
UninstallDisplayIcon={app}\Scriblucent.exe
UninstallDisplayName=Scriblucent
LicenseFile=..\..\LICENSE
OutputDir={#OutputDir}
OutputBaseFilename=Scriblucent-{#AppVersion}-win-{#TargetArch}-Setup
Compression=lzma2
SolidCompression=yes
CloseApplications=yes
RestartApplications=no
SetupLogging=yes

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[InstallDelete]
; Replace shipped binaries on upgrade without touching user data in LocalAppData\Scriblucent.
Type: filesandordirs; Name: "{app}\AIWorker"
Type: filesandordirs; Name: "{app}\runtimes"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Scriblucent"; Filename: "{app}\Scriblucent.exe"
Name: "{autodesktop}\Scriblucent"; Filename: "{app}\Scriblucent.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Scriblucent.exe"; Description: "Open Scriblucent"; Flags: nowait postinstall skipifsilent
