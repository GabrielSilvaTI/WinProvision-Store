using System.Text.Json;
using WinProvision.Core.Models;

namespace WinProvision.Core.Services.Profile;

/// <summary>
/// Resultado da reconciliação entre um ProfileManifest (o que o usuário quer)
/// e o estado atual da máquina (o que já está instalado).
/// </summary>
public record ReconcileResult(
    List<ProfileAppRef> ToInstall,
    List<ProfileAppRef> AlreadySatisfied
);

/// <summary>
/// Export/Import de perfis de seleção (loadouts) + reconciliação idempotente
/// contra o estado instalado. Não fala diretamente com o winget — recebe a
/// lista de Ids já instalados (via WingetExecutor / detecção estruturada, a
/// mesma fonte usada pela aba "Atualizações") e devolve só o diff a aplicar.
///
/// TODO (quando os detalhes de implementação forem definidos):
/// - Trocar IEnumerable&lt;string&gt; installedAppIds pela assinatura real que o
///   WingetExecutor expuser (ex.: IEnumerable&lt;InstalledAppInfo&gt;) se ele
///   carregar mais que só o Id (versão instalada, por ex., pra respeitar
///   PinnedVersion corretamente).
/// - Decidir se Import vai só popular a aba "Instalados" (marcar seleção) ou
///   também disparar instalação direta via WingetExecutor (caso de uso de
///   provisionamento em máquina nova).
/// </summary>
public class ProfileService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>Constrói um ProfileManifest a partir da seleção atual do usuário na Store.</summary>
    public ProfileManifest BuildFromSelection(IEnumerable<AppEntry> selectedApps, string? profileName = null)
    {
        return new ProfileManifest
        {
            SchemaVersion = 1,
            CreatedAt = DateTime.UtcNow,
            Name = profileName,
            Apps = selectedApps
                .Select(app => new ProfileAppRef { Id = app.Id })
                .ToList()
        };
    }

    /// <summary>Serializa e grava o perfil em disco (ex.: via SaveFileDialog na UI).</summary>
    public async Task ExportAsync(ProfileManifest profile, string filePath, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(profile, JsonOptions);
        await File.WriteAllTextAsync(filePath, json, ct);
    }

    /// <summary>Lê e desserializa um perfil de um arquivo .json (ex.: via OpenFileDialog na UI).</summary>
    public async Task<ProfileManifest> ImportAsync(string filePath, CancellationToken ct = default)
    {
        var json = await File.ReadAllTextAsync(filePath, ct);
        var profile = JsonSerializer.Deserialize<ProfileManifest>(json, JsonOptions);

        if (profile is null)
            throw new InvalidDataException($"Não foi possível interpretar o perfil em '{filePath}'.");

        // Schema ausente/zero => trata como legado; hoje só existe v1, mas o
        // campo já fica pronto pra migração futura sem quebrar perfis antigos.
        if (profile.SchemaVersion <= 0)
            profile.SchemaVersion = 1;

        return profile;
    }

    /// <summary>
    /// Reconcilia o perfil contra o que já está instalado. Idempotente por
    /// natureza: rodar o mesmo perfil várias vezes sempre produz o mesmo
    /// ToInstall (vazio se nada mudou), sem reinstalar o que já está lá.
    /// </summary>
    public ReconcileResult Reconcile(ProfileManifest profile, IEnumerable<string> installedAppIds)
    {
        var installedSet = new HashSet<string>(installedAppIds, StringComparer.OrdinalIgnoreCase);

        var toInstall = new List<ProfileAppRef>();
        var alreadySatisfied = new List<ProfileAppRef>();

        foreach (var appRef in profile.Apps)
        {
            if (installedSet.Contains(appRef.Id))
                alreadySatisfied.Add(appRef);
            else
                toInstall.Add(appRef);
        }

        return new ReconcileResult(toInstall, alreadySatisfied);
    }
}