namespace WinProvision.Core.Services.Indexing;

public record NoiseFilterResult(bool IsNoise, string? Reason);

/// <summary>
/// Regras de exclusão de "ruído": fontes, redistribuíveis de runtime, pacotes de
/// idioma, componentes isolados de sistema. Serializável para JSON
/// (config/noise-rules.json) para permitir ajuste fino sem recompilar.
/// </summary>
public class NoiseRules
{
    public List<string> IdPrefixExclusions { get; set; } = [];
    public List<string> NameKeywordExclusions { get; set; } = [];
    public List<string> AllowlistIds { get; set; } = [];

    public static NoiseRules Default => new()
    {
        IdPrefixExclusions =
        [
            "Microsoft.VCRedist",
            "Microsoft.VCLibs",
            "Microsoft.DotNet.",
            "Microsoft.NET.",
            "Microsoft.WindowsAppRuntime",
            "Microsoft.UI.Xaml",
            "Microsoft.DirectX",
            "Fonts.",
        ],
        NameKeywordExclusions =
        [
            "Language Pack",
            "LanguagePack",
            "Redistributable",
            "Runtime Libraries",
        ],
        AllowlistIds = []
    };
}

/// <summary>
/// Avalia se um pacote deve ser descartado do catálogo por ser "ruído" para
/// um usuário final navegando numa loja de apps (utilitários de infraestrutura,
/// não aplicativos que alguém instalaria intencionalmente pelo nome).
/// </summary>
public class NoiseFilter
{
    private readonly NoiseRules _rules;

    public NoiseFilter(NoiseRules? rules = null)
    {
        _rules = rules ?? NoiseRules.Default;
    }

    public NoiseFilterResult Evaluate(string id, string name, string publisher, List<string> tags)
    {
        // Override manual sempre vence — útil para corrigir falsos positivos sem tocar em regra.
        if (_rules.AllowlistIds.Contains(id, StringComparer.OrdinalIgnoreCase))
            return new NoiseFilterResult(false, null);

        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(publisher))
            return new NoiseFilterResult(true, "Campos obrigatórios ausentes no manifesto");

        foreach (var prefix in _rules.IdPrefixExclusions)
        {
            if (id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return new NoiseFilterResult(true, $"Prefixo de Id excluído: {prefix}");
        }

        foreach (var keyword in _rules.NameKeywordExclusions)
        {
            if (name.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                return new NoiseFilterResult(true, $"Palavra-chave excluída no nome: {keyword}");
        }

        return new NoiseFilterResult(false, null);
    }
}
