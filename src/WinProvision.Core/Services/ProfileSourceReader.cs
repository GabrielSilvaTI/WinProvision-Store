using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace WinProvision.Core.Services;

/// <summary>
/// Lê o conteúdo de um perfil .json a partir de um "caminho-ou-URL": um caminho local
/// (File.ReadAllTextAsync) ou um link http(s) direto pro conteúdo — ex.: a URL "raw" de um
/// Gist (<c>https://gist.githubusercontent.com/usuario/id/raw/perfil.json</c>). Usado por
/// ProfileService/ProvisioningService e pelos modos CLI (/auto e /Provision), pra qualquer
/// um deles poder receber um link em vez de um arquivo em disco sem duplicar essa lógica.
///
/// Histórico de bug resolvido aqui (09/2026): a versão anterior usava um HttpClient
/// "nu" sem nenhum header anti-cache. Como CDNs (Fastly/Cloudflare) e o WinHTTP cache
/// do Windows costumam servir o conteúdo de /raw do Gist por alguns minutos de TTL
/// implícito, uma execução via URL muitas vezes puxava um JSON antigo mesmo depois de
/// "Sincronizar agora" ter atualizado o Gist — parecia "falta de sincronia", mas era
/// só cache de rede. A correção combina:
///
/// <list type="bullet">
/// <item><description>Cabeçalhos <c>Cache-Control: no-cache, no-store</c>, <c>Pragma: no-cache</c>
///   e <c>If-None-Match</c> desligado para a requisição sempre chegar à origem.</description></item>
/// <item><description>Adição automática de <c>?ts=&lt;unix-millis&gt;</c> como cache-bust quando
///   a URL é do domínio <c>gist.githubusercontent.com</c> (onde o problema mais aparece).</description></item>
/// <item><description>3 tentativas com backoff exponencial (1s / 3s / 7s) para falhas de rede
///   transitórias — o First Logon Commands roda exatamente no momento em que a placa de
///   rede ainda está subindo.</description></item>
/// <item><description>Timeout generoso de 45s (antes 30s), necessário em conexões lentas
///   de primeiro logon.</description></item>
/// </list>
/// </summary>
public static class ProfileSourceReader
{
    private const int DownloadAttempts = 3;
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromSeconds(45);

    public static bool IsHttpUrl(string source) =>
        Uri.TryCreate(source, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    /// <summary>
    /// Acrescenta um cache-buster "?ts=&lt;unix-millis&gt;" (ou "&amp;ts=..." se a URL já tiver
    /// query string) à URL informada. Usado por <see cref="ReadTextAsync"/> antes de baixar o
    /// perfil. Retorna a URL sem alteração se não for uma URL absoluta válida.
    /// </summary>
    public static string AppendCacheBustQuery(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return url;

        string sep = string.IsNullOrEmpty(uri.Query) ? "?" : "&";
        long ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return $"{url}{sep}ts={ts}";
    }

    /// <summary>
    /// Baixa (se for URL) ou lê do disco (caso contrário) o texto do perfil. Não faz cache —
    /// cada chamada busca de novo, o que é o comportamento certo pra um link de Gist que pode
    /// ter sido atualizado entre uma execução e outra.
    /// </summary>
    public static async Task<string> ReadTextAsync(string source, CancellationToken ct = default)
    {
        if (!IsHttpUrl(source))
        {
            return await File.ReadAllTextAsync(source, ct);
        }

        string url = AppendCacheBustQuery(source);

        Exception? last = null;
        for (int attempt = 1; attempt <= DownloadAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var http = new HttpClient { Timeout = DownloadTimeout };
                http.DefaultRequestHeaders.CacheControl = new CacheControlHeaderValue
                {
                    NoCache = true,
                    NoStore = true,
                    MustRevalidate = true,
                };
                http.DefaultRequestHeaders.Pragma.Clear();
                http.DefaultRequestHeaders.Pragma.Add(new NameValueHeaderValue("no-cache"));
                http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("WinProvision-Store", "1.0"));

                using var response = await http.GetAsync(url, HttpCompletionOption.ResponseContentRead, ct);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStringAsync(ct);
            }
            catch (Exception ex)
            {
                last = ex;
                if (attempt < DownloadAttempts)
                {
                    int delaySec = attempt switch { 1 => 1, 2 => 3, _ => 7 };
                    await Task.Delay(TimeSpan.FromSeconds(delaySec), ct);
                }
            }
        }

        throw new InvalidOperationException(
            $"Não foi possível baixar o perfil após {DownloadAttempts} tentativas: {last?.Message}", last);
    }
}
