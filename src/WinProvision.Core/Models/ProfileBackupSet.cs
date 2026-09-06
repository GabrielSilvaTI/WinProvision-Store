using WinProvision.Core.Models.Provisioning;

namespace WinProvision.Core.Models;

/// <summary>
/// Contêiner de backup cobrindo TODAS as guias de pacotes abertas no momento (não só a
/// ativa) — cada guia vira um <see cref="ProfileManifest"/> independente dentro de
/// <see cref="Tabs"/>, preservando o nome de cada uma. É o formato usado tanto pelo
/// backup local (<see cref="Services.Backup.LocalBackupService"/>) quanto pelo Gist
/// (<see cref="Services.Backup.GitHubBackupService"/>) — distinto do ProfileManifest
/// "solto" que ProfileService.ExportAsync/ImportAsync ainda usa para exportação manual
/// de um único perfil via SaveFileDialog, que continua existindo sem mudanças.
///
/// Guias vazias (sem nenhum item) não entram aqui — ver BackupAutoSyncService.
/// </summary>
public class ProfileBackupSet
{
    public int SchemaVersion { get; set; } = 1;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public List<ProfileManifest> Tabs { get; set; } = new();

    /// <summary>
    /// Estado de provisionamento de sistema atual desta sessão (tema, barra de tarefas,
    /// energia, nome, wallpaper) — null se nada foi aplicado nem configurado ainda. Fica no
    /// nível do ProfileBackupSet (não dentro de cada guia) porque é um ajuste da MÁQUINA,
    /// não de uma seleção de pacotes específica; ver
    /// <see cref="Services.Provisioning.ProvisioningService.Current"/>, a origem deste
    /// valor (aplicado de fato OU só exportado/importado pela tela Provisionamento). Um
    /// único arquivo (local ou Gist) já cobre apps + provisionamento — não existe um
    /// segundo arquivo/formato separado para isso.
    /// </summary>
    public ProvisioningManifest? Provisioning { get; set; }
}
