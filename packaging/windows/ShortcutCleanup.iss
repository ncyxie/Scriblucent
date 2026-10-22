type
  { Only the interface methods preceding and including the ones used are needed. }
  IShellLinkW = interface(IUnknown)
    '{000214F9-0000-0000-C000-000000000046}'
    function GetPath(Path: String; MaxPath: Integer; FindData: LongWord; Flags: DWORD): HResult;
  end;

  IPersist = interface(IUnknown)
    '{0000010C-0000-0000-C000-000000000046}'
    function GetClassID(var ClassID: TGUID): HResult;
  end;

  IPersistFile = interface(IPersist)
    '{0000010B-0000-0000-C000-000000000046}'
    function IsDirty: HResult;
    function Load(FileName: String; Mode: Longint): HResult;
  end;

procedure RemoveOwnedShortcut(ShortcutPath, ExpectedTarget: String);
var
  Obj: IUnknown;
  Link: IShellLinkW;
  PersistFile: IPersistFile;
  Target: String;
  TargetLength: Integer;
begin
  if not FileExists(ShortcutPath) then exit;
  { WScript.Shell reads targets through an ANSI API and can corrupt non-ASCII paths. }
  Obj := CreateComObject(StringToGuid('{00021401-0000-0000-C000-000000000046}'));
  PersistFile := IPersistFile(Obj);
  OleCheck(PersistFile.Load(ShortcutPath, 0));
  Link := IShellLinkW(Obj);
  SetLength(Target, 32768);
  OleCheck(Link.GetPath(Target, Length(Target), 0, 4)); { SLGP_RAWPATH; do not resolve targets. }
  TargetLength := 0;
  while (TargetLength < Length(Target)) and (Target[TargetLength + 1] <> #0) do
    TargetLength := TargetLength + 1;
  SetLength(Target, TargetLength);
  Link := nil;
  PersistFile := nil;
  Obj := nil;
  if CompareText(Target, ExpectedTarget) <> 0 then begin
    Log('Preserving shortcut with another target: ' + ShortcutPath + ' -> ' + Target);
    exit;
  end;
  if not DeleteFile(ShortcutPath) then
    RaiseException('Could not remove the deselected desktop shortcut: ' + ShortcutPath);
  Log('Removed deselected shortcut: ' + ShortcutPath);
end;
