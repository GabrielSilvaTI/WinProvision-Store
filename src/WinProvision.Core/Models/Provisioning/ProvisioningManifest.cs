namespace WinProvision.Core.Models.Provisioning;

public enum SystemThemeMode
{
    NaoDefinido = 0,
    Claro = 1,
    Escuro = 2,
}

/// <summary>Só tem efeito no Windows 11 — no Windows 10 a barra de tarefas é sempre à esquerda.</summary>
public enum TaskbarAlignmentMode
{
    NaoDefinido = 0,
    Esquerda = 1,
    Centro = 2,
}

public enum TaskbarSearchBoxMode
{
    NaoDefinido = 0,
    Oculta = 1,
    ApenasIcone = 2,
    CaixaCompleta = 3,
}

public enum PowerPlanMode
{
    NaoDefinido = 0,
    Economia = 1,
    Equilibrado = 2,
    AltoDesempenho = 3,
}

/// <summary>
/// Representa um perfil de provisionamento do SISTEMA — diferente de <see cref="ProfileManifest"/>
/// (que descreve apps a instalar via winget/ODT), este cobre ajustes de máquina aplicados via
/// Registro do Windows/Win32 (tema, barra de tarefas, plano de energia, nome do computador).
/// Usado tanto pela tela "Provisionamento" quanto pelo modo CLI
/// (<c>WinProvision.Store.exe /Provision caminho\perfil.json</c>).
///
/// Todo campo é opcional (null = "não mexer nesse ajuste"), o que permite perfis parciais —
/// ex.: um .json que só define o tema, sem tocar nos outros ajustes.
/// SchemaVersion segue a mesma convenção do ProfileManifest: ausente/zero é tratado como
/// legado (v0) na importação, pra perfis antigos não quebrarem o parser silenciosamente.
/// </summary>
public class ProvisioningManifest
{
    public int SchemaVersion { get; set; } = 1;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Nome opcional do perfil (ex.: "Estação de trabalho padrão").</summary>
    public string? Name { get; set; }

    /// <summary>
    /// Nome opcional de quem montou o perfil (ex.: "Gabriel", "Setor de TI", "Acme Corp").
    /// Durante o Apply é gravado no registro HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\OEMInformation
    /// como Manufacturer, então aparece na tela Configurações → Sistema → Sobre como "Autor do
    /// setup" / "Organização". Falta de privilégio Admin durante o Apply não quebra o Apply —
    /// a informação é apenas omitida.
    /// </summary>
    public string? Creator { get; set; }

    public SystemThemeMode? Theme { get; set; }

    public TaskbarAlignmentMode? TaskbarAlignment { get; set; }

    public bool? TaskbarAutoHide { get; set; }

    public TaskbarSearchBoxMode? TaskbarSearchBox { get; set; }

    public PowerPlanMode? PowerPlan { get; set; }

    /// <summary>
    /// Novo nome do computador. Requer reinício para ter efeito (ver
    /// <see cref="WinProvision.Core.Services.Provisioning.ProvisioningApplyResult.RestartRequired"/>) —
    /// a API do Windows usada (SetComputerNameEx) só grava o nome pendente, não renomeia "a quente".
    /// </summary>
    public string? MachineName { get; set; }

    /// <summary>
    /// Nome do arquivo original (ex.: "fundo.jpg") — usado só pra decidir a extensão ao gravar
    /// o wallpaper em disco durante o Apply; o conteúdo em si vai em <see cref="WallpaperImageBase64"/>.
    /// </summary>
    public string? WallpaperFileName { get; set; }

    /// <summary>
    /// Conteúdo do arquivo de wallpaper (.jpg/.png) codificado em Base64 — viaja dentro do
    /// próprio perfil .json (igual ao restante do perfil), então importar ou aplicar via CLI
    /// já traz a imagem junto, sem depender de um segundo arquivo ao lado do .json.
    /// </summary>
    public string? WallpaperImageBase64 { get; set; }

    /// <summary>
    /// Localização geográfica do usuário (código ISO 3166-1 de duas letras, ex.: "BR", "US") —
    /// aplicada via SetUserGeoName (kernel32.dll), a mesma API usada pela tela Configurações do
    /// Windows em "Hora e idioma &gt; Idioma e região". Cobre só a localização; o formato de
    /// data/hora/moeda (aba "Formatos" das Configurações) não tem uma API pública de gravação
    /// documentada e por isso fica fora deste manifesto.
    /// </summary>
    public string? Region { get; set; }

    /// <summary>
    /// Se true, busca e instala TODAS as atualizações de qualidade/segurança e drivers
    /// pendentes (via WUAPI) durante o Apply — sem seleção manual, porque o perfil costuma ser
    /// montado numa máquina para ser aplicado depois em OUTRA (a máquina-alvo recém-formatada),
    /// então uma lista buscada agora nem corresponderia ao hardware/estado dela. A busca em si
    /// só acontece no momento do Apply, já rodando na máquina-alvo.
    /// </summary>
    public bool? AutoInstallWindowsUpdates { get; set; }

    /// <summary>
    /// Se true, cria um ponto de restauração do sistema (via WMI SystemRestore) no início do
    /// Apply — mesmo raciocínio de <see cref="AutoInstallWindowsUpdates"/>: precisa acontecer
    /// na máquina-alvo no momento em que o perfil é aplicado, não na máquina onde o perfil foi
    /// montado.
    /// </summary>
    public bool? AutoCreateRestorePoint { get; set; }

    /// <summary>
    /// Se true, agenda uma tarefa no Agendador de Tarefas do Windows para limpar automaticamente
    /// todos os arquivos temporários do sistema (%TEMP%, Windows\Temp, etc.) ao fazer Logon.
    /// </summary>
    public bool? AutoCleanTempOnLogon { get; set; }

    /// <summary>
    /// Tempo (em minutos) até a tela ser desligada quando o PC está ligado na tomada (CA).
    /// null = "não alterar"; 0 = "Nunca" (equivalente a powercfg /change monitor-timeout-ac 0).
    /// Valores comuns do Windows: 1, 2, 3, 5, 10, 15, 20, 25, 30, 45, 60, 90, 120, 180.
    /// Aplicado via powercfg /change monitor-timeout-ac no plano ativo.
    /// </summary>
    public int? DisplayTimeoutOnAc { get; set; }

    /// <summary>
    /// Tempo (em minutos) até a tela ser desligada no modo bateria (CC).
    /// null = "não alterar"; 0 = "Nunca".
    /// Aplicado via powercfg /change monitor-timeout-dc no plano ativo.
    /// </summary>
    public int? DisplayTimeoutOnDc { get; set; }

    /// <summary>
    /// Tempo (em minutos) até o PC ser suspenso (S0 Low Power Idle / S3 / Hibernate / Modern Standby,
    /// conforme suportado pelo hardware) quando ligado na tomada (CA).
    /// null = "não alterar"; 0 = "Nunca".
    /// Aplicado via powercfg /change standby-timeout-ac no plano ativo.
    /// </summary>
    public int? StandbyTimeoutOnAc { get; set; }

    /// <summary>
    /// Tempo (em minutos) até o PC ser suspenso no modo bateria (CC).
    /// null = "não alterar"; 0 = "Nunca".
    /// Aplicado via powercfg /change standby-timeout-dc no plano ativo.
    /// </summary>
    public int? StandbyTimeoutOnDc { get; set; }

    /// <summary>
    /// Posições dos ícones de atalho que o usuário arrastou na simulação da Área de Trabalho
    /// (tela Provisionamento → Personalização). Null/vazio = "não mexer no layout". Ver
    /// <see cref="DesktopIconPlacement"/> — a posição é gravada em Coluna/Linha (não em pixel),
    /// pois a grade real de ícones da máquina-alvo depende de DPI/resolução/tamanho de ícone
    /// configurados nela, que podem não ter nada a ver com a máquina onde o perfil foi montado.
    /// Aplicado por <see cref="WinProvision.Core.Services.Provisioning.ProvisioningService"/>
    /// via a interface COM IFolderView do Shell do Windows — não existe uma chave de Registro
    /// documentada/estável para posição de ícone (ver comentário no Apply) — por isso exige que
    /// os atalhos (.lnk) já existam na Área de Trabalho no momento do Apply (rodar depois da
    /// instalação dos apps, não antes).
    /// </summary>
    public List<DesktopIconPlacement>? DesktopIconLayout { get; set; }
}

/// <summary>
/// Um atalho posicionado na simulação da Área de Trabalho, identificado pelo Id do pacote
/// winget (pra reencontrar o AppEntry ao reabrir a tela) e pelo nome de exibição (usado no
/// Apply pra localizar o arquivo .lnk correspondente de verdade na Área de Trabalho da
/// máquina-alvo, já que o Id do winget não tem relação com o nome do atalho criado pelo instalador).
/// Column/Row são relativos à grade de ícones (0 = primeira coluna/linha), não pixels.
/// </summary>
public class DesktopIconPlacement
{
    public string AppId { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public int Column { get; set; }

    public int Row { get; set; }
}
