using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;
using WinProvision.Core.Services;

// Cliente de demonstração/teste manual do StoreService + WingetExecutor.
// Baixa o catálogo já publicado (apps.json da branch 'database') e permite
// buscar/instalar interativamente. Útil enquanto a interface gráfica não existe.
// A geração do catálogo em si é feita pelo WinProvision.Indexer, um projeto separado.

Console.WriteLine("==================================================");
Console.WriteLine("    WinProvision Core - Cliente de Demonstração");
Console.WriteLine("==================================================");

CheckAdminStatus();

var storeService = new StoreService();
var wingetExecutor = new WingetExecutor();

Console.WriteLine("\n[1] Carregando catálogo de aplicativos...");
var timer = Stopwatch.StartNew();

var catalog = await storeService.LoadCatalogAsync();
timer.Stop();

Console.WriteLine($"[SUCESSO] {catalog.Count:N0} aplicativos carregados em {timer.ElapsedMilliseconds} ms!");

while (true)
{
    Console.WriteLine("\n--------------------------------------------------");
    Console.Write("Digite o nome de um app para buscar (ou 'sair'): ");
    string? query = Console.ReadLine();

    if (string.IsNullOrWhiteSpace(query) || query.Equals("sair", StringComparison.OrdinalIgnoreCase))
        break;

    timer.Restart();
    var results = storeService.Search(query).Take(10).ToList();
    timer.Stop();

    Console.WriteLine($"\nResultados para '{query}' ({results.Count} exibidos - busca em {timer.ElapsedMilliseconds} ms):");

    for (int i = 0; i < results.Count; i++)
    {
        var app = results[i];
        Console.WriteLine($" [{i + 1}] {app.Name} ({app.Publisher}) - score {app.Score}");
        Console.WriteLine($"     ID: {app.Id} | Versao: {app.Version}");
        Console.WriteLine($"     Icone: {app.IconUrl}");
    }

    if (results.Count == 0)
    {
        Console.WriteLine("Nenhum aplicativo encontrado.");
        continue;
    }

    Console.Write("\nDeseja testar a instalacao de algum aplicativo da lista? (digite o numero ou 'n'): ");
    string? selection = Console.ReadLine();

    if (int.TryParse(selection, out int index) && index >= 1 && index <= results.Count)
    {
        var selectedApp = results[index - 1];
        Console.WriteLine($"\nIniciando instalacao silenciosa de: {selectedApp.Name} ({selectedApp.Id})...");
        Console.WriteLine("Logs em tempo real do Winget:");
        Console.WriteLine("--------------------------------------------------");

        var result = await wingetExecutor.InstallAppAsync(selectedApp.Id, log =>
        {
            Console.WriteLine($" > {log}");
        });

        Console.WriteLine("--------------------------------------------------");
        Console.WriteLine(result.Success
            ? $"[SUCESSO] {selectedApp.Name} instalado com sucesso!"
            : $"[ERRO] Falha na instalacao. Código de saída: {result.ExitCode}");
    }
}

Console.WriteLine("\nTeste encerrado com sucesso.");

[SupportedOSPlatform("windows")]
static void CheckAdminStatus()
{
    bool isAdmin = new WindowsPrincipal(WindowsIdentity.GetCurrent())
        .IsInRole(WindowsBuiltInRole.Administrator);

    if (isAdmin)
    {
        Console.WriteLine("[STATUS] Executando como ADMINISTRADOR (Instalações silenciosas sem UAC).");
    }
    else
    {
        Console.WriteLine("[AVISO] Executando em MODO USUÁRIO.");
        Console.WriteLine("        Instalações que exigem privilégios podem solicitar elevação UAC.");
    }
}
