#define AppName "UnifiedCalendar"
#define AppPublisher "Logic and Design Inc."
#define AppExeName "UnifiedCalendar.App.exe"
; Permanent application/uninstaller contract. Never change after release; keep AppIdentity in sync.
#define RunningApplicationMutexName "Local\UnifiedCalendar.Running"
#define PublishDir AddBackslash(SourcePath) + "..\artifacts\publish\win-x64"
#define AppExePath AddBackslash(PublishDir) + AppExeName
#define InstallerOutputDir AddBackslash(SourcePath) + "..\artifacts\installer"

#if !FileExists(AppExePath)
  #error "Published application not found. Run build.ps1 before building the installer."
#endif

; ProductVersion is user-visible, while NumericAppVersion supplies the four-part numeric executable version resource.
#define AppVersion GetStringFileInfo(AppExePath, "ProductVersion")
#define NumericAppVersion GetVersionNumbersString(AppExePath)

[Setup]
; This AppId is the permanent product identity. Never change it: doing so creates a second installation instead of upgrading.
AppId={{B4ABCCB7-6C6A-4EEC-A270-91B8F1054CD8}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
UninstallDisplayName={#AppName}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#InstallerOutputDir}
OutputBaseFilename={#AppName}-Setup-{#AppVersion}
SetupIconFile=..\src\UnifiedCalendar.App\Resources\UnifiedCalendar.ico
UninstallDisplayIcon={app}\{#AppExeName}
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
CloseApplications=yes
CloseApplicationsFilter={#AppExeName}
RestartApplications=no
VersionInfoVersion={#NumericAppVersion}

#ifdef SIGN
SignTool=MySignTool
SignToolRetryCount=2
SignedUninstaller=yes
#endif

[Languages]
Name: "japanese"; MessagesFile: "compiler:Languages\Japanese.isl"

[Files]
Source: "..\THIRD-PARTY-NOTICES.txt"; DestDir: "{app}"; Flags: ignoreversion
; Keep non-PE payloads in the package without passing them to SignTool.
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "*.exe,*.dll"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PublishDir}\*.exe"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs signonce
Source: "{#PublishDir}\*.dll"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs signonce

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"

[Registry]
; The application is the only writer during installation. This entry only removes a stale per-user startup value on uninstall.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "UnifiedCalendar"; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{#AppName} を起動する"; Flags: nowait postinstall skipifsilent

[Messages]
ErrorCloseApplications=UnifiedCalendar を自動的に終了できませんでした。通知領域の UnifiedCalendar アイコンを右クリックし、「終了」を選んでから続行してください。

[Code]
function InitializeUninstall(): Boolean;
begin
  Result := True;
  while CheckForMutexes('{#RunningApplicationMutexName}') do
  begin
    if MsgBox(
      'UnifiedCalendar が実行中です。通知領域の UnifiedCalendar アイコンを右クリックし、「終了」を選んでから続行してください。',
      mbError,
      MB_OKCANCEL) = IDCANCEL then
    begin
      Result := False;
      Exit;
    end;
  end;
end;
