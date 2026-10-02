using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Collections.Generic;

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
        private int _batchSupport;

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
            await foreach (var first in _channel.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                var batch = new List<LogEntry>(25) { first };
                while (batch.Count < 25 && _channel.Reader.TryRead(out var next))
                    batch.Add(next);

                int sentCount;
                if (Volatile.Read(ref _batchSupport) < 0)
                {
                    sentCount = await TrySendLegacyAsync(batch).ConfigureAwait(false);
                }
                else
                {
                    var result = await TrySendBatchAsync(batch).ConfigureAwait(false);
                    if (result.Success)
                    {
                        Volatile.Write(ref _batchSupport, 1);
                        continue;
                    }

                    if (result.LegacyWorker)
                    {
                        Volatile.Write(ref _batchSupport, -1);
                        sentCount = await TrySendLegacyAsync(batch).ConfigureAwait(false);
                    }
                    else
                    {
                        sentCount = 0;
                    }
                }

                if (sentCount < batch.Count)
                    Interlocked.Add(ref _dropped, batch.Count - sentCount);
            }
        }

        private async Task<PostResult> TrySendBatchAsync(IReadOnlyList<LogEntry> batch)
        {
            var entries = new object[batch.Count];
            for (int i = 0; i < batch.Count; i++)
                entries[i] = new { id = batch[i].Id, message = batch[i].Message, percent = batch[i].Percent };

            string body = JsonSerializer.Serialize(new { entries }, JsonOptions);
            return await PostWithRetryAsync(body, allowLegacyFallback: true).ConfigureAwait(false);
        }

        private async Task<int> TrySendLegacyAsync(IReadOnlyList<LogEntry> batch)
        {
            int sent = 0;
            foreach (var entry in batch)
            {
                string body = JsonSerializer.Serialize(new { message = entry.Message, percent = entry.Percent }, JsonOptions);
                if ((await PostWithRetryAsync(body, allowLegacyFallback: false).ConfigureAwait(false)).Success)
                    sent++;
            }
            return sent;
        }

        private async Task<PostResult> PostWithRetryAsync(string body, bool allowLegacyFallback)
        {
            for (int attempt = 1; attempt <= 4; attempt++)
            {
                try
                {
                    using var content = new StringContent(body, Encoding.UTF8, "application/json");
                    using var response = await Http.PostAsync(CloudLogSessionId.PushUrl(_sessionId), content).ConfigureAwait(false);
                    if (response.IsSuccessStatusCode) return new PostResult(true, false);
                    if (allowLegacyFallback && response.StatusCode == HttpStatusCode.BadRequest)
                        return new PostResult(false, true);

                    bool transient = response.StatusCode is HttpStatusCode.RequestTimeout
                        or HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500;
                    if (!transient) return new PostResult(false, false);
                }
                catch (HttpRequestException)
                {
                    // Falhas de conexão podem ser temporárias durante o primeiro logon.
                }
                catch (TaskCanceledException)
                {
                    // Timeout da chamada: tentar novamente com backoff curto.
                }

                if (attempt < 4)
                    await Task.Delay(TimeSpan.FromMilliseconds(300 * attempt)).ConfigureAwait(false);
            }

            return new PostResult(false, false);
        }
    }

    private readonly record struct PostResult(bool Success, bool LegacyWorker);

    private sealed record LogEntry(string Id, string Message, int Percent)
    {
        public LogEntry(string message, int percent) : this(Guid.NewGuid().ToString("N"), message, percent) { }
    }
}
