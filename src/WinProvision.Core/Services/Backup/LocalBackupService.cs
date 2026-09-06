using System.Text.Json;
using WinProvision.Core.Models;

namespace WinProvision.Core.Services.Backup;

/// <summary>
/// Backup local do perfil de seleção — cobre TODAS as guias abertas (ver
/// <see cref="ProfileBackupSet"/>), não só a ativa —, gravado em disco
/// independentemente de o usuário estar conectado ao GitHub — login é opcional (ver
/// <see cref="GitHubBackupService"/>), mas o backup local sempre acontece, tanto pela
/// rotina automática (<see cref="BackupAutoSyncService"/>) quanto pelo botão
/// "Sincronizar agora" da tela de Configurações.
///
/// Mantém a última versão ("latest") sempre sobrescrita para restauração rápida, mais
/// um pequeno histórico rotativo (snapshots com timestamp) para o caso de a "latest"
/// ter sido salva já com uma seleção indesejada — sem isso, um backup automático mal
/// timed poderia "confirmar" um erro do usuário sem chance de voltar atrás.
/// </summary>
public class LocalBackupService
{
    private const int MaxSnapshots = 10;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _backupDir;
    private readonly string _latestPath;
    private readonly SemaphoreSlim _ioLock = new(1, 1);

    public LocalBackupService()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinProvision", "Backup"))
    {
    }

    /// <summary>Construtor interno com diretório explícito — facilita testes sem tocar no LocalAppData real.</summary>
    internal LocalBackupService(string backupDir)
    {
        _backupDir = backupDir;
        _latestPath = Path.Combine(_backupDir, "profile-latest.json");
    }

    public string LatestPath => _latestPath;

    /// <summary>Data/hora (UTC) do último backup local, ou null se nenhum foi feito ainda.</summary>
    public DateTime? LastBackupUtc => File.Exists(_latestPath)
        ? File.GetLastWriteTimeUtc(_latestPath)
        : null;

    public async Task SaveAsync(ProfileBackupSet backupSet, CancellationToken ct = default)
    {
        await _ioLock.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(_backupDir);

            string json = JsonSerializer.Serialize(backupSet, JsonOptions);
            await File.WriteAllTextAsync(_latestPath, json, ct);

            string snapshotPath = Path.Combine(_backupDir, $"profile-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json");
            await File.WriteAllTextAsync(snapshotPath, json, ct);

            PruneOldSnapshots();
        }
        finally
        {
            _ioLock.Release();
        }
    }

    /// <summary>Lê a última versão salva. Retorna null se nunca houve backup ou se o arquivo estiver corrompido.</summary>
    public async Task<ProfileBackupSet?> TryLoadLatestAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_latestPath))
            return null;

        try
        {
            string json = await File.ReadAllTextAsync(_latestPath, ct);
            return JsonSerializer.Deserialize<ProfileBackupSet>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Mantém só os N snapshots mais recentes — evita crescer sem limite em máquinas usadas por muito tempo.</summary>
    private void PruneOldSnapshots()
    {
        var snapshots = new DirectoryInfo(_backupDir)
            .GetFiles("profile-????????-??????.json")
            .OrderByDescending(f => f.CreationTimeUtc)
            .Skip(MaxSnapshots);

        foreach (var file in snapshots)
        {
            try { file.Delete(); } catch (IOException) { /* melhor esforço — não é crítico */ }
        }
    }
}
