; LotusSetup.iss
; Inno Setup script for the Lotus browser.
; Built by build-installer.ps1, which passes /DMyAppVersion and /DPublishDir.
; Get Inno Setup here: https://jrsoftware.org/isdl.php

#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif
#ifndef PublishDir
  #define PublishDir "publish\win-x64"
#endif

#define MyAppName "Lotus"
#define MyAppPublisher "Allen Lambert Jr"
#define MyAppExeName "Lotus.exe"

[Setup]
AppId={{B6C1B9D1-4B7E-4B0E-9B8E-8C7D4E9B6F31}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
UninstallDisplayIcon={app}\{#MyAppExeName}
OutputDir=installer-output
OutputBaseFilename=Setup-Lotus-{#MyAppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
SetupIconFile=icon.ico

; Lotus writes its default-browser registration to HKCU, so a per-user
; install (no admin prompt) keeps everything under the same registry hive.
; Switch to "admin" instead if you'd rather install to Program Files for
; all users.
PrivilegesRequired=lowest
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"
Name: "launchafter"; Description: "Launch {#MyAppName} after installation"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
; Register Lotus as a default-browser candidate right away so it shows up
; in Windows Settings > Default apps the first time the user looks.
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent; Tasks: launchafter

[UninstallDelete]
; Clean up any user data Lotus writes at runtime (adjust path if different).
Type: filesandordirs; Name: "{userappdata}\Lotus"

[Code]
// Checks whether the WebView2 Runtime is installed (Lotus depends on it).
// If missing, points the user at Microsoft's evergreen bootstrapper instead
// of silently failing to launch after install.
function IsWebView2Installed(): Boolean;
var
  Version: String;
begin
  Result :=
    RegQueryStringValue(HKLM, 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', Version) or
    RegQueryStringValue(HKCU, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', Version);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ErrorCode: Integer;
begin
  if (CurStep = ssPostInstall) and (not IsWebView2Installed()) then
  begin
    if MsgBox('Lotus requires the Microsoft Edge WebView2 Runtime, which was not detected on this PC. Open the download page now?',
       mbConfirmation, MB_YESNO) = IDYES then
    begin
      ShellExec('open', 'https://developer.microsoft.com/microsoft-edge/webview2/', '', '', SW_SHOW, ewNoWait, ErrorCode);
    end;
  end;
end;
