; DayDeck 설치 프로그램 (Inno Setup 6). scripts/build-installer.ps1이 만든다.
;  - 설치할 폴더를 고를 수 있고, 그 폴더 안의 Data 폴더가 데이터 폴더가 된다 (할 일·일정·메모·설정·백업).
;  - 기본은 "나만 설치"(관리자 권한 필요 없음, %LOCALAPPDATA%\Programs\DayDeck). "모든 사용자용"을 고르면 Program Files에
;    설치하고, Data 폴더에만 사용자 쓰기 권한을 준다 (프로그램 파일은 그대로 보호).
;  - 제거할 때 Data 폴더를 지울지 묻는다 (기본: 남김).

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceExe
  #define SourceExe "..\artifacts\publish\win-x64\DayDeck.exe"
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts\installer"
#endif

[Setup]
AppId={{2C0099C2-52D9-4541-89E5-D7E43C249320}
AppName=DayDeck
AppVersion={#AppVersion}
AppVerName=DayDeck {#AppVersion}
AppPublisher=에이든초이
AppPublisherURL=https://github.com/aydenchoi/DayDeck
AppSupportURL=https://github.com/aydenchoi/DayDeck/issues
VersionInfoVersion={#AppVersion}
DefaultDirName={autopf}\DayDeck
DisableDirPage=no
UsePreviousAppDir=yes
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog commandline
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir={#OutputDir}
OutputBaseFilename=DayDeck-{#AppVersion}-setup
SetupIconFile=..\src\DayDeck.App\Assets\app.ico
UninstallDisplayIcon={app}\DayDeck.exe
UninstallDisplayName=DayDeck
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
; 실행 중인 DayDeck을 닫고 설치·제거한다 (트레이에 있어도).
CloseApplications=force
RestartApplications=no

[Languages]
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"

[Messages]
SelectDirBrowseLabel=계속하려면 다음을 클릭합니다. 다른 폴더를 선택하려면 찾아보기를 클릭합니다.%n%n데이터(할 일·일정·메모·설정·백업)는 이 폴더 안의 Data 폴더에 저장됩니다. 이 폴더를 통째로 복사하면 다른 PC나 USB에서도 그대로 쓸 수 있습니다.

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Dirs]
; 모든 사용자용(Program Files) 설치에서도 앱이 Data에 쓸 수 있게 한다. 제거할 때는 지우지 않는다 (아래 [Code]에서 묻는다).
Name: "{app}\Data"; Permissions: users-modify; Flags: uninsneveruninstall; Check: IsAdminInstallMode
Name: "{app}\Data"; Flags: uninsneveruninstall; Check: not IsAdminInstallMode

[Files]
Source: "{#SourceExe}"; DestDir: "{app}"; DestName: "DayDeck.exe"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\DayDeck"; Filename: "{app}\DayDeck.exe"
Name: "{autodesktop}\DayDeck"; Filename: "{app}\DayDeck.exe"; Tasks: desktopicon

[Registry]
; 앱이 등록한 로그인 자동 실행은 제거할 때 지운다 (설치할 때는 만들지 않음).
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueName: "DayDeck"; ValueType: none; Flags: uninsdeletevalue

[Run]
Filename: "{app}\DayDeck.exe"; Description: "{cm:LaunchProgram,DayDeck}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; 업데이트 후 남은 지난 버전 파일
Type: files; Name: "{app}\*.old"

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep <> usPostUninstall then
    Exit;
  DataDir := ExpandConstant('{app}\Data');
  if not DirExists(DataDir) then
    Exit;
  // 조용히 제거(/SUPPRESSMSGBOXES)할 때는 "아니요": 데이터를 남긴다.
  if SuppressibleMsgBox('DayDeck 데이터(할 일·일정·메모·설정·백업)도 지울까요?' + #13#10 + #13#10 +
      DataDir + #13#10 + #13#10 + '"아니요"를 누르면 이 폴더가 그대로 남아, 다시 설치하면 이어서 쓸 수 있습니다.',
      mbConfirmation, MB_YESNO or MB_DEFBUTTON2, IDNO) = IDYES then
  begin
    DelTree(DataDir, True, True, True);
    // 설치 폴더가 비었으면 지운다 (사용자가 넣어 둔 다른 파일이 있으면 남김).
    RemoveDir(ExpandConstant('{app}'));
  end;
end;
