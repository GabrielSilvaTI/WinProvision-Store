using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WinProvision.Core.Models;
using WinProvision.Core.Services;

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

            string json = JsonSerializer.Serialize(backupSet, WinProvisionJsonOptions.Default);
            await WriteAllTextUtf8NoBomAsync(_latestPath, json, ct);

            string snapshotPath = Path.Combine(_backupDir, $"profile-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json");
            await WriteAllTextUtf8NoBomAsync(snapshotPath, json, ct);

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
            return JsonSerializer.Deserialize<ProfileBackupSet>(json, WinProvisionJsonOptions.Default);
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

    /// <summary>
    /// Escreve texto JSON em UTF-8 SEM BOM (byte order mark) e sem \r\n inconsistente.
    /// O uso de Utf8JsonWriter direto garante que dois runs idênticos no mesmo conteúdo
    /// produzam o mesmo arquivo em nível de bytes (hash estável) — essencial para quem
    /// versiona os perfis em Gists/Git, e evita que o app "perceba mudanças" só por
    /// causa de BOM/quebras de linha diferentes.
    /// </summary>
    private static async Task WriteAllTextUtf8NoBomAsync(string path, string json, CancellationToken ct)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 4096, useAsync: true);
        await stream.WriteAsync(bytes, 0, bytes.Length, ct);
        await stream.FlushAsync(ct);
    }
}
