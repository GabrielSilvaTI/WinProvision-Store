namespace WinProvision.Core.Models.Provisioning;

public enum SystemThemeMode
{
    NaoDefinido = 0,
    Claro = 1,
    Escuro = 2,
}

public enum AccentColorMode
{
    NaoDefinido = 0,
    Automatico = 1,
    Personalizado = 2,
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

    /// <summary>Compatibilidade com perfis legados que aplicavam um único tema ao Windows e aos apps.</summary>
    public SystemThemeMode? Theme { get; set; }

    public SystemThemeMode? SystemTheme { get; set; }

    public SystemThemeMode? AppsTheme { get; set; }

    public AccentColorMode? AccentColorMode { get; set; }

    /// <summary>Cor personalizada em #RRGGBB; usada quando AccentColorMode é Personalizado.</summary>
    public string? AccentColor { get; set; }

    public TaskbarAlignmentMode? TaskbarAlignment { get; set; }

    public bool? TaskbarAutoHide { get; set; }

    public TaskbarSearchBoxMode? TaskbarSearchBox { get; set; }

    public PowerPlanMode? PowerPlan { get; set; }

    /// <summary>Solicita ao Windows sincronizar automaticamente data e hora pela fonte já configurada no sistema.</summary>
    public bool? EnableAutomaticTime { get; set; }

    /// <summary>Ativa o ajuste automático do fuso horário com base na localização, sem alterar a permissão de localização do usuário.</summary>
    public bool? EnableAutomaticTimeZone { get; set; }

    public bool? ShowFileExtensions { get; set; }

    public bool? ShowHiddenFiles { get; set; }

    public bool? OpenExplorerToThisPc { get; set; }

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
    /// Campo legado aceito ao importar perfis antigos; a localização geográfica não é mais aplicada.
    /// </summary>
    public string? Region { get; set; }

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

}
