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
}
