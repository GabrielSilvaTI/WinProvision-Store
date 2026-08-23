using System.Text.Json.Serialization;

namespace WinProvision.Core.Models;

/// <summary>
/// Modelo do aplicativo listado no apps.json da loja.
/// Campos vêm do manifesto do winget-pkgs (locale/instalador) e são
/// enriquecidos pela engine de indexação (Score, RegionTags, GitHubStars).
/// </summary>
public class AppEntry
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("publisher")]
    public string Publisher { get; set; } = string.Empty;

    [JsonPropertyName("publisherUrl")]
    public string? PublisherUrl { get; set; }

    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("moniker")]
    public string? Moniker { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("homepage")]
    public string? Homepage { get; set; }

    [JsonPropertyName("packageUrl")]
    public string? PackageUrl { get; set; }

    [JsonPropertyName("license")]
    public string? License { get; set; }

    [JsonPropertyName("licenseUrl")]
    public string? LicenseUrl { get; set; }

    [JsonPropertyName("releaseNotesUrl")]
    public string? ReleaseNotesUrl { get; set; }

    [JsonPropertyName("tags")]
    public List<string> Tags { get; set; } = [];

    [JsonPropertyName("architectures")]
    public List<string> Architectures { get; set; } = [];

    /// <summary>
    /// Score de relevância calculado (0-100). Ver ScoringEngine para a fórmula.
    /// </summary>
    [JsonPropertyName("score")]
    public int Score { get; set; }

    /// <summary>
    /// Tags de região com apelo identificado (ex.: ["BR"]). Vazio = sem apelo regional detectado.
    /// </summary>
    [JsonPropertyName("regionTags")]
    public List<string> RegionTags { get; set; } = [];

    /// <summary>
    /// Indica se foi possível localizar um repositório GitHub para enriquecer o score
    /// com estrelas/forks/atividade. Apps sem isso NÃO são penalizados no score
    /// (ver ScoringEngine) — a maioria dos apps proprietários mais usados não tem repo público.
    /// </summary>
    [JsonPropertyName("hasGitHubMetrics")]
    public bool HasGitHubMetrics { get; set; }

    [JsonPropertyName("gitHubStars")]
    public int? GitHubStars { get; set; }

    /// <summary>
    /// Preenchido em tempo de execução pelo IconService. Não vem do apps.json.
    /// </summary>
    [JsonIgnore]
    public string IconUrl { get; set; } = string.Empty;
}
