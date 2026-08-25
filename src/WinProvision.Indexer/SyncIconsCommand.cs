using WinProvision.Core.Services.IconSync;

namespace WinProvision.Indexer;

/// <summary>
/// Parser de argumentos e ponto de entrada do subcomando `sync-icons`, despachado
/// pelo Program.cs quando o primeiro argumento é literalmente "sync-icons" — o modo
/// padrão de indexação (varredura do winget-pkgs) continua funcionando sem alteração
/// nenhuma pra quem já chama `WinProvision.Indexer.dll <manifests> <output>` direto.
/// </summary>
public static class SyncIconsCommand
{
    private static readonly string[] RequiredFlags =
        ["catalog", "winstall-dir", "external-dir", "unigetui-dir", "approved-mappings", "output-dir"];

    public static async Task<int> RunAsync(string[] args)
    {
        var flags = ParseFlags(args);
        var missing = RequiredFlags.Where(f => !flags.ContainsKey(f)).ToList();

        if (missing.Count > 0)
        {
            Console.Error.WriteLine(
                "Uso: WinProvision.Indexer sync-icons --catalog <caminho-ou-url> --winstall-dir <pasta> " +
                "--external-dir <pasta> --unigetui-dir <pasta> --approved-mappings <arquivo> --output-dir <pasta>");
            Console.Error.WriteLine($"Faltando: {string.Join(", ", missing.Select(m => $"--{m}"))}");
            return 1;
        }

        var options = new IconSyncOptions(
            CatalogPath: flags["catalog"],
            WinstallDir: flags["winstall-dir"],
            ExternalDir: flags["external-dir"],
            UniGetUiDir: flags["unigetui-dir"],
            ApprovedMappingsPath: flags["approved-mappings"],
            OutputDir: flags["output-dir"]);

        Console.WriteLine("==================================================");
        Console.WriteLine("  WinProvision Store - Sincronização de Ícones");
        Console.WriteLine("==================================================");

        var pipeline = new IconSyncPipeline();
        var stats = await pipeline.RunAsync(options);

        Console.WriteLine($"\n      Catálogo:                                  {stats.CatalogSize:N0} apps");
        Console.WriteLine($"      Resolvidos via Winstall aprovado:          {stats.ResolvedFromWinstallApproved:N0}");
        Console.WriteLine($"      Resolvidos via package-icons externo:      {stats.ResolvedFromExternal:N0}");
        Console.WriteLine($"      Resolvidos via UniGetUI:                   {stats.ResolvedFromUniGetUi:N0}");
        Console.WriteLine($"      Sem ícone encontrado:                      {stats.Unresolved:N0}");
        Console.WriteLine($"      Candidatos de revisão do Winstall gerados: {stats.WinstallReviewCandidatesGenerated:N0}");
        Console.WriteLine($"\n[SUCESSO] icons-database.json e winstall-review-candidates.json publicados em '{options.OutputDir}'.");

        return 0;
    }

    private static Dictionary<string, string> ParseFlags(string[] args)
    {
        var flags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // args[0] é o nome do subcomando ("sync-icons") — o parsing começa depois dele.
        for (int i = 1; i < args.Length; i++)
        {
            if (args[i].StartsWith("--") && i + 1 < args.Length)
            {
                flags[args[i][2..]] = args[i + 1];
                i++;
            }
        }

        return flags;
    }
}
