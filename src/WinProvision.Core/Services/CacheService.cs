namespace WinProvision.Core.Services;

/// <summary>
/// Ponto único para invalidar os caches da aplicação sem apagar dados do usuário
/// (perfis, backups ou credenciais).
/// </summary>
public sealed class CacheService
{
    private readonly StoreService _storeService;
    private readonly WinProvisionApiService _apiService;
    private readonly IconService _iconService;
    private readonly PackageMetricsService _packageMetricsService;

    public CacheService(
        StoreService storeService,
        WinProvisionApiService apiService,
        IconService iconService,
        PackageMetricsService packageMetricsService)
    {
        _storeService = storeService;
        _apiService = apiService;
        _iconService = iconService;
        _packageMetricsService = packageMetricsService;
    }

    public async Task ClearAsync()
    {
        _storeService.ClearCache();
        await _apiService.ClearLocalCacheAsync();
        _iconService.ClearCache();
        await _packageMetricsService.ClearAsync();

        // A camada de imagem mantém cache próprio para evitar downloads duplicados.
        // A UI chama AsyncImage.ClearCache() quando disponível; este serviço permanece
        // no Core para não criar dependência WPF.
    }
}
