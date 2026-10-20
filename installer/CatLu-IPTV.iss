#ifndef AppVersion
  #error Запустите installer\build.ps1 для сборки релиза.
#endif
#define AppName "CatLu IPTV"

[Setup]
AppId={{613B55C6-0CA0-4642-83A2-940097CA42EA}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=CatLu
AppPublisherURL=https://github.com/Maksimasz/windows-iptv-player
DefaultDirName={localappdata}\Programs\CatLu IPTV
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
DisableProgramGroupPage=yes
AllowNoIcons=yes
UninstallDisplayIcon={app}\CatLu IPTV.exe
SetupIconFile=..\IptvPlayer\Assets\catlu.ico
OutputDir=..\release\{#AppVersion}
OutputBaseFilename=CatLu-IPTV-{#AppVersion}-Setup-x64
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Создать значок на рабочем столе"; GroupDescription: "Ярлыки:"; Flags: unchecked

[Files]
Source: "..\release\{#AppVersion}\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\CatLu IPTV"; Filename: "{app}\CatLu IPTV.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\CatLu IPTV"; Filename: "{app}\CatLu IPTV.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\CatLu IPTV.exe"; Description: "Запустить CatLu IPTV"; Flags: nowait postinstall skipifsilent
