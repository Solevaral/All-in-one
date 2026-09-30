; Установщик All in One (Inno Setup 6.3+ / 7).
; iscc /DAppVersion=0.2.1 /DFlavor=net9 /DSourceExe=..\publish\net9\AllInOne.exe installer\AllInOne.iss
; Flavor: net9 (нужен .NET 9 Desktop Runtime) или standalone (.NET внутри exe).
;
; Раскладка: {app}\AllInOne.exe, {app}\modules\<Имя> — программы, {app}\data — настройки и данные модулей.
; Установщик кладёт только AllInOne.exe; modules и data наполняет сам All in One и при обновлении их не трогает.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef Flavor
  #define Flavor "net9"
#endif
#ifndef SourceExe
  #define SourceExe "..\publish\" + Flavor + "\AllInOne.exe"
#endif

[Setup]
AppId={{6F3C2A8E-4B1D-4E7A-9C5B-2D8E1F0A7B34}
AppName=All in One
AppVersion={#AppVersion}
AppVerName=All in One {#AppVersion}
AppPublisher=Solevaral
AppPublisherURL=https://github.com/Solevaral/All-in-one
AppSupportURL=https://github.com/Solevaral/All-in-one/issues
AppUpdatesURL=https://github.com/Solevaral/All-in-one/releases
DefaultDirName={autopf}\AllInOne
UsePreviousAppDir=yes
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir=..\artifacts
OutputBaseFilename=AllInOne-{#AppVersion}-setup-{#Flavor}
SetupIconFile=..\src\AllInOne.Host\app.ico
UninstallDisplayIcon={app}\AllInOne.exe
UninstallDisplayName=All in One
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
; All in One закрывается командой --exit (модули остаются работать), поэтому Restart Manager не нужен.
CloseApplications=no
RestartApplications=no

[Languages]
Name: "ru"; MessagesFile: "compiler:Languages\Russian.isl"

[Tasks]
Name: "desktopicon"; Description: "Ярлык на рабочем столе"

[Dirs]
Name: "{app}\modules"
Name: "{app}\data"

[Files]
Source: "{#SourceExe}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\All in One"; Filename: "{app}\AllInOne.exe"
Name: "{autodesktop}\All in One"; Filename: "{app}\AllInOne.exe"; Tasks: desktopicon

[Run]
; runascurrentuser: AllInOne.exe требует администратора, а postinstall по умолчанию запускает от обычного
; пользователя — CreateProcess падал с кодом 740. Установщик уже работает от администратора.
Filename: "{app}\AllInOne.exe"; Description: "Запустить All in One"; Flags: nowait postinstall skipifsilent runascurrentuser
; Обновление из самого All in One (/VERYSILENT /UPDATE): запустить новую версию, она подключится к модулям.
Filename: "{app}\AllInOne.exe"; Parameters: "--post-update"; Flags: nowait runascurrentuser; Check: IsUpdateMode

[UninstallRun]
Filename: "{app}\AllInOne.exe"; Parameters: "--stop-all"; Flags: runhidden waituntilterminated; RunOnceId: "StopAllModules"
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /TN AllInOne /F"; Flags: runhidden waituntilterminated; RunOnceId: "DeleteAutostartTask"

[Code]
function IsUpdateMode: Boolean;
var
  I: Integer;
begin
  Result := False;
  for I := 1 to ParamCount do
    if CompareText(ParamStr(I), '/UPDATE') = 0 then
    begin
      Result := True;
      Exit;
    end;
end;

function HasNonAscii(const S: String): Boolean;
var
  I: Integer;
begin
  Result := False;
  for I := 1 to Length(S) do
    if Ord(S[I]) > 127 then
    begin
      Result := True;
      Exit;
    end;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if CurPageID = wpSelectDir then
  begin
    if HasNonAscii(WizardDirValue) then
    begin
      MsgBox('Путь содержит не-латинские символы. zapret из такого пути не запускается.', mbError, MB_OK);
      Result := False;
    end
    else if Pos('onedrive', Lowercase(WizardDirValue)) > 0 then
    begin
      MsgBox('Папка в OneDrive: синхронизация блокирует файлы модулей.', mbError, MB_OK);
      Result := False;
    end;
  end;
end;

{ Закрыть запущенный All in One перед заменой exe. Модули не останавливаются: новая версия подключится к ним. }
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Exe: String;
  Code: Integer;
begin
  Result := '';
  Exe := ExpandConstant('{app}\AllInOne.exe');
  if FileExists(Exe) then
    Exec(Exe, '--exit', '', SW_HIDE, ewWaitUntilTerminated, Code);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  App: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    App := ExpandConstant('{app}');
    if UninstallSilent then
      Exit;
    if MsgBox('Удалить модули и настройки?' + #13#10#13#10 +
              'Да — удалить папки modules и data.' + #13#10 +
              'Нет — оставить их для повторной установки.',
              mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
    begin
      DelTree(App + '\modules', True, True, True);
      DelTree(App + '\data', True, True, True);
      RemoveDir(App);
    end;
  end;
end;
