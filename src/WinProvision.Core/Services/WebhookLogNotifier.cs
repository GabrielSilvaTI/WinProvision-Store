using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WinProvision.Core.Services;

/// <summary>
/// Envia o log completo de uma execução CLI (/auto ou /Provision) para uma URL de webhook
/// ao final do processo — com sucesso ou em caso de erro — pra acompanhar provisionamentos
/// automatizados sem precisar acessar a máquina depois (ex.: um canal do Discord recebendo
/// o resultado de cada rollout).
///
/// Reconhece automaticamente uma URL de "Discord Webhook" (Integrações &gt; Webhooks, no
/// canal) e nesse caso anexa o log inteiro como arquivo de texto, já que o conteúdo de uma
/// mensagem do Discord é limitado. Qualquer outra URL http(s) recebe um POST JSON simples
/// com o log completo — dá pra apontar para qualquer endpoint próprio (ex.: um Webhook do
/// Teams, um Logic App, um endpoint HTTP caseiro).
///
/// Melhor-esforço, mesma filosofia do <see cref="CliFileLogger"/>: nunca lança — se o envio
/// falhar (sem internet, URL inválida, etc.), só registra um aviso no próprio log. A
/// instalação/provisionamento já terminou nesse ponto e não pode ser afetada pelo envio.
/// </summary>
public static class WebhookLogNotifier
{
    // Margem de segurança sob o limite de 4096 caracteres de uma descrição de embed do
    // Discord — o log completo (sem corte) sempre vai também como arquivo anexo.
    private const int DiscordEmbedDescriptionLimit = 3500;

    /// <param name="webhookUrl">URL http(s) do webhook (ex.: URL de "Discord Webhook").</param>
    /// <param name="title">Rótulo curto da execução (ex.: nome do perfil aplicado).</param>
    /// <param name="success">Se a execução terminou sem falhas (define cor/selo da notificação).</param>
    /// <param name="fullLog">Conteúdo completo do log acumulado durante a execução.</param>
    /// <param name="log">Sink de log — recebe só o resultado do próprio envio (sucesso/falha do webhook em si).</param>
    public static async Task SendAsync(
        string webhookUrl,
        string title,
        bool success,
        string fullLog,
        Action<string> log,
        CancellationToken ct = default)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

            bool isDiscord = webhookUrl.Contains("discord.com/api/webhooks", StringComparison.OrdinalIgnoreCase)
                || webhookUrl.Contains("discordapp.com/api/webhooks", StringComparison.OrdinalIgnoreCase);

            using HttpResponseMessage response = isDiscord
                ? await SendDiscordAsync(http, webhookUrl, title, success, fullLog, ct)
                : await SendGenericAsync(http, webhookUrl, title, success, fullLog, ct);

            if (response.IsSuccessStatusCode)
            {
                log("[WinProvision] Log completo enviado ao webhook com sucesso.");
            }
            else
            {
                log($"[WinProvision] AVISO: o webhook respondeu {(int)response.StatusCode} ({response.ReasonPhrase}) ao receber o log.");
            }
        }
        catch (Exception ex)
        {
            log($"[WinProvision] AVISO: não deu para enviar o log ao webhook: {ex.Message}");
        }
    }

    private static async Task<HttpResponseMessage> SendDiscordAsync(
        HttpClient http, string webhookUrl, string title, bool success, string fullLog, CancellationToken ct)
    {
        string status = success ? "✅ Concluído" : "⚠️ Concluído com falhas";
        string preview = fullLog.Length > DiscordEmbedDescriptionLimit
            ? "…(início omitido, veja o arquivo anexo)…\n" + fullLog[^DiscordEmbedDescriptionLimit..]
            : fullLog;

        var payload = new
        {
            embeds = new[]
            {
                new
                {
                    title = $"WinProvision — {title}",
                    description = $"**{status}**\n```\n{preview}\n```",
                    color = success ? 0x2ECC71 : 0xE67E22,
                },
            },
        };

        using var form = new MultipartFormDataContent();

        var payloadContent = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        form.Add(payloadContent, "payload_json");

        var fileContent = new ByteArrayContent(Encoding.UTF8.GetBytes(fullLog));
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        form.Add(fileContent, "files[0]", "winprovision.log");

        return await http.PostAsync(webhookUrl, form, ct);
    }

    private static async Task<HttpResponseMessage> SendGenericAsync(
        HttpClient http, string webhookUrl, string title, bool success, string fullLog, CancellationToken ct)
    {
        var payload = new
        {
            title,
            success,
            timestamp = DateTime.Now.ToString("O"),
            log = fullLog,
        };

        using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        return await http.PostAsync(webhookUrl, content, ct);
    }
}
