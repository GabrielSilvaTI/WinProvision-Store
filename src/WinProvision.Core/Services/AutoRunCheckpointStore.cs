using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WinProvision.Core.Services;

/// <summary>
/// Checkpoint local do /auto. O arquivo guarda apenas o hash do perfil, IDs e resultados
/// (nunca o manifesto, URL privada ou credenciais), permitindo retomar a mesma execução.
/// </summary>
internal sealed class AutoRunCheckpointStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _path;
    private AutoRunCheckpoint _state;

    private AutoRunCheckpointStore(string path, AutoRunCheckpoint state)
    {
        _path = path;
        _state = state;
    }

    public static async Task<AutoRunCheckpointStore> OpenAsync(string checkpointKey, CancellationToken ct)
    {
        string root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinProvision", "AutoRuns");
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, checkpointKey + ".json");

        AutoRunCheckpoint? state = null;
        if (File.Exists(path))
        {
            try
            {
                await using var input = File.OpenRead(path);
                state = await JsonSerializer.DeserializeAsync<AutoRunCheckpoint>(input, JsonOptions, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                string corruptPath = path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss");
                try { File.Move(path, corruptPath); } catch { }
            }
        }

        state ??= new AutoRunCheckpoint { CheckpointKey = checkpointKey };
        return new AutoRunCheckpointStore(path, state);
    }

    public static string Fingerprint(string manifestJson)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(manifestJson));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static string SourceKey(string profileSource)
    {
        string identity;
        if (Uri.TryCreate(profileSource, UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https")
            identity = "url:" + uri.GetComponents(UriComponents.HttpRequestUrl, UriFormat.UriEscaped);
        else
            identity = "file:" + Path.GetFullPath(profileSource);
        return Fingerprint(identity);
    }

    public AutoCheckpointEntry? GetItem(string key) =>
        _state.Items.TryGetValue(key, out var entry) ? entry : null;

    public AutoCheckpointEntry? GetStage(string key) =>
        _state.Stages.TryGetValue(key, out var entry) ? entry : null;

    public Task SetItemAsync(string key, AutoCheckpointEntry entry, CancellationToken ct = default) =>
        SetAsync(_state.Items, key, entry, ct);

    public Task SetStageAsync(string key, AutoCheckpointEntry entry, CancellationToken ct = default) =>
        SetAsync(_state.Stages, key, entry, ct);

    public async Task MarkRunCompletedAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _state.RunCompleted = true;
            _state.UpdatedUtc = DateTimeOffset.UtcNow;
            string temporaryPath = _path + ".tmp";
            await using (var output = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write,
                FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(output, _state, JsonOptions, ct).ConfigureAwait(false);
                await output.FlushAsync(ct).ConfigureAwait(false);
            }
            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally { _gate.Release(); }
    }

    private async Task SetAsync(Dictionary<string, AutoCheckpointEntry> entries, string key,
        AutoCheckpointEntry entry, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            entry.UpdatedUtc = DateTimeOffset.UtcNow;
            entries[key] = entry;
            _state.UpdatedUtc = DateTimeOffset.UtcNow;

            string temporaryPath = _path + ".tmp";
            await using (var output = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write,
                FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(output, _state, JsonOptions, ct).ConfigureAwait(false);
                await output.FlushAsync(ct).ConfigureAwait(false);
            }

            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            _gate.Release();
        }
    }
}

internal sealed class AutoRunCheckpoint
{
    public int SchemaVersion { get; set; } = 1;
    public string CheckpointKey { get; set; } = string.Empty;
    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public bool RunCompleted { get; set; }
    public Dictionary<string, AutoCheckpointEntry> Stages { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, AutoCheckpointEntry> Items { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

internal sealed class AutoCheckpointEntry
{
    public string Status { get; set; } = "Pending";
    public string? Method { get; set; }
    public int? ExitCode { get; set; }
    public bool InstallerStarted { get; set; }
    public long DurationMilliseconds { get; set; }
    public string? Message { get; set; }
    public string? LogPath { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
}
