using System.Net;
using System.Net.Http.Headers;

namespace WinProvision.Core.Services;

/// <summary>
/// Resolve o tamanho de um instalador consultando apenas os headers HTTP (HEAD, com
/// fallback para uma requisição Range GET de 1 byte) da URL do instalador — sem baixar
/// o arquivo inteiro.
///
/// Compartilhado entre duas pontas que precisavam exatamente da mesma lógica (antes
/// duplicada em WingetExecutor):
///   - WinProvision.Indexer (Program.cs, passo 6): roda em sync-time, uma vez por
///     pacote, direto contra a(s) InstallerUrl extraída(s) do manifesto winget-pkgs
///     (ver ManifestMapper.GetInstallerUrls) — resultado persistido em
///     AppEntry.InstallerSizeBytes no apps.json.
///   - WingetExecutor.GetPackageInstallerSizeAsync: fallback ao vivo, só para o caso
///     raro de um app que chegou ao cliente sem tamanho pré-calculado pelo Indexer.
/// </summary>
public static class InstallerSizeResolver
{
    private static readonly HttpClient Client = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true
        })
        {
            // 8s por requisição: com até 2 URLs x 2 requisições (HEAD + fallback Range
            // GET) cada, o pior caso fica em ~32s por pacote em vez de passar de 1 minuto.
            Timeout = TimeSpan.FromSeconds(8)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("WinProvision.Store/1.0");
        return client;
    }

    /// <summary>
    /// Tenta obter o Content-Length remoto da URL informada. Retorna null quando o
    /// servidor não expõe o tamanho por HEAD nem por Range (ou quando a requisição
    /// falha/expira) — nunca lança, para que o chamador possa simplesmente tentar a
    /// próxima URL candidata.
    /// </summary>
    public static async Task<long?> TryGetRemoteContentLengthAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var head = new HttpRequestMessage(HttpMethod.Head, url);
            using HttpResponseMessage response = await Client.SendAsync(
                head, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (response.Content.Headers.ContentLength is > 0)
                return response.Content.Headers.ContentLength.Value;

            if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.PartialContent)
            {
                long? rangeLength = ParseContentRangeLength(response.Content.Headers.ContentRange);
                if (rangeLength is > 0)
                    return rangeLength;
            }
        }
        catch (HttpRequestException) { }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) { }

        try
        {
            using var range = new HttpRequestMessage(HttpMethod.Get, url);
            range.Headers.Range = new RangeHeaderValue(0, 0);
            using HttpResponseMessage response = await Client.SendAsync(
                range, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            long? total = ParseContentRangeLength(response.Content.Headers.ContentRange);
            if (total is > 0)
                return total;

            // Alguns servidores ignoram Range e retornam Content-Length do arquivo.
            if (response.StatusCode == HttpStatusCode.OK && response.Content.Headers.ContentLength is > 0)
                return response.Content.Headers.ContentLength.Value;
        }
        catch (HttpRequestException) { }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) { }

        return null;
    }

    private static long? ParseContentRangeLength(ContentRangeHeaderValue? range)
    {
        if (range?.Length is > 0)
            return range.Length.Value;
        return null;
    }
}
