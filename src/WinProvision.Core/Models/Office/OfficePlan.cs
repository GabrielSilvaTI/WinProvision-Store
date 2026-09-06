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
    public static readonly OfficePlan Microsoft365Enterprise =
        new("Microsoft 365 Apps for enterprise", OfficeEditionCategory.Corporate365, "O365ProPlusRetail", "Current", false);

    public static readonly OfficePlan Microsoft365Business =
        new("Microsoft 365 Apps for business", OfficeEditionCategory.Corporate365, "O365BusinessRetail", "Current", false);

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
        new("Microsoft 365 (Family & Pessoal)", OfficeEditionCategory.Personal, "O365HomePremRetail", "Current", false);

    public static readonly OfficePlan HomeStudent2024 =
        new("Office Home & Student 2024", OfficeEditionCategory.Personal, "HomeStudent2024Retail", "PerpetualVL2024", false);

    public static readonly OfficePlan HomeStudent2021 =
        new("Office Home & Student 2021", OfficeEditionCategory.Personal, "HomeStudent2021Retail", "PerpetualVL2021", false);

    public static readonly OfficePlan HomeBusiness2024 =
        new("Office Home & Business 2024", OfficeEditionCategory.Personal, "HomeBusiness2024Retail", "PerpetualVL2024", false);

    public static readonly OfficePlan HomeBusiness2021 =
        new("Office Home & Business 2021", OfficeEditionCategory.Personal, "HomeBusiness2021Retail", "PerpetualVL2021", false);

    public static readonly OfficePlan Home2024 =
        new("Office Home 2024", OfficeEditionCategory.Personal, "Home2024Retail", "PerpetualVL2024", false);

    // --- Visio ---
    public static readonly OfficePlan VisioPro2024 =
        new("Visio LTSC Professional 2024", OfficeEditionCategory.VisioProject, "VisioPro2024Volume", "PerpetualVL2024", true);

    public static readonly OfficePlan VisioPro2021 =
        new("Visio LTSC Professional 2021", OfficeEditionCategory.VisioProject, "VisioPro2021Volume", "PerpetualVL2021", true);

    public static readonly OfficePlan VisioPro2019 =
        new("Visio Professional 2019", OfficeEditionCategory.VisioProject, "VisioPro2019Volume", "PerpetualVL2019", true);

    public static readonly OfficePlan VisioStd2021 =
        new("Visio LTSC Standard 2021", OfficeEditionCategory.VisioProject, "VisioStd2021Volume", "PerpetualVL2021", true);

    // --- Project ---
    public static readonly OfficePlan ProjectPro2024 =
        new("Project LTSC Professional 2024", OfficeEditionCategory.VisioProject, "ProjectPro2024Volume", "PerpetualVL2024", true);

    public static readonly OfficePlan ProjectPro2021 =
        new("Project LTSC Professional 2021", OfficeEditionCategory.VisioProject, "ProjectPro2021Volume", "PerpetualVL2021", true);

    public static readonly OfficePlan ProjectPro2019 =
        new("Project Professional 2019", OfficeEditionCategory.VisioProject, "ProjectPro2019Volume", "PerpetualVL2019", true);

    public static readonly OfficePlan ProjectStd2021 =
        new("Project LTSC Standard 2021", OfficeEditionCategory.VisioProject, "ProjectStd2021Volume", "PerpetualVL2021", true);
}

public static class OfficePlanCatalog
{
    public static readonly IReadOnlyList<OfficePlan> All =
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
        OfficePlan.VisioPro2024,
        OfficePlan.VisioPro2021,
        OfficePlan.VisioPro2019,
        OfficePlan.VisioStd2021,
        OfficePlan.ProjectPro2024,
        OfficePlan.ProjectPro2021,
        OfficePlan.ProjectPro2019,
        OfficePlan.ProjectStd2021,
    ];

    public static IEnumerable<OfficePlan> ByCategory(OfficeEditionCategory category) =>
        All.Where(p => p.Category == category);

    /// <summary>Resolve um plano do catálogo a partir do ProductId cru lido do registro (ProductReleaseIds).</summary>
    public static OfficePlan? ByProductId(string productId) =>
        All.FirstOrDefault(p => string.Equals(p.ProductId, productId, StringComparison.OrdinalIgnoreCase));
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

    /// <summary>Excludes menos comuns, agrupados nas opções avançadas em vez da grade principal.</summary>
    public static readonly IReadOnlyList<(string Id, string DisplayName)> AdvancedApps =
    [
        ("Groove", "OneDrive for Business (legado)"),
        ("Lync", "Skype for Business"),
        ("Bing", "Suplementos Bing"),
    ];
}

/// <summary>
/// Nomes de canal de atualização válidos para o atributo Channel do ODT em produtos
/// de assinatura Microsoft 365 (learn.microsoft.com/microsoft-365-apps/deploy/overview-update-channels).
/// Produtos de licença perpétua/volume não usam esta lista — eles têm um único
/// canal PerpetualVLxxxx fixo, definido no próprio <see cref="OfficePlan"/>.
/// </summary>
public static class OfficeChannelCatalog
{
    public static readonly IReadOnlyList<(string Id, string DisplayName)> SubscriptionChannels =
    [
        ("Current", "Canal Atual"),
        ("MonthlyEnterprise", "Enterprise Mensal"),
        ("SemiAnnual", "Semestral (Corrente)"),
        ("SemiAnnualPreview", "Semestral (Prévia)"),
        ("Beta", "Beta / Insiders"),
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
    /// Sobrescreve o Channel padrão do plano (só faz sentido para produtos de
    /// assinatura Microsoft 365 — Current, MonthlyEnterprise, SemiAnnual,
    /// SemiAnnualPreview, Beta; produtos de licença perpétua/volume usam sempre o
    /// canal PerpetualVLxxxx fixo do próprio plano).
    /// </summary>
    string? ChannelOverride = null,
    /// <summary>Gera o elemento &lt;Updates Enabled="TRUE|FALSE"/&gt; do ODT, controlando a política de atualização automática do Office nesta máquina.</summary>
    bool AutoUpdatesEnabled = true);

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
    bool CleanStoreEdition = true);
