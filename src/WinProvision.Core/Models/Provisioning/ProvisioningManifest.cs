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

    /// <summary>Nome opcional de quem montou o perfil (ex.: "Gabriel"). Só identificação — não afeta o Apply.</summary>
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
}
