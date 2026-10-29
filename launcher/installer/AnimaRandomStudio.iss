#ifndef AppVersion
  #error AppVersion is required
#endif
#ifndef SourceExe
  #error SourceExe is required
#endif
#ifndef OutputDirectory
  #error OutputDirectory is required
#endif
#ifndef InstallerAppId
  #define InstallerAppId "AnimaRandomStudio"
#endif
#ifndef InstallerMutex
  #define InstallerMutex "Local\AnimaRandomStudioLauncher"
#endif
#if InstallerAppId == "AnimaRandomStudio"
  #define ShortcutName "Comfy Image Atelier"
  #define LegacyShortcutName "Anima Random Studio"
#else
  #define ShortcutName InstallerAppId
  #define LegacyShortcutName InstallerAppId + ".Legacy"
#endif

[Setup]
AppId={#InstallerAppId}
AppName={#ShortcutName}
AppVersion={#AppVersion}
AppPublisher=Comfy Image Atelier
AppPublisherURL=https://github.com/159159pzy-crypto/comfy-Image-Atelier
DefaultDirName={localappdata}\Programs\AnimaRandomStudio
DisableDirPage=no
DefaultGroupName=Comfy Image Atelier
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir={#OutputDirectory}
OutputBaseFilename=AnimaRandomStudio-Setup-win-x64
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\AnimaRandomStudio.exe
AppMutex={#InstallerMutex}
CloseApplications=no
RestartApplications=no
SetupLogging=yes
VersionInfoVersion={#AppVersion}

[Tasks]
Name: desktopicon; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
Source: "{#SourceExe}"; DestDir: "{app}"; DestName: "AnimaRandomStudio.exe"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#ShortcutName}"; Filename: "{app}\AnimaRandomStudio.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\{#ShortcutName}"; Filename: "{app}\AnimaRandomStudio.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\AnimaRandomStudio.exe"; Parameters: "--show"; Description: "Open Comfy Image Atelier"; Flags: nowait postinstall skipifsilent unchecked

[Code]
procedure MigrateOwnedShortcut(const Directory: String);
var
  OldPath, NewPath, Target: String;
  Shell, Link: Variant;
begin
  OldPath := Directory + '\{#LegacyShortcutName}.lnk';
  NewPath := Directory + '\{#ShortcutName}.lnk';
  if not FileExists(OldPath) or not FileExists(NewPath) then exit;
  Target := ExpandConstant('{app}\AnimaRandomStudio.exe');
  try
    Shell := CreateOleObject('WScript.Shell');
    Link := Shell.CreateShortcut(OldPath);
    if (CompareText(ExpandFileName(String(Link.TargetPath)), Target) = 0) and (String(Link.Arguments) = '') then
      DeleteFile(OldPath);
  except
    Log('Existing shortcut was preserved because its target could not be confirmed.');
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then begin
    MigrateOwnedShortcut(ExpandConstant('{autoprograms}'));
    MigrateOwnedShortcut(ExpandConstant('{autodesktop}'));
  end;
end;

function PackedVersion(const Text: String): Int64;
var
  Parts: TArrayOfString;
  I, Value: Integer;
begin
  Result := 0;
  Parts := StringSplit(Text, ['.'], stExcludeEmpty);
  if (GetArrayLength(Parts) < 3) or (GetArrayLength(Parts) > 4) then begin Result := -1; exit; end;
  for I := 0 to 3 do begin
    Value := 0;
    if I < GetArrayLength(Parts) then Value := StrToIntDef(Parts[I], -1);
    if (Value < 0) or (Value > 65535) then begin Result := -1; exit; end;
    Result := (Result shl 16) or Value;
  end;
end;

function CheckExisting(const CheckDowngrade: Boolean): String;
var
  Existing, Probe, ExistingVersion, ReportPath, Report, EffectiveVersion: String;
  ReportBytes: AnsiString;
  Status, StartAt, EndAt: Integer;
begin
  Result := '';
  Existing := ExpandConstant('{app}\AnimaRandomStudio.exe');
  if FileExists(Existing) then begin
    if not GetVersionNumbersString(Existing, ExistingVersion) then begin
      Result := 'Cannot identify the installed launcher. Close it and repair the installation manually.';
      exit;
    end;
    if CheckDowngrade and (PackedVersion(ExistingVersion) > PackedVersion('{#AppVersion}')) then begin
      Result := 'A newer launcher is already installed. Downgrades are not supported.';
      exit;
    end;
  end;
  if CheckDowngrade then begin
    // A portable launcher can already own the shared state even on first Setup.
    // Probe the exact incoming EXE before any installed files are changed.
    ExtractTemporaryFile('AnimaRandomStudio.exe');
    Probe := ExpandConstant('{tmp}\AnimaRandomStudio.exe');
  end else begin
    if not FileExists(Existing) then exit;
    Probe := Existing;
  end;
  ReportPath := ExpandConstant('{tmp}\anima-installation-status.json');
  DeleteFile(ReportPath);
  if not Exec(Probe, '--installation-status --report "' + ReportPath + '"', '', SW_HIDE, ewWaitUntilTerminated, Status) then begin
    Result := 'Cannot check the installed launcher. Close it and try again.';
    exit;
  end;
  if (Status <> 0) or not LoadStringFromFile(ReportPath, ReportBytes) then begin
    Result := 'The installed launcher could not confirm that installation is safe.';
    exit;
  end;
  Report := String(ReportBytes);
  StringChangeEx(Report, ' ', '', True);
  StringChangeEx(Report, #13, '', True);
  StringChangeEx(Report, #10, '', True);
  StringChangeEx(Report, #9, '', True);
  if (Pos('"protocolVersion":1', Report) = 0) or (Pos('"canInstall":true', Report) = 0) or
     (Pos('"updateInProgress":false', Report) = 0) or (Pos('"running":false', Report) = 0) then
    Result := 'Close Comfy Image Atelier and wait for its update to finish before installing.';
  if Result <> '' then exit;
  if CheckDowngrade then begin
    StartAt := Pos('"effectiveVersion":"', Report);
    if StartAt = 0 then begin Result := 'Cannot identify the active launcher version.'; exit; end;
    EffectiveVersion := Copy(Report, StartAt + Length('"effectiveVersion":"'), MaxInt);
    EndAt := Pos('"', EffectiveVersion);
    if EndAt = 0 then begin Result := 'Invalid active launcher version.'; exit; end;
    EffectiveVersion := Copy(EffectiveVersion, 1, EndAt - 1);
    if PackedVersion(EffectiveVersion) < 0 then begin Result := 'Invalid active launcher version.'; exit; end;
    if PackedVersion(EffectiveVersion) > PackedVersion('{#AppVersion}') then
      Result := 'A newer launcher is already active. Downgrades are not supported.';
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := CheckExisting(True);
end;

function InitializeUninstall(): Boolean;
var
  Error: String;
begin
  Error := CheckExisting(False);
  Result := Error = '';
  if not Result then SuppressibleMsgBox(Error, mbError, MB_OK, IDOK);
end;

// No UninstallDelete rules: user data, models, cached versions and update state
// live outside {app} and must survive uninstall/reinstall.
