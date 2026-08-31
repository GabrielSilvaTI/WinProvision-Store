using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace WinProvision.Core.Services;

/// <summary>
/// Lê o conteúdo de um perfil .json a partir de um "caminho-ou-URL": um caminho local
/// (File.ReadAllTextAsync) ou um link http(s) direto pro conteúdo — ex.: a URL "raw" de um
/// Gist (<c>https://gist.githubusercontent.com/usuario/id/raw/perfil.json</c>). Usado por
/// ProfileService/ProvisioningService e pelos modos CLI (/auto e /Provision), pra qualquer
/// um deles poder receber um link em vez de um arquivo em disco sem duplicar essa lógica.
/// </summary>
public static class ProfileSourceReader
{
    public static bool IsHttpUrl(string source) =>
        Uri.TryCreate(source, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

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

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        return await http.GetStringAsync(source, ct);
    }
}
