namespace WinProvision.Core.Services;

/// <summary>
/// Ponto único para invalidar os caches da aplicação sem apagar dados do usuário
/// (perfis, backups ou credenciais).
/// </summary>
public sealed class CacheService
{
    private readonly StoreService _storeService;
    private readonly IconService _iconService;
    private readonly PackageMetricsService _packageMetricsService;

    public CacheService(StoreService storeService, IconService iconService, PackageMetricsService packageMetricsService)
    {
        _storeService = storeService;
        _iconService = iconService;
        _packageMetricsService = packageMetricsService;
    }

    public async Task ClearAsync()
    {
        _storeService.ClearCache();
        _iconService.ClearCache();
        await _packageMetricsService.ClearAsync();

        // A camada de imagem mantém cache próprio para evitar downloads duplicados.
        // A UI chama AsyncImage.ClearCache() quando disponível; este serviço permanece
        // no Core para não criar dependência WPF.
    }
}
