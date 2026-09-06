using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinProvision.Core.Services;

/// <summary>
/// Opções de serialização/desserialização de JSON usadas por TODOS os serviços
/// do app (profiles, manifestos de provisionamento, backup local/Gist, configuração
/// da conta GitHub etc.). Ter um ponto centralizado garante:
///
/// <list type="bullet">
/// <item><description>O app SEMPRE lê o que escreveu — nenhum serviço usa opções
///   diferentes sem sabermos (era uma das causas do Bootstrap puxar arquivos
///   antigos: inconsistências entre ler com uma policy e gravar com outra).</description></item>
/// <item><description>Round-trip idempotente: serializar → desserializar → serializar
///   de novo produz a mesma estrutura (campos em camelCase, indentações consistentes,
///   enums como string, datas em ISO-8601).</description></item>
/// <item><description>Leitura tolerante (allow trailing commas, ignora comments,
///   case-insensitive match nos nomes de propriedade) — evita rejeitar perfis
///   salvos por versões ligeiramente mais antigas do app, ou editados manualmente.</description></item>
/// <item><description>Enums serializados como <b>string</b> em vez de int: no .json
///   salvo por app v1 aparecia <c>"theme": 2</c>; se alguém reordenar os membros
///   de <c>SystemThemeMode</c>, tudo fica errado. Como string (<c>"theme": "Escuro"</c>)
///   esse risco some. Desserialização ainda aceita tanto o int antigo (compatibilidade
///   retroativa) quanto a string nova.</description></item>
/// <item><description><c>DefaultIgnoreCondition = WhenWritingNull</c>: deixa o JSON
///   bem mais limpo e compacto (campos "não alterar" = null desaparecem) sem perder
///   a semântica — o leitor continua interpretando ausência/null como "não mexer".</description></item>
/// </list>
///
/// Todas as classes que serializam/desserializam QUALQUER arquivo .json do app —
/// perfis/manifestos (ProfileService, ProvisioningService, ProfileManifestParser),
/// backup (GitHubBackupService, LocalBackupService, BackupModels), presets/config
/// (CliPresetsService, SettingsPage, ProvisioningPage), caches internos
/// (IconService, PackageMetricsService, IgnoredUpdatesService, StoreService) e o
/// catálogo gerado pelo pipeline externo (WinProvision.Indexer/CatalogExporter) —
/// devem usar as duas instâncias abaixo, nunca criar o seu próprio
/// JsonSerializerOptions. Isso vale mesmo quando o consumidor e o gerador do
/// arquivo estão em projetos diferentes (ex.: Indexer escreve o apps.json que a
/// Store lê): os dois lados precisam concordar na mesma política de leitura.
/// </summary>
public static class WinProvisionJsonOptions
{
    /// <summary>Opção usada para GRAVAR arquivos/perfis/json de preview na UI.</summary>
    public static readonly JsonSerializerOptions Default = Build(indented: true);

    /// <summary>Variante compacta (sem indentação) — útil para payloads de API pequenos
    /// (ex.: conta GitHub interna). Não substitui a Default em disco; é só um atalho.</summary>
    public static readonly JsonSerializerOptions Compact = Build(indented: false);

    private static JsonSerializerOptions Build(bool indented)
    {
        var opts = new JsonSerializerOptions
        {
            WriteIndented = indented,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            NumberHandling = JsonNumberHandling.AllowReadingFromString,
        };

        // 1. Enum como string, mas aceitando tanto a string quanto o número antigo.
        //    Ordem importa: o JsonStringEnumConverter é colocado por último, então
        //    os conversores built-in pegam primeiro no número se for numérico.
        opts.Converters.Add(new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: true));

        return opts;
    }
}
