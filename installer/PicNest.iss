; PicNest per-user Windows installer.
; The published folder is intentionally packaged as files rather than a .NET
; single-file bundle because PicNest loads native LibVLC and OpenCV components.

#define MyAppName "PicNest"
#define MyAppPublisher "PicNest"
#define MyAppURL "https://github.com/TriMickTri/picnest"
#define MyAppExeName "PicNest.exe"
#define MyPublishDir AddBackslash(SourcePath) + "..\artifacts\publish\win-x64"

#ifndef MyAppVersion
  #define MyAppVersion "0.1.0"
#endif

[Setup]
AppId={{8D8C8ACB-7B58-4F59-8A93-764B098B18D7}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
AppUpdatesURL={#MyAppURL}/releases
DefaultDirName={localappdata}\Programs\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
OutputDir={#MyPublishDir}\..\..\installer
OutputBaseFilename=PicNest-Setup-{#MyAppVersion}-win-x64
SetupIconFile=..\Assets\picnest.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "{#MyPublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "libvlc\win-arm64\*,libvlc\win-x86\*,*.pdb"

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent

; Deliberately no [UninstallDelete] rule for %LOCALAPPDATA%\PicNest. It contains
; the user's catalog, preferences, and derived thumbnails, not application files.
