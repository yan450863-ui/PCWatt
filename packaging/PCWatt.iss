#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#ifndef PayloadDir
  #error PayloadDir is required
#endif
#ifndef ReleaseDir
  #error ReleaseDir is required
#endif

[Setup]
AppId={{2D48DE33-FB4A-4C21-89D2-93A600C89D91}
AppName=PC Watt
AppVersion={#AppVersion}
AppPublisher=PC Watt contributors
AppPublisherURL=https://github.com/yan450863-ui/PCWatt
AppSupportURL=https://github.com/yan450863-ui/PCWatt/issues
AppUpdatesURL=https://github.com/yan450863-ui/PCWatt/releases
AppCopyright=Copyright (c) 2026 PC Watt contributors
DefaultDirName={localappdata}\Programs\PCWatt
DefaultGroupName=PC Watt
DisableProgramGroupPage=yes
DisableWelcomePage=no
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir={#ReleaseDir}
OutputBaseFilename=PCWatt-{#AppVersion}-Setup-win-x64
SetupIconFile=..\src\PCWatt.App\Assets\PCWatt.ico
UninstallDisplayIcon={app}\PCWatt.App.exe
LicenseFile=..\LICENSE
InfoBeforeFile=..\docs\INSTALLATION.txt
WizardStyle=modern
WizardSizePercent=110
Compression=lzma2
SolidCompression=yes
CloseApplications=no
RestartApplications=no
Uninstallable=yes
VersionInfoVersion={#AppVersion}.0
VersionInfoDescription=PC Watt 安装程序
VersionInfoProductName=PC Watt

[Languages]
Name: "chinesesimp"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; Flags: checkedonce

[Files]
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb,*.db,*.db-wal,*.db-shm,*.log,package-check.json"

[Icons]
Name: "{group}\PC Watt"; Filename: "{app}\PCWatt.App.exe"; WorkingDir: "{app}"
Name: "{group}\卸载 PC Watt"; Filename: "{uninstallexe}"
Name: "{autodesktop}\PC Watt"; Filename: "{app}\PCWatt.App.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\PCWatt.App.exe"; Description: "打开 PC Watt"; Flags: nowait postinstall skipifsilent

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  StartupCommand: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    { Remove only this installation's entry. User data and other launch targets stay intact. }
    if RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'PC Watt', StartupCommand) then
      if CompareText(StartupCommand, '"' + ExpandConstant('{app}\PCWatt.App.exe') + '" --background') = 0 then
        RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'PC Watt');
  end;
end;
