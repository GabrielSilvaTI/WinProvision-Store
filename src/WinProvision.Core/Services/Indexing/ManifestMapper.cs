using WinProvision.Core.Models;

namespace WinProvision.Core.Services.Indexing;

/// <summary>
/// Converte um RawManifestBundle (dicionários genéricos vindos do YAML) em um
/// AppEntry tipado, pronto para filtro de ruído, classificação regional e scoring.
/// </summary>
public static class ManifestMapper
{
    public static AppEntry ToAppEntry(RawManifestBundle bundle)
    {
        var locale = bundle.LocaleManifest;
        var installer = bundle.InstallerManifest;

        var architectures = installer?
            .GetObjectList("Installers")
            .Select(i => i.GetString("Architecture"))
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Select(a => a!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? [];

        return new AppEntry
        {
            Id = bundle.PackageIdentifier,
            Version = bundle.PackageVersion,
            Name = locale.GetString("PackageName") ?? bundle.PackageIdentifier,
            Publisher = locale.GetString("Publisher") ?? "Desconhecido",
            PublisherUrl = locale.GetString("PublisherUrl") ?? locale.GetString("PublisherSupportUrl"),
            Description = locale.GetString("ShortDescription") ?? locale.GetString("Description"),
            Homepage = locale.GetString("Homepage") ?? locale.GetString("PackageUrl"),
            PackageUrl = locale.GetString("PackageUrl"),
            License = locale.GetString("License"),
            LicenseUrl = locale.GetString("LicenseUrl"),
            Moniker = locale.GetString("Moniker"),
            ReleaseNotesUrl = locale.GetString("ReleaseNotesUrl"),
            Tags = locale.GetStringList("Tags"),
            Architectures = architectures
        };
    }
}
