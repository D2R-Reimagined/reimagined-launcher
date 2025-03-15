[Setup]
AppName=Reimagined Launcher
AppVersion=1.0
DefaultDirName={userappdata}\ReimaginedLauncher
DefaultGroupName=Reimagined Launcher
Uninstallable=yes
DirExistsWarning=no

[Files]
Source: "C:\dev\d2r\reimagined-launcher\ReimaginedLauncherMaui\bin\Release\net9.0-windows10.0.19041.0\win10-x64\publish\*"; DestDir: "{app}"; Flags: recursesubdirs

[Icons]
Name: "{commondesktop}\Reimagined Launcher"; Filename: "{app}\ReimaginedLauncherMaui.exe"
Name: "{group}\Reimagined Launcher"; Filename: "{app}\ReimaginedLauncherMaui.exe"

[Run]
Filename: "{app}\ReimaginedLauncherMaui.exe"; Description: "Launch Reimagined Launcher"; Flags: nowait postinstall skipifsilent

[Code]
procedure CreateUserDataFolder;
var
  UserDataPath: string;
begin
  UserDataPath := ExpandConstant('{userappdata}\ReimaginedLauncher');
  if not DirExists(UserDataPath) then
  begin
    CreateDir(UserDataPath);
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then
  begin
    CreateUserDataFolder;
  end;
end;
