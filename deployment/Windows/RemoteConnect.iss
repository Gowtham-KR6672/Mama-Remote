; Inno Setup 6 script for a signed-capable Windows installer (https://jrsoftware.org/isinfo.php).
; Build after scripts\publish-windows.ps1:
;   "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" deployment\Windows\RemoteConnect.iss
; Sign the output with: signtool sign /fd SHA256 /tr <timestamp-url> /td SHA256 /a artifacts\RemoteConnect-Setup-<ver>.exe
; Per-user install by default (no admin); unattended access is never enabled by the installer.

#define AppName "RemoteConnect"
#define AppVersion "0.1.0"
#define AppExe "RemoteConnect.Desktop.exe"
#define SourceDir "..\..\artifacts\RemoteConnect-win-x64"

[Setup]
AppId={{6B2E7C1A-3F1D-4E4B-9D7A-52C1E0A9B7F3}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=RemoteConnect
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\..\artifacts
OutputBaseFilename=RemoteConnect-Setup-{#AppVersion}
SetupIconFile=..\..\src\Desktop\RemoteConnect.Desktop\Assets\app.ico
UninstallDisplayIcon={app}\{#AppExe}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes

[Tasks]
Name: "autostart"; Description: "Start {#AppName} when I sign in (waits in the tray, ready for connections)"; Flags: checkedonce
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked
Name: "firewall"; Description: "Allow LAN connections through Windows Firewall (Private/Domain networks)"; Check: IsAdminInstallMode

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#AppName}"; ValueData: """{app}\{#AppExe}"" --autostart"; Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""RemoteConnect (TCP-In)"" dir=in action=allow program=""{app}\{#AppExe}"" protocol=TCP localport=47070 profile=private,domain"; Flags: runhidden; Tasks: firewall
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""RemoteConnect discovery (UDP-In)"" dir=in action=allow program=""{app}\{#AppExe}"" protocol=UDP localport=47071 profile=private,domain"; Flags: runhidden; Tasks: firewall
Filename: "{app}\{#AppExe}"; Description: "Start {#AppName} now"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/IM {#AppExe} /F"; Flags: runhidden; RunOnceId: "StopApp"
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""RemoteConnect (TCP-In)"""; Flags: runhidden; RunOnceId: "FwTcp"; Check: IsAdminInstallMode
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""RemoteConnect discovery (UDP-In)"""; Flags: runhidden; RunOnceId: "FwUdp"; Check: IsAdminInstallMode
