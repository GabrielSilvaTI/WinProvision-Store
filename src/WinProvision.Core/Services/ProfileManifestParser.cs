using System.Linq;
using System.Text.Json;
using WinProvision.Core.Models;

namespace WinProvision.Core.Services;

/// <summary>
/// Interpreta o JSON de um perfil aceitando os dois formatos publicados pelo app como
/// EQUIVALENTES, do ponto de vista de quem importa (/auto, /Provision, "Importar" em
/// Pacotes/Provisionamento, restauração de backup):
///
/// <list type="bullet">
/// <item><description><see cref="ProfileManifest"/> — "perfil único", com <c>apps</c> na raiz.
/// É o que os botões "Exportar" (Pacotes/Provisionamento/Configurações) geram.</description></item>
/// <item><description><see cref="ProfileBackupSet"/> — "conjunto de backup", com <c>tabs</c> na
/// raiz (uma entrada por guia de Pacotes). É o formato salvo automaticamente pelo
/// <see cref="Backup.BackupAutoSyncService"/> (local e no Gist secreto via
/// <see cref="Backup.GitHubBackupService"/>) — sincronizado quase em tempo real a cada
/// instalação/remoção ou ajuste de provisionamento.</description></item>
/// </list>
///
/// Antes desta classe existir, o modo CLI /auto (via <see cref="Profile.ProfileService"/>) e o
/// /Provision (via <see cref="Provisioning.ProvisioningService"/>) só sabiam ler o primeiro
/// formato: apontar qualquer um dos dois pra URL "raw" do Gist de backup automático fazia
/// <c>apps</c> vir sempre vazio na raiz (os apps estavam lá, só que aninhados em
/// <c>tabs[].apps</c>), e o app relatava "nada a fazer" mesmo com apps configurados.
///
/// Quando o JSON é um <see cref="ProfileBackupSet"/>, todas as guias são achatadas numa lista
/// só de apps — Ids duplicados entre guias mantêm apenas a primeira ocorrência, mesma regra já
/// usada por Configurações → Backup → "Exportar perfil completo" (ver
/// SettingsPage.ExportAllButton_Click) — e o provisionamento de nível raiz é reaproveitado do
/// mesmo jeito, sem exigir nenhuma mudança de quem chama <see cref="Parse"/>.
/// </summary>
public static class ProfileManifestParser
{
    /// <summary>
    /// Faz o parse. Retorna null se o JSON não puder ser interpretado como nenhum dos dois
    /// formatos (chamador decide como reportar isso).
    /// </summary>
    /// <param name="json">Conteúdo já lido (arquivo local ou baixado de uma URL).</param>
    /// <param name="fallbackName">
    /// Nome a usar quando o resultado vier de um <see cref="ProfileBackupSet"/> com mais de uma
    /// guia (não há um único nome de perfil nesse caso) — normalmente o nome do arquivo/URL de
    /// origem. Com exatamente uma guia, o nome dela prevalece.
    /// </param>
    public static ProfileManifest? Parse(string json, string? fallbackName = null)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        bool looksLikeBackupSet = root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("tabs", out var tabsElement)
            && tabsElement.ValueKind == JsonValueKind.Array;

        if (!looksLikeBackupSet)
        {
            return JsonSerializer.Deserialize<ProfileManifest>(json, WinProvisionJsonOptions.Default);
        }

        var backupSet = JsonSerializer.Deserialize<ProfileBackupSet>(json, WinProvisionJsonOptions.Default);
        if (backupSet is null)
        {
            return null;
        }

        var mergedApps = backupSet.Tabs
            .SelectMany(tab => tab.Apps)
            .GroupBy(app => app.Id, System.StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();

        return new ProfileManifest
        {
            SchemaVersion = backupSet.SchemaVersion,
            CreatedAt = backupSet.CreatedAt,
            Name = backupSet.Tabs.Count == 1 ? backupSet.Tabs[0].Name : fallbackName,
            Apps = mergedApps,
            Provisioning = backupSet.Provisioning,
        };
    }
}
