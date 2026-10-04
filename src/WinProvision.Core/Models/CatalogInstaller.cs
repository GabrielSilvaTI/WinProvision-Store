using System.Text.Json.Serialization;

namespace WinProvision.Core.Models;

/// <summary>Opção de instalação extraída do manifesto WinGet e embutida no detalhe do catálogo V2.</summary>
public class CatalogInstaller
{
    [JsonPropertyName("architecture")] public string? Architecture { get; set; }
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("nestedType")] public string? NestedType { get; set; }
    [JsonPropertyName("nestedInstallerFile")] public string? NestedInstallerFile { get; set; }
    [JsonPropertyName("portableCommandAlias")] public string? PortableCommandAlias { get; set; }
    [JsonPropertyName("scope")] public string? Scope { get; set; }
    [JsonPropertyName("locale")] public string? Locale { get; set; }
    [JsonPropertyName("url")] public string Url { get; set; } = string.Empty;
    [JsonPropertyName("sha256")] public string? Sha256 { get; set; }
    [JsonPropertyName("silentArgs")] public string? SilentArgs { get; set; }
    [JsonPropertyName("silentSource")] public string SilentSource { get; set; } = "none";
    [JsonPropertyName("silentSupported")] public bool SilentSupported { get; set; }
    [JsonPropertyName("productCode")] public string? ProductCode { get; set; }
    [JsonPropertyName("successCodes")] public List<int> SuccessCodes { get; set; } = [];

    [JsonIgnore]
    public bool IsZipWithNestedInstaller =>
        string.Equals(Type, "zip", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(NestedInstallerFile);

    [JsonIgnore]
    public bool IsPortable => string.Equals(Type, "portable", StringComparison.OrdinalIgnoreCase)
        || (string.Equals(Type, "zip", StringComparison.OrdinalIgnoreCase)
            && string.Equals(NestedType, "portable", StringComparison.OrdinalIgnoreCase));
}
