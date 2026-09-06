using System.Text.Json;
using WinProvision.Core.Models;
using WinProvision.Core.Models.Provisioning;
using WinProvision.Core.Services;

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
/// Planos de Office (ProfileAppRef.OfficeOptions preenchido) não passam por
/// essa reconciliação — winget nunca "vê" uma instalação do Office (ela roda
/// via ODT, fora do winget), então eles são tratados à parte na importação
/// (ver PackagesPage.ImportProfileButton_Click): sempre recriados diretamente
/// a partir do próprio .json, sem depender do catálogo remoto de apps.
///
/// TODO (quando os detalhes de implementação forem definidos):
/// - Trocar IEnumerable&lt;string&gt; installedAppIds pela assinatura real que o
///   WingetExecutor expuser (ex.: IEnumerable&lt;InstalledAppInfo&gt;) se ele
///   carregar mais que só o Id (versão instalada, por ex., pra respeitar
///   PinnedVersion corretamente).
/// </summary>
public class ProfileService
{
    /// <summary>
    /// Constrói um ProfileManifest a partir da seleção atual do usuário na Store.
    /// <paramref name="provisioning"/> é opcional: quando informado (normalmente
    /// <see cref="Provisioning.ProvisioningService.Current"/>), o perfil resultante é o
    /// ".json completo" — apps/Office + provisionamento juntos no mesmo arquivo, prontos
    /// tanto para /auto quanto para o backup/Gist cobrir os dois de uma vez.
    /// </summary>
    public ProfileManifest BuildFromSelection(
        IEnumerable<AppEntry> selectedApps,
        string? profileName = null,
        ProvisioningManifest? provisioning = null)
    {
        return new ProfileManifest
        {
            SchemaVersion = 1,
            CreatedAt = DateTime.UtcNow,
            Name = profileName,
            Provisioning = provisioning,
            Apps = selectedApps
                .Select(app => new ProfileAppRef
                {
                    Id = app.Id,
                    // Planos de Office não existem no catálogo remoto de apps, então
                    // levam os campos de exibição + OfficeOptions junto no próprio
                    // .json — apps winget comuns (Office == null) continuam só com o
                    // Id, resolvidos de volta contra o catálogo na importação.
                    OfficeOptions = app.Office,
                    Name = app.Office is not null ? app.Name : null,
                    Publisher = app.Office is not null ? app.Publisher : null,
                    IconUrl = app.Office is not null ? app.IconUrl : null,
                    Description = app.Office is not null ? app.Description : null,
                })
                .ToList()
        };
    }

    /// <summary>Serializa e grava o perfil em disco (ex.: via SaveFileDialog na UI).</summary>
    public async Task ExportAsync(ProfileManifest profile, string filePath, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(profile, WinProvisionJsonOptions.Default);
        await File.WriteAllTextAsync(filePath, json, ct);
    }

    /// <summary>
    /// Lê e desserializa um perfil de um caminho local ou de uma URL http(s) (ex.: link "raw" de
    /// Gist). Aceita tanto um perfil único (ProfileManifest) quanto um conjunto de backup
    /// completo (ProfileBackupSet, o mesmo que o backup automático/Gist secreto mantém
    /// atualizado) — ver <see cref="ProfileManifestParser"/> para os detalhes de como os dois
    /// são tratados como equivalentes.
    /// </summary>
    public async Task<ProfileManifest> ImportAsync(string filePath, CancellationToken ct = default)
    {
        var json = await ProfileSourceReader.ReadTextAsync(filePath, ct);
        var profile = ProfileManifestParser.Parse(json, Path.GetFileNameWithoutExtension(filePath));

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