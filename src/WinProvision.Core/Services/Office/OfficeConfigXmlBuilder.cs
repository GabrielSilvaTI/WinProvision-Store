using System;
using System.Xml.Linq;
using WinProvision.Core.Models.Office;

namespace WinProvision.Core.Services.Office;

/// <summary>
/// Monta o configuration.xml exigido pelo ODT (setup.exe /configure), seguindo o
/// schema oficial: Configuration > Add > Product > Language / ExcludeApp, mais
/// Display e Updates. Nenhum elemento aqui é inventado.
/// </summary>
public static class OfficeConfigXmlBuilder
{
    public static XDocument Build(OfficeInstallRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Plan);
        var products = new[] { request.Plan }.Concat(request.AdditionalProducts ?? []).ToArray();
        var validationError = ValidateRequest(request, products);
        if (validationError is not null)
            throw new ArgumentException(validationError, nameof(request));

        var product = BuildProduct(request.Plan, request);

        var add = new XElement("Add",
            new XAttribute("OfficeClientEdition", request.Architecture),
            products.Select(p => ReferenceEquals(p, request.Plan) || p == request.Plan
                ? product
                : BuildProduct(p, request)));

        if (request.ChannelOverride is { Length: > 0 } channelOverride)
        {
            add.Add(new XAttribute("Channel", channelOverride));
        }
        else if (request.Plan.Channel is { Length: > 0 } channel)
        {
            add.Add(new XAttribute("Channel", channel));
        }

        var display = BuildDisplayElement(request.DisplayLevel, request.AcceptEula);
        var updates = new XElement("Updates", new XAttribute("Enabled", request.AutoUpdatesEnabled ? "TRUE" : "FALSE"));

        var configuration = new XElement("Configuration", add, display, updates);

        return new XDocument(new XDeclaration("1.0", "utf-8", null), configuration);
    }

    /// <summary>Valida os produtos antes de gerar ou executar a configuração.</summary>
    public static string? ValidateRequest(OfficeInstallRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Plan);
        return ValidateRequest(request, new[] { request.Plan }.Concat(request.AdditionalProducts ?? []).ToArray());
    }

    private static string? ValidateRequest(OfficeInstallRequest request, IReadOnlyList<OfficePlan> products)
    {
        if (request.Architecture is not (32 or 64)) return "A arquitetura deve ser 32 ou 64 bits.";
        if (!IsSafeToken(request.LanguageId)) return "O idioma principal é inválido.";
        if (products.Count == 0 || products.Any(p => p is null || !IsSafeToken(p.ProductId))) return "Há um Product ID inválido.";
        if (products.Select(p => p.ProductId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != products.Count)
            return "O mesmo produto não pode ser adicionado mais de uma vez.";
        if (products.Skip(1).Any(p => p.Category != OfficeEditionCategory.VisioProject))
            return "Produtos adicionais precisam ser Visio ou Project.";
        if (products.Count > 3)
            return "Selecione no máximo um plano principal, um Visio e um Project.";
        if (products.Skip(1).Any(p => p.Category == request.Plan.Category && p.ProductId == request.Plan.ProductId))
            return "Selecione produtos adicionais diferentes do plano principal.";

        var effectiveChannel = request.ChannelOverride ?? request.Plan.Channel;
        if (effectiveChannel is { Length: > 0 } && !IsSafeToken(effectiveChannel))
            return "O canal de atualização é inválido.";
        if (request.ChannelOverride is { Length: > 0 } && !request.Plan.SupportsSelectableChannel)
            return "O canal deste plano é fixo e não pode ser substituído.";
        if (request.ChannelOverride is { Length: > 0 } channelOverride &&
            !OfficeChannelCatalog.SubscriptionChannels.Any(c => string.Equals(c.Id, channelOverride, StringComparison.OrdinalIgnoreCase)))
            return "O canal informado não é compatível com o ODT.";
        bool mainUsesSubscriptionChannel = OfficeChannelCatalog.SubscriptionChannels.Any(c =>
            string.Equals(c.Id, effectiveChannel, StringComparison.OrdinalIgnoreCase));
        if (products.Skip(1).Any(p =>
                !string.Equals(p.Channel, effectiveChannel, StringComparison.OrdinalIgnoreCase) &&
                !(mainUsesSubscriptionChannel && p.SupportsSelectableChannel)))
            return "Os produtos selecionados usam canais ODT diferentes. Escolha versões compatíveis para gerar um único XML.";
        if (request.AdditionalLanguageIds?.Any(id => !IsSafeToken(id)) == true)
            return "Há um idioma adicional inválido.";
        if (request.ExcludedApps is null || request.ExcludedApps.Any(id => !IsSafeToken(id)))
            return "Há um aplicativo excluído com identificador inválido.";
        return null;
    }

    private static XElement BuildProduct(OfficePlan plan, OfficeInstallRequest request)
    {
        var product = new XElement("Product", new XAttribute("ID", plan.ProductId),
            new XElement("Language", new XAttribute("ID", request.LanguageId)));
        if (plan == request.Plan && request.AdditionalLanguageIds is { Count: > 0 })
        {
            foreach (var languageId in request.AdditionalLanguageIds.Where(id => !string.Equals(id, request.LanguageId, StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase))
                product.Add(new XElement("Language", new XAttribute("ID", languageId)));
        }
        if (plan == request.Plan)
            foreach (var app in request.ExcludedApps.Distinct(StringComparer.OrdinalIgnoreCase))
                product.Add(new XElement("ExcludeApp", new XAttribute("ID", app)));
        return product;
    }

    private static bool IsSafeToken(string? value) => !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 100 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');

    /// <summary>
    /// Monta um configuration.xml contendo só o elemento &lt;Updates&gt; — permite
    /// ligar/desligar a atualização automática do Office sem reinstalar/reparar nada,
    /// aplicando via setup.exe /configure (mecanismo oficial e documentado do ODT).
    /// </summary>
    public static XDocument BuildUpdatesOnly(bool enabled)
    {
        var updates = new XElement("Updates", new XAttribute("Enabled", enabled ? "TRUE" : "FALSE"));
        var configuration = new XElement("Configuration", updates);
        return new XDocument(new XDeclaration("1.0", "utf-8", null), configuration);
    }

    public static async Task<string> WriteUpdatesOnlyToFolderAsync(bool enabled, string folder, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, "configuration-updates.xml");

        var doc = BuildUpdatesOnly(enabled);
        await using var stream = File.Create(path);
        await doc.SaveAsync(stream, SaveOptions.None, cancellationToken);

        return path;
    }

    /// <summary>
    /// Monta o configuration.xml para uma remoção (setup.exe /configure), usando o
    /// elemento oficial &lt;Remove&gt; do schema do ODT. Com <see cref="OfficeRemoveRequest.RemoveAll"/>
    /// definido, gera &lt;Remove All="TRUE"/&gt;, que remove todos os produtos
    /// Click-to-Run instalados na máquina — não é necessário informar cada SKU.
    /// </summary>
    public static XDocument BuildRemove(OfficeRemoveRequest request)
    {
        XElement remove;

        if (request.RemoveAll)
        {
            remove = new XElement("Remove", new XAttribute("All", "TRUE"));
        }
        else
        {
            remove = new XElement("Remove");
            foreach (var productId in request.ProductIds ?? Array.Empty<string>())
            {
                remove.Add(new XElement("Product", new XAttribute("ID", productId)));
            }
        }

        var display = BuildDisplayElement(request.DisplayLevel, acceptEula: true);
        var configuration = new XElement("Configuration", remove, display);

        // Adiciona RemoveMSI se solicitado - isso remove instalações MSI do Office
        // que não são removidas pelo método padrão do ODT
        if (request.UseRemoveMSI)
        {
            var removeMsi = new XElement("RemoveMSI",
                new XAttribute("All", "TRUE"),
                new XAttribute("IgnoreProduct", "FALSE"));
            configuration.Add(removeMsi);
        }

        return new XDocument(new XDeclaration("1.0", "utf-8", null), configuration);
    }

    private static XElement BuildDisplayElement(OfficeDisplayLevel level, bool acceptEula) =>
        new("Display",
            new XAttribute("Level", level == OfficeDisplayLevel.Silent ? "None" : "Full"),
            new XAttribute("AcceptEULA", acceptEula ? "TRUE" : "FALSE"));

    public static async Task<string> WriteToFolderAsync(OfficeInstallRequest request, string folder, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, "configuration.xml");

        var doc = Build(request);
        await using var stream = File.Create(path);
        await doc.SaveAsync(stream, SaveOptions.None, cancellationToken);

        return path;
    }

    public static async Task<string> WriteRemoveToFolderAsync(OfficeRemoveRequest request, string folder, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, "configuration-remove.xml");

        var doc = BuildRemove(request);
        await using var stream = File.Create(path);
        await doc.SaveAsync(stream, SaveOptions.None, cancellationToken);

        return path;
    }
}
