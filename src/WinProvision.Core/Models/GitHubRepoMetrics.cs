namespace WinProvision.Core.Models;

/// <summary>
/// Métricas de um repositório GitHub associado a um pacote (via Homepage/PackageUrl).
/// Persistido em disco (metrics-cache.json) entre execuções da pipeline para não
/// refazer o fetch de milhares de repositórios todos os dias.
/// </summary>
public class GitHubRepoMetrics
{
    public int Stars { get; set; }
    public int Forks { get; set; }
    public DateTimeOffset? PushedAt { get; set; }
    public DateTimeOffset FetchedAt { get; set; }
}
