namespace WinProvision.Core.Services.Indexing;

/// <summary>
/// Compara versões de pacotes do winget, que nem sempre seguem SemVer estrito
/// (ex.: "2024.08.1", "1.2.3-beta", "23H2", "r45"). Compara segmento a segmento,
/// numericamente quando possível, com fallback textual.
/// </summary>
public class VersionComparer : IComparer<string>
{
    public static readonly VersionComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (x == y) return 0;
        if (x is null) return -1;
        if (y is null) return 1;

        var partsX = SplitSegments(x);
        var partsY = SplitSegments(y);
        int length = Math.Max(partsX.Count, partsY.Count);

        for (int i = 0; i < length; i++)
        {
            string a = i < partsX.Count ? partsX[i] : string.Empty;
            string b = i < partsY.Count ? partsY[i] : string.Empty;

            if (long.TryParse(a, out long numA) && long.TryParse(b, out long numB))
            {
                int cmp = numA.CompareTo(numB);
                if (cmp != 0) return cmp;
            }
            else
            {
                int cmp = string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
                if (cmp != 0) return cmp;
            }
        }

        return 0;
    }

    private static List<string> SplitSegments(string version)
        => version.Split(['.', '-', '+'], StringSplitOptions.RemoveEmptyEntries).ToList();
}
