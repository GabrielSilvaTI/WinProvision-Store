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
        var product = new XElement("Product", new XAttribute("ID", request.Plan.ProductId),
            new XElement("Language", new XAttribute("ID", request.LanguageId)));

        // Idiomas adicionais: cada um vira seu próprio <Language ID="..."/> dentro do
        // mesmo <Product>, exatamente como o schema do ODT documenta para pacotes de
        // idioma extras instalados junto com o principal.
        if (request.AdditionalLanguageIds is { Count: > 0 })
        {
            foreach (var languageId in request.AdditionalLanguageIds)
            {
                if (string.Equals(languageId, request.LanguageId, StringComparison.OrdinalIgnoreCase))
                    continue;

                product.Add(new XElement("Language", new XAttribute("ID", languageId)));
            }
        }

        foreach (var app in request.ExcludedApps)
        {
            product.Add(new XElement("ExcludeApp", new XAttribute("ID", app)));
        }

        var add = new XElement("Add",
            new XAttribute("OfficeClientEdition", request.Architecture),
            product);

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
