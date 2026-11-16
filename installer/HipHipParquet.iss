; HipHipParquet Inno Setup Script
; Build: ISCC.exe HipHipParquet.iss /DMyAppVersion=1.2.3 /DSourceDir=..\publish
; SourceDir must contain HipHipParquet.exe

#ifndef MyAppVersion
  #define MyAppVersion "1.16.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish"
#endif

#define MyAppName      "HipHipParquet"
#define MyAppPublisher "jhew"
#define MyAppURL       "https://github.com/jhew/HipHipParquet"
#define MyAppExeName   "HipHipParquet.exe"

[Setup]
AppId={{F3A5C8D2-1B6E-4F9A-B3D7-8E2C5A4F1B9D}}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
AppUpdatesURL={#MyAppURL}/releases
DefaultDirName={autopf}\{#MyAppName}
DisableDirPage=yes
DefaultGroupName={#MyAppName}
AllowNoIcons=yes
LicenseFile=..\LICENSE
OutputDir=..\installer-output
OutputBaseFilename=HipHipParquet-{#MyAppVersion}-Setup
SetupIconFile=..\Assets\app.ico
Compression=lzma/ultra64
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
ChangesAssociations=yes
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
VersionInfoVersion={#MyAppVersion}
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription=Data file viewer and quality analysis tool
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#MyAppVersion}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "fileassoc"; Description: "Add ""Open in Hip Hip Parquet"" to the File Explorer menu for data files"; GroupDescription: "File Explorer:"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Registry]
; HKA writes to HKCU for a per-user install and HKLM for an all-users install.
; The app is added to each type's "Open with" list and right-click menu. It only
; becomes the default app for .parquet/.pqt, and only when nothing has claimed them.
#define ProgId "HipHipParquet.DataFile"
; Written as they appear inside a quoted Inno value, so "" is one literal quote.
#define OpenCmd '""{app}\' + MyAppExeName + '"" ""%1""'
#define AppIcon '""{app}\' + MyAppExeName + '"",0'

Root: HKA; Subkey: "Software\Classes\{#ProgId}"; ValueType: string; ValueName: ""; ValueData: "Data file"; Flags: uninsdeletekey; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\{#ProgId}\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{#AppIcon}"; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\{#ProgId}\shell\open\command"; ValueType: string; ValueName: ""; ValueData: "{#OpenCmd}"; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\Applications\{#MyAppExeName}"; ValueType: string; ValueName: "FriendlyAppName"; ValueData: "Hip Hip Parquet"; Flags: uninsdeletekey; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\Applications\{#MyAppExeName}\shell\open\command"; ValueType: string; ValueName: ""; ValueData: "{#OpenCmd}"; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.parquet"; ValueType: string; ValueName: ""; ValueData: "{#ProgId}"; Flags: createvalueifdoesntexist; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.pqt"; ValueType: string; ValueName: ""; ValueData: "{#ProgId}"; Flags: createvalueifdoesntexist; Tasks: fileassoc

#sub RegisterExtension
  #define Ext Exts[i]
Root: HKA; Subkey: "Software\Classes\{#Ext}\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\Applications\{#MyAppExeName}\SupportedTypes"; ValueType: string; ValueName: "{#Ext}"; ValueData: ""; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\SystemFileAssociations\{#Ext}\shell\HipHipParquet.Open"; ValueType: string; ValueName: ""; ValueData: "Open in Hip Hip Parquet"; Flags: uninsdeletekey; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\SystemFileAssociations\{#Ext}\shell\HipHipParquet.Open"; ValueType: string; ValueName: "Icon"; ValueData: "{#AppIcon}"; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\SystemFileAssociations\{#Ext}\shell\HipHipParquet.Open\command"; ValueType: string; ValueName: ""; ValueData: "{#OpenCmd}"; Tasks: fileassoc
#endsub

#dim Exts[9] {".parquet", ".pqt", ".csv", ".tsv", ".tab", ".json", ".jsonl", ".ndjson", ".xlsx"}
#define i
#for {i = 0; i < DimOf(Exts); i++} RegisterExtension
#undef i

[Icons]
Name: "{group}\{#MyAppName}";          Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}";    Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
