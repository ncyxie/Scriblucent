function EnumProcesses(var ProcessId: LongWord; Bytes: LongWord; var BytesNeeded: LongWord): Boolean;
  external 'EnumProcesses@psapi.dll stdcall';
function OpenProcess(Access: LongWord; InheritHandle: Boolean; ProcessId: LongWord): THandle;
  external 'OpenProcess@kernel32.dll stdcall';
function QueryFullProcessImageName(Process: THandle; Flags: LongWord; Buffer: String; var Size: LongWord): Boolean;
  external 'QueryFullProcessImageNameW@kernel32.dll stdcall';
function CloseHandle(Handle: THandle): Boolean;
  external 'CloseHandle@kernel32.dll stdcall';

function ScriblucentRunning(Directory: String): Boolean;
var
  Ids: array[0..16383] of LongWord;
  Needed, Size: LongWord;
  Index: Integer;
  Handle: THandle;
  Image, Prefix, Name: String;
begin
  Result := False;
  if Directory = '' then exit;
  if not EnumProcesses(Ids[0], 65536, Needed) or (Needed >= 65536) then begin Result := True; exit; end;
  Prefix := AddBackslash(GetShortName(Directory));
  for Index := 0 to (Needed div 4) - 1 do begin
    Handle := OpenProcess($1000, False, Ids[Index]);
    if Handle <> 0 then begin
      try
        SetLength(Image, 32768);
        Size := Length(Image);
        if QueryFullProcessImageName(Handle, 0, Image, Size) then begin
          SetLength(Image, Size);
          Name := ExtractFileName(Image);
          if (CompareText(Name, 'Scriblucent.exe') = 0) or (CompareText(Name, 'Scriblucent.AI.Worker.exe') = 0) then
            if CompareText(Copy(GetShortName(Image), 1, Length(Prefix)), Prefix) = 0 then begin Result := True; exit; end;
        end;
      finally
        CloseHandle(Handle);
      end;
    end;
  end;
end;
