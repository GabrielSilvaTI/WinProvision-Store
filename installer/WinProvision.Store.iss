; ============================================================================
; Instalador do WinProvision Store — Inno Setup 6.3+
;
; Gerado no CI pelo build-release.yml. Uso manual (a partir da raiz do repo):
;   ISCC.exe /DAppVersion=1.0.2 /DFileVersion=1.0.2.0 ^
;            /DSourceDir=C:\caminho\publish ^
;            /DOutputDir=C:\caminho\dist installer\WinProvision.Store.iss
;
; O publish deixou de ser single-file (PublishSingleFile quebra a ativação COM
; do winget — ver histórico do repo): agora é self-contained multi-arquivo, e
; o instalador empacota a pasta inteira. Por isso o Bootstrap não pode mais
; baixar um EXE solto de releases/latest/download/... — precisa baixar e rodar
; o instalador (WinProvision.Store-Setup.exe) em modo silencioso (ver nota no
; workflow sobre o que isso muda no lado do Bootstrap).
; Este instalador é a via "para pessoas": atalho no Menu Iniciar, entrada em
; "Aplicativos instalados" e desinstalador de verdade.
; ============================================================================

#ifndef AppVersion
  #define AppVersion "0.0.0-dev"
#endif
#ifndef FileVersion
  #define FileVersion "0.0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish"
#endif
#ifndef OutputDir
  #define OutputDir "..\dist"
#endif

#define AppName      "WinProvision Store"
#define AppExeName   "WinProvision.Store.exe"
#define AppPublisher "GabrielSilvaTI"
#define AppURL       "https://github.com/GabrielSilvaTI/WinProvision-Store"

[Setup]
; NUNCA troque este GUID: é ele que faz a versão nova atualizar a antiga (em vez de
; instalar lado a lado) e que liga o desinstalador à entrada em "Aplicativos instalados".
AppId={{57E31A2E-BBE2-48F4-8902-CB249FE1D38C}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL={#AppURL}/issues
AppUpdatesURL={#AppURL}/releases
VersionInfoVersion={#FileVersion}
VersionInfoProductName={#AppName}
VersionInfoDescription=Instalador do {#AppName}

DefaultDirName={autopf}\{#AppName}
DisableProgramGroupPage=yes
DisableDirPage=auto
DirExistsWarning=no

; O app roda SEM elevação (app.manifest = asInvoker; quando algo precisa de admin, ele pede
; UAC só para aquele comando). Por isso o padrão é instalar só para o usuário atual, sem UAC
; (em %LOCALAPPDATA%\Programs). O assistente pergunta se a pessoa prefere "para todos os
; usuários" (Program Files, pede UAC). Em modo silencioso: /CURRENTUSER ou /ALLUSERS.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog

ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763

OutputDir={#OutputDir}
; Nome fixo (sem versão) de propósito: dá um link estável em
; releases/latest/download/WinProvision.Store-Setup.exe
OutputBaseFilename=WinProvision.Store-Setup

SetupIconFile=..\WinProvision.Store\Assets\app.ico
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName}

; lzma2/fast prioriza velocidade de CI; com a pasta multi-arquivo (não mais
; single-file) o ganho de comprimir mais forte tende a compensar mais do que
; compensava antes — pode valer testar lzma2/max/ultra se o tempo de CI sobrar.
Compression=lzma2/fast
SolidCompression=yes
; windows11: estilo claro/escuro nativo do Inno Setup 6.6+, com cantos
; arredondados e título da janela seguindo o tema do Windows. dynamic:
; alterna sozinho entre claro e escuro conforme o tema do Windows.
; Cores batendo com a paleta do app (App.xaml.cs, ApplyThemePalette):
; fundo claro #F7F9FC, fundo escuro #101B2D.
WizardStyle=modern dynamic windows11
WizardBackColor=#F7F9FC
WizardBackColorDynamicDark=#101B2D

; Ícone do WinProvision Store (fundo transparente) nas páginas Welcome/Finished
; e no canto superior das demais. WizardImageBackColor pinta o que sobra ao
; redor, já que o PNG é quadrado e a área da imagem é alta e estreita.
WizardImageFile=..\docs\assets\winprovision-icon.png
WizardSmallImageFile=..\docs\assets\winprovision-icon.png
WizardImageBackColor=#F7F9FC
WizardImageBackColorDynamicDark=#101B2D

; Se o app estiver aberto durante uma atualização, o instalador oferece fechá-lo.
; Escopo restrito ao próprio EXE (Restart Manager não precisa varrer mais nada).
CloseApplications=yes
CloseApplicationsFilter={#AppExeName}
RestartApplications=no

[Languages]
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; Pasta inteira do publish (self-contained multi-arquivo): DLLs, .deps.json,
; runtimeconfig.json etc., junto com o WinProvision.Store.exe. recursesubdirs
; pega os runtimes nativos que o publish costuma colocar em subpastas.
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(AppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[Code]
// O WingetExecutor (fallback quando a API COM do winget falha) chama winget.exe
// pela linha de comando com o caminho do app embutido nos argumentos. Caminho
// fora de ASCII já causou parsing quebrado em ferramentas de linha de comando
// do Windows, então barra aqui em vez de deixar o usuário descobrir depois.
function IsCharValid(Value: Char): Boolean;
begin
  Result := Ord(Value) <= $007F;
end;

function IsDirNameValid(const Value: string): Boolean;
var
  I: Integer;
begin
  Result := True;
  for I := 1 to Length(Value) do
    if not IsCharValid(Value[I]) then
    begin
      Result := False;
      Exit;
    end;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (CurPageID = wpSelectDir) and not IsDirNameValid(WizardForm.DirEdit.Text) then
  begin
    Result := False;
    MsgBox('O caminho de instalação não pode ter acentos ou caracteres especiais.' + #13#10 +
           'Use só letras sem acento, números e espaços.', mbError, MB_OK);
  end;
end;

// Ao desinstalar:
//  - %LOCALAPPDATA%\WinProvisionStore = cache (catálogo/ícones), regenerável -> sempre removido.
//  - %LOCALAPPDATA%\WinProvision = backups locais, logs, presets, ODT/Office, atualizações
//    ignoradas -> só é removido se a pessoa confirmar (padrão: manter). Em desinstalação
//    silenciosa, mantém.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
  begin
    DelTree(ExpandConstant('{localappdata}\WinProvisionStore'), True, True, True);

    if not UninstallSilent then
    begin
      if SuppressibleMsgBox(
           'Remover também os dados do WinProvision (backups locais, logs e configurações em %LOCALAPPDATA%\WinProvision)?' + #13#10 + #13#10 +
           'Escolha "Não" se pretende reinstalar e continuar de onde parou.',
           mbConfirmation, MB_YESNO or MB_DEFBUTTON2, IDNO) = IDYES then
      begin
        DelTree(ExpandConstant('{localappdata}\WinProvision'), True, True, True);
      end;
    end;
  end;
end;
