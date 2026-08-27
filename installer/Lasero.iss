#define AppName "Lasero Desktop"
#define AppPublisher "Lasero"
#define AppExeName "Lasero.App.exe"
#define AppUrl "https://lasero.net"

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif

#ifndef PublishDir
  #define PublishDir "..\artifacts\publish\win-x64"
#endif

[Setup]
AppId={{4B9D72D8-4F8A-4E8D-9A4C-0F7C2E319A61}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}
AppUpdatesURL={#AppUrl}
DefaultDirName={localappdata}\Programs\Lasero
DefaultGroupName=Lasero
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\artifacts\installer
OutputBaseFilename=Lasero-Desktop-Setup-{#AppVersion}
SetupIconFile=..\Lasero.App\Assets\Lasero.ico
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
WizardSizePercent=110
DisableWelcomePage=no
DisableReadyPage=no
AllowNoIcons=yes
CloseApplications=yes
RestartApplications=no
ChangesAssociations=yes
MinVersion=10.0.17763
VersionInfoVersion={#AppVersion}.0
VersionInfoCompany={#AppPublisher}
VersionInfoDescription=Instalace aplikace Lasero Desktop
VersionInfoProductName={#AppName}
VersionInfoProductVersion={#AppVersion}

[Languages]
Name: "czech"; MessagesFile: "compiler:Languages\Czech.isl"

[Tasks]
Name: "desktopicon"; Description: "Vytvořit zástupce na ploše"; GroupDescription: "Zástupci:"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Lasero"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; AppUserModelID: "Lasero.Desktop"
Name: "{autodesktop}\Lasero"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon; AppUserModelID: "Lasero.Desktop"

[Registry]
Root: HKCU; Subkey: "Software\Classes\.lasero"; ValueType: string; ValueName: ""; ValueData: "Lasero.Project"; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\Lasero.Project"; ValueType: string; ValueName: ""; ValueData: "Projekt Lasero"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\Lasero.Project\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#AppExeName},0"
Root: HKCU; Subkey: "Software\Classes\Lasero.Project\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExeName}"" ""%1"""

[Run]
Filename: "{app}\{#AppExeName}"; Description: "Spustit Lasero"; Flags: nowait postinstall skipifsilent

[Code]
procedure InitializeWizard;
begin
  WizardForm.WelcomeLabel1.Caption := 'Vítejte v instalaci Lasero';
  WizardForm.WelcomeLabel2.Caption :=
    'Lasero vás provede od návrhu přes výběr materiálu až po bezpečné spuštění gravírování.' + #13#10 + #13#10 +
    'První přihlášení vyžaduje internet. Návrh, vzorník a ovládání připojené gravírky potom fungují také offline.' + #13#10 + #13#10 +
    'Instalace nezmění ani neodstraní LightBurn.';
end;

function UpdateReadyMemo(Space, NewLine, MemoUserInfoInfo, MemoDirInfo, MemoTypeInfo,
  MemoComponentsInfo, MemoGroupInfo, MemoTasksInfo: String): String;
begin
  Result :=
    'Lasero se nainstaluje pro váš účet bez administrátorského oprávnění.' + NewLine + NewLine +
    MemoDirInfo + NewLine +
    MemoTasksInfo + NewLine + NewLine +
    'Přechod z LightBurnu' + NewLine +
    '• LightBurn zůstane beze změny a můžete jej dál používat.' + NewLine +
    '• Návrhy přenesete přes SVG, PNG, JPG nebo G-code.' + NewLine +
    '• Soubory .lbrn a .lbrn2 se zatím neimportují přímo.' + NewLine + NewLine +
    'Po instalaci: přihlaste se, připojte zařízení a před první úlohou použijte rámování.';
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if CurPageID = wpFinished then
  begin
    WizardForm.FinishedHeadingLabel.Caption := 'Lasero je připraveno';
    WizardForm.FinishedLabel.Caption :=
      'Spusťte aplikaci a pokračujte třemi kroky: vytvořte návrh, vyberte materiál a připojte gravírku.';
  end;
end;
