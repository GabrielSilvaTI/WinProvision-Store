using Microsoft.Win32;
using WinProvision.Core.Models.Office;

namespace WinProvision.Core.Services.Office;

/// <summary>
/// Lê (somente leitura) o estado de instalação do Office Click-to-Run a partir do
/// registro, para permitir listar "o que já está instalado nesta máquina" na tela de
/// desinstalação — sem precisar reinstalar nada só para descobrir isso.
///
/// Só lê chaves de estado de instalação documentadas publicamente
/// (learn.microsoft.com/microsoft-365-apps/deploy/click-to-run-registry-values):
/// ProductReleaseIds, *.ExcludedApps, VersionToReport, Platform e ClientCulture, todas
/// sob HKLM\SOFTWARE\Microsoft\Office\ClickToRun\Configuration. Não lê nem grava nada
/// relacionado a licenciamento/ativação — isso é fora do escopo deste serviço.
/// </summary>
public class OfficeInstalledProductsDetector
{
    private const string ConfigurationKeyPath = @"SOFTWARE\Microsoft\Office\ClickToRun\Configuration";

    /// <summary>Retorna a lista de produtos Click-to-Run instalados, ou vazia se nenhum Office C2R for encontrado.</summary>
    public IReadOnlyList<OfficeInstalledProduct> GetInstalledProducts()
    {
        using var key = Registry.LocalMachine.OpenSubKey(ConfigurationKeyPath);
        if (key == null)
            return [];

        string? productReleaseIds = key.GetValue("ProductReleaseIds") as string;
        if (string.IsNullOrWhiteSpace(productReleaseIds))
            return [];

        string? version = key.GetValue("VersionToReport") as string ?? key.GetValue("ClientVersionToReport") as string;
        string? platform = key.GetValue("Platform") as string;
        string? culture = key.GetValue("ClientCulture") as string ?? key.GetValue("ScenarioCulture") as string;

        var products = new List<OfficeInstalledProduct>();

        foreach (var rawId in productReleaseIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var excludedRaw = key.GetValue($"{rawId}.ExcludedApps") as string;
            var excludedApps = string.IsNullOrWhiteSpace(excludedRaw)
                ? Array.Empty<string>()
                : excludedRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            products.Add(new OfficeInstalledProduct(
                ProductId: rawId,
                KnownPlan: OfficePlanCatalog.ByProductId(rawId),
                VersionToReport: version,
                Platform: platform,
                ClientCulture: culture,
                ExcludedApps: excludedApps));
        }

        return products;
    }

    /// <summary>Atalho para saber se existe qualquer instalação Click-to-Run na máquina.</summary>
    public bool HasAnyInstallation() => GetInstalledProducts().Count > 0;
}
