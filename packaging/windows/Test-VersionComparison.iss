#ifndef OutputDir
  #error OutputDir is required
#endif
[Setup]
AppName=Scriblucent version comparison tests
AppVersion=1.0.0
CreateAppDir=no
Uninstallable=no
PrivilegesRequired=lowest
OutputDir={#OutputDir}
OutputBaseFilename=VersionComparisonTests

[Code]
#include "VersionComparison.iss"

procedure AssertOrder(Left, Right: String; Expected: Integer);
begin
  if CompareSemanticVersions(Left, Right) <> Expected then
    RaiseException('Incorrect version order: ' + Left + ' / ' + Right);
end;

function InitializeSetup: Boolean;
begin
  AssertOrder('1.0.0-beta.2', '1.0.0-beta.3', -1);
  AssertOrder('1.0.0-beta.10', '1.0.0-beta.3', 1);
  AssertOrder('1.0.0', '1.0.0-beta.3', 1);
  AssertOrder('1.0.0-beta.3', '1.0.0-beta.3+build', 0);
  AssertOrder('1.0.0-rc.1', '1.0.0-beta.99', 1);
  AssertOrder('1.0.0-beta', '1.0.0-beta.1', -1);
  AssertOrder('1.0.0-1', '1.0.0-beta', -1);
  AssertOrder('1.0.0-BETA', '1.0.0-beta', -1);
  AssertOrder('1.0.0-beta.100000000000000000000', '1.0.0-beta.2', 1);
  AssertOrder('1.10.0', '1.9.99', 1);
  AssertOrder('2.0.0-beta.1', '1.99.99', 1);
  if not SaveStringToFile(ExpandConstant('{param:ResultFile}'), 'passed', False) then
    RaiseException('Could not save test result');
  { Abort before any wizard or installation; the caller verifies the result marker. }
  Result := False;
end;
