; Instalador do GameStream (Inno Setup 6).
; Gere com: powershell -ExecutionPolicy Bypass -File tools/build-installer.ps1

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#ifndef AppDir
  #define AppDir "..\artifacts\app"
#endif
#define AppName "GameStream"
#define AppExe "GameStream.exe"

[Setup]
AppId={{6B0C7E5A-3F1D-4C8E-9A57-2E4D1B9F7C31}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=GameStream contributors
AppComments=Transmita a tela do seu jogo, com som, para amigos pela internet.
; Instalação por usuário: não pede senha de administrador.
PrivilegesRequired=lowest
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
; Windows 10 1903+ (captura de janelas pelo Windows Graphics Capture), 64 bits.
MinVersion=10.0.18362
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
LicenseFile=..\LICENSE
; Política de privacidade mostrada durante a instalação (exigência da assinatura de código da SignPath Foundation).
InfoBeforeFile=privacy.txt
SetupIconFile=..\GameStream\Assets\app.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
WizardStyle=modern
Compression=lzma2/ultra64
SolidCompression=yes
OutputDir=..\artifacts
OutputBaseFilename=GameStream-Setup-{#AppVersion}
; Fecha o app aberto antes de atualizar.
CloseApplications=force
RestartApplications=no

[Languages]
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "fixedcodes"; Description: "Código fixo das salas: publicar no ntfy.sh, criptografado, o endereço das salas salvas"; GroupDescription: "Serviços de terceiros (dá para mudar depois em Configurações):"
Name: "updatecheck"; Description: "Avisar quando houver atualização (consulta o GitHub)"; GroupDescription: "Serviços de terceiros (dá para mudar depois em Configurações):"

[InstallDelete]
; Arquivos de versões antigas que não existem mais (a instalação é feita por cima da anterior).
Type: files; Name: "{app}\wwwroot\viewer.js"

[Files]
Source: "{#AppDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Garante que nada do app ficou rodando (túnel/captura).
Filename: "{sys}\taskkill.exe"; Parameters: "/F /T /IM {#AppExe}"; Flags: runhidden; RunOnceId: "KillApp"

[Registry]
; Escolhas da instalação sobre serviços de terceiros (o app usa enquanto o usuário não mudar em Configurações).
Root: HKCU; Subkey: "Software\GameStream"; ValueType: dword; ValueName: "FixedRoomCodes"; ValueData: 1; Tasks: fixedcodes; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\GameStream"; ValueType: dword; ValueName: "FixedRoomCodes"; ValueData: 0; Tasks: not fixedcodes; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\GameStream"; ValueType: dword; ValueName: "CheckUpdates"; ValueData: 1; Tasks: updatecheck; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\GameStream"; ValueType: dword; ValueName: "CheckUpdates"; ValueData: 0; Tasks: not updatecheck; Flags: uninsdeletekey
; "Iniciar com o Windows" (ligado nas Configurações do app): a entrada sai junto na desinstalação.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "GameStream"; Flags: uninsdeletevalue dontcreatekey

[UninstallDelete]
; FFmpeg, cloudflared e dados do WebView2 baixados pelo app.
Type: filesandordirs; Name: "{localappdata}\GameStream"

[Code]
const
  WebView2ClientKey = 'Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';
  WebView2BootstrapperUrl = 'https://go.microsoft.com/fwlink/p/?LinkId=2124703';

var
  DownloadPage: TDownloadWizardPage;

function HasVersion(Root: Integer; Key: String): Boolean;
var
  Version: String;
begin
  Result := RegQueryStringValue(Root, Key, 'pv', Version) and (Version <> '') and (Version <> '0.0.0.0');
end;

{ O WebView2 Runtime (motor do Edge usado pela interface) já vem no Windows 11
  e na maioria dos Windows 10. Se faltar, baixamos o instalador oficial da Microsoft. }
function WebView2Installed: Boolean;
begin
  Result := HasVersion(HKLM, 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}')
         or HasVersion(HKLM, WebView2ClientKey)
         or HasVersion(HKCU, WebView2ClientKey);
end;

procedure InitializeWizard;
begin
  DownloadPage := CreateDownloadPage(SetupMessage(msgWizardPreparing), 'Baixando o Microsoft Edge WebView2 Runtime…', nil);
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  ResultCode: Integer;
begin
  Result := True;
  if (CurPageID = wpReady) and not WebView2Installed then
  begin
    DownloadPage.Clear;
    DownloadPage.Add(WebView2BootstrapperUrl, 'MicrosoftEdgeWebview2Setup.exe', '');
    DownloadPage.Show;
    try
      try
        DownloadPage.Download;
        Exec(ExpandConstant('{tmp}\MicrosoftEdgeWebview2Setup.exe'), '/silent /install', '', SW_SHOW, ewWaitUntilTerminated, ResultCode);
      except
        MsgBox('Não foi possível instalar o WebView2 Runtime automaticamente.' #13#10 +
               'Instale manualmente em https://developer.microsoft.com/microsoft-edge/webview2 e abra o GameStream de novo.',
               mbInformation, MB_OK);
      end;
    finally
      DownloadPage.Hide;
    end;
  end;
end;
