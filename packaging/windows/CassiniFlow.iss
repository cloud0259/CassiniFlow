; CassiniFlow Inno Setup Script
; Generates CassiniFlow-Setup-x64.exe installer for Windows 64-bit

#ifndef MyAppVersion
#define MyAppVersion "1.0.0"
#endif

#define MyAppName "CassiniFlow"
#define MyAppPublisher "CassiniFlow Team"
#define MyAppURL "https://github.com/cloud0259/CassiniFlow"
#define MyAppExeName "CassiniFlow.Desktop.exe"
#define MyCliExeName "CassiniFlow.Cli.exe"

[Setup]
AppId={{5B246BC3-F86D-4D3B-A873-5D1BC703F8A1}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
AllowNoIcons=yes
OutputDir=..\..\release-artifacts
OutputBaseFilename=CassiniFlow-Setup-x64
SetupIconFile=..\..\src\CassiniFlow.Desktop\Assets\cassiniflow.ico
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog commandline
ChangesAssociations=yes

[Languages]
Name: "french"; MessagesFile: "compiler:Languages\French.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "assoc_ser"; Description: "Associer les vidéos astronomiques SER (.ser) avec CassiniFlow"; GroupDescription: "Associations de fichiers :"
Name: "assoc_fits"; Description: "Associer les images astronomiques FITS (.fit, .fits) avec CassiniFlow"; GroupDescription: "Associations de fichiers :"; Flags: unchecked

[Files]
Source: "..\..\staging\win-x64\Desktop\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\..\staging\win-x64\Cli\*"; DestDir: "{app}\cli"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\..\README.md"; DestDir: "{app}"; Flags: ignoreversion isreadme
Source: "..\..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\Assets\cassiniflow.ico"
Name: "{group}\{#MyAppName} CLI"; Filename: "{app}\cli\{#MyCliExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\Assets\cassiniflow.ico"; Tasks: desktopicon

[Registry]
; SER file association
Root: HKA; Subkey: "Software\Classes\.ser"; ValueType: string; ValueName: ""; ValueData: "CassiniFlow.SerFile"; Flags: uninsdeletevalue; Tasks: assoc_ser
Root: HKA; Subkey: "Software\Classes\CassiniFlow.SerFile"; ValueType: string; ValueName: ""; ValueData: "Séquence vidéo astronomique SER"; Flags: uninsdeletekey; Tasks: assoc_ser
Root: HKA; Subkey: "Software\Classes\CassiniFlow.SerFile\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\Assets\cassiniflow.ico,0"; Tasks: assoc_ser
Root: HKA; Subkey: "Software\Classes\CassiniFlow.SerFile\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""; Tasks: assoc_ser

; FITS file association
Root: HKA; Subkey: "Software\Classes\.fits"; ValueType: string; ValueName: ""; ValueData: "CassiniFlow.FitsFile"; Flags: uninsdeletevalue; Tasks: assoc_fits
Root: HKA; Subkey: "Software\Classes\.fit"; ValueType: string; ValueName: ""; ValueData: "CassiniFlow.FitsFile"; Flags: uninsdeletevalue; Tasks: assoc_fits
Root: HKA; Subkey: "Software\Classes\CassiniFlow.FitsFile"; ValueType: string; ValueName: ""; ValueData: "Image astronomique FITS"; Flags: uninsdeletekey; Tasks: assoc_fits
Root: HKA; Subkey: "Software\Classes\CassiniFlow.FitsFile\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\Assets\cassiniflow.ico,0"; Tasks: assoc_fits
Root: HKA; Subkey: "Software\Classes\CassiniFlow.FitsFile\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""; Tasks: assoc_fits

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

