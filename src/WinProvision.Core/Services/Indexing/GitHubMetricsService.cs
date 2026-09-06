using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using WinProvision.Core.Models;

namespace WinProvision.Core.Services.Indexing;

/// <summary>
/// Enriquece pacotes com estrelas/forks/data do último push via API REST do GitHub,
/// para os pacotes cuja Homepage/PackageUrl apontam para um repositório GitHub.
///
/// Mantém cache em disco (injetado via construtor, persistido pelo chamador) para não
/// refazer o fetch de milhares de repositórios a cada execução diária — só reconsulta
/// quando o cache expira (7 dias) ou o pacote é novo no catálogo.
///
/// Com GITHUB_TOKEN (injetado automaticamente pelo GitHub Actions), o limite sobe de
/// 60 para 5.000 requisições/hora. Se o rate limit for atingido mesmo assim, a engine
/// não falha: os pacotes restantes seguem com o score neutro (ver ScoringEngine).
/// </summary>
public class GitHubMetricsService
{
    private static readonly Regex GitHubRepoRegex = new(
        @"github\.com/(?<owner>[^/\s]+)/(?<repo>[^/\s#?]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly HttpClient _httpClient;
    private readonly ConcurrentDictionary<string, GitHubRepoMetrics> _cache;
    private readonly TimeSpan _cacheTtl = TimeSpan.FromDays(7);
    private readonly SemaphoreSlim _throttle = new(5);

    private int _requestsMade;
    private volatile bool _rateLimitHit;

    public bool RateLimitHit => _rateLimitHit;
    public int RequestsMade => _requestsMade;

    public GitHubMetricsService(string? githubToken, Dictionary<string, GitHubRepoMetrics>? existingCache = null)
    {
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("WinProvision-Store-Indexer", "1.0"));
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        if (!string.IsNullOrWhiteSpace(githubToken))
        {
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", githubToken);
        }

        _cache = existingCache != null
            ? new ConcurrentDictionary<string, GitHubRepoMetrics>(existingCache)
            : new ConcurrentDictionary<string, GitHubRepoMetrics>();
    }

    /// <summary>Tenta extrair "owner/repo" da primeira URL que apontar para um repositório GitHub.</summary>
    public static string? ExtractRepoSlug(params string?[] candidateUrls)
    {
        foreach (var url in candidateUrls)
        {
            if (string.IsNullOrWhiteSpace(url)) continue;

            var match = GitHubRepoRegex.Match(url);
            if (!match.Success) continue;

            string repo = match.Groups["repo"].Value.TrimEnd('/').Replace(".git", "");
            return $"{match.Groups["owner"].Value}/{repo}";
        }

        return null;
    }

    public async Task<GitHubRepoMetrics?> GetMetricsAsync(string repoSlug)
    {
        bool hasCached = _cache.TryGetValue(repoSlug, out var cached);

        if (hasCached && DateTimeOffset.UtcNow - cached!.FetchedAt < _cacheTtl)
            return cached;

        if (RateLimitHit)
            return hasCached ? cached : null;

        await _throttle.WaitAsync();
        try
        {
            if (RateLimitHit) return hasCached ? cached : null;

            var response = await _httpClient.GetAsync($"https://api.github.com/repos/{repoSlug}");
            Interlocked.Increment(ref _requestsMade);

            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            {
                _rateLimitHit = true;
                return hasCached ? cached : null;
            }

            if (!response.IsSuccessStatusCode)
                return hasCached ? cached : null;

            using var stream = await response.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);
            var root = doc.RootElement;

            var metrics = new GitHubRepoMetrics
            {
                Stars = root.TryGetProperty("stargazers_count", out var stars) ? stars.GetInt32() : 0,
                Forks = root.TryGetProperty("forks_count", out var forks) ? forks.GetInt32() : 0,
                PushedAt = root.TryGetProperty("pushed_at", out var pushed) && pushed.ValueKind == JsonValueKind.String
                    ? DateTimeOffset.Parse(pushed.GetString()!)
                    : null,
                FetchedAt = DateTimeOffset.UtcNow
            };

            _cache[repoSlug] = metrics;
            return metrics;
        }
        catch
        {
            return hasCached ? cached : null;
        }
        finally
        {
            _throttle.Release();
        }
    }

    /// <summary>Retorna o cache atualizado para ser persistido em disco pelo chamador.</summary>
    public Dictionary<string, GitHubRepoMetrics> ExportCache() => new(_cache);
}
