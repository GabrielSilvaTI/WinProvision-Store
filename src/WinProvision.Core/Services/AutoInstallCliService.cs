using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using WinProvision.Core.Models;
using WinProvision.Core.Models.Office;
using WinProvision.Core.Services.Backup;
using WinProvision.Core.Services.Office;
using WinProvision.Core.Services.Profile;
using WinProvision.Core.Services.Provisioning;

namespace WinProvision.Core.Services;

/// <summary>
/// Ponta de entrada única da linha de comando (ex.: <c>WinProvision.Store.exe /auto
/// caminho\para\perfil.json</c> ou <c>WinProvision.Store.exe /auto
/// https://gist.githubusercontent.com/usuario/id/raw/perfil.json</c>) — lê um
/// <see cref="ProfileManifest"/> (.json local ou via link http(s)) e aplica tudo que ele
/// descrever: apps winget, planos de Office e, se presente, a seção
/// <see cref="ProfileManifest.Provisioning"/> (tema, barra de tarefas, energia, nome da
/// máquina, wallpaper) — um único arquivo cobre os dois casos, e é o mesmo formato usado
/// pela sincronização via Gist (<see cref="Backup.GitHubBackupService"/>), então o que foi
/// sincronizado na nuvem já é o que este modo aplica.
///
/// Não depende de nenhuma peça de UI (OperationsQueueService/janelas) de propósito — isso
/// roda com a MainWindow nunca sendo criada (ver App.xaml.cs), então tudo aqui fala direto
/// com WingetExecutor/OfficeDeploymentToolService/ProvisioningService e reporta progresso
/// via o delegate de log (que por padrão só escreve no Console, mas quem chamar pode passar
/// o próprio sink — ex.: um CliFileLogger.Log, ou até um callback que empurra as linhas pra
/// UI do WinProvision principal).
///
/// Antes de instalar qualquer app/Office, garante que o winget está disponível via
/// <see cref="WingetBootstrapper"/> — no cenário-alvo (First Logon Commands, sessão
/// interativa) ele costuma ainda estar carregando as dependências APPX de provisionamento,
/// então essa checagem baixa/instala o que faltar (VCLibs, UI.Xaml e o próprio App
/// Installer) em vez de deixar cada item falhar um por um por winget.exe não existir ainda.
/// </summary>
[SupportedOSPlatform("windows")]
public class AutoInstallCliService
{
    private readonly ProfileService _profileService;
    private readonly StoreService _storeService;
    private readonly WingetExecutor _wingetExecutor;
    private readonly WingetBootstrapper _wingetBootstrapper;
    private readonly OfficeDeploymentToolService _officeService;
    private readonly ProvisioningService _provisioningService;
    private readonly BackupAutoSyncService _backupSyncService;

    private Action<string> _log = Console.WriteLine;

    public AutoInstallCliService(
        ProfileService profileService,
        StoreService storeService,
        WingetExecutor wingetExecutor,
        WingetBootstrapper wingetBootstrapper,
        OfficeDeploymentToolService officeService,
        ProvisioningService provisioningService,
        BackupAutoSyncService backupSyncService)
    {
        _profileService = profileService;
        _storeService = storeService;
        _wingetExecutor = wingetExecutor;
        _wingetBootstrapper = wingetBootstrapper;
        _officeService = officeService;
        _provisioningService = provisioningService;
        _backupSyncService = backupSyncService;
    }

    /// <summary>
    /// Executa o perfil de ponta a ponta.
    /// </summary>
    /// <param name="profileSource">Caminho local do perfil .json, ou uma URL http(s) direta pro conteúdo (ex.: link "raw" de Gist).</param>
    /// <param name="log">
    /// Sink de log opcional — recebe cada linha já formatada (mesmo texto que ia pro
    /// Console antes). Se omitido, cai de volta pra Console.WriteLine. Passe
    /// <see cref="CliFileLogger"/>.Log aqui pra também gravar em arquivo.
    /// </param>
    /// <returns>
    /// Código de saída detalhado (ver <see cref="AutoInstallExitCode"/>) — o processo
    /// (App.xaml.cs) usa isso como exit code, pra scripts/o app principal poderem checar
    /// com precisão o que aconteceu, não só "deu certo/não deu".
    /// </returns>
    public async Task<AutoInstallExitCode> RunAsync(string profileSource, Action<string>? log = null, CancellationToken ct = default)
    {
        _log = log ?? Console.WriteLine;

        bool isUrl = ProfileSourceReader.IsHttpUrl(profileSource);

        if (!isUrl && !File.Exists(profileSource))
        {
            _log($"[WinProvision] ERRO: perfil não encontrado em '{profileSource}'.");
            return AutoInstallExitCode.ProfileNotFound;
        }

        _log(isUrl
            ? $"[WinProvision] Baixando perfil: {profileSource}"
            : $"[WinProvision] Lendo perfil: {profileSource}");

        ProfileManifest manifest;
        try
        {
            manifest = await _profileService.ImportAsync(profileSource, ct);
        }
        catch (Exception ex)
        {
            _log($"[WinProvision] ERRO ao {(isUrl ? "baixar" : "ler")} o perfil: {ex.Message}");
            return AutoInstallExitCode.ProfileReadError;
        }

        try
        {
            return await RunManifestAsync(manifest, profileSource, ct);
        }
        catch (Exception ex)
        {
            _log($"[WinProvision] ERRO inesperado: {ex.Message}");
            return AutoInstallExitCode.UnexpectedError;
        }
    }

    private async Task<AutoInstallExitCode> RunManifestAsync(ProfileManifest manifest, string profileSource, CancellationToken ct)
    {
        string profileLabel = manifest.Name ?? Path.GetFileNameWithoutExtension(profileSource);
        bool hasProvisioning = manifest.Provisioning is not null;

        if (manifest.Apps.Count == 0 && !hasProvisioning)
        {
            _log($"[WinProvision] Perfil \"{profileLabel}\" não define nada a fazer (nem apps, nem provisionamento).");
            return AutoInstallExitCode.Success;
        }

        _log(hasProvisioning
            ? $"[WinProvision] Perfil \"{profileLabel}\" — {manifest.Apps.Count} item(ns) a instalar + ajustes de provisionamento do sistema."
            : $"[WinProvision] Perfil \"{profileLabel}\" — {manifest.Apps.Count} item(ns) a instalar.");

        int succeeded = 0;
        int failed = 0;
        bool restartRequired = false;
        bool wingetUnavailable = false;

        if (manifest.Apps.Count > 0)
        {
            // Checa/garante o winget ANTES de tentar qualquer item — tanto apps winget
            // quanto Office (via ODT, ver OfficeDeploymentToolService) dependem dele. Sem
            // isso, no cenário-alvo (First Logon Commands, sessão interativa recém-criada)
            // cada item falharia individualmente enquanto o Windows ainda está terminando
            // de provisionar os pacotes APPX de sistema.
            var bootstrapResult = await _wingetBootstrapper.EnsureWingetAsync(_log, ct);

            if (!bootstrapResult.IsUsable)
            {
                _log($"[WinProvision] ERRO: winget não está disponível e o bootstrap automático não conseguiu " +
                     $"deixá-lo funcional ({bootstrapResult.ErrorMessage}). Pulando {manifest.Apps.Count} " +
                     $"item(ns) de app/Office — todos dependem do winget.");
                failed += manifest.Apps.Count;
                wingetUnavailable = true;
            }
            else
            {
                // Carrega o catálogo remoto uma vez só, só pra resolver o nome bonito dos
                // apps winget no log — se falhar (sem rede, por ex.), segue instalando pelo
                // Id mesmo.
                List<AppEntry> catalog;
                try
                {
                    catalog = await _storeService.LoadCatalogAsync(false, ct);
                }
                catch
                {
                    catalog = new List<AppEntry>();
                }

                foreach (var appRef in manifest.Apps)
                {
                    bool ok = appRef.OfficeOptions is { } officeOptions
                        ? await InstallOfficeAsync(appRef, officeOptions, ct)
                        : await InstallWingetAsync(appRef, catalog, ct);

                    if (ok) succeeded++; else failed++;
                }
            }
        }

        if (manifest.Provisioning is { } provisioning)
        {
            _log("[WinProvision] Aplicando ajustes de provisionamento do sistema...");

            var result = await _provisioningService.ApplyAsync(provisioning, _log, ct);
            succeeded += result.Steps.Count(s => s.Success);
            failed += result.Steps.Count(s => !s.Success);
            restartRequired = result.RestartRequired;
        }

        // Sincroniza o backup (local + Gist, se conectado) explicitamente aqui, sem esperar
        // o debounce de BackupAutoSyncService — o processo CLI encerra (Shutdown) logo após
        // este método retornar, então esperar o debounce padrão faria o backup nunca rodar.
        // Melhor-esforço: uma falha aqui (ex.: sem internet) nunca deve mudar o código de
        // saída desta execução.
        try
        {
            await _backupSyncService.RunSyncAsync(ct);
        }
        catch
        {
            // ignorado — mesma filosofia de melhor-esforço do próprio BackupAutoSyncService
        }

        _log(failed == 0
            ? $"[WinProvision] Concluído: {succeeded} item(ns) instalado(s)/aplicado(s) com sucesso."
            : $"[WinProvision] Concluído com falhas: {succeeded} sucesso(s), {failed} falha(s).");

        if (restartRequired)
        {
            _log("[WinProvision] AVISO: reinicie o Windows para que todos os ajustes de provisionamento tenham efeito.");
        }

        // wingetUnavailable é reportado como um código de saída próprio (em vez de cair em
        // CompletedWithFailures) porque é um problema categoricamente diferente de "um app
        // falhou ao instalar": aqui nenhum item que dependia do winget chegou a ser
        // tentado, então quem chama de fora (script/task sequence) consegue reagir de forma
        // diferente — ex.: reter e tentar de novo mais tarde, em vez de simplesmente logar
        // uma falha pontual.
        if (wingetUnavailable)
        {
            return AutoInstallExitCode.WingetUnavailable;
        }

        return failed == 0 ? AutoInstallExitCode.Success : AutoInstallExitCode.CompletedWithFailures;
    }

    private async Task<bool> InstallWingetAsync(ProfileAppRef appRef, List<AppEntry> catalog, CancellationToken ct)
    {
        string displayName = catalog.FirstOrDefault(a => string.Equals(a.Id, appRef.Id, StringComparison.OrdinalIgnoreCase))?.Name
            ?? appRef.Id;

        _log($"[WinProvision] Instalando \"{displayName}\" ({appRef.Id})...");

        try
        {
            var result = await _wingetExecutor.InstallAppAsync(
                appRef.Id,
                onLogReceived: line => LogLine(displayName, line),
                cancellationToken: ct);

            _log(result.Success
                ? $"[WinProvision] \"{displayName}\": OK."
                : $"[WinProvision] \"{displayName}\": FALHOU (código de saída {result.ExitCode}).");

            return result.Success;
        }
        catch (Exception ex)
        {
            _log($"[WinProvision] \"{displayName}\": ERRO ({ex.Message}).");
            return false;
        }
    }

    private async Task<bool> InstallOfficeAsync(ProfileAppRef appRef, OfficeInstallOptions options, CancellationToken ct)
    {
        var plan = OfficePlanCatalog.All.FirstOrDefault(p => string.Equals(p.ProductId, options.ProductId, StringComparison.OrdinalIgnoreCase));
        string label = appRef.Name ?? plan?.DisplayName ?? options.ProductId;

        if (plan is null)
        {
            _log($"[WinProvision] \"{label}\": FALHOU (ProductId '{options.ProductId}' não existe no catálogo desta versão do app).");
            return false;
        }

        var request = new OfficeInstallRequest(
            plan,
            options.Architecture,
            options.LanguageId,
            options.ExcludedApps,
            DisplayNone: options.Silent,
            AdditionalLanguageIds: options.AdditionalLanguageIds,
            DisplayLevel: options.Silent ? OfficeDisplayLevel.Silent : OfficeDisplayLevel.Visible,
            ChannelOverride: options.ChannelOverride,
            AutoUpdatesEnabled: options.AutoUpdatesEnabled);

        _log($"[WinProvision] Instalando \"{label}\" (Office/ODT)...");

        try
        {
            bool success = await _officeService.RunConfigureAsync(
                request,
                onStatus: line => LogLine(label, line),
                cancellationToken: ct);

            _log(success
                ? $"[WinProvision] \"{label}\": OK."
                : $"[WinProvision] \"{label}\": FALHOU.");

            return success;
        }
        catch (Exception ex)
        {
            _log($"[WinProvision] \"{label}\": ERRO ({ex.Message}).");
            return false;
        }
    }

    private void LogLine(string label, string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;

        _log($"[WinProvision]   {label}: {line.Trim()}");
    }
}
