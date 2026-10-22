procedure RemoveOwnedShortcut(ShortcutPath, ExpectedTarget: String);
var
  Shell, Folder, Item, Link: Variant;
  Target: String;
begin
  if not FileExists(ShortcutPath) then exit;
  { WScript.Shell reads targets through an ANSI API and can corrupt non-ASCII paths.
    Shell.Application returns the original Unicode target instead. }
  Shell := CreateOleObject('Shell.Application');
  Folder := Shell.NameSpace(ExtractFileDir(ShortcutPath));
  Item := Folder.ParseName(ExtractFileName(ShortcutPath));
  Link := Item.GetLink;
  Target := Link.Path;
  Link := Unassigned;
  Item := Unassigned;
  Folder := Unassigned;
  Shell := Unassigned;
  { Shell may expand an 8.3 directory such as RUNNER~1 to its long name. }
  if CompareText(GetShortName(Target), GetShortName(ExpectedTarget)) <> 0 then begin
    Log('Preserving shortcut with another target: ' + ShortcutPath + ' -> ' + Target);
    exit;
  end;
  if not DeleteFile(ShortcutPath) then
    RaiseException('Could not remove the deselected desktop shortcut: ' + ShortcutPath);
  Log('Removed deselected shortcut: ' + ShortcutPath);
end;
