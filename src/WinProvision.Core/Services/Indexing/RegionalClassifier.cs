namespace WinProvision.Core.Services.Indexing;

public record RegionRule(List<string> DomainSuffixes, List<string> PublisherPrefixes, List<string> Keywords);

/// <summary>
/// Classifica pacotes com apelo regional através de heurísticas de domínio do site,
/// publishers conhecidos e palavras-chave na descrição/tags. Hoje cobre o Brasil;
/// a estrutura em dicionário é extensível para outras regiões (bastaria adicionar
/// outra entrada em DefaultRegions).
///
/// É heurística, então pode gerar falsos positivos/negativos ocasionais — recomendo
/// manter uma allowlist/denylist manual versionada à parte para correção pontual,
/// sem precisar mexer nas regras gerais.
/// </summary>
public class RegionalClassifier
{
    private readonly Dictionary<string, RegionRule> _regions;

    public RegionalClassifier(Dictionary<string, RegionRule>? regions = null)
    {
        _regions = regions ?? DefaultRegions();
    }

    public List<string> Classify(string id, string publisher, string? homepage, string? description, List<string> tags)
    {
        var matches = new List<string>();
        string haystack = $"{description} {string.Join(' ', tags)}".ToLowerInvariant();
        string host = TryGetHost(homepage);

        foreach (var (regionCode, rule) in _regions)
        {
            bool domainMatch = rule.DomainSuffixes.Any(suffix => host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
            bool publisherMatch = rule.PublisherPrefixes.Any(prefix =>
                publisher.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            bool keywordMatch = rule.Keywords.Any(k => haystack.Contains(k, StringComparison.OrdinalIgnoreCase));

            if (domainMatch || publisherMatch || keywordMatch)
                matches.Add(regionCode);
        }

        return matches;
    }

    private static string TryGetHost(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return string.Empty;
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : string.Empty;
    }

    private static Dictionary<string, RegionRule> DefaultRegions() => new()
    {
        ["BR"] = new RegionRule(
            DomainSuffixes: [".com.br", ".gov.br", ".org.br", ".net.br"],
            PublisherPrefixes: ["GovBR.", "Serpro.", "ReceitaFederal.", "CAIXA.", "BancoDoBrasil."],
            Keywords:
            [
                "receita federal", "governo federal", "declaração do imposto de renda",
                " pix ", "nota fiscal eletrônica", "nfe", "serasa",
                "gov.br", "certificado digital icp-brasil"
            ]
        )
    };
}
