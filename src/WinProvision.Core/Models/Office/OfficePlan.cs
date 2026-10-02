using System;
using System.Collections.Generic;
using System.Linq;

namespace WinProvision.Core.Models.Office;

public enum OfficeEditionCategory
{
    Corporate365,
    Ltsc,
    Personal,
    VisioProject
}

/// <summary>
/// Um plano instalável de Office. ProductId e Channel seguem exatamente os valores
/// documentados em learn.microsoft.com/microsoft-365-apps/deploy — nunca inventados.
/// </summary>
public record OfficePlan(
    string DisplayName,
    OfficeEditionCategory Category,
    string ProductId,
    string? Channel,
    bool IsVolumeLicensed)
{
    /// <summary>
    /// Produtos Retail/assinatura aceitam canais de manutenção Microsoft 365;
    /// Office LTSC mantém o canal PerpetualVL obrigatório.
    /// </summary>
    public bool SupportsSelectableChannel => Category != OfficeEditionCategory.Ltsc &&
        (!IsVolumeLicensed || (Category == OfficeEditionCategory.VisioProject && ProductId.Contains("2024", StringComparison.OrdinalIgnoreCase)));

    public string? Description { get; init; }
    public string? IconUrl { get; init; }
    public string? BannerUrl { get; init; }
    public IReadOnlyList<string> Screenshots { get; init; } = Array.Empty<string>();

    public static readonly OfficePlan Microsoft365Enterprise =
        new("Microsoft 365 Apps for enterprise", OfficeEditionCategory.Corporate365, "O365ProPlusRetail", "Current", false);

    public static readonly OfficePlan Microsoft365Business =
        new("Microsoft 365 Apps for business", OfficeEditionCategory.Corporate365, "O365BusinessRetail", "Current", false)
        {
            Description = "Microsoft 365 Apps para empresas. A oferta da Store não substitui o Product ID exigido pelo ODT.",
            IconUrl = "https://pub-166b41912a994dbe86583ba10596d673.r2.dev/Office/Icon/MS365.png",
        };

    public static readonly OfficePlan LtscProPlus2024 =
        new("Office LTSC Professional Plus 2024", OfficeEditionCategory.Ltsc, "ProPlus2024Volume", "PerpetualVL2024", true);

    public static readonly OfficePlan LtscProPlus2021 =
        new("Office LTSC Professional Plus 2021", OfficeEditionCategory.Ltsc, "ProPlus2021Volume", "PerpetualVL2021", true);

    public static readonly OfficePlan LtscProPlus2019 =
        new("Office LTSC Professional Plus 2019", OfficeEditionCategory.Ltsc, "ProPlus2019Volume", "PerpetualVL2019", true);

    public static readonly OfficePlan LtscStandard2024 =
        new("Office LTSC Standard 2024", OfficeEditionCategory.Ltsc, "Standard2024Volume", "PerpetualVL2024", true);

    public static readonly OfficePlan LtscStandard2021 =
        new("Office LTSC Standard 2021", OfficeEditionCategory.Ltsc, "Standard2021Volume", "PerpetualVL2021", true);

    public static readonly OfficePlan LtscStandard2019 =
        new("Office LTSC Standard 2019", OfficeEditionCategory.Ltsc, "Standard2019Volume", "PerpetualVL2019", true);

    public static readonly OfficePlan Family =
        new("Microsoft 365 Family", OfficeEditionCategory.Personal, "O365HomePremRetail", "Current", false)
        {
            Description = "Configuração Microsoft 365 para uso pessoal e familiar. Personal, Family e Premium usam este Product ID do ODT; a assinatura e seus benefícios são vinculados à conta Microsoft.",
            IconUrl = "pack://application:,,,/Assets/Office/Microsoft365FamilyIcon.jpg",
            BannerUrl = "pack://application:,,,/Assets/Office/Microsoft365FamilyBanner.jpg",
        };

    public static readonly OfficePlan HomeStudent2024 =
        new("Office Home & Student 2024", OfficeEditionCategory.Personal, "HomeStudent2024Retail", "Current", false);

    public static readonly OfficePlan HomeStudent2021 =
        new("Office Home & Student 2021", OfficeEditionCategory.Personal, "HomeStudent2021Retail", "Current", false);

    public static readonly OfficePlan HomeBusiness2024 =
        new("Office Home & Business 2024", OfficeEditionCategory.Personal, "HomeBusiness2024Retail", "Current", false);

    public static readonly OfficePlan HomeBusiness2021 =
        new("Office Home & Business 2021", OfficeEditionCategory.Personal, "HomeBusiness2021Retail", "Current", false);

    public static readonly OfficePlan Home2024 =
        new("Office Home 2024", OfficeEditionCategory.Personal, "Home2024Retail", "Current", false);

    // --- Visio ---
    // IDs Retail para assinaturas Microsoft 365 e licenças de varejo, conforme a lista oficial do ODT.
    public static readonly OfficePlan VisioProSubscription =
        new("Visio Professional (Microsoft 365)", OfficeEditionCategory.VisioProject, "VisioProRetail", "Current", false);

    public static readonly OfficePlan VisioStandardRetail =
        new("Visio Standard", OfficeEditionCategory.VisioProject, "VisioStdRetail", "Current", false);

    public static readonly OfficePlan VisioPro2024Retail =
        new("Visio Professional 2024 (Retail)", OfficeEditionCategory.VisioProject, "VisioPro2024Retail", "Current", false);

    public static readonly OfficePlan VisioStandard2024Retail =
        new("Visio Standard 2024 (Retail)", OfficeEditionCategory.VisioProject, "VisioStd2024Retail", "Current", false);

    public static readonly OfficePlan VisioPro2021Retail =
        new("Visio Professional 2021 (Retail)", OfficeEditionCategory.VisioProject, "VisioPro2021Retail", "Current", false);

    public static readonly OfficePlan VisioStandard2021Retail =
        new("Visio Standard 2021 (Retail)", OfficeEditionCategory.VisioProject, "VisioStd2021Retail", "Current", false);

    public static readonly OfficePlan VisioPro2019Retail =
        new("Visio Professional 2019 (Retail)", OfficeEditionCategory.VisioProject, "VisioPro2019Retail", "Current", false);

    public static readonly OfficePlan VisioStandard2019Retail =
        new("Visio Standard 2019 (Retail)", OfficeEditionCategory.VisioProject, "VisioStd2019Retail", "Current", false);

    public static readonly OfficePlan VisioPro2024 =
        new("Visio LTSC Professional 2024", OfficeEditionCategory.VisioProject, "VisioPro2024Volume", "PerpetualVL2024", true);

    public static readonly OfficePlan VisioPro2021 =
        new("Visio LTSC Professional 2021", OfficeEditionCategory.VisioProject, "VisioPro2021Volume", "PerpetualVL2021", true);

    public static readonly OfficePlan VisioPro2019 =
        new("Visio Professional 2019", OfficeEditionCategory.VisioProject, "VisioPro2019Volume", "PerpetualVL2019", true);

    public static readonly OfficePlan VisioStd2021 =
        new("Visio LTSC Standard 2021", OfficeEditionCategory.VisioProject, "VisioStd2021Volume", "PerpetualVL2021", true);

    // --- Project ---
    public static readonly OfficePlan ProjectProSubscription =
        new("Project Professional (Microsoft 365)", OfficeEditionCategory.VisioProject, "ProjectProRetail", "Current", false);

    public static readonly OfficePlan ProjectStandardRetail =
        new("Project Standard", OfficeEditionCategory.VisioProject, "ProjectStdRetail", "Current", false);

    public static readonly OfficePlan ProjectPro2024Retail =
        new("Project Professional 2024 (Retail)", OfficeEditionCategory.VisioProject, "ProjectPro2024Retail", "Current", false);

    public static readonly OfficePlan ProjectStandard2024Retail =
        new("Project Standard 2024 (Retail)", OfficeEditionCategory.VisioProject, "ProjectStd2024Retail", "Current", false);

    public static readonly OfficePlan ProjectPro2021Retail =
        new("Project Professional 2021 (Retail)", OfficeEditionCategory.VisioProject, "ProjectPro2021Retail", "Current", false);

    public static readonly OfficePlan ProjectStandard2021Retail =
        new("Project Standard 2021 (Retail)", OfficeEditionCategory.VisioProject, "ProjectStd2021Retail", "Current", false);

    public static readonly OfficePlan ProjectPro2019Retail =
        new("Project Professional 2019 (Retail)", OfficeEditionCategory.VisioProject, "ProjectPro2019Retail", "Current", false);

    public static readonly OfficePlan ProjectStandard2019Retail =
        new("Project Standard 2019 (Retail)", OfficeEditionCategory.VisioProject, "ProjectStd2019Retail", "Current", false);

    public static readonly OfficePlan ProjectPro2024 =
        new("Project LTSC Professional 2024", OfficeEditionCategory.VisioProject, "ProjectPro2024Volume", "PerpetualVL2024", true);

    public static readonly OfficePlan ProjectPro2021 =
        new("Project LTSC Professional 2021", OfficeEditionCategory.VisioProject, "ProjectPro2021Volume", "PerpetualVL2021", true);

    public static readonly OfficePlan ProjectPro2019 =
        new("Project Professional 2019", OfficeEditionCategory.VisioProject, "ProjectPro2019Volume", "PerpetualVL2019", true);

    public static readonly OfficePlan ProjectStd2021 =
        new("Project LTSC Standard 2021", OfficeEditionCategory.VisioProject, "ProjectStd2021Volume", "PerpetualVL2021", true);
}

/// <summary>Oferta comercial da Store ligada a um plano de implantação ODT por uma relação explícita.</summary>
public sealed record OfficeStoreOffer(string StoreProductId, string DisplayName, string OdtProductId,
    string? Description = null, string? IconUrl = null, string? BannerUrl = null,
    IReadOnlyList<string>? Screenshots = null);

public static class OfficeStoreOfferCatalog
{
    private static readonly HashSet<string> OffersExpectedToHaveScreenshots = new(StringComparer.OrdinalIgnoreCase)
    {
        "CFQ7TTC0K5DM", "CFQ7TTC0K5BF",
    };
    private static IReadOnlyList<OfficeStoreOffer> _all = Array.AsReadOnly(new[]
    {
        new OfficeStoreOffer("CFQ7TTC0K5DM", "Microsoft 365 Family", "O365HomePremRetail", "Assinatura Microsoft 365 Family.", "pack://application:,,,/Assets/Office/Microsoft365FamilyIcon.jpg", "pack://application:,,,/Assets/Office/Microsoft365FamilyBanner.jpg"),
        new OfficeStoreOffer("CFQ7TTC0K5BF", "Microsoft 365 Personal", "O365HomePremRetail", "Assinatura Microsoft 365 Personal.", "pack://application:,,,/Assets/Office/Microsoft365PersonalIcon.jpg", "pack://application:,,,/Assets/Office/Microsoft365PersonalBanner.jpg"),
        new OfficeStoreOffer("CFQ7TTC0K5CH", "Microsoft 365 Business Standard", "O365BusinessRetail", "Assinatura Microsoft 365 para empresas.", "https://pub-166b41912a994dbe86583ba10596d673.r2.dev/Office/Icon/MS365.png", "pack://application:,,,/Assets/Office/Microsoft365FamilyBanner.jpg"),
    });
    private static readonly object Sync = new();
    public static IReadOnlyList<OfficeStoreOffer> All { get { lock (Sync) return _all; } }
    public static bool HasFeaturedScreenshots
    {
        get
        {
            lock (Sync)
                return OffersExpectedToHaveScreenshots.All(id => _all.Any(offer =>
                    offer.StoreProductId.Equals(id, StringComparison.OrdinalIgnoreCase) && offer.Screenshots is { Count: > 0 }));
        }
    }
    public static void ApplyRemote(IEnumerable<OfficeStoreOffer> offers)
    {
        ArgumentNullException.ThrowIfNull(offers);
        lock (Sync)
        {
            var merged = _all.ToDictionary(x => x.StoreProductId, StringComparer.OrdinalIgnoreCase);
            foreach (var offer in offers)
            {
                if (merged.TryGetValue(offer.StoreProductId, out var fallback))
                {
                    merged[offer.StoreProductId] = offer with
                    {
                        Description = offer.Description ?? fallback.Description,
                        IconUrl = IsPackAsset(fallback.IconUrl) ? fallback.IconUrl : offer.IconUrl ?? fallback.IconUrl,
                        BannerUrl = IsPackAsset(fallback.BannerUrl) ? fallback.BannerUrl : offer.BannerUrl ?? fallback.BannerUrl,
                        Screenshots = offer.Screenshots is { Count: > 0 } ? offer.Screenshots : fallback.Screenshots,
                    };
                }
                else
                {
                    merged[offer.StoreProductId] = offer;
                }
            }
            // Algumas ofertas de subscrição têm IDs comerciais alternativos, mas
            // representam o mesmo produto/plano. Mostrar só um cartão por oferta ODT.
            _all = Array.AsReadOnly(merged.Values
                .GroupBy(x => $"{x.OdtProductId}|{x.DisplayName}", StringComparer.OrdinalIgnoreCase)
                .Select(group => group.FirstOrDefault(x => x.StoreProductId.Equals("CFQ7TTC0K5BF", StringComparison.OrdinalIgnoreCase)) ?? group.First())
                .OrderBy(x => x.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToArray());
        }
    }

    private static bool IsPackAsset(string? url) => url?.StartsWith("pack://application:", StringComparison.OrdinalIgnoreCase) == true;
}

public static class OfficePlanCatalog
{
    private static readonly IReadOnlyList<OfficePlan> BuiltIn =
    [
        OfficePlan.Microsoft365Enterprise,
        OfficePlan.Microsoft365Business,
        OfficePlan.LtscProPlus2024,
        OfficePlan.LtscProPlus2021,
        OfficePlan.LtscProPlus2019,
        OfficePlan.LtscStandard2024,
        OfficePlan.LtscStandard2021,
        OfficePlan.LtscStandard2019,
        OfficePlan.Family,
        OfficePlan.HomeStudent2024,
        OfficePlan.HomeStudent2021,
        OfficePlan.HomeBusiness2024,
        OfficePlan.HomeBusiness2021,
        OfficePlan.Home2024,
        // ODT aceita estes Product IDs Retail para assinaturas Visio/Project e edições avulsas.
        // Mantê-los no catálogo local permite criar a configuração sem depender da API remota.
        OfficePlan.VisioProSubscription,
        OfficePlan.VisioStandardRetail,
        OfficePlan.VisioPro2024Retail,
        OfficePlan.VisioStandard2024Retail,
        OfficePlan.VisioPro2021Retail,
        OfficePlan.VisioStandard2021Retail,
        OfficePlan.VisioPro2019Retail,
        OfficePlan.VisioStandard2019Retail,
        OfficePlan.VisioPro2024,
        OfficePlan.VisioPro2021,
        OfficePlan.VisioPro2019,
        OfficePlan.VisioStd2021,
        OfficePlan.ProjectProSubscription,
        OfficePlan.ProjectStandardRetail,
        OfficePlan.ProjectPro2024Retail,
        OfficePlan.ProjectStandard2024Retail,
        OfficePlan.ProjectPro2021Retail,
        OfficePlan.ProjectStandard2021Retail,
        OfficePlan.ProjectPro2019Retail,
        OfficePlan.ProjectStandard2019Retail,
        OfficePlan.ProjectPro2024,
        OfficePlan.ProjectPro2021,
        OfficePlan.ProjectPro2019,
        OfficePlan.ProjectStd2021,
    ];

    private static IReadOnlyList<OfficePlan> _all = BuiltIn;
    private static readonly object Sync = new();
    public static IReadOnlyList<OfficePlan> All { get { lock (Sync) return _all; } }

    /// <summary>Aplica um catálogo validado, sempre preservando os planos embutidos como fallback.</summary>
    public static void ApplyRemote(IEnumerable<OfficePlan> plans)
    {
        ArgumentNullException.ThrowIfNull(plans);
        lock (Sync)
        {
            var merged = BuiltIn.ToDictionary(p => p.ProductId, StringComparer.OrdinalIgnoreCase);
            foreach (var plan in plans)
            {
                if (merged.TryGetValue(plan.ProductId, out var fallback))
                {
                    merged[plan.ProductId] = plan with
                    {
                        DisplayName = plan.DisplayName,
                        Description = plan.Description ?? fallback.Description,
                        IconUrl = IsPackAsset(fallback.IconUrl) ? fallback.IconUrl : plan.IconUrl ?? fallback.IconUrl,
                        BannerUrl = IsPackAsset(fallback.BannerUrl) ? fallback.BannerUrl : plan.BannerUrl ?? fallback.BannerUrl,
                        Screenshots = plan.Screenshots.Count > 0 ? plan.Screenshots : fallback.Screenshots,
                    };
                }
                else
                {
                    merged[plan.ProductId] = plan;
                }
            }
            _all = Array.AsReadOnly(merged.Values.OrderBy(p => p.Category).ThenBy(p => p.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToArray());
        }
    }

    public static IEnumerable<OfficePlan> ByCategory(OfficeEditionCategory category) =>
        All.Where(p => p.Category == category);

    /// <summary>Resolve um plano do catálogo a partir do ProductId cru lido do registro (ProductReleaseIds).</summary>
    public static OfficePlan? ByProductId(string productId) =>
        All.FirstOrDefault(p => string.Equals(p.ProductId, productId, StringComparison.OrdinalIgnoreCase));

    private static bool IsPackAsset(string? url) => url?.StartsWith("pack://application:", StringComparison.OrdinalIgnoreCase) == true;
}

/// <summary>
/// Catálogo fixo dos aplicativos que aparecem na grade "Seleção de Aplicativos" e na
/// lista "Produtos Instalados" da tela do Office — os IDs são exatamente os aceitos
/// pelo elemento &lt;ExcludeApp ID="..."/&gt; do ODT. IconUrl aponta pros ícones oficiais
/// fornecidos para o projeto; carregados em runtime via AsyncImage (não embutidos no build).
/// </summary>
public static class OfficeAppCatalog
{
    private const string IconBaseUrl = "https://pub-166b41912a994dbe86583ba10596d673.r2.dev/Office/Icon";

    public static readonly IReadOnlyList<(string Id, string DisplayName, string IconUrl)> CoreApps =
    [
        ("Word", "Word", $"{IconBaseUrl}/Word.png"),
        ("Excel", "Excel", $"{IconBaseUrl}/Excel.png"),
        ("PowerPoint", "PowerPoint", $"{IconBaseUrl}/PowerPoint.png"),
        ("Outlook", "Outlook", $"{IconBaseUrl}/Outlook.png"),
        ("OneNote", "OneNote", $"{IconBaseUrl}/OneNote.png"),
        ("Access", "Access", $"{IconBaseUrl}/Access.png"),
        ("Publisher", "Publisher", $"{IconBaseUrl}/Publisher.png"),
        ("Teams", "Teams", $"{IconBaseUrl}/Teams.png"),
    ];

    /// <summary>Produtos opcionais Office implantados como Product ODT, e não como ExcludeApp.</summary>
    public static readonly IReadOnlyList<(string Id, string DisplayName, string IconUrl)> AdditionalProducts =
    [
        ("Visio", "Visio", $"{IconBaseUrl}/Visio.png"),
        ("Project", "Project", $"{IconBaseUrl}/Project.png"),
    ];

    /// <summary>Excludes menos comuns, agrupados nas opções avançadas em vez da grade principal.</summary>
    public static readonly IReadOnlyList<(string Id, string DisplayName)> AdvancedApps =
    [
        ("Groove", "OneDrive for Business (legado)"),
        ("Lync", "Skype for Business"),
        ("Bing", "Suplementos Bing"),
    ];
}

/// <summary>
/// Nomes aceitos pelo ODT para canais de atualização do Microsoft 365.
/// Office LTSC mantém o canal PerpetualVLxxxx exigido pela edição.
/// </summary>
public static class OfficeChannelCatalog
{
    public static readonly IReadOnlyList<(string Id, string DisplayName)> SubscriptionChannels =
    [
        ("Current", "Canal Atual"),
        ("MonthlyEnterprise", "Enterprise Mensal"),
        ("SemiAnnual", "Semestral (Corrente)"),
        ("SemiAnnualPreview", "Semestral (Prévia)"),
        ("CurrentPreview", "Canal Atual (Prévia)"),
        ("BetaChannel", "Beta / Insiders"),
    ];
}

/// <summary>Nível de interface do setup.exe do ODT durante /configure (atributo Display/Level).</summary>
public enum OfficeDisplayLevel
{
    /// <summary>Level="None" — nenhuma UI, nenhuma barra de progresso do instalador nativo.</summary>
    Silent,
    /// <summary>Level="Full" — UI completa do instalador da Microsoft.</summary>
    Visible,
}

public record OfficeInstallRequest(
    OfficePlan Plan,
    int Architecture,                 // 32 ou 64
    string LanguageId,                // idioma principal, ex: "pt-br"
    IReadOnlyList<string> ExcludedApps,
    bool DisplayNone = true,
    bool AcceptEula = true,
    IReadOnlyList<string>? AdditionalLanguageIds = null,
    OfficeDisplayLevel DisplayLevel = OfficeDisplayLevel.Silent,
    /// <summary>
    /// Sobrescreve o Channel padrão dos planos que aceitam seleção de canal.
    /// Office LTSC preserva o canal PerpetualVLxxxx obrigatório.
    /// </summary>
    string? ChannelOverride = null,
    /// <summary>Gera o elemento &lt;Updates Enabled="TRUE|FALSE"/&gt; do ODT, controlando a política de atualização automática do Office nesta máquina.</summary>
    bool AutoUpdatesEnabled = true,
    IReadOnlyList<OfficePlan>? AdditionalProducts = null,
    /// <summary>Origem local opcional para reutilizar conteúdo já baixado pelo ODT.</summary>
    string? SourcePath = null);

/// <summary>
/// Um produto Click-to-Run detectado no registro (ver Configuration\ProductReleaseIds),
/// já mapeado para o OfficePlan correspondente quando reconhecido pelo catálogo.
/// </summary>
public record OfficeInstalledProduct(
    string ProductId,
    OfficePlan? KnownPlan,
    string? VersionToReport,
    string? Platform,
    string? ClientCulture,
    IReadOnlyList<string> ExcludedApps)
{
    public string DisplayName => KnownPlan?.DisplayName ?? ProductId;
}

/// <summary>
/// Pedido de remoção via setup.exe /configure com o elemento &lt;Remove&gt;. Use
/// <see cref="RemoveAll"/> para a tag RemoveAll (equivalente a &lt;Remove All="TRUE"/&gt;,
/// removendo todos os produtos Click-to-Run da máquina de uma vez), ou informe
/// <see cref="ProductIds"/> para remover produtos específicos previamente detectados.
/// </summary>
public record OfficeRemoveRequest(
    bool RemoveAll,
    IReadOnlyList<string>? ProductIds = null,
    OfficeDisplayLevel DisplayLevel = OfficeDisplayLevel.Silent,
    /// <summary>Também remove a edição da Microsoft Store (pacote AppX) do Office, se presente.</summary>
    bool CleanStoreEdition = true,
    /// <summary>Usa RemoveMSI para remover instalações MSI do Office (método tradicional) que não são removidas pelo método padrão.</summary>
    bool UseRemoveMSI = true,
    /// <summary>Usa o método de desinstalação agressivo (GetHelpCmd) se o método padrão falhar.</summary>
    bool UseAggressiveUninstall = true);

/// <summary>Representa uma instalação do Office detectada no sistema.</summary>
public record OfficeInstallation(string DisplayName, string UninstallString);

/// <summary>Resultado de uma operação de desinstalação do Office.</summary>
public enum OfficeUninstallOutcome
{
    /// <summary>Office não está instalado.</summary>
    NotInstalled,
    /// <summary>Removido com sucesso usando o método padrão.</summary>
    RemovedByStandardMethod,
    /// <summary>Removido com sucesso usando o método agressivo (GetHelpCmd).</summary>
    RemovedByAggressiveMethod,
    /// <summary>Falha na desinstalação.</summary>
    Failed
}
