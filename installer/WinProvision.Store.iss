; ============================================================================
; Instalador do WinProvision Store — Inno Setup 6.3+
;
; Gerado no CI pelo build-release.yml. Uso manual (a partir da raiz do repo):
;   ISCC.exe /DAppVersion=1.0.2 /DFileVersion=1.0.2.0 ^
;            /DSourceExe=C:\caminho\publish\WinProvision.Store.exe ^
;            /DOutputDir=C:\caminho\dist installer\WinProvision.Store.iss
;
; O EXE cru (WinProvision.Store.exe) continua sendo publicado no release: o
; Bootstrap baixa ele direto (releases/latest/download/WinProvision.Store.exe).
; Este instalador é a via "para pessoas": atalho no Menu Iniciar, entrada em
; "Aplicativos instalados" e desinstalador de verdade.
; ============================================================================

#ifndef AppVersion
  #define AppVersion "0.0.0-dev"
#endif
#ifndef FileVersion
  #define FileVersion "0.0.0.0"
#endif
#ifndef SourceExe
  #define SourceExe "..\publish\WinProvision.Store.exe"
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

; O EXE single-file já é comprimido; lzma2 aqui rende pouco, então prioriza velocidade de CI.
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

; Se o app estiver aberto durante uma atualização, o instalador oferece fechá-lo.
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceExe}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(AppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[Code]
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
