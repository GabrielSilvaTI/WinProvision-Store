using WinProvision.Core.Models.Office;

namespace WinProvision.Core.Models;

/// <summary>
/// Uma entrada dentro de um ProfileManifest. Usa o Package Id do winget como
/// chave estável (mesmo identificador já usado em AppEntry.Id) em vez de nome
/// exibido, pra não depender de texto que pode mudar entre catálogos/idiomas.
///
/// Para apps winget comuns, só Id/PinnedVersion importam — o resto do AppEntry
/// (nome, ícone, publisher...) é resolvido de volta contra o catálogo remoto
/// vivo (StoreService) na importação. Planos de Office não existem nesse
/// catálogo remoto, então quando <see cref="OfficeOptions"/> está preenchido,
/// os campos de exibição abaixo viajam junto no próprio .json — o perfil fica
/// autocontido pros dois casos (apps + Office) no mesmo arquivo.
/// </summary>
public class ProfileAppRef
{
    public string Id { get; set; } = string.Empty;

    /// <summary>Null = sempre instalar/atualizar para a versão mais recente disponível.</summary>
    public string? PinnedVersion { get; set; }

    /// <summary>Presente só quando este item é um plano de Office (ver AppEntry.Office).</summary>
    public OfficeInstallOptions? OfficeOptions { get; set; }

    // Campos de exibição, preenchidos só junto com OfficeOptions (ver acima) —
    // apps winget comuns continuam null aqui e são resolvidos via StoreService.
    public string? Name { get; set; }
    public string? Publisher { get; set; }
    public string? IconUrl { get; set; }
    public string? Description { get; set; }
}
