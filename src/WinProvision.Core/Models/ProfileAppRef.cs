namespace WinProvision.Core.Models;

/// <summary>
/// Uma entrada dentro de um ProfileManifest. Usa o Package Id do winget como
/// chave estável (mesmo identificador já usado em AppEntry.Id) em vez de nome
/// exibido, pra não depender de texto que pode mudar entre catálogos/idiomas.
/// </summary>
public class ProfileAppRef
{
    public string Id { get; set; } = string.Empty;

    /// <summary>Null = sempre instalar/atualizar para a versão mais recente disponível.</summary>
    public string? PinnedVersion { get; set; }
}
