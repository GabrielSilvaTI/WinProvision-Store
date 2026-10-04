using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;

namespace WinProvision.Core.Services;

/// <summary>Envia apenas eventos agregáveis de instalação concluída para a curadoria da Store.</summary>
public sealed class InstallMetricsService
{
    private const string Endpoint = "https://winprovision-store-metrics.gabriel-silva20090.workers.dev/v1/install";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public void ReportSuccessfulInstall(string packageId, string source)
    {
        string normalizedSource = source.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(packageId) || normalizedSource is not ("winget" or "msstore"))
            return;

        _ = SendAsync(packageId.Trim(), normalizedSource);
    }

    private static async Task SendAsync(string packageId, string source)
    {
        try
        {
            byte[] body = JsonSerializer.SerializeToUtf8Bytes(new InstallMetricEvent(
                packageId,
                source,
                Guid.NewGuid().ToString("D")), JsonOptions);
            using var content = new ByteArrayContent(body);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            using HttpResponseMessage response = await Http.PostAsync(Endpoint, content).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            Debug.WriteLine($"[InstallMetrics] Evento de instalação não enviado: {ex.Message}");
        }
    }

    private sealed record InstallMetricEvent(string PackageId, string Source, string EventId);
}
