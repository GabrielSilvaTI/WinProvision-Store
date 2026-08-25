namespace WinProvision.Core.Models;

/// <summary>
/// Representa um perfil de seleção do usuário (loadout): quais apps devem estar
/// instalados numa máquina, independente do catálogo completo (apps.json remoto).
/// SchemaVersion existe para que arquivos gerados por versões antigas do app
/// (ou por outra IA/dev mexendo no projeto em paralelo) não quebrem o parser
/// silenciosamente — trate ausência do campo como schema legado (v0).
/// </summary>
public class ProfileManifest
{
    public int SchemaVersion { get; set; } = 1;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Nome opcional do perfil (ex.: "Setup Dev", "Máquina Notebook").</summary>
    public string? Name { get; set; }

    public List<ProfileAppRef> Apps { get; set; } = new();
}
