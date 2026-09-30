using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace WinProvision.Core.Services;

/// <summary>
/// Encaminha logs do /auto para o Worker em ordem. A fila desacopla a rede da instalação,
/// tenta novamente falhas transitórias e é drenada antes do processo terminar.
/// </summary>
public static class CloudLogService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Abre uma fila de envio para uma sessão de provisionamento.</summary>
    public static CloudLogSession StartSession(string sessionId) => new(sessionId);

    public sealed class CloudLogSession : IAsyncDisposable
    {
        private readonly string _sessionId;
        private readonly Channel<LogEntry> _channel = Channel.CreateBounded<LogEntry>(new BoundedChannelOptions(1024)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
        private readonly Task _sender;
        private int _dropped;
        private int _completed;

        internal CloudLogSession(string sessionId)
        {
            _sessionId = sessionId;
            _sender = SendLoopAsync();
        }

        /// <summary>Enfileira uma mensagem sem aguardar rede. Retorna false se a fila estiver cheia.</summary>
        public bool Send(string message, int percent = -1)
        {
            if (Volatile.Read(ref _completed) != 0) return false;
            if (_channel.Writer.TryWrite(new LogEntry(message, Math.Clamp(percent, -1, 100)))) return true;
            Interlocked.Increment(ref _dropped);
            return false;
        }

        /// <summary>Fecha a sessão e aguarda o envio dos logs pendentes.</summary>
        public async Task<int> CompleteAsync(string? finalMessage = null)
        {
            if (Interlocked.Exchange(ref _completed, 1) == 0)
            {
                if (!string.IsNullOrWhiteSpace(finalMessage))
                {
                    if (!_channel.Writer.TryWrite(new LogEntry(finalMessage, 100)))
                        Interlocked.Increment(ref _dropped);
                }
                _channel.Writer.TryComplete();
            }

            await _sender.ConfigureAwait(false);
            return Volatile.Read(ref _dropped);
        }

        public async ValueTask DisposeAsync() => await CompleteAsync().ConfigureAwait(false);

        private async Task SendLoopAsync()
        {
            bool endpointUnavailable = false;
            await foreach (var entry in _channel.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                if (endpointUnavailable)
                {
                    Interlocked.Increment(ref _dropped);
                    continue;
                }

                bool sent = false;
                for (int attempt = 1; attempt <= 3; attempt++)
                {
                    try
                    {
                        string body = JsonSerializer.Serialize(new { message = entry.Message, percent = entry.Percent }, JsonOptions);
                        using var content = new StringContent(body, Encoding.UTF8, "application/json");
                        using var response = await Http.PostAsync(CloudLogSessionId.PushUrl(_sessionId), content).ConfigureAwait(false);
                        if (response.IsSuccessStatusCode)
                        {
                            sent = true;
                            break;
                        }

                        bool transient = response.StatusCode is HttpStatusCode.RequestTimeout
                            or HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500;
                        if (!transient)
                        {
                            endpointUnavailable = true;
                            break;
                        }
                    }
                    catch (HttpRequestException)
                    {
                        // Falhas de conexão podem ser temporárias durante o primeiro logon.
                    }
                    catch (TaskCanceledException)
                    {
                        // Timeout da chamada: tentar novamente com backoff curto.
                    }

                    if (attempt < 3)
                        await Task.Delay(TimeSpan.FromMilliseconds(300 * attempt)).ConfigureAwait(false);
                }

                if (!sent)
                {
                    Interlocked.Increment(ref _dropped);
                    endpointUnavailable = true;
                }
            }
        }
    }

    private sealed record LogEntry(string Message, int Percent);
}
