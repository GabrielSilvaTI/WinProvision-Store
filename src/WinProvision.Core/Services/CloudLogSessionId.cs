using System;
using System.Linq;
using System.Text;

namespace WinProvision.Core.Services;

/// <summary>
/// Gera um session ID curto no formato <c>palavra-palavra-numero</c> (ex.: <c>swift-river-4821</c>)
/// para rastrear uma execuçao de /cloudlog no Worker de logs.
/// Também converte a URL de visualização em QR Code ASCII para exibição no console.
/// </summary>
public static class CloudLogSessionId
{
    private static readonly string[] Adjectives =
    [
        "swift", "calm", "bold", "bright", "clear", "cool", "dark", "deep",
        "fair", "fast", "firm", "free", "full", "glad", "gold", "good",
        "keen", "kind", "late", "light", "lone", "mild", "nice", "open",
        "pale", "pure", "quiet", "rich", "safe", "sharp", "slim", "smart",
        "soft", "still", "tall", "true", "vast", "warm", "wide", "wise",
    ];

    private static readonly string[] Nouns =
    [
        "river", "stone", "flame", "cloud", "storm", "field", "grove", "blade",
        "creek", "delta", "drift", "ember", "flare", "frost", "glade", "gleam",
        "haven", "blaze", "ledge", "maple", "marsh", "mist", "north", "oasis",
        "orbit", "ridge", "shore", "spark", "brook", "trace", "trail", "vale",
        "vault", "villa", "vista", "wave", "winds", "woods", "yard", "zenith",
    ];

    /// <summary>Gera um session ID único no formato <c>adjetivo-substantivo-numero</c>.</summary>
    public static string Generate()
    {
        var rng = Random.Shared;
        string adj  = Adjectives[rng.Next(Adjectives.Length)];
        string noun = Nouns[rng.Next(Nouns.Length)];
        int    num  = rng.Next(1000, 10000);
        return $"{adj}-{noun}-{num}";
    }

    /// <summary>URL de visualização pública do log da sessão.</summary>
    public static string ViewUrl(string sessionId) =>
        $"https://winprovision-logs.gabriel-silva20090.workers.dev/view?session={sessionId}";

    /// <summary>URL do endpoint de push (POST) para enviar mensagens.</summary>
    public static string PushUrl(string sessionId) =>
        $"https://winprovision-logs.gabriel-silva20090.workers.dev/push?session={sessionId}";

    /// <summary>
    /// Renderiza a <paramref name="url"/> como QR Code ASCII com módulos ██ (escuro) e
    /// espaço duplo (claro). Usa uma implementação pure-C# sem dependências externas.
    /// </summary>
    public static string QrAscii(string url)
    {
        bool[,] matrix = QrEncoder.Encode(url);
        int size = matrix.GetLength(0);

        var sb = new StringBuilder();
        const string dark   = "██";
        const string light  = "  ";
        const string border = "  ";

        // Borda quiet-zone superior
        string topBottom = border + string.Concat(Enumerable.Repeat(dark, size + 2)) + border;
        sb.AppendLine(topBottom);

        for (int row = 0; row < size; row++)
        {
            sb.Append(border).Append(dark);
            for (int col = 0; col < size; col++)
                sb.Append(matrix[row, col] ? dark : light);
            sb.Append(dark).AppendLine(border);
        }

        sb.AppendLine(topBottom);
        return sb.ToString();
    }
}
