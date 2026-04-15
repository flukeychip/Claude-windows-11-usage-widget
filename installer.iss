#define AppName "Claude Taskbar Widget"
#define AppVersion "1.2.0"
#define AppPublisher "flukeychip"
#define AppExeName "TaskbarWidget.exe"
#define BuildDir "bin\x64\Release\net48"

[Setup]
AppId={{A3F7E2B1-4C9D-4E8F-B2A1-7D3C5E9F1234}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir=installer_output
OutputBaseFilename=ClaudeTaskbarWidget_Setup
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
CloseApplicationsFilter=TaskbarWidget.exe
RestartApplications=yes
; Don't require admin — install to user's local programs if no admin
PrivilegesRequiredOverridesAllowed=dialog
PrivilegesRequired=lowest
; Run app after install
[Run]
Filename: "{app}\{#AppExeName}"; Description: "Launch Claude Taskbar Widget"; Flags: nowait postinstall skipifsilent

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "startupentry"; Description: "Start automatically with Windows"; GroupDescription: "Additional options:"

[Files]
; Main exe
Source: "{#BuildDir}\{#AppExeName}";          DestDir: "{app}"; Flags: ignoreversion
; Config file
Source: "{#BuildDir}\{#AppExeName}.config";   DestDir: "{app}"; Flags: ignoreversion
; WebView2 DLLs
Source: "{#BuildDir}\Microsoft.Web.WebView2.Core.dll";     DestDir: "{app}"; Flags: ignoreversion
Source: "{#BuildDir}\Microsoft.Web.WebView2.Wpf.dll";      DestDir: "{app}"; Flags: ignoreversion
Source: "{#BuildDir}\Microsoft.Web.WebView2.WinForms.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#BuildDir}\WebView2Loader.dll";                  DestDir: "{app}"; Flags: ignoreversion
Source: "{#BuildDir}\runtimes\win-x64\native\WebView2Loader.dll"; DestDir: "{app}\runtimes\win-x64\native"; Flags: ignoreversion
; JSON lib
Source: "{#BuildDir}\Newtonsoft.Json.dll";    DestDir: "{app}"; Flags: ignoreversion

[Icons]
; Start Menu shortcut
Name: "{group}\{#AppName}";       Filename: "{app}\{#AppExeName}"
; Desktop shortcut (optional — user can choose during install)
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional options:"; Flags: unchecked

[Registry]
; Auto-start with Windows (only if user checked the task)
Root: HKCU; Subkey: "SOFTWARE\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "TaskbarWidget"; ValueData: """{app}\{#AppExeName}"""; Flags: uninsdeletevalue; Tasks: startupentry

[UninstallRun]
; Kill the app before uninstalling
Filename: "taskkill.exe"; Parameters: "/IM {#AppExeName} /F"; Flags: runhidden; RunOnceId: "KillApp"
