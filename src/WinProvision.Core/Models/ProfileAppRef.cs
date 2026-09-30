using WinProvision.Core.Models.Office;

namespace WinProvision.Core.Models;

/// <summary>
/// Uma entrada dentro de um ProfileManifest. Usa o Package Id do winget como
/// chave estável (mesmo identificador já usado em AppEntry.Id) em vez de nome
/// exibido, pra não depender de texto que pode mudar entre catálogos/idiomas.
///
/// Id e Source identificam pacote e repositório de instalação; assim o /auto não
/// precisa baixar o catálogo inteiro antes de começar. Perfis antigos sem Source
/// continuam compatíveis: sem origem, o /auto usa a origem winget, exceto IDs de produto
/// da Microsoft Store, reconhecidos pelo formato de nove caracteres. Planos de Office não existem nesse
/// catálogo remoto, então quando <see cref="OfficeOptions"/> está preenchido,
/// os campos de exibição abaixo viajam junto no próprio .json — o perfil fica
/// autocontido pros dois casos (apps + Office) no mesmo arquivo.
/// </summary>
public class ProfileAppRef
{
    public string Id { get; set; } = string.Empty;

    /// <summary>Origem do pacote (por exemplo, winget ou msstore). Nulo em perfis antigos.</summary>
    public string? Source { get; set; }

    /// <summary>Null = sempre instalar/atualizar para a versão mais recente disponível.</summary>
    public string? PinnedVersion { get; set; }

    /// <summary>Presente só quando este item é um plano de Office (ver AppEntry.Office).</summary>
    public OfficeInstallOptions? OfficeOptions { get; set; }

    // Metadados de exibição opcionais, preenchidos para tornar o perfil útil offline.
    public string? Name { get; set; }
    public string? Publisher { get; set; }
    public string? IconUrl { get; set; }
    public string? Description { get; set; }
}
