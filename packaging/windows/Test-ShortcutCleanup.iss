#ifndef OutputDir
  #error OutputDir is required
#endif
[Setup]
AppName=Scriblucent shortcut cleanup tests
AppVersion=1.0.0
CreateAppDir=no
Uninstallable=no
PrivilegesRequired=lowest
OutputDir={#OutputDir}
OutputBaseFilename=ShortcutCleanupTests

[Code]
#include "ShortcutCleanup.iss"

function InitializeSetup: Boolean;
var
  Directory, Target, ShortcutPath: String;
begin
  Directory := ExpandConstant('{tmp}\Scriblucent setup Žinių biblioteka');
  if not ForceDirectories(Directory) then RaiseException('Could not create shortcut test directory');
  Target := Directory + '\Scriblucent.exe';
  if not SaveStringToFile(Target, 'Shortcut target fixture', False) then
    RaiseException('Could not create shortcut test target');
  ShortcutPath := Directory + '\Scriblucent.lnk';
  CreateShellLink(ShortcutPath, '', Target, '', Directory, '', 0, SW_SHOWNORMAL);
  RemoveOwnedShortcut(ShortcutPath, ExpandConstant('{srcexe}'));
  if not FileExists(ShortcutPath) then RaiseException('Removed another application shortcut');
  RemoveOwnedShortcut(ShortcutPath, Target);
  if FileExists(ShortcutPath) then RaiseException('Retained the owned Unicode shortcut');
  CreateShellLink(ShortcutPath, '', Target, '', Directory, '', 0, SW_SHOWNORMAL);
  RemoveOwnedShortcut(ShortcutPath, GetShortName(Target));
  if FileExists(ShortcutPath) then RaiseException('Retained the owned short-path shortcut');
  RemoveOwnedShortcut(ShortcutPath, Target); { Already absent is harmless. }
  if not SaveStringToFile(ExpandConstant('{param:ResultFile}'), 'passed', False) then
    RaiseException('Could not save shortcut test result');
  Result := False;
end;
