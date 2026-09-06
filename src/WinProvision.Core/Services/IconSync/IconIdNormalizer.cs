using System.Text.RegularExpressions;

namespace WinProvision.Core.Services.IconSync;

/// <summary>
/// Normaliza identificadores para comparação entre fontes heterogêneas de ícone.
/// Cada fonte usa uma convenção de nome diferente para o mesmo pacote
/// (PackageIdentifier completo do winget, nome de arquivo do package-icons, chave
/// dos JSONs do UniGetUI...), então a forma confiável de casar é reduzir tudo ao
/// mesmo denominador: minúsculo, sem separadores, sem extensão de arquivo de imagem.
/// </summary>
public static partial class IconIdNormalizer
{
    private static readonly string[] ImageExtensions =
        [".png", ".jpg", ".jpeg", ".svg", ".ico", ".webp", ".gif"];

    private static readonly HashSet<string> StopTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "app", "for", "and", "of", "inc", "ltd", "llc", "co", "corp"
    };

    public static string Normalize(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        string trimmed = value;
        string ext = Path.GetExtension(value);
        if (ImageExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
        {
            trimmed = value[..^ext.Length];
        }

        Span<char> buffer = stackalloc char[trimmed.Length];
        int count = 0;
        foreach (char c in trimmed)
        {
            if (char.IsLetterOrDigit(c))
                buffer[count++] = char.ToLowerInvariant(c);
        }

        return new string(buffer[..count]);
    }

    /// <summary>
    /// Tokeniza um nome (arquivo, Id de pacote ou nome de exibição) em palavras,
    /// separando por delimitadores comuns e por transições de caixa (camelCase).
    /// Usado só pelo gerador de candidatos fuzzy do Winstall — o match automático
    /// das outras fontes usa <see cref="Normalize"/> puro, sem tokenização.
    /// </summary>
    public static HashSet<string> Tokenize(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return [];

        string spaced = CamelCaseBoundary().Replace(value, " ");
        string[] rawTokens = NonAlphaNumeric().Split(spaced);

        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in rawTokens)
        {
            string t = raw.Trim().ToLowerInvariant();
            if (t.Length < 2 || StopTokens.Contains(t)) continue;
            tokens.Add(t);
        }

        return tokens;
    }

    [GeneratedRegex(@"(?<=[a-z0-9])(?=[A-Z])")]
    private static partial Regex CamelCaseBoundary();

    [GeneratedRegex(@"[^a-zA-Z0-9]+")]
    private static partial Regex NonAlphaNumeric();
}
