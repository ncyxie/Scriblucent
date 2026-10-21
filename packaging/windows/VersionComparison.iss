function TakeVersionPart(var Value: String; Separator: String): String;
var
  Index: Integer;
begin
  Index := Pos(Separator, Value);
  if Index = 0 then begin
    Result := Value;
    Value := '';
  end else begin
    Result := Copy(Value, 1, Index - 1);
    Delete(Value, 1, Index);
  end;
end;

function IsNumericIdentifier(Value: String): Boolean;
var
  Index: Integer;
begin
  Result := Length(Value) > 0;
  for Index := 1 to Length(Value) do
    if (Value[Index] < '0') or (Value[Index] > '9') then Result := False;
end;

function CompareIdentifier(Left, Right: String; Numeric: Boolean): Integer;
begin
  Result := 0;
  if Numeric then begin
    if Length(Left) < Length(Right) then Result := -1;
    if Length(Left) > Length(Right) then Result := 1;
  end;
  if Result = 0 then begin
    if Left < Right then Result := -1;
    if Left > Right then Result := 1;
  end;
end;

function CompareSemanticVersions(Left, Right: String): Integer;
var
  LeftCore, RightCore, LeftPart, RightPart: String;
  Index: Integer;
  LeftNumeric, RightNumeric: Boolean;
begin
  { Build metadata does not affect precedence. }
  LeftCore := TakeVersionPart(Left, '+');
  RightCore := TakeVersionPart(Right, '+');
  Left := LeftCore;
  Right := RightCore;
  LeftCore := TakeVersionPart(Left, '-');
  RightCore := TakeVersionPart(Right, '-');
  for Index := 1 to 3 do begin
    LeftPart := TakeVersionPart(LeftCore, '.');
    RightPart := TakeVersionPart(RightCore, '.');
    Result := CompareIdentifier(LeftPart, RightPart, True);
    if Result <> 0 then exit;
  end;
  if (Left = '') and (Right = '') then begin Result := 0; exit; end;
  if Left = '' then begin Result := 1; exit; end;
  if Right = '' then begin Result := -1; exit; end;
  while (Left <> '') and (Right <> '') do begin
    LeftPart := TakeVersionPart(Left, '.');
    RightPart := TakeVersionPart(Right, '.');
    LeftNumeric := IsNumericIdentifier(LeftPart);
    RightNumeric := IsNumericIdentifier(RightPart);
    if LeftNumeric and not RightNumeric then Result := -1
    else if RightNumeric and not LeftNumeric then Result := 1
    else Result := CompareIdentifier(LeftPart, RightPart, LeftNumeric and RightNumeric);
    if Result <> 0 then exit;
  end;
  Result := 0;
  if Left <> '' then Result := 1;
  if Right <> '' then Result := -1;
end;
