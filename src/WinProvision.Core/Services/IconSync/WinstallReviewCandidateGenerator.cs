using WinProvision.Core.Models;

namespace WinProvision.Core.Services.IconSync;

/// <summary>
/// Sugere possíveis correspondências entre arquivos de ícone do Winstall (cujo nome
/// não é o PackageIdentifier) e apps do catálogo, por sobreposição de tokens (índice
/// de Jaccard) entre o nome do arquivo e o Id/Nome de cada app.
///
/// Isso é SÓ um gerador de sugestões: nunca escreve no arquivo de mapeamento aprovado.
/// Um catálogo público mostrando o ícone errado é pior que não mostrar ícone nenhum
/// (cai no genérico embutido do IconService) — similaridade de texto tem falsos
/// positivos previsíveis demais (ex.: "Photo" vs "Photoshop") pra publicar sem revisão
/// humana. Ver docs/ICON_APPROVAL.md para o fluxo de aprovação.
/// </summary>
public class WinstallReviewCandidateGenerator
{
    private const double MinConfidenceToSuggest = 0.34;
    private const int MaxCandidatesPerFile = 1;

    public List<WinstallReviewCandidate> Generate(
        string winstallDir,
        HashSet<string> alreadyApprovedFileNames,
        List<AppEntry> catalog)
    {
        var candidates = new List<WinstallReviewCandidate>();
        if (!Directory.Exists(winstallDir)) return candidates;

        var catalogTokens = catalog
            .Select(app => (App: app, Tokens: IconIdNormalizer.Tokenize($"{app.Id} {app.Name}")))
            .Where(x => x.Tokens.Count > 0)
            .ToList();

        foreach (var file in Directory.EnumerateFiles(winstallDir, "*", SearchOption.AllDirectories))
        {
            string relativePath = Path.GetRelativePath(winstallDir, file).Replace('\\', '/');
            if (alreadyApprovedFileNames.Contains(relativePath)) continue;

            var fileTokens = IconIdNormalizer.Tokenize(Path.GetFileNameWithoutExtension(file));
            if (fileTokens.Count == 0) continue;

            var best = catalogTokens
                .Select(x => (x.App, Confidence: JaccardSimilarity(fileTokens, x.Tokens)))
                .Where(x => x.Confidence >= MinConfidenceToSuggest)
                .OrderByDescending(x => x.Confidence)
                .Take(MaxCandidatesPerFile);

            foreach (var (app, confidence) in best)
            {
                candidates.Add(new WinstallReviewCandidate(relativePath, app.Id, app.Name, Math.Round(confidence, 2)));
            }
        }

        return candidates.OrderByDescending(c => c.Confidence).ToList();
    }

    private static double JaccardSimilarity(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 || b.Count == 0) return 0;

        int intersection = a.Count(b.Contains);
        int union = a.Count + b.Count - intersection;

        return union == 0 ? 0 : intersection / (double)union;
    }
}
