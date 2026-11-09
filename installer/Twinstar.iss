; Twinstar installer (Inno Setup 6). Build with installer\build.ps1.
; Installs for the current user only, so it never asks for administrator rights.
; Compile with /DSANDBOX to test: startup settings then go to a throwaway registry key,
; and nothing belonging to an existing install is stopped or removed.

#define AppName "Twinstar"
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

#ifdef SANDBOX
  #define WinlogonKey "Software\TwinstarInstallerTest\Winlogon"
  #define RunKey "Software\TwinstarInstallerTest\Run"
#else
  #define WinlogonKey "Software\Microsoft\Windows NT\CurrentVersion\Winlogon"
  #define RunKey "Software\Microsoft\Windows\CurrentVersion\Run"
#endif

[Setup]
AppId={{7E4B3F52-9C1D-4A86-B0E7-2D5C8F61A3B9}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=Nikolas1201
AppPublisherURL=https://github.com/Nikolas1201/twinstar
AppSupportURL=https://github.com/Nikolas1201/twinstar/issues
DefaultDirName={localappdata}\Programs\Twinstar
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=..\dist
OutputBaseFilename=Twinstar-Setup-{#AppVersion}
SetupIconFile=..\assets\twinstar.ico
UninstallDisplayIcon={app}\Twinstar.exe
UninstallDisplayName={#AppName}
Compression=lzma2/ultra64
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
WizardStyle=modern

[Tasks]
Name: "startup"; Description: "Open Twinstar after Windows starts (recommended)"; GroupDescription: "When you sign in to Windows:"; Flags: exclusive
Name: "shell"; Description: "Replace the Windows desktop with Twinstar (full console mode; takes effect after you sign out and back in)"; GroupDescription: "When you sign in to Windows:"; Flags: exclusive unchecked
Name: "manual"; Description: "Do nothing; I'll open Twinstar myself"; GroupDescription: "When you sign in to Windows:"; Flags: exclusive unchecked
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
Source: "..\dist\publish\Twinstar.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{userprograms}\Twinstar"; Filename: "{app}\Twinstar.exe"
Name: "{userdesktop}\Twinstar"; Filename: "{app}\Twinstar.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Twinstar.exe"; Description: "Open Twinstar now"; Flags: postinstall nowait skipifsilent unchecked

[Code]
const
  WinlogonKey = '{#WinlogonKey}';
  RunKey = '{#RunKey}';

function ShellMentions(Name: String): Boolean;
var
  Value: String;
begin
  Result := RegQueryStringValue(HKCU, WinlogonKey, 'Shell', Value)
    and (Pos(Lowercase(Name), Lowercase(Value)) > 0);
end;

{ Removes only our own startup entries: a Shell value belonging to something else is left alone. }
procedure ClearStartup(Name: String);
begin
  if ShellMentions(Name) then RegDeleteValue(HKCU, WinlogonKey, 'Shell');
  RegDeleteValue(HKCU, RunKey, Name);
end;

procedure StopProcess(ExeName: String);
var
  Code: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM ' + ExeName, '', SW_HIDE, ewWaitUntilTerminated, Code);
end;

procedure StopRunningCopies;
begin
#ifndef SANDBOX
  StopProcess('Twinstar.exe');
  StopProcess('HybridConsole.exe'); { the app's name before 1.0 }
#endif
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopRunningCopies;
  Result := '';
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Exe: String;
begin
  if CurStep <> ssPostInstall then Exit;
  Exe := '"' + ExpandConstant('{app}\Twinstar.exe') + '"';
  ClearStartup('Twinstar');
  ClearStartup('HybridConsole');
  if WizardIsTaskSelected('shell') then RegWriteStringValue(HKCU, WinlogonKey, 'Shell', Exe);
  if WizardIsTaskSelected('startup') then RegWriteStringValue(HKCU, RunKey, 'Twinstar', Exe);
#ifndef SANDBOX
  DelTree(ExpandConstant('{localappdata}\Programs\HybridConsole'), True, True, True);
#endif
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Code: Integer;
begin
  if CurUninstallStep = usUninstall then
  begin
    StopRunningCopies;
    ClearStartup('Twinstar');
    ClearStartup('HybridConsole');
    { Twinstar may have been the desktop: make sure Windows has one again. }
    if FindWindowByClassName('Shell_TrayWnd') = 0 then
      Exec(ExpandConstant('{win}\explorer.exe'), '', '', SW_SHOW, ewNoWait, Code);
  end;
  if (CurUninstallStep = usPostUninstall) and not UninstallSilent then
    if MsgBox('Also delete your Twinstar settings and downloaded artwork?', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
      DelTree(ExpandConstant('{userappdata}\Twinstar'), True, True, True);
end;
