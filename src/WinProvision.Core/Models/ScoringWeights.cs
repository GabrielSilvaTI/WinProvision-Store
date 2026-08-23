namespace WinProvision.Core.Models;

/// <summary>
/// Pesos do algoritmo de score, carregados de config/scoring-weights.json.
/// Ficar fora do código permite recalibrar sem precisar recompilar/redeployar a engine.
/// </summary>
public class ScoringWeights
{
    public double CompletenessWeight { get; set; } = 0.35;
    public double PopularityWeight { get; set; } = 0.35;
    public double MaintenanceWeight { get; set; } = 0.30;

    /// <summary>
    /// Score neutro (0-100) usado para popularidade e/ou manutenção quando o pacote
    /// não tem repositório GitHub identificável. Evita punir apps proprietários
    /// (Chrome, Zoom, Adobe Reader, Spotify...) que são justamente os mais usados
    /// no mundo real mas não têm stars/forks para medir.
    /// </summary>
    public double NeutralScoreWhenNoGitHubData { get; set; } = 55;

    /// <summary>Meses até o score de manutenção decair a zero por falta de atividade.</summary>
    public int MaintenanceDecayMonths { get; set; } = 24;
}
