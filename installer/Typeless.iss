; Per-user installer for Typeless. Build after build.ps1 -Package -SelfContained:
;   iscc installer\Typeless.iss /DArch=x64|arm64 [/DVersion=x.y.z] [/DSourceDir=<app dir>]

#ifndef Arch
  #define Arch "x64"
#endif
#ifndef Version
  #define VersionFile FileOpen(AddBackslash(SourcePath) + "..\VERSION")
  #define Version Trim(FileRead(VersionFile))
  #expr FileClose(VersionFile)
#endif
#ifndef SourceDir
  #define SourceDir AddBackslash(SourcePath) + "..\out\" + Arch
#endif

#if Arch == "arm64"
  #define InnoArch "arm64"
#elif Arch == "x64"
  #define InnoArch "x64compatible"
#else
  #error Arch must be x64 or arm64
#endif

[Setup]
AppId={{80258A07-5649-4B6B-BE32-71B949A51C74}
AppName=Typeless
AppVersion={#Version}
AppVerName=Typeless {#Version}
AppPublisher=Typeless
AppPublisherURL=https://github.com/Fahadxx/typeless
AppSupportURL=https://github.com/Fahadxx/typeless/issues
AppUpdatesURL=https://github.com/Fahadxx/typeless/releases
VersionInfoVersion={#Version}.0
; Same folder as the dev build, so the log and the shell DLL lookup do not change.
DefaultDirName={localappdata}\Typeless\app
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed={#InnoArch}
ArchitecturesInstallIn64BitMode={#InnoArch}
MinVersion=10.0.19041
LicenseFile=..\LICENSE
SetupIconFile=..\src\Typeless\typeless.ico
UninstallDisplayIcon={app}\Typeless.exe
UninstallDisplayName=Typeless
OutputDir=..\out
OutputBaseFilename=TypelessSetup-{#Version}-{#Arch}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; Restart Manager would offer to close Explorer or COM Surrogate; PrepareToInstall frees the files instead.
CloseApplications=no
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "ffmpeg"; Description: "Also install ffmpeg (adds video, audio and webp formats; uses winget, needs internet)"; Flags: unchecked; Check: HasWinget
Name: "topmenu"; Description: "Show Typeless in the main Windows 11 right-click menu (asks for administrator approval)"; Flags: unchecked; Check: IsWin11

[InstallDelete]
; Files from an older version. Safe because PrepareToInstall refuses any folder but our own.
Type: filesandordirs; Name: "{app}\*"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion
Source: "..\THIRD-PARTY-NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "prepare.ps1"; Flags: dontcopy

[Run]
Filename: "{localappdata}\Microsoft\WindowsApps\winget.exe"; Parameters: "install --id Gyan.FFmpeg --exact --accept-source-agreements --accept-package-agreements"; StatusMsg: "Installing ffmpeg with winget..."; Tasks: ffmpeg; Flags: waituntilterminated

[Code]
const
  ShellDll = 'TypelessShell.dll';

var
  MenuKind: String;
  TopMenuWanted: Boolean;

function HasWinget: Boolean;
begin
  Result := FileExists(ExpandConstant('{localappdata}\Microsoft\WindowsApps\winget.exe'));
end;

function IsWin11: Boolean;
var
  Version: TWindowsVersion;
begin
  GetWindowsVersionEx(Version);
  Result := Version.Build >= 22000;
end;

// Version of an existing install: this installer's Apps entry, else a dev build's exe (build.ps1).
function InstalledVersion: String;
var
  Exe: String;
begin
  Result := '';
  if RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{80258A07-5649-4B6B-BE32-71B949A51C74}_is1',
    'DisplayVersion', Result) then Exit;
  Exe := ExpandConstant('{localappdata}\Typeless\app\Typeless.exe');
  if FileExists(Exe) and GetVersionNumbersString(Exe, Result) then
  begin
    // 0.12.0.0 -> 0.12.0
    if Copy(Result, Length(Result) - 1, 2) = '.0' then
      Result := Copy(Result, 1, Length(Result) - 2);
  end
  else
    Result := '';
end;

// Silent runs (/SUPPRESSMSGBOXES) take the default answer: update and reinstall go ahead, a downgrade stops.
function InitializeSetup: Boolean;
var
  Old: String;
  OldV, NewV: Int64;
  Cmp: Integer;
begin
  Result := True;
  Old := InstalledVersion;
  if (Old = '') or not StrToVersion(Old, OldV) or not StrToVersion('{#Version}', NewV) then Exit;
  Cmp := ComparePackedVersion(OldV, NewV);
  if Cmp < 0 then
    Result := SuppressibleMsgBox('Typeless ' + Old + ' is installed. Update it to {#Version}?',
      mbConfirmation, MB_YESNO, IDYES) = IDYES
  else if Cmp = 0 then
    Result := SuppressibleMsgBox('Typeless {#Version} is already installed.' + #13#10#13#10 +
      'Reinstall it anyway (repair)?', mbInformation, MB_YESNO or MB_DEFBUTTON2, IDYES) = IDYES
  else
    Result := SuppressibleMsgBox('A newer Typeless (' + Old + ') is already installed.' + #13#10#13#10 +
      'Replace it with the older {#Version}?', mbConfirmation, MB_YESNO or MB_DEFBUTTON2, IDNO) = IDYES;
end;

function PowerShell: String;
begin
  Result := ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe');
end;

// The surrogate dllhost lets go of the shell DLL a moment after the package is removed.
function WaitForDelete(const Path: String): Boolean;
var
  I: Integer;
begin
  for I := 1 to 60 do
  begin
    if not FileExists(Path) or DeleteFile(Path) then
    begin
      Result := True;
      Exit;
    end;
    Sleep(250);
  end;
  Result := False;
end;

// The folder page is hidden, but /DIR= (winget --location) still sets {app}. Setup empties {app} on
// upgrade and the uninstaller deletes it, so never run anywhere else.
function IsOwnDir: Boolean;
begin
  Result := CompareText(ExpandConstant('{app}'), ExpandConstant('{localappdata}\Typeless\app')) = 0;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Rc: Integer;
begin
  if not IsOwnDir then
  begin
    Result := 'Typeless always installs to ' + ExpandConstant('{localappdata}\Typeless\app') + '. Run Setup without /DIR.';
    Exit;
  end;
  Result := '';
  ExtractTemporaryFile('prepare.ps1');
  Exec(PowerShell, '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + ExpandConstant('{tmp}\prepare.ps1') +
    '" -App "' + ExpandConstant('{app}') + '"', '', SW_HIDE, ewWaitUntilTerminated, Rc);
  if not WaitForDelete(ExpandConstant('{app}\' + ShellDll)) then
    Result := ShellDll + ' is still in use by Windows Explorer. Sign out and back in, then run Setup again.';
end;

function RunTypeless(const Args: String; var Lines: TArrayOfString): Boolean;
var
  Rc: Integer;
  Output: TExecOutput;
begin
  Result := ExecAndCaptureOutput(ExpandConstant('{app}\Typeless.exe'), Args, '', SW_HIDE, ewWaitUntilTerminated, Rc, Output) and (Rc = 0);
  Lines := Output.StdOut;
end;

function ValueOf(const Lines: TArrayOfString; const Key: String): String;
var
  I: Integer;
begin
  Result := '';
  for I := 0 to GetArrayLength(Lines) - 1 do
    if Pos(Key + '=', Lines[I]) = 1 then
      Result := Copy(Lines[I], Length(Key) + 2, MaxInt);
end;

// The topmenu task: sign the package on this PC with a new certificate, trust it (one UAC prompt), and keep
// Typeless.local.msix only when Windows trusts it, so --register falls back to the classic menu otherwise.
procedure TrustLocalCert;
var
  Lines: TArrayOfString;
  Cert, Thumb: String;
  Rc: Integer;
  Trusted: Boolean;
begin
  WizardForm.StatusLabel.Caption := 'Asking Windows to trust this PC''s Typeless certificate...';
  if not RunTypeless('--sign-local', Lines) then
    Log('Typeless --sign-local failed');
  Cert := ValueOf(Lines, 'cert');
  Thumb := ValueOf(Lines, 'thumbprint');
  Log('Typeless --sign-local: thumbprint=' + Thumb);
  if Cert = '' then
    Log('No certificate to trust')
  else if GetEnv('TYPELESS_NO_ELEVATE') = '1' then
    Log('TYPELESS_NO_ELEVATE=1: administrator approval skipped')
  else if ShellExec('runas', ExpandConstant('{app}\Typeless.exe'), '--trust-cert ' + Cert, '', SW_HIDE, ewWaitUntilTerminated, Rc) then
    Log('Typeless --trust-cert: rc=' + IntToStr(Rc))
  else
    Log('Typeless --trust-cert not run: ' + SysErrorMessage(Rc));
  Trusted := False;
  if Thumb <> '' then
    if RunTypeless('--local-cert ' + Thumb, Lines) then
      Trusted := ValueOf(Lines, 'trusted') = 'yes';
  if Trusted then
    Log('Typeless --local-cert: trusted=yes')
  else
  begin
    DeleteFile(ExpandConstant('{app}\Typeless.local.msix'));
    Log('Typeless --local-cert: trusted=no, removed Typeless.local.msix');
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Lines: TArrayOfString;
begin
  if CurStep = ssPostInstall then
  begin
    TopMenuWanted := WizardIsTaskSelected('topmenu');
    if TopMenuWanted then
      TrustLocalCert;
    WizardForm.StatusLabel.Caption := 'Adding Convert with Typeless to the right-click menu...';
    if RunTypeless('--register', Lines) then
      MenuKind := ValueOf(Lines, 'menu');
    Log('Typeless --register: menu=' + MenuKind);
  end;
end;

function YesNo(const Value: String): String;
begin
  if Value = 'yes' then Result := 'found' else Result := 'not found';
end;

procedure CurPageChanged(CurPageID: Integer);
var
  Lines: TArrayOfString;
  Text: String;
begin
  if CurPageID <> wpFinished then Exit;
  Text := 'Typeless is installed. Right-click a file and choose Convert with Typeless';
  if MenuKind = 'top-level' then
    Text := Text + '.'
  else
    Text := Text + ' (on Windows 11 it is under Show more options).';
  if (MenuKind <> 'top-level') and TopMenuWanted then
    Text := Text + #13#10#13#10 + 'The main-menu option needs administrator approval. Run Setup again to retry.';
  if RunTypeless('--backends', Lines) then
  begin
    // No line may start with '#': the preprocessor would read it as a directive.
    Text := Text + #13#10#13#10 + 'Optional tools:' + #13#10 +
      '  Microsoft Word: ' + YesNo(ValueOf(Lines, 'word')) + #13#10 +
      '  Microsoft PowerPoint: ' + YesNo(ValueOf(Lines, 'powerpoint')) + #13#10 +
      '  LibreOffice: ' + YesNo(ValueOf(Lines, 'libreoffice')) + #13#10 +
      '  ffmpeg: ' + YesNo(ValueOf(Lines, 'ffmpeg'));
    if ValueOf(Lines, 'ffmpeg') <> 'yes' then
      Text := Text + #13#10#13#10 + 'Without ffmpeg, video, audio and webp formats are hidden. Run Setup again to install it.';
    if (ValueOf(Lines, 'word') <> 'yes') and (ValueOf(Lines, 'libreoffice') <> 'yes') then
      Text := Text + #13#10#13#10 + 'Without Word or LibreOffice, document formats are hidden.';
  end;
  WizardForm.FinishedLabel.Caption := Text;
  WizardForm.AdjustLabelHeight(WizardForm.FinishedLabel);
end;

// Offers to remove the certificate the topmenu task trusted. Runs while the app's Typeless.exe still exists.
// A silent uninstall leaves it: it has no private key anywhere, and the next install replaces it.
procedure AskUntrust(const Exe: String);
var
  Lines: TArrayOfString;
  Rc: Integer;
begin
  if not RunTypeless('--local-cert', Lines) then Exit;
  if ValueOf(Lines, 'trusted') <> 'yes' then Exit;
  if MsgBox('Remove the certificate Typeless added so Windows trusts its menu? (asks for administrator approval)',
    mbConfirmation, MB_YESNO) <> IDYES then Exit;
  if GetEnv('TYPELESS_NO_ELEVATE') = '1' then
    Log('TYPELESS_NO_ELEVATE=1: administrator approval skipped')
  else if ShellExec('runas', Exe, '--untrust-cert', '', SW_HIDE, ewWaitUntilTerminated, Rc) then
    Log('Typeless --untrust-cert: rc=' + IntToStr(Rc))
  else
    Log('Typeless --untrust-cert not run: ' + SysErrorMessage(Rc));
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Rc: Integer;
  Data, Exe: String;
  KeepLog: Boolean;
begin
  Data := ExpandConstant('{localappdata}\Typeless');
  if not IsOwnDir then Exit;
  if CurUninstallStep = usUninstall then
  begin
    Exe := ExpandConstant('{app}\Typeless.exe');
    if FileExists(Exe) then
    begin
      Exec(Exe, '--shutdown', '', SW_HIDE, ewWaitUntilTerminated, Rc);
      Exec(Exe, '--unregister', '', SW_HIDE, ewWaitUntilTerminated, Rc);
      if not UninstallSilent then
        AskUntrust(Exe);
    end;
    WaitForDelete(ExpandConstant('{app}\' + ShellDll));
  end
  else if CurUninstallStep = usPostUninstall then
  begin
    DelTree(ExpandConstant('{app}'), True, True, True);
    DelTree(GetEnv('TEMP') + '\Typeless', True, True, True);
    KeepLog := False;
    if not UninstallSilent then
      KeepLog := MsgBox('Keep the Typeless log (' + Data + '\typeless.log)?', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES;
    if KeepLog then
    begin
      DelTree(Data + '\lo-profile', True, True, True);
      DelTree(Data + '\pkg', True, True, True);
      DeleteFile(Data + '\Typeless.msix');
      DeleteFile(Data + '\TypelessDev.cer');
    end
    else
      DelTree(Data, True, True, True);
  end;
end;
