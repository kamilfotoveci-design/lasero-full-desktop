; LASERO Desktop installer (Inno Setup 6.5+)
; Build with:  .\build-installer.ps1   (passes AppVersion, AppVersionNumeric, PublishDir, SignTool)
; Encoding of this file: UTF-8 with BOM (required for the Czech strings).

#define AppName "LASERO Desktop"
#define AppPublisher "LASERO"
#define AppExeName "Lasero.App.exe"
#define AppUrl "https://lasero.net"
#define AppGuid "{4B9D72D8-4F8A-4E8D-9A4C-0F7C2E319A61}"

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#ifndef AppVersionNumeric
  #define AppVersionNumeric "0.1.0.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\artifacts\publish\win-x64-0.1.0"
#endif

[Setup]
; Same AppId as before: a newer setup upgrades the existing installation in place.
AppId={{#AppGuid}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}
AppUpdatesURL={#AppUrl}
AppCopyright=Copyright (C) 2026 {#AppPublisher}
SetupMutex=LaseroDesktopSetupMutex

; Per-user by default (no UAC); the first dialog offers "for all users" which elevates.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
DefaultDirName={autopf}\{#AppName}
UsePreviousAppDir=yes
DisableProgramGroupPage=yes
AllowNoIcons=yes
; 64-bit only. Allowed architectures and the Windows build are checked in code so the
; refusal message is clear and Czech.
ArchitecturesInstallIn64BitMode=x64compatible

OutputDir=..\artifacts\installer
OutputBaseFilename=Lasero-Desktop-Setup-{#AppVersion}
SetupIconFile=assets\lasero-setup.ico
UninstallDisplayIcon={app}\lasero-uninstall.ico
UninstallDisplayName={#AppName}
WizardStyle=modern
WizardSizePercent=110
WizardImageFile=assets\wizard-164.bmp,assets\wizard-192.bmp,assets\wizard-246.bmp
WizardSmallImageFile=assets\small-55.bmp,assets\small-64.bmp,assets\small-80.bmp,assets\small-96.bmp
Compression=lzma2/ultra64
SolidCompression=yes
LZMAUseSeparateProcess=yes

CloseApplications=yes
RestartApplications=no
ChangesAssociations=yes
ShowLanguageDialog=auto
UsePreviousLanguage=no

VersionInfoVersion={#AppVersionNumeric}
VersionInfoProductVersion={#AppVersionNumeric}
VersionInfoProductTextVersion={#AppVersion}
VersionInfoCompany={#AppPublisher}
VersionInfoProductName={#AppName}
VersionInfoDescription={#AppName} - instalace
VersionInfoCopyright=Copyright (C) 2026 {#AppPublisher}
VersionInfoOriginalFileName=Lasero-Desktop-Setup-{#AppVersion}.exe

#ifdef SignTool
SignTool=lasero
SignedUninstaller=yes
#endif

[Languages]
Name: "czech"; MessagesFile: "compiler:Languages\Czech.isl"; LicenseFile: "license-cs.txt"; InfoBeforeFile: "info-before.txt"; InfoAfterFile: "info-after.txt"
Name: "english"; MessagesFile: "compiler:Default.isl"; LicenseFile: "license-en.txt"; InfoBeforeFile: "info-before-en.txt"; InfoAfterFile: "info-after-en.txt"

[Messages]
czech.WelcomeLabel1=Vítejte v instalaci aplikace [name]
czech.WelcomeLabel2=Průvodce nainstaluje aplikaci [name/ver] pro návrh, přípravu a ovládání laserových gravírek.%n%nJde o testovací verzi. Doporučuje se před pokračováním ukončit spuštěné kopie aplikace a uložit rozdělanou práci.%n%nLightBurn ani jiný software se instalací nemění.
czech.FinishedHeadingLabel=Aplikace [name] je nainstalována
czech.FinishedLabel=Aplikaci lze spustit pomocí zástupce v nabídce Start nebo volbou níže.
english.WelcomeLabel1=Welcome to the [name] setup
english.WelcomeLabel2=This will install [name/ver] for designing, preparing and running laser engravers.%n%nThis is a test version. Close any running copy of the application and save your work before continuing.%n%nLightBurn and other software are not changed.
english.FinishedHeadingLabel=[name] is installed
english.FinishedLabel=The application can be started from the Start menu shortcut or with the option below.

[CustomMessages]
czech.TaskDesktop=Vytvořit zástupce na ploše
czech.TaskAssoc=Otevírat soubory .lasero aplikací {#AppName}
czech.TaskGroup=Zástupci a přiřazení souborů:
czech.RunApp=Spustit LASERO
czech.WinTooOld=Aplikace %1 vyžaduje Windows 10 verze 1809 (sestavení 17763) nebo novější, případně Windows 11.%n%nTento počítač má starší verzi systému (%2). Je nutné nejprve aktualizovat Windows. Instalace bude ukončena.
czech.Need64=Aplikace {#AppName} vyžaduje 64bitový Windows (x64) a na tomto počítači ji nelze nainstalovat.
czech.AppRunning=Aplikace {#AppName} je právě spuštěná.%n%nUložte rozdělanou práci, aplikaci ukončete a zvolte Opakovat. Volba Storno instalaci přeruší.
czech.UninstRunning=Aplikace {#AppName} je právě spuštěná.%n%nUložte rozdělanou práci, aplikaci ukončete a zvolte Opakovat. Volba Storno odinstalaci přeruší.
czech.ReadyUser=Instalace proběhne pouze pro aktuálního uživatele, bez oprávnění správce.
czech.ReadyAll=Instalace proběhne pro všechny uživatele tohoto počítače.
czech.ReadyUpgrade=Nalezena nainstalovaná verze %1. Bude aktualizována na verzi {#AppVersion}; projekty a nastavení zůstanou zachovány.
czech.ReadyLightBurn=Přechod z LightBurnu:%n  - LightBurn zůstane beze změny a lze jej dál používat%n  - návrhy je možné přenést přes SVG, PNG, JPG nebo G-code%n  - soubory .lbrn a .lbrn2 se zatím přímo neimportují
czech.UsbCaption=Ovladač USB pro gravírku
czech.UsbDesc=Nepovinné - pouze pokud se gravírka po připojení neobjeví jako port COM
czech.UsbText=Většina gravírek se připojuje přes USB převodník. Windows 10 a 11 obvykle potřebný ovladač doplní samy při prvním připojení. Pokud se ve Správci zařízení v části Porty (COM a LPT) žádný nový port neobjeví nebo u zařízení svítí žlutý vykřičník, může chybět ovladač podle použitého čipu:%n%n  - CH340 / CH341 (WCH): https://www.wch-ic.com%n  - CP210x (Silicon Labs): https://www.silabs.com%n  - FTDI: https://ftdichip.com%n%nOvladač je vhodné stahovat jen ze stránky výrobce čipu nebo stroje. Instalátor nic nestahuje ani neinstaluje automaticky.
czech.PurgeQuestion=Odstranit také uživatelská data aplikace {#AppName} (nastavení, přihlášení, zálohy projektů, historii úloh a protokoly) ze složky%n%n%1%n%nSoubory projektů .lasero uložené jinde zůstanou zachovány. Smazaná data nelze obnovit.%n%nVolba Ano uživatelská data odstraní, volba Ne je zachová.
english.TaskDesktop=Create a desktop shortcut
english.TaskAssoc=Open .lasero files with {#AppName}
english.TaskGroup=Shortcuts and file association:
english.RunApp=Launch LASERO
english.WinTooOld=%1 requires Windows 10 version 1809 (build 17763) or newer, or Windows 11.%n%nThis computer runs an older system (%2). Update Windows first. Setup will now exit.
english.Need64={#AppName} requires 64-bit Windows (x64) and cannot be installed on this computer.
english.AppRunning={#AppName} is currently running.%n%nSave your work, close the application and choose Retry. Cancel aborts the installation.
english.UninstRunning={#AppName} is currently running.%n%nSave your work, close the application and choose Retry. Cancel aborts the uninstallation.
english.ReadyUser=Installs for the current user only, without administrator rights.
english.ReadyAll=Installs for all users of this computer.
english.ReadyUpgrade=Installed version %1 was found. It will be updated to {#AppVersion}; projects and settings are kept.
english.ReadyLightBurn=Moving from LightBurn:%n  - LightBurn is left unchanged and can still be used%n  - designs can be carried over via SVG, PNG, JPG or G-code%n  - .lbrn and .lbrn2 files are not imported directly yet
english.UsbCaption=USB driver for the engraver
english.UsbDesc=Optional - only if the engraver does not appear as a COM port after connecting
english.UsbText=Most engravers connect through a USB serial chip. Windows 10 and 11 usually add the driver automatically on first connection. If no new port appears under Ports (COM and LPT) in Device Manager, or the device has a yellow warning mark, the driver for the chip may be missing:%n%n  - CH340 / CH341 (WCH): https://www.wch-ic.com%n  - CP210x (Silicon Labs): https://www.silabs.com%n  - FTDI: https://ftdichip.com%n%nDownload drivers only from the chip or machine manufacturer. Setup downloads and installs nothing automatically.
english.PurgeQuestion=Also remove the user data of {#AppName} (settings, sign-in, project backups, job history and logs) from the folder%n%n%1%n%n.lasero project files saved elsewhere are kept. Deleted data cannot be restored.%n%nYes removes the user data, No keeps it.

[Tasks]
Name: "desktopicon"; Description: "{cm:TaskDesktop}"; GroupDescription: "{cm:TaskGroup}"; Flags: unchecked
Name: "fileassoc"; Description: "{cm:TaskAssoc}"; GroupDescription: "{cm:TaskGroup}"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "assets\lasero-uninstall.ico"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\THIRD_PARTY_NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; AppUserModelID: "Lasero.Desktop"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon; AppUserModelID: "Lasero.Desktop"

[Registry]
; HKA = HKCU for a per-user install, HKLM for an all-users install.
Root: HKA; Subkey: "Software\Classes\.lasero"; ValueType: string; ValueName: ""; ValueData: "Lasero.Project"; Flags: uninsdeletevalue uninsdeletekeyifempty; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\Lasero.Project"; ValueType: string; ValueName: ""; ValueData: "Projekt LASERO"; Flags: uninsdeletekey; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\Lasero.Project\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#AppExeName},0"; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\Lasero.Project\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExeName}"" ""%1"""; Tasks: fileassoc

[Run]
; runasoriginaluser: after an all-users (elevated) install the app still starts without admin rights.
Filename: "{app}\{#AppExeName}"; Description: "{cm:RunApp}"; Flags: nowait postinstall skipifsilent runasoriginaluser

[Code]
const
  UninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{4B9D72D8-4F8A-4E8D-9A4C-0F7C2E319A61}_is1';
  AppProcess = '{#AppExeName}';

var
  PreviousVersion: String;
  UsbPage: TOutputMsgWizardPage;

function IsAppRunning: Boolean;
var
  Locator, Service, Processes: Variant;
begin
  Result := False;
  try
    Locator := CreateOleObject('WbemScripting.SWbemLocator');
    Service := Locator.ConnectServer('.', 'root\CIMV2');
    Processes := Service.ExecQuery('SELECT ProcessId FROM Win32_Process WHERE Name = ''' + AppProcess + '''');
    Result := (not VarIsNull(Processes)) and (Processes.Count > 0);
  except
    { WMI unavailable: rely on the Restart Manager prompt of CloseApplications }
  end;
end;

function ReadPreviousVersion: String;
begin
  Result := '';
  if not RegQueryStringValue(HKCU, UninstallKey, 'DisplayVersion', Result) then
    if not RegQueryStringValue(HKLM, UninstallKey, 'DisplayVersion', Result) then
      Result := '';
end;

function InitializeSetup: Boolean;
var
  Ver: TWindowsVersion;
begin
  Result := True;
  GetWindowsVersionEx(Ver);
  if (Ver.Major < 10) or ((Ver.Major = 10) and (Ver.Build < 17763)) then
  begin
    MsgBox(FmtMessage(CustomMessage('WinTooOld'), ['{#AppName}', Format('%d.%d.%d', [Ver.Major, Ver.Minor, Ver.Build])]), mbCriticalError, MB_OK);
    Result := False;
    Exit;
  end;
  if not IsX64Compatible then
  begin
    MsgBox(CustomMessage('Need64'), mbCriticalError, MB_OK);
    Result := False;
    Exit;
  end;
  PreviousVersion := ReadPreviousVersion;
end;

procedure InitializeWizard;
begin
  UsbPage := CreateOutputMsgPage(wpInfoAfter, CustomMessage('UsbCaption'), CustomMessage('UsbDesc'), CustomMessage('UsbText'));
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  { The Restart Manager (CloseApplications) already asked to close the app; this is the safety net
    for a copy that refused to close, e.g. because of unsaved work. }
  while IsAppRunning do
  begin
    if MsgBox(CustomMessage('AppRunning'), mbError, MB_RETRYCANCEL) = IDCANCEL then
    begin
      Result := CustomMessage('AppRunning');
      Exit;
    end;
  end;
end;

function UpdateReadyMemo(Space, NewLine, MemoUserInfoInfo, MemoDirInfo, MemoTypeInfo,
  MemoComponentsInfo, MemoGroupInfo, MemoTasksInfo: String): String;
var
  Mode: String;
begin
  if IsAdminInstallMode then Mode := CustomMessage('ReadyAll') else Mode := CustomMessage('ReadyUser');
  Result := Mode + NewLine + NewLine;
  if PreviousVersion <> '' then
    Result := Result + FmtMessage(CustomMessage('ReadyUpgrade'), [PreviousVersion]) + NewLine + NewLine;
  Result := Result + MemoDirInfo + NewLine + NewLine;
  if MemoTasksInfo <> '' then Result := Result + MemoTasksInfo + NewLine + NewLine;
  Result := Result + CustomMessage('ReadyLightBurn');
end;

{ ---- uninstall ---- }

function InitializeUninstall: Boolean;
begin
  Result := True;
  while IsAppRunning do
  begin
    if MsgBox(CustomMessage('UninstRunning'), mbError, MB_RETRYCANCEL) = IDCANCEL then
    begin
      Result := False;
      Exit;
    end;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
  Purge: Boolean;
  I: Integer;
begin
  if CurUninstallStep <> usPostUninstall then Exit;
  DataDir := ExpandConstant('{localappdata}\Lasero');
  if not DirExists(DataDir) then Exit;

  { Default is to keep the data. Silent uninstalls keep it unless /PURGEDATA is passed. }
  Purge := False;
  for I := 1 to ParamCount do
    if CompareText(ParamStr(I), '/PURGEDATA') = 0 then Purge := True;
  if (not Purge) and (not UninstallSilent) then
    Purge := SuppressibleMsgBox(FmtMessage(CustomMessage('PurgeQuestion'), [DataDir]) , mbConfirmation, MB_YESNO or MB_DEFBUTTON2, IDNO) = IDYES;
  if Purge then
    DelTree(DataDir, True, True, True);
end;
