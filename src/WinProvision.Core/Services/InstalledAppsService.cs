namespace WinProvision.Core.Services;

/// <summary>
/// Fonte única de verdade sobre quais apps do catálogo já estão instalados na máquina.
///
/// Sem esse cache, cada card da grade (e o painel de detalhes) precisaria rodar o
/// próprio "winget export" para saber se deve mostrar "Instalar" ou "Abrir" — caro e
/// redundante com dezenas de cards na tela. Em vez disso, o cache é carregado uma vez
/// (<see cref="EnsureLoadedAsync"/>) e atualizado localmente de forma otimista sempre
/// que uma instalação/remoção termina (<see cref="MarkInstalled"/>/<see cref="MarkUninstalled"/>),
/// disparando <see cref="Changed"/> para quem estiver assinado (HomePage, outras janelas
/// de detalhes abertas, etc.) sincronizar o <c>AppEntry.IsInstalled</c> correspondente.
/// </summary>
public class InstalledAppsService
{
    private readonly WingetExecutor _wingetExecutor;
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private HashSet<string> _installedIds = new(StringComparer.OrdinalIgnoreCase);
    private bool _loaded;

    public InstalledAppsService(WingetExecutor wingetExecutor)
    {
        _wingetExecutor = wingetExecutor;
    }

    /// <summary>Disparado sempre que o conjunto de apps instalados muda (load inicial, refresh, mark).</summary>
    public event Action? Changed;

    /// <summary>Carrega o cache uma única vez (chamadas subsequentes são no-op enquanto já carregado).</summary>
    public async Task EnsureLoadedAsync(CancellationToken cancellationToken = default)
    {
        if (_loaded)
            return;

        await RefreshAsync(cancellationToken);
    }

    /// <summary>Força uma releitura via "winget export", substituindo o cache atual.</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await _loadLock.WaitAsync(cancellationToken);
        try
        {
            List<string> ids = await _wingetExecutor.GetInstalledPackageIdsAsync(cancellationToken);
            _installedIds = new HashSet<string>(ids, StringComparer.OrdinalIgnoreCase);
            _loaded = true;
        }
        finally
        {
            _loadLock.Release();
        }

        Changed?.Invoke();
    }

    /// <summary>Consulta se um Package Id do winget já está instalado, segundo o cache atual.</summary>
    public bool IsInstalled(string appId) => _installedIds.Contains(appId);

    /// <summary>Marca localmente um app como instalado (chamado após um winget install bem-sucedido).</summary>
    public void MarkInstalled(string appId)
    {
        if (_installedIds.Add(appId))
        {
            Changed?.Invoke();
        }
    }

    /// <summary>Marca localmente um app como removido (chamado após um winget uninstall bem-sucedido).</summary>
    public void MarkUninstalled(string appId)
    {
        if (_installedIds.Remove(appId))
        {
            Changed?.Invoke();
        }
    }
}
