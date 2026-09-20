using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace WinProvision.Core.Services;

/// <summary>
/// Envia mensagens de log em tempo real para o Worker Cloudflare de acompanhamento
/// de execuçao do /auto (/cloudlog). Toda falha de rede é silenciosa — nunca
/// interrompe o pipeline de provisionamento.
/// </summary>
public static class CloudLogService
{
    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(5),
    };

    /// <summary>
    /// Envia uma linha de log e o percentual atual de progresso para o Worker.
    /// Fire-and-forget: retorna imediatamente; erros são descartados sem relançar.
    /// </summary>
    /// <param name="sessionId">Session ID gerado por <see cref="CloudLogSessionId.Generate"/>.</param>
    /// <param name="message">Linha de texto de log.</param>
    /// <param name="percent">Progresso 0-100. Use -1 quando indeterminado.</param>
    public static void Send(string sessionId, string message, int percent = -1)
    {
        _ = SendCoreAsync(sessionId, message, percent);
    }

    private static async Task SendCoreAsync(string sessionId, string message, int percent)
    {
        try
        {
            string url     = CloudLogSessionId.PushUrl(sessionId);
            string body    = JsonSerializer.Serialize(new { message, percent });
            var    content = new StringContent(body, Encoding.UTF8, "application/json");
            await _http.PostAsync(url, content).ConfigureAwait(false);
        }
        catch
        {
            // Silencioso — falha de rede nunca deve interromper o provisionamento.
        }
    }
}
