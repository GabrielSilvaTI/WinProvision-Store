using System.Text.Json;

namespace WinProvision.Core.Services;

/// <summary>
/// Opções de serialização compartilhadas por ProfileService e ProvisioningService — desde
/// que ProvisioningManifest passou a viajar dentro do próprio ProfileManifest (um único
/// arquivo de configuração pra tudo: apps, Office e provisionamento de sistema), os dois
/// precisam serializar/desserializar exatamente do mesmo jeito, ou um perfil escrito por um
/// lado poderia não bater com o que o outro espera ao ler de volta.
/// </summary>
public static class WinProvisionJsonOptions
{
    public static readonly JsonSerializerOptions Default = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}
