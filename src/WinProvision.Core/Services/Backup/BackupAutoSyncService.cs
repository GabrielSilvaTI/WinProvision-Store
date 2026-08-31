using System.Linq;
using WinProvision.Core.Models;
using WinProvision.Core.Services;
using WinProvision.Core.Services.Profile;
using WinProvision.Core.Services.Provisioning;

namespace WinProvision.Core.Services.Backup;

/// <summary>
/// Liga o backup automático ao ciclo de vida real do app: assina
/// <see cref="InstalledAppsService.Changed"/> — que já dispara depois de todo
/// install/uninstall bem-sucedido via <see cref="OperationRunner"/> (MarkInstalled/
/// MarkUninstalled) — e <see cref="ProvisioningService.Changed"/> — que dispara depois
/// de todo ApplyAsync que realmente aplicou algo — e a cada mudança agenda uma
/// sincronização com debounce.
///
/// Cobre TODAS as guias abertas em <see cref="PackageCollectionService.Tabs"/> (não só
/// a ativa) — cada guia com pelo menos um item vira um <see cref="ProfileManifest"/>
/// dentro do <see cref="ProfileBackupSet"/> salvo — MAIS o último ajuste de
/// provisionamento atual (<see cref="ProvisioningService.Current"/>), se houver.
/// Um único ProfileBackupSet (um único arquivo, local ou Gist) cobre os dois: guias
/// vazias (ex.: uma guia nova ainda sem seleção) são ignoradas, pra não poluir o backup
/// com perfis em branco, mas isso não impede um backup só de provisionamento (sem
/// nenhuma guia com itens) de ser salvo.
///
/// Debounce existe porque uma operação em lote (ex.: importar um perfil com 15 apps)
/// dispara 15 eventos em sequência rápida; sem agrupar isso, seriam 15 escritas em
/// disco e 15 chamadas à API do Gist para o mesmo resultado final. Só o backup local
/// é obrigatório (não depende de login); o de nuvem só roda se
/// <see cref="GitHubBackupService.IsConnected"/> — login continua 100% opcional.
///
/// Registrado como singleton e resolvido ansiosamente uma vez no startup (ver
/// App.xaml.cs) só para o construtor rodar e a assinatura dos eventos acontecer — a
/// UI (SettingsPage) nunca precisa segurar uma referência a este serviço.
/// </summary>
public class BackupAutoSyncService : IDisposable
{
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromSeconds(4);

    private readonly InstalledAppsService _installedAppsService;
    private readonly ProvisioningService _provisioningService;
    private readonly PackageCollectionService _collectionService;
    private readonly ProfileService _profileService;
    private readonly LocalBackupService _localBackup;
    private readonly GitHubBackupService _cloudBackup;

    private readonly System.Threading.Timer _debounceTimer;
    private readonly SemaphoreSlim _runLock = new(1, 1);

    /// <summary>Disparado após cada tentativa de sincronização automática (sucesso ou falha), para a UI (ex.: SettingsPage aberta) refletir o status sem precisar dar poll.</summary>
    public event Action? SyncAttempted;

    public BackupAutoSyncService(
        InstalledAppsService installedAppsService,
        ProvisioningService provisioningService,
        PackageCollectionService collectionService,
        ProfileService profileService,
        LocalBackupService localBackup,
        GitHubBackupService cloudBackup)
    {
        _installedAppsService = installedAppsService;
        _provisioningService = provisioningService;
        _collectionService = collectionService;
        _profileService = profileService;
        _localBackup = localBackup;
        _cloudBackup = cloudBackup;

        _debounceTimer = new System.Threading.Timer(_ => _ = RunSyncAsync(), null, Timeout.Infinite, Timeout.Infinite);
        _installedAppsService.Changed += OnInstalledAppsChanged;
        _provisioningService.Changed += OnInstalledAppsChanged;
    }

    private void OnInstalledAppsChanged()
    {
        _debounceTimer.Change(DebounceDelay, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Também usado pelo botão "Sincronizar agora" das Configurações — mesma lógica, sem esperar o debounce.</summary>
    public async Task RunSyncAsync(CancellationToken ct = default)
    {
        if (!await _runLock.WaitAsync(0, ct))
            return; // já tem uma sincronização em andamento — a próxima mudança reagenda naturalmente

        try
        {
            var nonEmptyTabs = _collectionService.Tabs.Where(t => t.Items.Count > 0).ToList();
            var provisioning = _provisioningService.Current;

            // Nada de pacotes E nada de provisionamento configurado ainda => não há o que
            // salvar (evita gravar/subir um ProfileBackupSet totalmente vazio).
            if (nonEmptyTabs.Count == 0 && provisioning is null)
                return;

            var backupSet = new ProfileBackupSet
            {
                Tabs = nonEmptyTabs
                    .Select(t => _profileService.BuildFromSelection(t.Items, t.Title))
                    .ToList(),
                Provisioning = provisioning
            };

            await _localBackup.SaveAsync(backupSet, ct);

            if (_cloudBackup.IsConnected)
                await _cloudBackup.UploadProfileAsync(backupSet, ct);
        }
        catch (Exception)
        {
            // Backup automático é melhor-esforço: uma falha (ex.: sem internet no
            // momento) nunca deve derrubar o app nem interromper a operação de
            // instalação/remoção que disparou este ciclo.
        }
        finally
        {
            _runLock.Release();
            SyncAttempted?.Invoke();
        }
    }

    public void Dispose()
    {
        _installedAppsService.Changed -= OnInstalledAppsChanged;
        _provisioningService.Changed -= OnInstalledAppsChanged;
        _debounceTimer.Dispose();
    }
}
