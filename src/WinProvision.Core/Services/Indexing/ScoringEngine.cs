using WinProvision.Core.Models;

namespace WinProvision.Core.Services.Indexing;

/// <summary>
/// Calcula o score de relevância (0-100) de cada app combinando três componentes:
///
///  - Completude do manifesto (descrição, homepage, licença, tags, arquiteturas...)
///  - Popularidade (estrelas/forks do GitHub, em escala logarítmica, quando há repo)
///  - Manutenção (recência do último push no GitHub, quando há repo)
///
/// IMPORTANTE: diferente de uma abordagem ingênua que dá peso fixo obrigatório ao
/// GitHub, aqui popularidade/manutenção usam um score NEUTRO (configurável, default 55)
/// quando o pacote não tem repositório GitHub associado. Isso é essencial porque a
/// imensa maioria dos apps proprietários mais usados do mundo real (Chrome, Spotify,
/// Zoom, Adobe Reader, Discord, Office...) não tem repo público — penalizá-los por
/// isso inverteria o ranking a favor de projetos open source pequenos e obscuros.
/// </summary>
public class ScoringEngine
{
    private readonly ScoringWeights _weights;

    public ScoringEngine(ScoringWeights? weights = null)
    {
        _weights = weights ?? new ScoringWeights();
    }

    public int Compute(AppEntry app, GitHubRepoMetrics? githubMetrics)
    {
        double completeness = ScoreCompleteness(app);

        double popularity = githubMetrics != null
            ? ScorePopularity(githubMetrics)
            : _weights.NeutralScoreWhenNoGitHubData;

        double maintenance = githubMetrics?.PushedAt != null
            ? ScoreMaintenance(githubMetrics.PushedAt.Value)
            : _weights.NeutralScoreWhenNoGitHubData;

        double total = completeness * _weights.CompletenessWeight
                     + popularity * _weights.PopularityWeight
                     + maintenance * _weights.MaintenanceWeight;

        return (int)Math.Round(Math.Clamp(total, 0, 100));
    }

    private static double ScoreCompleteness(AppEntry app)
    {
        int checks = 0, passed = 0;

        void Check(bool condition)
        {
            checks++;
            if (condition) passed++;
        }

        Check(!string.IsNullOrWhiteSpace(app.Description));
        Check(!string.IsNullOrWhiteSpace(app.Homepage));
        Check(!string.IsNullOrWhiteSpace(app.License));
        Check(app.Tags.Count > 0);
        Check(!string.IsNullOrWhiteSpace(app.Moniker));
        Check(app.Architectures.Count > 0);
        Check(!string.IsNullOrWhiteSpace(app.ReleaseNotesUrl));

        return checks == 0 ? 0 : passed / (double)checks * 100;
    }

    private static double ScorePopularity(GitHubRepoMetrics metrics)
    {
        // Escala log para não deixar projetos com centenas de milhares de estrelas
        // dominarem de forma desproporcional o ranking frente a projetos sólidos e menores.
        double weighted = metrics.Stars + metrics.Forks * 0.5;
        double logScore = Math.Log10(weighted + 1) * 20; // log10(100_000) * 20 ≈ 100
        return Math.Clamp(logScore, 0, 100);
    }

    private double ScoreMaintenance(DateTimeOffset lastPush)
    {
        double monthsSincePush = (DateTimeOffset.UtcNow - lastPush).TotalDays / 30.0;
        if (monthsSincePush <= 1) return 100;

        double decay = 100 * (1 - monthsSincePush / _weights.MaintenanceDecayMonths);
        return Math.Clamp(decay, 0, 100);
    }
}
