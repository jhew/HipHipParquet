; HipHipParquet Inno Setup Script
; Build: ISCC.exe HipHipParquet.iss /DMyAppVersion=1.2.3 /DSourceDir=..\publish
; SourceDir must contain HipHipParquet.exe

#ifndef MyAppVersion
  #define MyAppVersion "1.14.0"
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

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}";          Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}";    Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
