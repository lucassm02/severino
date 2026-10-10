; Severino installer. Built by scripts/build-installer.ps1, which passes AppVersion and PublishDir.
; Kept as UTF-8 with BOM: Inno Setup reads the Portuguese accents right only with it.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\artifacts\publish"
#endif

#define ServiceName "Severino.Helper"
#define HelperKey "SOFTWARE\Severino\Helper"

[Setup]
AppId={{6F3B1C2A-8D4E-4F5A-9B7C-2E1D0A9F8B76}
AppName=Severino
AppVersion={#AppVersion}
AppVerName=Severino {#AppVersion}
AppPublisher=Severino
AppPublisherURL=https://github.com/lucassm02/severino
VersionInfoVersion={#AppVersion}
; Under Program Files on purpose: the Helper runs as SYSTEM, and a folder the user can write to
; would let any program of theirs replace it.
DefaultDirName={autopf}\Severino
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputBaseFilename=Severino-Setup-{#AppVersion}
SetupIconFile=..\src\Severino.App\Assets\severino.ico
UninstallDisplayIcon={app}\Severino.exe
UninstallDisplayName=Severino
WizardStyle=modern
Compression=lzma2/ultra64
SolidCompression=yes
; Asks to close a running Severino before installing over it or removing it.
AppMutex=Local\Severino.SingleInstance
CloseApplications=yes

[Languages]
Name: "ptbr"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[InstallDelete]
; A framework-dependent Helper (scripts/dev-helper.ps1, or an older build) leaves this folder,
; which the self-contained one does not have and the uninstaller would never remove.
Type: filesandordirs; Name: "{app}\Helper\runtimes"

[Files]
Source: "{#PublishDir}\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PublishDir}\helper\*"; DestDir: "{app}\Helper"; Flags: ignoreversion recursesubdirs createallsubdirs
; The PowerShell module, where both Windows PowerShell and PowerShell 7 find it with no setup.
Source: "..\powershell\Severino\*"; DestDir: "{commonpf64}\WindowsPowerShell\Modules\Severino"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\Severino"; Filename: "{app}\Severino.exe"
Name: "{autodesktop}\Severino"; Filename: "{app}\Severino.exe"; Tasks: desktopicon

[Registry]
Root: HKLM; Subkey: "SOFTWARE\Severino"; Flags: uninsdeletekeyifempty
; Only this account may talk to the Helper's pipe.
Root: HKLM; Subkey: "{#HelperKey}"; ValueType: string; ValueName: "AllowedUserSid"; ValueData: "{code:OriginalUserSid}"; Flags: uninsdeletekey

[Run]
; As the user who started the installer, not as the admin who approved the UAC.
Filename: "{app}\Severino.exe"; Description: "Abrir o Severino"; Flags: postinstall nowait skipifsilent runasoriginaluser

[UninstallRun]
; The hosts block first, while the Helper's binary is still here.
Filename: "{app}\Helper\Severino.Helper.exe"; Parameters: "--clear-hosts"; Flags: runhidden waituntilterminated; RunOnceId: "ClearHosts"
; The CA, autostart and proxy exceptions, for the account running the uninstaller. Windows asks
; to confirm removing the CA. Inno cannot run this as another user (runasoriginaluser exists only
; in [Run]); InitializeUninstall warns when the installing user is someone else.
Filename: "{app}\Severino.exe"; Parameters: "--cleanup"; Flags: waituntilterminated; RunOnceId: "Cleanup"
; net stop waits for the service to stop, so its files can be deleted right after.
Filename: "{sys}\net.exe"; Parameters: "stop {#ServiceName}"; Flags: runhidden waituntilterminated; RunOnceId: "StopService"
Filename: "{sys}\sc.exe"; Parameters: "delete {#ServiceName}"; Flags: runhidden waituntilterminated; RunOnceId: "DeleteService"

[UninstallDelete]
Type: filesandordirs; Name: "{app}\Helper\logs"

[Code]
var
  UserSid: String;

{ Runs a command through cmd and returns what it printed, or '' on failure. }
function CommandOutput(const Command: String; const AsOriginalUser: Boolean): String;
var
  OutputFile: String;
  ResultCode: Integer;
  Started: Boolean;
  Lines: AnsiString;
begin
  Result := '';
  { ProgramData, not the temp folder: with another admin approving the UAC, the original user
    could not write to that admin's temp. }
  OutputFile := ExpandConstant('{commonappdata}\severino-') + GetDateTimeString('yyyymmddhhnnsszzz', #0, #0) + '.txt';
  if AsOriginalUser then
    Started := ExecAsOriginalUser(ExpandConstant('{cmd}'), '/c ' + Command + ' > "' + OutputFile + '"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode)
  else
    Started := Exec(ExpandConstant('{cmd}'), '/c ' + Command + ' > "' + OutputFile + '"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  if Started and (ResultCode = 0) and LoadStringFromFile(OutputFile, Lines) then
    Result := Trim(String(Lines));
  DeleteFile(OutputFile);
end;

{ The SID in the last field of whoami's CSV: "dominio\usuario","S-1-5-21-...". }
function SidOf(const AsOriginalUser: Boolean): String;
var
  Line: String;
  Start: Integer;
begin
  Result := '';
  Line := CommandOutput('whoami /user /fo csv /nh', AsOriginalUser);
  Start := Pos(',"S-', Line);
  if Start > 0 then
  begin
    Result := Copy(Line, Start + 2, Length(Line));
    Result := Copy(Result, 1, Pos('"', Result) - 1);
  end;
end;

function OriginalUserSid(Param: String): String;
begin
  Result := UserSid;
end;

procedure Run(const FileName, Params: String);
var
  ResultCode: Integer;
begin
  Exec(FileName, Params, '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  UserSid := SidOf(True);
  if UserSid = '' then
  begin
    Result := 'Não foi possível identificar o seu usuário do Windows, que o serviço auxiliar precisa conhecer.';
    exit;
  end;
  { An update: stop and remove the old service so its files can be replaced; it comes back below. }
  Run(ExpandConstant('{sys}\net.exe'), 'stop {#ServiceName}');
  Run(ExpandConstant('{sys}\sc.exe'), 'delete {#ServiceName}');
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Exe: String;
  ResultCode: Integer;
begin
  if CurStep <> ssPostInstall then
    exit;
  Exe := ExpandConstant('{app}\Helper\Severino.Helper.exe');
  { binPath keeps its own quotes: the path has spaces, and an unquoted one is a known way to hijack a service. }
  if not Exec(ExpandConstant('{sys}\sc.exe'),
      'create {#ServiceName} binPath= "\"' + Exe + '\"" start= auto DisplayName= "Severino Helper"',
      '', SW_HIDE, ewWaitUntilTerminated, ResultCode) or (ResultCode <> 0) then
  begin
    SuppressibleMsgBox('Não foi possível registrar o serviço auxiliar (código ' + IntToStr(ResultCode) + '). '
      + 'O Severino abre, mas não grava o arquivo hosts até você reinstalar.', mbError, MB_OK, IDOK);
    exit;
  end;
  Run(ExpandConstant('{sys}\sc.exe'), 'description {#ServiceName} "Mantém o bloco do Severino no arquivo hosts."');
  { If it ever crashes, Windows starts it again. }
  Run(ExpandConstant('{sys}\sc.exe'), 'failure {#ServiceName} reset= 86400 actions= restart/5000/restart/5000/restart/30000');
  Run(ExpandConstant('{sys}\net.exe'), 'start {#ServiceName}');
end;

function InitializeUninstall(): Boolean;
var
  Installed: String;
begin
  Result := True;
  if not RegQueryStringValue(HKLM, '{#HelperKey}', 'AllowedUserSid', Installed) then
    exit;
  if CompareText(Installed, SidOf(False)) <> 0 then
    Result := SuppressibleMsgBox(
      'O Severino foi instalado para outro usuário do Windows. A desinstalação remove o serviço auxiliar e o bloco do arquivo hosts, '
      + 'mas não consegue tirar a CA local, a inicialização automática e as exceções de proxy daquele usuário.' + #13#10#13#10
      + 'Para removê-las também, cancele, entre com aquele usuário, abra o Severino e use Configurações > Limpar tudo.' + #13#10#13#10
      + 'Desinstalar mesmo assim?', mbConfirmation, MB_YESNO, IDYES) = IDYES;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Data: String;
begin
  if CurUninstallStep <> usPostUninstall then
    exit;
  Data := ExpandConstant('{localappdata}\Severino');
  if DirExists(Data) and (SuppressibleMsgBox(
      'Apagar também as suas rotas e configurações?' + #13#10#13#10
      + 'Ficam em ' + Data + '. Se você reinstalar o Severino, elas voltam como estavam.',
      mbConfirmation, MB_YESNO or MB_DEFBUTTON2, IDNO) = IDYES) then
    DelTree(Data, True, True, True);
end;
