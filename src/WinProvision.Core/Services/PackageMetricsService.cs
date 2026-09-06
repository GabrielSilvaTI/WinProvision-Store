using System.Collections.Concurrent;
using System.Text.Json;
using WinProvision.Core.Models;

namespace WinProvision.Core.Services;

/// <summary>
/// Obtém e mantém em cache metadados leves dos pacotes (principalmente tamanho do instalador).
/// O cache é persistido em disco para evitar executar "winget show" repetidamente.
/// </summary>
public sealed class PackageMetricsService
{
    private readonly WingetExecutor _wingetExecutor;
    private readonly string _cachePath;
    private readonly SemaphoreSlim _ioLock = new(1, 1);
    private readonly ConcurrentDictionary<string, PackageMetric> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Task<long?>> _inFlight = new(StringComparer.OrdinalIgnoreCase);
    private bool _loaded;

    private static readonly TimeSpan CacheTtl = TimeSpan.FromDays(7);
    private static readonly TimeSpan NegativeCacheTtl = TimeSpan.FromMinutes(30);

    public PackageMetricsService(WingetExecutor wingetExecutor)
    {
        _wingetExecutor = wingetExecutor;
        string folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinProvisionStore", "Cache");
        Directory.CreateDirectory(folder);
        _cachePath = Path.Combine(folder, "package-metrics.json");
    }

    public async Task<long?> GetInstallerSizeAsync(AppEntry app, CancellationToken cancellationToken = default)
    {
        if (app.Office is not null)
            return null;

        // O Indexer agora calcula isso em sync-time (ver AppEntry.InstallerSizeBytes,
        // preenchido a partir da InstallerUrl do manifesto pelo InstallerSizeResolver
        // no passo 6 da pipeline) e persiste no apps.json. Quando o valor já vem do
        // catálogo, usamos ele direto — sem tocar no cache em disco nem rodar
        // "winget show" ao vivo, que agora é só o fallback dos poucos pacotes que o
        // Indexer não conseguiu resolver.
        if (app.InstallerSizeBytes is > 0)
            return app.InstallerSizeBytes;

        await EnsureLoadedAsync(cancellationToken);

        if (_cache.TryGetValue(app.Id, out var cached))
        {
            TimeSpan ttl = cached.InstallerSizeBytes.HasValue ? CacheTtl : NegativeCacheTtl;
            if (DateTimeOffset.UtcNow - cached.FetchedAt < ttl)
                return cached.InstallerSizeBytes;
        }

        // A consulta compartilhada não usa o token da página. Assim, trocar de perfil
        // ou navegar para outra tela não deixa uma Task cancelada presa no dicionário
        // _inFlight. O chamador ainda pode cancelar a própria espera com WaitAsync.
        Task<long?> request = _inFlight.GetOrAdd(app.Id, _ => FetchAndCacheAsync(app.Id, CancellationToken.None));
        try
        {
            return await request.WaitAsync(cancellationToken);
        }
        finally
        {
            _inFlight.TryRemove(new KeyValuePair<string, Task<long?>>(app.Id, request));
        }
    }

    public async Task ClearAsync()
    {
        await _ioLock.WaitAsync();
        try
        {
            _cache.Clear();
            _inFlight.Clear();
            _loaded = true;
            try
            {
                if (File.Exists(_cachePath))
                    File.Delete(_cachePath);
            }
            catch
            {
                // Limpeza é best-effort; não deve impedir o uso da aplicação.
            }
        }
        finally
        {
            _ioLock.Release();
        }
    }

    private async Task<long?> FetchAndCacheAsync(string appId, CancellationToken cancellationToken)
    {
        long? size = await _wingetExecutor.GetPackageInstallerSizeAsync(appId, cancellationToken);
        _cache[appId] = new PackageMetric(size, DateTimeOffset.UtcNow);
        await SaveAsync(cancellationToken);
        return size;
    }

    private async Task EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (_loaded)
            return;

        await _ioLock.WaitAsync(cancellationToken);
        try
        {
            if (_loaded)
                return;

            if (File.Exists(_cachePath))
            {
                try
                {
                    string json = await File.ReadAllTextAsync(_cachePath, cancellationToken);
                    var data = JsonSerializer.Deserialize<Dictionary<string, PackageMetric>>(json);
                    if (data is not null)
                    {
                        foreach (var item in data)
                            _cache[item.Key] = item.Value;
                    }
                }
                catch
                {
                    _cache.Clear();
                }
            }

            _loaded = true;
        }
        finally
        {
            _ioLock.Release();
        }
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        await _ioLock.WaitAsync(cancellationToken);
        try
        {
            string json = JsonSerializer.Serialize(_cache, new JsonSerializerOptions { WriteIndented = false });
            string temp = _cachePath + ".tmp";
            await File.WriteAllTextAsync(temp, json, cancellationToken);
            File.Move(temp, _cachePath, true);
        }
        catch
        {
            // O cache nunca pode quebrar a operação principal.
        }
        finally
        {
            _ioLock.Release();
        }
    }

    private sealed record PackageMetric(long? InstallerSizeBytes, DateTimeOffset FetchedAt);
}
