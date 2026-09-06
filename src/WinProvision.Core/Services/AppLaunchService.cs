using Microsoft.Win32;
using WinProvision.Core.Models;

namespace WinProvision.Core.Services;

/// <summary>
/// Resolve o executável de um app já instalado para permitir "Abrir" a partir da Store.
///
/// O winget não tem (até hoje) um comando "run"/"launch" na CLI oficial, então a técnica
/// usada aqui - varrer as chaves de Uninstall do Registro do Windows e casar pelo nome do
/// app - é a mesma que ferramentas como o UniGetUI usam na prática: é somente leitura,
/// não exige elevação e é rápida (não spawna nenhum processo para localizar o caminho,
/// só para efetivamente abrir o app no final).
/// </summary>
public class AppLaunchService
{
    private static readonly string[] LocalMachineUninstallRoots =
    [
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
        @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
    ];

    private const string CurrentUserUninstallRoot = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    private readonly Dictionary<string, string> _resolvedCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Tenta localizar o executável principal do app instalado. Retorna null se não achar.</summary>
    public Task<string?> TryResolveExecutableAsync(AppEntry app, CancellationToken cancellationToken = default) =>
        Task.Run(() => TryResolveExecutable(app), cancellationToken);

    /// <summary>Abre o executável já resolvido (ver <see cref="TryResolveExecutableAsync"/>).</summary>
    public bool TryLaunch(string executablePath)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(executablePath)
            {
                UseShellExecute = true
            });
            return true;
        }
        catch
        {
            // Executável pode ter sido movido/removido depois da resolução; melhor
            // reportar falha ao chamador (que mostra mensagem amigável) do que derrubar a UI.
            return false;
        }
    }

    private string? TryResolveExecutable(AppEntry app)
    {
        if (_resolvedCache.TryGetValue(app.Id, out var cached) && File.Exists(cached))
            return cached;

        var candidates = new List<(string Path, int Score)>();

        foreach (var root in LocalMachineUninstallRoots)
        {
            CollectCandidates(Registry.LocalMachine, root, app, candidates);
        }

        CollectCandidates(Registry.CurrentUser, CurrentUserUninstallRoot, app, candidates);

        if (candidates.Count == 0)
            return null;

        string bestPath = candidates.OrderByDescending(c => c.Score).First().Path;
        _resolvedCache[app.Id] = bestPath;
        return bestPath;
    }

    private static void CollectCandidates(RegistryKey baseKey, string subKeyPath, AppEntry app, List<(string Path, int Score)> candidates)
    {
        using RegistryKey? uninstallKey = baseKey.OpenSubKey(subKeyPath);
        if (uninstallKey is null)
            return;

        foreach (string subKeyName in uninstallKey.GetSubKeyNames())
        {
            using RegistryKey? entry = uninstallKey.OpenSubKey(subKeyName);
            if (entry is null)
                continue;

            if (entry.GetValue("DisplayName") is not string { Length: > 0 } displayName)
                continue;

            int score = MatchScore(displayName, app);
            if (score <= 0)
                continue;

            string? exePath = ExtractExecutablePath(entry);
            if (!string.IsNullOrWhiteSpace(exePath) && File.Exists(exePath))
            {
                candidates.Add((exePath, score));
            }
        }
    }

    private static string? ExtractExecutablePath(RegistryKey entry)
    {
        // DisplayIcon geralmente aponta pro exe principal do app (às vezes com ",0" no
        // final - índice do ícone dentro do próprio arquivo, que precisa ser removido).
        if (entry.GetValue("DisplayIcon") is string { Length: > 0 } icon)
        {
            string cleanPath = icon.Split(',')[0].Trim('"');
            if (cleanPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(cleanPath))
                return cleanPath;
        }

        // Fallback: procura o maior .exe direto dentro do InstallLocation (heurística
        // simples - o executável principal costuma ser o maior arquivo da pasta raiz).
        if (entry.GetValue("InstallLocation") is string { Length: > 0 } installLocation &&
            Directory.Exists(installLocation))
        {
            return Directory.EnumerateFiles(installLocation, "*.exe", SearchOption.TopDirectoryOnly)
                .OrderByDescending(f => new FileInfo(f).Length)
                .FirstOrDefault();
        }

        return null;
    }

    private static int MatchScore(string displayName, AppEntry app)
    {
        string cleanDisplay = Normalize(displayName);
        string cleanAppName = Normalize(app.Name);

        if (cleanDisplay == cleanAppName)
            return 100;

        if (cleanDisplay.Contains(cleanAppName) || cleanAppName.Contains(cleanDisplay))
            return 60;

        if (displayName.Contains(app.Name, StringComparison.OrdinalIgnoreCase))
            return 40;

        return 0;
    }

    private static string Normalize(string value) =>
        value.Replace(" ", "").Replace("-", "").Replace(".", "").ToLowerInvariant();
}
