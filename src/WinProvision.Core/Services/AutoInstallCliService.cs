using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
#if WINDOWS
using System.Security.Principal;
#endif
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WinProvision.Core.Models;
using WinProvision.Core.Models.Office;
using WinProvision.Core.Models.Provisioning;
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
/// pela sincronização via Cloudflare Worker (<see cref="Backup.CloudBackupService"/>), então o que foi
/// sincronizado na nuvem já é o que este modo aplica.
///
/// Não depende de nenhuma peça de UI (OperationsQueueService/janelas) de propósito — isso
/// roda com a MainWindow nunca sendo criada (ver App.xaml.cs), então tudo aqui fala direto
/// com WingetExecutor/OfficeDeploymentToolService/ProvisioningService e reporta progresso
/// via o delegate de log (que por padrão só escreve no Console, mas quem chamar pode passar
/// o próprio sink — ex.: um CliFileLogger.Log, ou até um callback que empurra as linhas pra
/// UI do WinProvision principal).
///
/// O WinGet é provisionado antes da primeira operação para habilitar a API COM e reutilizado
/// durante o restante da execução.
/// </summary>
[SupportedOSPlatform("windows")]
public class AutoInstallCliService
{
    private readonly ProfileService _profileService;
    private readonly WingetExecutor _wingetExecutor;
    private readonly OfficeDeploymentToolService _officeService;
    private readonly ProvisioningService _provisioningService;
    private readonly BackupAutoSyncService _backupSyncService;

    private Action<string> _log = Console.WriteLine;

    // ── Configuração de Retries ────────────────────────────────────────────────────────────
    // Pacotes winget: 3 tentativas, aguarda 5s entre cada. Cobre erros transitórios do winget
    // (bloqueio de outro processo, mutex do MSI, CDN instável no primeiro logon).
    private const int WingetRetryCount = 3;
    private static readonly TimeSpan WingetRetryDelay = TimeSpan.FromSeconds(5);
    private const int MaxParallelPackageInstalls = 2;

    // Download do perfil: 3 tentativas, aguarda 3s. Cobre instabilidade de rede transiente
    // no primeiro logon (DHCP/DNS pode ainda estar estabilizando).
    private const int ProfileDownloadRetryCount = 3;
    private static readonly TimeSpan ProfileDownloadRetryDelay = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Metadados fixos de cada etapa (título/descrição exibidos pela AutoWindow) — a ordem
    /// aqui é a mesma em que <see cref="PlanStages"/> as devolve.
    /// </summary>
    private static readonly Dictionary<AutoInstallStage, AutoInstallStageInfo> StageCatalog = new()
    {
        [AutoInstallStage.PackagesAndApps] = new(AutoInstallStage.PackagesAndApps,
            "Coleção de Pacotes", "Checando e preparando os pacotes selecionados..."),
        [AutoInstallStage.MicrosoftOffice] = new(AutoInstallStage.MicrosoftOffice,
            "Pacote Office", "Provisionando o Microsoft Office..."),
        [AutoInstallStage.SystemPersonalization] = new(AutoInstallStage.SystemPersonalization,
            "Personalização", "Aplicando personalizações e configurações visuais..."),
        [AutoInstallStage.PredefinedConfigurations] = new(AutoInstallStage.PredefinedConfigurations,
            "Configurações Finais", "Finalizando configurações e otimizando o sistema..."),
    };

    /// <summary>
    /// Decide, a partir do conteúdo de <paramref name="manifest"/>, quais etapas a AutoWindow
    /// deve exibir — só as que de fato têm algo a fazer (ex.: sem apps de Office no perfil,
    /// a etapa "Microsoft Office" nem aparece). Chamado pela UI antes de <see cref="RunManifestAsync"/>
    /// começar, pra montar a lista de etapas de uma vez.
    /// </summary>
    public static List<AutoInstallStageInfo> PlanStages(ProfileManifest manifest)
    {
        var stages = new List<AutoInstallStageInfo>();

        int packageCount = manifest.Apps
            .Where(a => a.OfficeOptions is null)
            .Select(PackageIdentityKey)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        int officeCount = manifest.Apps.Where(a => a.OfficeOptions is not null)
            .Select(OfficeIdentityKey).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        bool hasPackages = packageCount > 0;
        bool hasOffice = officeCount > 0;
        bool hasConfigurations = false;

        if (manifest.Provisioning is { } provisioning)
        {
            bool hasPersonalization =
                provisioning.Theme is not null && provisioning.Theme != SystemThemeMode.NaoDefinido
                || provisioning.SystemTheme is not null && provisioning.SystemTheme != SystemThemeMode.NaoDefinido
                || provisioning.AppsTheme is not null && provisioning.AppsTheme != SystemThemeMode.NaoDefinido
                || provisioning.AccentColorMode is not null && provisioning.AccentColorMode != AccentColorMode.NaoDefinido
                || provisioning.TaskbarAlignment is not null && provisioning.TaskbarAlignment != TaskbarAlignmentMode.NaoDefinido
                || provisioning.TaskbarAutoHide is not null
                || provisioning.TaskbarSearchBox is not null && provisioning.TaskbarSearchBox != TaskbarSearchBoxMode.NaoDefinido
                || !string.IsNullOrWhiteSpace(provisioning.WallpaperImageBase64);

            hasConfigurations =
                provisioning.PowerPlan is not null && provisioning.PowerPlan != PowerPlanMode.NaoDefinido
                || provisioning.DisplayTimeoutOnAc is not null
                || provisioning.DisplayTimeoutOnDc is not null
                || provisioning.StandbyTimeoutOnAc is not null
                || provisioning.StandbyTimeoutOnDc is not null
                || provisioning.EnableAutomaticTime == true
                || provisioning.EnableAutomaticTimeZone == true
                || provisioning.ShowFileExtensions is not null
                || provisioning.ShowHiddenFiles is not null
                || provisioning.OpenExplorerToThisPc is not null
                || !string.IsNullOrWhiteSpace(provisioning.MachineName)
                || !string.IsNullOrWhiteSpace(provisioning.Creator)
                || !string.IsNullOrWhiteSpace(provisioning.Name);

            if (hasPersonalization)
                stages.Add(StageCatalog[AutoInstallStage.SystemPersonalization] with
                {
                    WorkUnits = CountProvisioningSteps(provisioning, personalization: true)
                });
        }

        if (hasPackages)
            stages.Add(StageCatalog[AutoInstallStage.PackagesAndApps] with { WorkUnits = packageCount });
        if (hasOffice)
            stages.Add(StageCatalog[AutoInstallStage.MicrosoftOffice] with { WorkUnits = officeCount });
        if (hasConfigurations && manifest.Provisioning is { } configurationManifest)
            stages.Add(StageCatalog[AutoInstallStage.PredefinedConfigurations] with
            {
                WorkUnits = CountProvisioningSteps(configurationManifest, personalization: false)
            });

        return stages;
    }

    public AutoInstallCliService(
        ProfileService profileService,
        WingetExecutor wingetExecutor,
        OfficeDeploymentToolService officeService,
        ProvisioningService provisioningService,
        BackupAutoSyncService backupSyncService)
    {
        _profileService = profileService;
        _wingetExecutor = wingetExecutor;
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
    public async Task<AutoInstallExitCode> RunAsync(
        string profileSource,
        Action<string>? log = null,
        IProgress<AutoInstallStageEvent>? stageProgress = null,
        CancellationToken ct = default,
        string? logPath = null)
    {
        _log = log ?? Console.WriteLine;
        using var executionGuard = new AutoExecutionGuard();
        _log("[WinProvision] Proteção contra suspensão/reinício automático ativa durante o /auto.");

        bool isUrl = ProfileSourceReader.IsHttpUrl(profileSource);

        if (!isUrl && !File.Exists(profileSource))
        {
            _log($"[WinProvision] ERRO: perfil não encontrado em '{profileSource}'.");
            return AutoInstallExitCode.ProfileNotFound;
        }

        ProfileManifest manifest;
        try
        {
            manifest = await ImportManifestWithRetryAsync(profileSource, _log, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log($"[WinProvision] ERRO ao ler o perfil: {ex.Message}");
            return AutoInstallExitCode.ProfileReadError;
        }

        try
        {
            return await RunManifestAsync(manifest, profileSource, _log, stageProgress, ct, logPath);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log($"[WinProvision] ERRO inesperado: {ex.Message}");
            return AutoInstallExitCode.UnexpectedError;
        }
    }

    /// <summary>Importa o manifesto sem iniciar a execução. A AutoWindow usa este passo para
    /// montar a lista dinâmica de etapas antes de qualquer alteração no sistema.</summary>
    public async Task<ProfileManifest> ImportManifestAsync(string profileSource, CancellationToken ct = default)
    {
        bool isUrl = ProfileSourceReader.IsHttpUrl(profileSource);

        if (!isUrl && !File.Exists(profileSource))
            throw new FileNotFoundException("Perfil não encontrado.", profileSource);

        return await _profileService.ImportAsync(profileSource, ct, preserveDuplicateApps: true);
    }

    /// <summary>Importa o perfil com retry de rede e registra o tempo gasto antes de iniciar as instalações.</summary>
    public async Task<ProfileManifest> ImportManifestWithRetryAsync(
        string profileSource,
        Action<string>? log = null,
        CancellationToken ct = default)
    {
        Action<string> sink = log ?? _log;
        bool isUrl = ProfileSourceReader.IsHttpUrl(profileSource);
        sink(isUrl
            ? $"[WinProvision] Baixando perfil: {profileSource}"
            : $"[WinProvision] Lendo perfil: {profileSource}");

        var timer = Stopwatch.StartNew();
        Exception? lastException = null;
        int attempts = isUrl ? ProfileDownloadRetryCount : 1;
        int attemptsMade = 0;
        for (int attempt = 1; attempt <= attempts; attempt++)
        {
            attemptsMade = attempt;
            ct.ThrowIfCancellationRequested();
            if (attempt > 1)
            {
                sink($"[WinProvision] Tentando baixar o perfil novamente (tentativa {attempt}/{attempts}, aguardando {ProfileDownloadRetryDelay.TotalSeconds:0}s)…");
                await Task.Delay(ProfileDownloadRetryDelay, ct);
            }

            try
            {
                ProfileManifest manifest = await ImportManifestAsync(profileSource, ct);
                timer.Stop();
                sink($"[WinProvision] Perfil carregado em {FormatElapsed(timer.Elapsed)}.");
                return manifest;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (attempt < attempts && IsTransientProfileException(ex))
            {
                lastException = ex;
                sink($"[WinProvision] Falha transitória ao carregar o perfil (tentativa {attempt}/{attempts}): {ex.Message}");
            }
            catch (Exception ex)
            {
                lastException = ex;
                break;
            }
        }

        timer.Stop();
        sink($"[WinProvision] Falha ao carregar o perfil após {attemptsMade} tentativa(s), em {FormatElapsed(timer.Elapsed)}.");
        throw new InvalidDataException($"Não foi possível carregar o perfil: {lastException?.Message}", lastException);
    }

    /// <summary>Executa um manifesto já importado, permitindo que a UI acompanhe cada etapa.</summary>
    public Task<AutoInstallExitCode> RunManifestAsync(
        ProfileManifest manifest,
        string profileSource,
        Action<string>? log = null,
        IProgress<AutoInstallStageEvent>? stageProgress = null,
        CancellationToken ct = default,
        string? logPath = null)
    {
        _log = log ?? Console.WriteLine;
        return RunManifestCoreAsync(manifest, profileSource, stageProgress, ct, logPath);
    }

    private async Task<AutoInstallExitCode> RunManifestCoreAsync(
        ProfileManifest manifest,
        string profileSource,
        IProgress<AutoInstallStageEvent>? stageProgress,
        CancellationToken ct,
        string? logPath)
    {
        var totalTimer = Stopwatch.StartNew();
        string profileLabel = manifest.Name ?? Path.GetFileNameWithoutExtension(profileSource);
        bool hasProvisioning = manifest.Provisioning is not null;

        if (manifest.Apps.Count == 0 && !hasProvisioning)
        {
            _log($"[WinProvision] Perfil \"{profileLabel}\" não define nada a fazer (nem apps, nem provisionamento).");
            return AutoInstallExitCode.Success;
        }

        LogExecutionContext(manifest);
        var preflight = AutoProfilePreflight.Validate(manifest);
        foreach (string warning in preflight.Warnings.Distinct(StringComparer.OrdinalIgnoreCase))
            _log($"[WinProvision] PRÉ-VALIDAÇÃO: AVISO — {warning}");
        foreach (string error in preflight.Errors)
            _log($"[WinProvision] PRÉ-VALIDAÇÃO: ERRO — {error}");
        if (!preflight.IsValid)
        {
            _log("[WinProvision] Perfil rejeitado antes de iniciar downloads ou alterações no sistema.");
            AutoInstallStage? firstStage = PlanStages(manifest).FirstOrDefault()?.Stage;
            if (firstStage is { } stage)
                stageProgress?.Report(new AutoInstallStageEvent(stage, AutoInstallStageState.Failed, 0,
                    $"Pré-validação: {preflight.Errors[0]}"));
            return AutoInstallExitCode.PreflightFailed;
        }

        string serializedManifest = JsonSerializer.Serialize(manifest, WinProvisionJsonOptions.Profile);
        string manifestFingerprint = AutoRunCheckpointStore.Fingerprint(serializedManifest);
        string checkpointId = AutoRunCheckpointStore.SourceKey(profileSource);
        var checkpoint = await AutoRunCheckpointStore.OpenAsync(
            checkpointId, ct).ConfigureAwait(false);
        _log($"[WinProvision] Checkpoint de retomada: %LOCALAPPDATA%\\WinProvision\\AutoRuns\\{checkpointId}.json (perfil SHA-256 {manifestFingerprint}).");
        if (!string.IsNullOrWhiteSpace(logPath))
            _log($"[WinProvision] Log local: {logPath}");

        _log(hasProvisioning
            ? $"[WinProvision] Perfil \"{profileLabel}\" — {manifest.Apps.Count} item(ns) a instalar + ajustes de provisionamento do sistema."
            : $"[WinProvision] Perfil \"{profileLabel}\" — {manifest.Apps.Count} item(ns) a instalar.");

        int succeeded = 0;
        int failed = 0;
        bool restartRequired = false;
        bool wingetUnavailable = false;
        var stages = PlanStages(manifest).Select(s => s.Stage).ToHashSet();

        void Stage(AutoInstallStage stage, AutoInstallStageState state, double progress = 0, string? detail = null, WingetMethod? method = null)
            => stageProgress?.Report(new AutoInstallStageEvent(stage, state, Math.Clamp(progress, 0, 100), detail, method));

        void StageProgress(AutoInstallStage stage, double progress, string? detail = null, WingetMethod? method = null)
            => Stage(stage, AutoInstallStageState.InProgress, progress, detail, method);

        var packageCandidates = manifest.Apps.Where(a => a.OfficeOptions is null).ToList();
        var seenPackageKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var packageApps = packageCandidates
            .Where(app => seenPackageKeys.Add(PackageIdentityKey(app)))
            .ToList();
        int duplicatePackageCount = packageCandidates.Count - packageApps.Count;
        if (duplicatePackageCount > 0)
            _log($"[WinProvision] Perfil contém {duplicatePackageCount} referência(s) duplicada(s); cada pacote será instalado uma única vez.");
        var officeCandidates = manifest.Apps.Where(a => a.OfficeOptions is not null).ToList();
        var seenOfficeKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var officeApps = officeCandidates.Where(app => seenOfficeKeys.Add(OfficeIdentityKey(app))).ToList();
        if (officeCandidates.Count != officeApps.Count)
            _log($"[WinProvision] Perfil contém {officeCandidates.Count - officeApps.Count} referência(s) Office duplicada(s); cada configuração será processada uma única vez.");
        var itemResults = new List<AutoItemResult>(manifest.Apps.Count);

        Task? installWarmupTask = null;
        bool hasPendingPackages = packageApps.Any(app =>
        {
            var saved = checkpoint.GetItem(PackageCheckpointKey(app));
            return saved?.Status != "Completed" && saved?.Status != "Running" && saved?.InstallerStarted != true;
        });
        if (hasPendingPackages)
        {
            if (checkpoint.GetStage(AutoInstallStage.PackagesAndApps.ToString())?.Status == "Running")
                _log("[WinProvision] Preparação de pacotes interrompida; os itens já concluídos serão mantidos e os estados incertos não serão repetidos.");
            await checkpoint.SetStageAsync(AutoInstallStage.PackagesAndApps.ToString(),
                new AutoCheckpointEntry { Status = "Running", Message = "Preparação do WinGet/catálogos", LogPath = logPath }, ct);
            bool includesMicrosoftStore = packageApps.Any(app =>
                string.Equals(ResolvePackageSource(app), "msstore", StringComparison.OrdinalIgnoreCase));
            installWarmupTask = OperationRunner.WarmupConfiguredInstallHandlerAsync(
                includesMicrosoftStore, _log, ct);
        }

        var provisioning = manifest.Provisioning;
        var provisioningTimer = provisioning is null ? null : Stopwatch.StartNew();
        var provisioningSteps = new List<ProvisioningStepResult>();
        bool provisioningRestartRequired = false;

        async Task ApplyProvisioningPhaseAsync(
            AutoInstallStage stage,
            ProvisioningManifest phaseManifest,
            bool personalization)
        {
            string stageFingerprint = AutoRunCheckpointStore.Fingerprint(
                JsonSerializer.Serialize(phaseManifest, WinProvisionJsonOptions.Profile));
            string stageKey = $"{stage}:{stageFingerprint}";
            if (checkpoint.GetStage(stageKey)?.Status == "Completed")
            {
                _log($"[WinProvision] Etapa '{stageKey}' já concluída neste perfil; ignorando.");
                Stage(stage, AutoInstallStageState.Completed, 100, "Retomada: etapa já concluída.");
                return;
            }
            if (checkpoint.GetStage(stageKey)?.Status == "Running")
                _log($"[WinProvision] A etapa '{stageKey}' foi interrompida durante a execução. Os ajustes são idempotentes; reaplicando a etapa para completar os itens restantes.");
            await checkpoint.SetStageAsync(stageKey, new AutoCheckpointEntry { Status = "Running", LogPath = logPath }, ct);
            int total = CountProvisioningSteps(phaseManifest, personalization);
            int done = 0;
            StageProgress(stage, 0, personalization ? "Preparando personalização…" : "Preparando configurações do sistema…");

            try
            {
                var result = await _provisioningService.ApplyAsync(
                    phaseManifest,
                    _log,
                    ct,
                    step =>
                    {
                        provisioningSteps.Add(step);
                        string settingKey = $"setting:{stageKey}:{step.Setting}";
                        checkpoint.SetItemAsync(settingKey, new AutoCheckpointEntry
                        {
                            Status = step.Success ? "Completed" : "Failed",
                            Message = step.Message, Method = "Windows API/fallback",
                            DurationMilliseconds = (long)step.Elapsed.TotalMilliseconds, LogPath = logPath,
                        }, CancellationToken.None).GetAwaiter().GetResult();
                        done++;
                        StageProgress(stage, total == 0 ? 100 : done * 100d / total, step.Setting);
                    },
                    updateCurrent: false,
                    stepStarting: setting =>
                    {
                        string settingKey = $"setting:{stageKey}:{setting}";
                        if (checkpoint.GetItem(settingKey)?.Status == "Running")
                            _log($"[WinProvision] Ajuste '{setting}' foi interrompido; repetição segura por ser uma configuração idempotente.");
                        checkpoint.SetItemAsync(settingKey, new AutoCheckpointEntry
                        {
                            Status = "Running", Method = "Provisioning API/fallback", LogPath = logPath,
                        }, CancellationToken.None).GetAwaiter().GetResult();
                    });

                succeeded += result.Steps.Count(step => step.Success);
                failed += result.Steps.Count(step => !step.Success);
                provisioningRestartRequired |= result.RestartRequired;
                await checkpoint.SetStageAsync(stageKey, new AutoCheckpointEntry
                {
                    Status = result.Steps.Any(step => !step.Success) ? "CompletedWithWarnings" : "Completed",
                    Message = $"{result.Steps.Count(step => step.Success)} sucesso(s), {result.Steps.Count(step => !step.Success)} falha(s)",
                    LogPath = logPath,
                }, ct);
                var firstFailedStep = result.Steps.FirstOrDefault(step => !step.Success);
                bool phaseFailed = firstFailedStep is not null;
                string? failureDetail = firstFailedStep is null
                    ? null
                    : $"{firstFailedStep.Setting}: {DescribeFailure(WingetErrorTranslator.Classify(firstFailedStep.Message).ToString(), firstFailedStep.Message)}";
                Stage(stage, phaseFailed ? AutoInstallStageState.Failed : AutoInstallStageState.Completed, 100, failureDetail);
            }
            catch
            {
                await checkpoint.SetStageAsync(stageKey, new AutoCheckpointEntry { Status = "Running", Message = "Interrompida; será reavaliada no próximo início.", LogPath = logPath }, CancellationToken.None);
                Stage(stage, AutoInstallStageState.Failed);
                throw;
            }
        }

        if (provisioning is not null && stages.Contains(AutoInstallStage.SystemPersonalization))
        {
            _log("[WinProvision] Aplicando personalização antes da instalação dos pacotes...");
            await ApplyProvisioningPhaseAsync(
                AutoInstallStage.SystemPersonalization,
                CreatePersonalizationManifest(provisioning),
                personalization: true);
        }

        Task<Dictionary<int, string?>>? officePrefetchTask = null;
        if (officeApps.Count > 0)
        {
            // O download do conteúdo grande começa antes dos pacotes e segue em paralelo
            // com eles. O ODT limita sua própria transferência; as instalações continuam
            // sem duplicar execução e o cache é consumido na etapa Office.
            if (officeApps.Select((app, index) => (app, index)).Any(pair =>
            {
                string key = "office:" + OfficeIdentityKey(pair.app);
                var saved = checkpoint.GetItem(key);
                return saved?.Status != "Completed" && saved?.Status != "Running" && saved?.InstallerStarted != true;
            }))
            {
                await checkpoint.SetStageAsync(AutoInstallStage.MicrosoftOffice.ToString(),
                    new AutoCheckpointEntry { Status = "Running", Message = "Preparação do payload", LogPath = logPath }, ct);
                officePrefetchTask = PrepareOfficePayloadsAsync();
            }
        }

        async Task<Dictionary<int, string?>> PrepareOfficePayloadsAsync()
        {
            var sources = new Dictionary<int, string?>();
            var preparedByCache = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            StageProgress(AutoInstallStage.MicrosoftOffice, 0,
                "Preparando o conteúdo do Office em paralelo com os outros pacotes…");

            for (int index = 0; index < officeApps.Count; index++)
            {
                var appRef = officeApps[index];
                string checkpointKey = "office:" + OfficeIdentityKey(appRef);
                var saved = checkpoint.GetItem(checkpointKey);
                if (saved?.Status == "Completed" || saved?.Status == "Running" || saved?.InstallerStarted == true)
                {
                    sources[index] = null;
                    continue;
                }
                if (!TryCreateOfficeRequest(appRef.OfficeOptions!, out var request, out _, out var error))
                {
                    _log($"[WinProvision] Pré-download Office ignorado para '{appRef.Name ?? appRef.OfficeOptions!.ProductId}': {error}");
                    sources[index] = null;
                    continue;
                }

                string cachePath = _officeService.GetPayloadCachePath(request!);
                if (!preparedByCache.TryGetValue(cachePath, out string? sourcePath))
                {
                    sourcePath = await _officeService.PrepareInstallSourceAsync(
                        request!, line => LogLine(appRef.Name ?? request!.Plan.DisplayName, line), ct)
                        .ConfigureAwait(false);
                    preparedByCache[cachePath] = sourcePath;
                }
                sources[index] = sourcePath;
            }

            string detail = sources.Values.Any(path => path is not null)
                ? "Conteúdo do Office preparado; iniciando instalação…"
                : "Preparação local indisponível; o ODT baixará da CDN durante a instalação…";
            StageProgress(AutoInstallStage.MicrosoftOffice, 0, detail);
            return sources;
        }

        if (packageApps.Count > 0)
        {
            var packageStageTimer = Stopwatch.StartNew();
            // A barra representa itens realmente concluídos. O percentual informado por
            // cada motor aparece no detalhe da fase atual, sem estimar peso ou tempo restante.
            StageProgress(AutoInstallStage.PackagesAndApps, 0, $"Preparando {packageApps.Count} pacote(s)…");
            await checkpoint.SetStageAsync(AutoInstallStage.PackagesAndApps.ToString(),
                new AutoCheckpointEntry { Status = "Running", LogPath = logPath }, ct);
            bool allSucceeded = true;
            if (installWarmupTask is not null)
            {
                if (!installWarmupTask.IsCompleted)
                    StageProgress(AutoInstallStage.PackagesAndApps, 0, "Aquecendo WinGet e conectando aos catálogos…");
                try
                {
                    await installWarmupTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _log($"[WinProvision] Aquecimento do WinGet não concluído; seguindo com a cadeia normal: {ex.Message}");
                }
            }
            var packageProgress = new double[packageApps.Count];
            var packageResults = new AutoItemResult[packageApps.Count];
            var packageProgressLock = new object();
            using var installGate = new SemaphoreSlim(MaxParallelPackageInstalls, MaxParallelPackageInstalls);
            var installedSnapshotLock = new object();
            Task<(bool Succeeded, List<string> PackageIds)>? installedSnapshotTask = null;

            Task<(bool Succeeded, List<string> PackageIds)> GetInstalledSnapshotAsync()
            {
                lock (installedSnapshotLock)
                    return installedSnapshotTask ??= _wingetExecutor.TryGetInstalledPackageIdsAsync(ct);
            }

            if (officePrefetchTask is not null)
            {
                // No máximo um download de pacotes concorre com o conteúdo grande do
                // Office. Ao terminar a pré-carga, a capacidade normal é liberada.
                await installGate.WaitAsync(ct).ConfigureAwait(false);
                _ = ReleasePackageSlotAfterOfficePrefetchAsync(officePrefetchTask, installGate);
            }
            int packageWingetUnavailable = 0;
            int packageRestartRequired = 0;
            int allSucceededFlag = 1;

            async Task InstallPackageAtAsync(int index)
            {
                await installGate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    var appRef = packageApps[index];
                    string label = appRef.Name ?? appRef.Id;
                    string checkpointKey = PackageCheckpointKey(appRef);
                    var saved = checkpoint.GetItem(checkpointKey);
                    if (saved?.Status == "Completed")
                    {
                        packageResults[index] = new AutoItemResult(label, appRef.Id, ResolvePackageSource(appRef),
                            saved.Method ?? "retomado", true, TimeSpan.FromMilliseconds(saved.DurationMilliseconds), saved.Message, saved.ExitCode, false, "Concluído anteriormente");
                        packageProgress[index] = 100;
                        Interlocked.Increment(ref succeeded);
                        return;
                    }
                    if (saved?.Status == "Running" || saved?.InstallerStarted == true)
                    {
                        // Uma instalação interrompida não é repetida às cegas. Primeiro
                        // consultamos o inventário exato do WinGet; se o ID já consta como
                        // instalado, registramos a retomada como concluída.
                        try
                        {
                            var installedSnapshot = await GetInstalledSnapshotAsync().ConfigureAwait(false);
                            if (installedSnapshot.Succeeded && installedSnapshot.PackageIds.Contains(
                                    appRef.Id, StringComparer.OrdinalIgnoreCase))
                            {
                                string verifiedMessage = "Encontrado no inventário do WinGet durante a retomada.";
                                await checkpoint.SetItemAsync(checkpointKey, new AutoCheckpointEntry
                                {
                                    Status = "Completed", InstallerStarted = true,
                                    Method = saved.Method ?? "verificação do WinGet",
                                    ExitCode = saved.ExitCode, DurationMilliseconds = saved.DurationMilliseconds,
                                    Message = verifiedMessage, LogPath = logPath,
                                }, CancellationToken.None);
                                packageResults[index] = new AutoItemResult(label, appRef.Id, ResolvePackageSource(appRef),
                                    saved.Method ?? "verificação do WinGet", true,
                                    TimeSpan.FromMilliseconds(saved.DurationMilliseconds), verifiedMessage,
                                    saved.ExitCode, true, "Concluído e verificado na retomada");
                                packageProgress[index] = 100;
                                Interlocked.Increment(ref succeeded);
                                StageProgress(AutoInstallStage.PackagesAndApps, packageProgress.Average(),
                                    $"Verificado como instalado: {label}");
                                _log($"[WinProvision] {label}: instalação anterior confirmada pelo inventário do WinGet; nenhuma nova instalação iniciada.");
                                return;
                            }
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            _log($"[WinProvision] Não foi possível verificar {label} no inventário do WinGet; mantendo resultado incerto: {ex.Message}");
                        }

                        string interrupted = saved.Status == "Running"
                            ? "A execução anterior foi interrompida com o instalador em andamento; resultado incerto e não repetido automaticamente. Verifique o aplicativo; os demais itens seguros continuarão."
                            : "O instalador anterior chegou a iniciar e falhou; não será repetido automaticamente. Verifique o aplicativo; os demais itens seguros continuarão.";
                        _log($"[WinProvision] {label}: {interrupted}");
                        await checkpoint.SetItemAsync(checkpointKey, new AutoCheckpointEntry
                        {
                            Status = "InterruptedUnknown", InstallerStarted = true,
                            Method = saved.Method, ExitCode = saved.ExitCode, Message = interrupted, LogPath = logPath,
                        }, CancellationToken.None);
                        packageResults[index] = new AutoItemResult(label, appRef.Id, ResolvePackageSource(appRef),
                            saved.Method ?? "desconhecido", false, null, interrupted, saved.ExitCode, true, "Resultado desconhecido");
                        packageProgress[index] = 100;
                        Interlocked.Increment(ref failed);
                        Interlocked.Exchange(ref allSucceededFlag, 0);
                        StageProgress(AutoInstallStage.PackagesAndApps, packageProgress.Average(),
                            $"Interrompido: {label} · resultado incerto; instalação não repetida");
                        return;
                    }
                    InstallProgressPhase? lastReportedPhase = null;
                    var progressThrottle = Stopwatch.StartNew();
                    var phaseDurations = new TimeSpan[3];
                    Stopwatch? activePhaseTimer = null;

                    void ReportPackageProgress(InstallProgressUpdate update)
                    {
                        string phase = update.Phase switch
                        {
                            InstallProgressPhase.Downloading => "Baixando",
                            InstallProgressPhase.Installing => "Instalando",
                            _ => "Preparando"
                        };
                        lock (packageProgressLock)
                        {
                            if (lastReportedPhase != update.Phase)
                            {
                                if (lastReportedPhase is { } previousPhase && activePhaseTimer is not null)
                                    phaseDurations[(int)previousPhase] += activePhaseTimer.Elapsed;
                                lastReportedPhase = update.Phase;
                                activePhaseTimer = Stopwatch.StartNew();
                            }
                            double overallProgress = packageProgress.Average();

                            bool shouldReport = progressThrottle.ElapsedMilliseconds >= 120;
                            if (shouldReport)
                            {
                                progressThrottle.Restart();
                            }
                            if (!shouldReport)
                                return;

                            string detail = update.Percent is double percent
                                ? $"{phase} {label} · progresso informado pelo instalador: {percent:0}%"
                                : $"{phase} {label}…";
                            StageProgress(AutoInstallStage.PackagesAndApps, overallProgress,
                                detail, update.Method);
                        }
                    }

                    lock (packageProgressLock)
                        StageProgress(AutoInstallStage.PackagesAndApps,
                            packageProgress.Average(), $"Instalando {label}…", WingetMethod.Unknown);
                    await checkpoint.SetItemAsync(checkpointKey, new AutoCheckpointEntry
                    {
                        Status = "Running", InstallerStarted = false, Method = "Preparando", LogPath = logPath,
                    }, ct).ConfigureAwait(false);
                    var installResult = await InstallWingetAsync(appRef, ct, ReportPackageProgress)
                        .ConfigureAwait(false);
                    lock (packageProgressLock)
                    {
                        if (lastReportedPhase is { } finalPhase && activePhaseTimer is not null)
                            phaseDurations[(int)finalPhase] += activePhaseTimer.Elapsed;
                    }
                    string phaseSummary = string.Join("; ", Enum.GetValues<InstallProgressPhase>()
                        .Where(phase => phaseDurations[(int)phase] > TimeSpan.Zero)
                        .Select(phase => $"{phase switch
                        {
                            InstallProgressPhase.Downloading => "download",
                            InstallProgressPhase.Preparing => "preparação",
                            _ => "instalação"
                        }}={FormatElapsed(phaseDurations[(int)phase])}"));
                    if (phaseSummary.Length > 0)
                        _log($"[WinProvision] Diagnóstico de fase de \"{label}\": {phaseSummary}. Tempos reportados pelos eventos do motor; podem não cobrir intervalos sem eventos.");
                    bool ok = installResult.Success;
                    if (installResult.ExitCode is 3010 or 1641)
                        Interlocked.Exchange(ref packageRestartRequired, 1);
                    packageResults[index] = new AutoItemResult(label, appRef.Id, installResult.Source,
                        installResult.Method, ok, installResult.Elapsed, installResult.Error,
                        installResult.ExitCode, installResult.InstallerStarted, installResult.FailureReason);
                    await checkpoint.SetItemAsync(checkpointKey, new AutoCheckpointEntry
                    {
                        Status = ok ? "Completed" : installResult.InstallerStarted ? "FailedAfterStart" : "FailedBeforeStart",
                        Method = installResult.Method, ExitCode = installResult.ExitCode,
                        DurationMilliseconds = (long)installResult.Elapsed.TotalMilliseconds,
                        InstallerStarted = installResult.InstallerStarted, Message = installResult.Error, LogPath = logPath,
                    }, CancellationToken.None).ConfigureAwait(false);
                    if (ok)
                        Interlocked.Increment(ref succeeded);
                    else
                    {
                        Interlocked.Increment(ref failed);
                        Interlocked.Exchange(ref allSucceededFlag, 0);
                    }
                    if (!ok && installResult.WingetUnavailable)
                        Interlocked.Exchange(ref packageWingetUnavailable, 1);

                    Enum.TryParse<WingetMethod>(installResult.Method, out var completedMethod);
                    lock (packageProgressLock)
                    {
                        packageProgress[index] = 100;
                        StageProgress(AutoInstallStage.PackagesAndApps, packageProgress.Average(),
                            ok ? $"Instalado {label}" : $"Falhou: {label} · {DescribeFailure(installResult.FailureReason, installResult.Error)}", completedMethod);
                    }
                }
                finally
                {
                    installGate.Release();
                }
            }

            Task[] packageTasks = Enumerable.Range(0, packageApps.Count)
                .Select(InstallPackageAtAsync)
                .ToArray();
            await Task.WhenAll(packageTasks).ConfigureAwait(false);
            itemResults.AddRange(packageResults);
            allSucceeded = Volatile.Read(ref allSucceededFlag) == 1;
            wingetUnavailable |= Volatile.Read(ref packageWingetUnavailable) == 1;
            restartRequired |= Volatile.Read(ref packageRestartRequired) == 1;
            Stage(AutoInstallStage.PackagesAndApps, allSucceeded ? AutoInstallStageState.Completed : AutoInstallStageState.Failed, 100,
                allSucceeded ? null : "Um ou mais pacotes falharam; consulte o diagnóstico detalhado no log.");
            await checkpoint.SetStageAsync(AutoInstallStage.PackagesAndApps.ToString(), new AutoCheckpointEntry
            {
                Status = allSucceeded ? "Completed" : "CompletedWithWarnings", LogPath = logPath,
            }, CancellationToken.None);
            _log($"[WinProvision] Etapa de pacotes concluída em {FormatElapsed(packageStageTimer.Elapsed)}.");
        }

        if (officeApps.Count > 0)
        {
            var officeStageTimer = Stopwatch.StartNew();
            // O download é uma fase real e fica destacado enquanto ocorre em paralelo.
            // O Click-to-Run não dá percentual confiável durante a instalação, então
            // a etapa só avança pelos itens concluídos nessa fase.
            Dictionary<int, string?> officeSources = officePrefetchTask is not null
                ? await officePrefetchTask.ConfigureAwait(false)
                : new Dictionary<int, string?>();
            StageProgress(AutoInstallStage.MicrosoftOffice, 0,
                $"Conteúdo preparado; instalando {officeApps.Count} produto(s) do Office…");
            await checkpoint.SetStageAsync(AutoInstallStage.MicrosoftOffice.ToString(),
                new AutoCheckpointEntry { Status = "Running", LogPath = logPath }, ct);
            bool allSucceeded = true;
            for (int i = 0; i < officeApps.Count; i++)
            {
                var appRef = officeApps[i];
                string label = appRef.Name ?? appRef.OfficeOptions!.ProductId;
                string officeKey = "office:" + OfficeIdentityKey(appRef);
                var savedOffice = checkpoint.GetItem(officeKey);
                if (savedOffice?.Status == "Completed")
                {
                    itemResults.Add(new AutoItemResult(label, appRef.OfficeOptions!.ProductId, "Office",
                        savedOffice.Method ?? "ODT", true, TimeSpan.FromMilliseconds(savedOffice.DurationMilliseconds), savedOffice.Message,
                        savedOffice.ExitCode, false, "Concluído anteriormente"));
                    succeeded++;
                    StageProgress(AutoInstallStage.MicrosoftOffice, (i + 1) * 100d / officeApps.Count,
                        $"Já concluído: {label}");
                    continue;
                }
                if (savedOffice?.Status == "Running" || savedOffice?.InstallerStarted == true)
                {
                    string interruptedOffice = savedOffice.Status == "Running"
                        ? "A instalação Office foi interrompida com o ODT em andamento; resultado incerto e não repetido automaticamente. Verifique o Office antes de executar novamente."
                        : "O ODT anterior iniciou e retornou falha; não será repetido automaticamente. Verifique o Office antes de executar novamente.";
                    await checkpoint.SetItemAsync(officeKey, new AutoCheckpointEntry
                    {
                        Status = "InterruptedUnknown", InstallerStarted = true,
                        Method = "ODT", ExitCode = savedOffice.ExitCode, Message = interruptedOffice, LogPath = logPath,
                        DurationMilliseconds = savedOffice.DurationMilliseconds,
                    }, CancellationToken.None);
                    itemResults.Add(new AutoItemResult(label, appRef.OfficeOptions!.ProductId, "Office", "ODT",
                        false, null, interruptedOffice,
                        savedOffice.ExitCode, true, "Resultado desconhecido"));
                    failed++;
                    allSucceeded = false;
                    StageProgress(AutoInstallStage.MicrosoftOffice, i * 100d / officeApps.Count,
                        $"Interrompido: {label} · resultado incerto; instalação não repetida");
                    continue;
                }
                double installBase = i * 100d / officeApps.Count;
                StageProgress(AutoInstallStage.MicrosoftOffice, installBase,
                    officeSources.GetValueOrDefault(i) is not null
                        ? $"Instalando {label} a partir do cache local…"
                        : $"Instalando {label} pela CDN da Microsoft…");
                await checkpoint.SetItemAsync(officeKey, new AutoCheckpointEntry
                {
                    Status = "Running", InstallerStarted = false, Method = "ODT", LogPath = logPath,
                }, ct);
                var officeResult = await InstallOfficeAsync(appRef, appRef.OfficeOptions!, ct,
                    progress: percent => StageProgress(AutoInstallStage.MicrosoftOffice, installBase,
                        $"Instalando {label} · progresso informado pelo ODT: {percent:0}%"),
                    sourcePath: officeSources.GetValueOrDefault(i));
                bool ok = officeResult.Success;
                if (officeResult.ExitCode is 3010 or 1641)
                    restartRequired = true;
                itemResults.Add(new AutoItemResult(label, appRef.OfficeOptions!.ProductId, "Office", "ODT",
                    ok, officeResult.Elapsed, officeResult.Error, officeResult.ExitCode,
                    officeResult.InstallerStarted, officeResult.FailureReason));
                await checkpoint.SetItemAsync(officeKey, new AutoCheckpointEntry
                {
                    Status = ok ? "Completed" : "FailedAfterStart", Method = "ODT",
                    ExitCode = officeResult.ExitCode, InstallerStarted = officeResult.InstallerStarted,
                    DurationMilliseconds = (long)officeResult.Elapsed.TotalMilliseconds,
                    Message = officeResult.Error, LogPath = logPath,
                }, CancellationToken.None);
                allSucceeded &= ok;
                if (ok) succeeded++; else failed++;
                StageProgress(AutoInstallStage.MicrosoftOffice, (i + 1) * 100d / officeApps.Count,
                    ok ? $"Instalado {label}" : $"Falhou: {label} · {DescribeFailure(officeResult.FailureReason, officeResult.Error)}");
            }
            Stage(AutoInstallStage.MicrosoftOffice, allSucceeded ? AutoInstallStageState.Completed : AutoInstallStageState.Failed, 100,
                allSucceeded ? null : "A instalação do Office falhou; consulte o diagnóstico detalhado no log.");
            await checkpoint.SetStageAsync(AutoInstallStage.MicrosoftOffice.ToString(), new AutoCheckpointEntry
            {
                Status = allSucceeded ? "Completed" : "CompletedWithWarnings", LogPath = logPath,
            }, CancellationToken.None);
            _log($"[WinProvision] Etapa Office concluída em {FormatElapsed(officeStageTimer.Elapsed)}.");
        }

        if (provisioning is not null && stages.Contains(AutoInstallStage.PredefinedConfigurations))
        {
            _log("[WinProvision] Aplicando configurações finais do sistema...");
            await ApplyProvisioningPhaseAsync(
                AutoInstallStage.PredefinedConfigurations,
                CreateConfigurationManifest(provisioning),
                personalization: false);
        }

        if (provisioning is not null && provisioningSteps.Count > 0)
            _provisioningService.SetCurrent(provisioning);

        restartRequired |= provisioningRestartRequired;
        if (provisioningTimer is not null && provisioningSteps.Count > 0)
            _log($"[WinProvision] Etapa de provisionamento concluída em {FormatElapsed(provisioningTimer.Elapsed)}.");

        var backupTimer = Stopwatch.StartNew();
        try
        {
            var backupResult = await _backupSyncService.RunSyncAsync(ct);
            if (backupResult.AlreadyRunning)
                _log("[WinProvision] Backup automático ignorado: já havia uma sincronização em andamento.");
            else if (!backupResult.LocalSucceeded || (backupResult.CloudAttempted && !backupResult.CloudSucceeded))
                _log($"[WinProvision] AVISO: backup automático incompleto em {FormatElapsed(backupTimer.Elapsed)}. " +
                     $"Local={(backupResult.LocalSucceeded ? "OK" : "falhou")}; nuvem={(backupResult.CloudAttempted ? (backupResult.CloudSucceeded ? "OK" : "falhou") : "não configurada")}. " +
                     $"Motivo: {backupResult.ErrorMessage ?? "sem detalhes"}");
            else
                _log($"[WinProvision] Backup automático concluído em {FormatElapsed(backupTimer.Elapsed)}.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log($"[WinProvision] AVISO: backup automático falhou em {FormatElapsed(backupTimer.Elapsed)}: {ex.Message}");
        }

        _log($"[WinProvision] Execução completa em {FormatElapsed(totalTimer.Elapsed)}.");

        _log(failed == 0
            ? $"[WinProvision] Concluído: {succeeded} item(ns) instalado(s)/aplicado(s) com sucesso."
            : $"[WinProvision] Concluído com falhas: {succeeded} sucesso(s), {failed} falha(s).");

        if (itemResults.Count > 0)
        {
            _log("[WinProvision] Resumo dos itens:");
            foreach (var item in itemResults)
                _log($"[WinProvision]   {(item.Success ? "OK" : "FALHOU")} | {item.Name} | ID={item.Id} | origem={item.Source} | método={item.Method} | código={(item.ExitCode?.ToString() ?? "n/d")} | duração={(item.Elapsed is { } elapsed ? FormatElapsed(elapsed) : "indisponível (execução interrompida)")} | log={logPath ?? "caminho registrado pelo iniciador"} | diagnóstico={item.FailureReason ?? "n/d"}" +
                     (string.IsNullOrWhiteSpace(item.Error) ? string.Empty : $" | motivo={item.Error}"));
        }

        foreach (var step in provisioningSteps)
            _log($"[WinProvision]   {(step.Success ? "OK" : "FALHOU")} | ajuste={step.Setting} | método=Windows API/fallback | código={(step.Success ? "0" : "n/d")} | duração={FormatElapsed(step.Elapsed)} | log={logPath ?? "caminho registrado pelo iniciador"} | motivo={step.Message}");

        if (restartRequired)
            _log("[WinProvision] AVISO: reinicie o Windows para que todos os ajustes de provisionamento tenham efeito.");

        if (failed == 0)
            await checkpoint.MarkRunCompletedAsync(CancellationToken.None).ConfigureAwait(false);

        // WingetUnavailable só quando o winget não pôde ser provisionado E isso custou itens:
        // se a API própria instalou tudo, o perfil foi cumprido.
        return failed == 0
            ? AutoInstallExitCode.Success
            : wingetUnavailable ? AutoInstallExitCode.WingetUnavailable : AutoInstallExitCode.CompletedWithFailures;
    }

    private async Task<PackageInstallResult> InstallWingetAsync(ProfileAppRef appRef, CancellationToken ct, Action<InstallProgressUpdate>? progress = null)
    {
        string displayName = appRef.Name ?? appRef.Id;
        // Perfis antigos não carregavam Source. IDs de produto da Store têm nove
        // caracteres alfanuméricos; os demais seguem pela origem winget.
        string source = ResolvePackageSource(appRef);

        _log($"[WinProvision] Instalando \"{displayName}\" ({appRef.Id})…");
        var installTimer = Stopwatch.StartNew();
        bool wingetWasUnavailable = false;
        bool retryableFailure = true;
        string method = "desconhecido";
        string? error = null;
        int? resultExitCode = null;
        bool installerStarted = false;
        string failureReason = "n/d";

        bool success = await RetryAsync(displayName, WingetRetryCount, WingetRetryDelay, async () =>
        {
            try
            {
                var result = await OperationRunner.InstallWithConfiguredHandlerAsync(
                    _wingetExecutor,
                    appRef.Id,
                    onLogReceived: line =>
                    {
                        LogLine(displayName, line);
                        if (TryParsePercent(line, out var pct))
                        {
                            var phase = line.Contains("install", StringComparison.OrdinalIgnoreCase)
                                || line.Contains("instal", StringComparison.OrdinalIgnoreCase)
                                ? InstallProgressPhase.Installing
                                : InstallProgressPhase.Downloading;
                            progress?.Invoke(new InstallProgressUpdate(phase, pct, WingetMethod.WingetExe));
                        }
                    },
                    cancellationToken: ct,
                    onProgress: update =>
                    {
                        if (update.Method is { } installMethod) method = installMethod.ToString();
                        progress?.Invoke(update);
                    },
                    source: source);

                wingetWasUnavailable |= result.WingetUnavailable;
                resultExitCode = result.ExitCode;
                installerStarted = result.InstallerStarted;
                failureReason = result.Success ? "Success" : result.FailureReason.ToString();
                retryableFailure = !result.InstallerStarted && IsTransientWingetFailure(result);
                if (result.WingetUnavailable) method = "WinGet indisponível";
                error = result.Success ? null : result.Output;
                if (!result.Success)
                    _log($"[WinProvision] \"{displayName}\": código de saída {result.ExitCode}.");

                return result.Success;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Se o serviço não devolveu o estado da tentativa, não sabemos se um
                // instalador já foi iniciado. Evita repetir o pacote por segurança.
                retryableFailure = false;
                installerStarted = true;
                failureReason = ex.GetType().Name;
                _log($"[WinProvision] \"{displayName}\": exceção ({ex.Message}); sem nova tentativa automática porque o estado do instalador é desconhecido.");
                return false;
            }
        }, ct, shouldRetry: () => retryableFailure);

        _log(success
            ? $"[WinProvision] \"{displayName}\": OK."
            : $"[WinProvision] \"{displayName}\": FALHOU em {FormatElapsed(installTimer.Elapsed)}.");
        _log($"[WinProvision] Tempo de \"{displayName}\": {FormatElapsed(installTimer.Elapsed)}.");

        progress?.Invoke(new InstallProgressUpdate(InstallProgressPhase.Installing, 100,
            Enum.TryParse<WingetMethod>(method, out var completedMethod) ? completedMethod : WingetMethod.Unknown));
        return new PackageInstallResult(success, wingetWasUnavailable, source, method, installTimer.Elapsed,
            Truncate(error, 240), resultExitCode, installerStarted, failureReason);
    }

    private async Task<OfficeInstallResult> InstallOfficeAsync(ProfileAppRef appRef, OfficeInstallOptions options,
        CancellationToken ct, Action<double>? progress = null, string? sourcePath = null)
    {
        var installTimer = Stopwatch.StartNew();
        string? error = null;
        int? exitCode = null;
        bool installerStarted = false;
        string failureReason = "n/d";
        string label = appRef.Name ?? options.ProductId;
        if (!TryCreateOfficeRequest(options, out var request, out var requestLabel, out var validationError))
        {
            _log($"[WinProvision] \"{label}\": configuração Office inválida: {validationError}");
            return new OfficeInstallResult(false, installTimer.Elapsed, validationError, null, false, "InvalidProfile");
        }
        label = appRef.Name ?? requestLabel;
        request = request! with { SourcePath = sourcePath };

        _log($"[WinProvision] Instalando \"{label}\" (Office/ODT)…");

        bool success;
        try
        {
            var result = await _officeService.RunConfigureDetailedAsync(
                request,
                onStatus: line => { LogLine(label, line); if (TryParsePercent(line, out var pct)) progress?.Invoke(pct); },
                cancellationToken: ct);
            success = result.Success;
            exitCode = result.ExitCode;
            installerStarted = result.ProcessStarted;
            failureReason = result.ElevationCanceled ? "ElevationCanceled"
                : result.Success ? "Success"
                : ContainsPolicyBlock(result.Output) ? "BlockedByPolicy"
                : WingetErrorTranslator.Classify(result.Output) is WingetFailureReason.ElevationRequired
                    ? "ElevationRequired" : "InstallError";
            if (!success)
                error = result.ElevationCanceled
                    ? "UAC recusado pelo usuário; a instalação foi cancelada."
                    : result.Output;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            success = false;
            installerStarted = true; // Estado conservador: exceção após iniciar o fluxo ODT não autoriza repetição.
            failureReason = "ResultadoDesconhecido";
            error = ex.Message;
            _log($"[WinProvision] \"{label}\": falha de execução ODT sem retry automático ({ex.Message}).");
        }

        _log(success
            ? $"[WinProvision] \"{label}\": OK."
            : $"[WinProvision] \"{label}\": FALHOU; diagnóstico={failureReason}, código={(exitCode?.ToString() ?? "n/d")}.");

        progress?.Invoke(100);
        return new OfficeInstallResult(success, installTimer.Elapsed, success ? null : Truncate(error, 240),
            exitCode, installerStarted, failureReason);
    }

    private static bool TryCreateOfficeRequest(OfficeInstallOptions options,
        out OfficeInstallRequest? request, out string label, out string? error)
    {
        request = null;
        error = null;
        var plan = OfficePlanCatalog.All.FirstOrDefault(p =>
            string.Equals(p.ProductId, options.ProductId, StringComparison.OrdinalIgnoreCase));
        label = plan?.DisplayName ?? options.ProductId;
        if (plan is null)
        {
            error = $"ProductId '{options.ProductId}' não existe no catálogo desta versão do app.";
            return false;
        }

        var additionalProducts = (options.AdditionalProductIds ?? [])
            .Select(OfficePlanCatalog.ByProductId).ToArray();
        if (additionalProducts.Any(product => product is null))
        {
            error = "Um ou mais produtos adicionais não existem no catálogo desta versão do app.";
            return false;
        }

        request = new OfficeInstallRequest(
            plan,
            options.Architecture,
            options.LanguageId,
            options.ExcludedApps,
            DisplayNone: options.Silent,
            AdditionalLanguageIds: options.AdditionalLanguageIds,
            DisplayLevel: options.Silent ? OfficeDisplayLevel.Silent : OfficeDisplayLevel.Visible,
            ChannelOverride: options.ChannelOverride,
            AutoUpdatesEnabled: options.AutoUpdatesEnabled,
            AdditionalProducts: additionalProducts.Cast<OfficePlan>().ToArray());

        error = OfficeConfigXmlBuilder.ValidateRequest(request);
        return error is null;
    }

    internal static bool ValidateOfficeOptions(OfficeInstallOptions options, out string? error) =>
        TryCreateOfficeRequest(options, out _, out _, out error);

    private void LogExecutionContext(ProfileManifest manifest)
    {
#if WINDOWS
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        bool isSystem = string.Equals(identity.User?.Value, "S-1-5-18", StringComparison.OrdinalIgnoreCase);
        bool isAdministrator = !isSystem
            && new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        string context = isSystem ? "SYSTEM" : isAdministrator ? "Administrador" : "Usuário padrão";
        _log($"[WinProvision] Contexto de execução: {context}; conta={identity.Name}; SID={identity.User?.Value ?? "indisponível"}.");

        var required = new List<string>();
        if (manifest.Apps.Any(app => app.OfficeOptions is not null)) required.Add("instalação Office/ODT (solicita UAC quando necessário)");
        if (manifest.Provisioning is { } provisioning)
        {
            if (!string.IsNullOrWhiteSpace(provisioning.MachineName)) required.Add("renomear o computador");
            if (!string.IsNullOrWhiteSpace(provisioning.Creator) || !string.IsNullOrWhiteSpace(provisioning.Name)) required.Add("gravar informações OEM");
        }
        if (required.Count > 0)
        {
            _log($"[WinProvision] Ações que podem exigir elevação: {string.Join("; ", required)}.");
            if (!isAdministrator && !isSystem)
                _log("[WinProvision] O processo está sem elevação. Se o UAC for recusado, o Windows negar acesso ou uma política de TI bloquear a ação, o fallback não contorna a política: o item será marcado com o motivo correspondente.");
        }
        if (manifest.Apps.Any(app => app.OfficeOptions is null))
            _log("[WinProvision] Pacotes: a elevação depende do escopo e do manifesto do instalador; pedidos UAC e bloqueios de política serão registrados separadamente.");
#else
        _log("[WinProvision] Contexto de execução do Windows indisponível nesta plataforma.");
#endif
    }

    private static async Task ReleasePackageSlotAfterOfficePrefetchAsync(
        Task officePrefetchTask, SemaphoreSlim installGate)
    {
        try { await officePrefetchTask.ConfigureAwait(false); }
        catch { /* A etapa Office trata a falha; aqui apenas liberamos o slot reservado. */ }
        finally { installGate.Release(); }
    }

    private static bool TryParsePercent(string line, out double percent)
    {
        percent = 0;
        var match = Regex.Match(line ?? string.Empty, @"(?<!\d)(?:100|[0-9]{1,2})\s*%", RegexOptions.CultureInvariant);
        return match.Success && double.TryParse(match.Value.TrimEnd('%').Trim(), out percent);
    }

    private static int CountProvisioningSteps(ProvisioningManifest manifest, bool personalization)
    {
        int count = 0;
        if (personalization)
        {
            SystemThemeMode? systemTheme = manifest.SystemTheme is { } configuredSystemTheme
                && configuredSystemTheme != SystemThemeMode.NaoDefinido
                    ? configuredSystemTheme
                    : manifest.Theme;
            SystemThemeMode? appsTheme = manifest.AppsTheme is { } configuredAppsTheme
                && configuredAppsTheme != SystemThemeMode.NaoDefinido
                    ? configuredAppsTheme
                    : manifest.Theme;
            if (systemTheme is { } system && system != SystemThemeMode.NaoDefinido) count++;
            if (appsTheme is { } apps && apps != SystemThemeMode.NaoDefinido) count++;
            if (manifest.AccentColorMode is { } accent && accent != AccentColorMode.NaoDefinido) count++;
            if (manifest.TaskbarAlignment is { } alignment && alignment != TaskbarAlignmentMode.NaoDefinido) count++;
            if (manifest.TaskbarAutoHide is not null) count++;
            if (manifest.TaskbarSearchBox is { } search && search != TaskbarSearchBoxMode.NaoDefinido) count++;
            if (!string.IsNullOrWhiteSpace(manifest.WallpaperImageBase64)) count++;
        }
        else
        {
            if (manifest.PowerPlan is { } power && power != PowerPlanMode.NaoDefinido) count++;
            if (manifest.DisplayTimeoutOnAc is not null || manifest.DisplayTimeoutOnDc is not null
                || manifest.StandbyTimeoutOnAc is not null || manifest.StandbyTimeoutOnDc is not null) count++;
            if (manifest.EnableAutomaticTime == true || manifest.EnableAutomaticTimeZone == true) count++;
            if (manifest.ShowFileExtensions is not null || manifest.ShowHiddenFiles is not null
                || manifest.OpenExplorerToThisPc is not null) count++;
            if (!string.IsNullOrWhiteSpace(manifest.MachineName)) count++;
            if (!string.IsNullOrWhiteSpace(manifest.Creator) || !string.IsNullOrWhiteSpace(manifest.Name)) count++;
        }

        return count;
    }

    private static ProvisioningManifest CreatePersonalizationManifest(ProvisioningManifest source) => new()
    {
        SchemaVersion = source.SchemaVersion,
        CreatedAt = source.CreatedAt,
        Theme = source.Theme,
        SystemTheme = source.SystemTheme,
        AppsTheme = source.AppsTheme,
        AccentColorMode = source.AccentColorMode,
        AccentColor = source.AccentColor,
        TaskbarAlignment = source.TaskbarAlignment,
        TaskbarAutoHide = source.TaskbarAutoHide,
        TaskbarSearchBox = source.TaskbarSearchBox,
        WallpaperFileName = source.WallpaperFileName,
        WallpaperImageBase64 = source.WallpaperImageBase64,
    };

    private static ProvisioningManifest CreateConfigurationManifest(ProvisioningManifest source) => new()
    {
        SchemaVersion = source.SchemaVersion,
        CreatedAt = source.CreatedAt,
        Name = source.Name,
        Creator = source.Creator,
        PowerPlan = source.PowerPlan,
        EnableAutomaticTime = source.EnableAutomaticTime,
        EnableAutomaticTimeZone = source.EnableAutomaticTimeZone,
        ShowFileExtensions = source.ShowFileExtensions,
        ShowHiddenFiles = source.ShowHiddenFiles,
        OpenExplorerToThisPc = source.OpenExplorerToThisPc,
        MachineName = source.MachineName,
        DisplayTimeoutOnAc = source.DisplayTimeoutOnAc,
        DisplayTimeoutOnDc = source.DisplayTimeoutOnDc,
        StandbyTimeoutOnAc = source.StandbyTimeoutOnAc,
        StandbyTimeoutOnDc = source.StandbyTimeoutOnDc,
    };

    private void LogLine(string label, string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;

        _log($"[WinProvision]   {label}: {line.Trim()}");
    }

    /// <summary>
    /// Executa <paramref name="operation"/> até <paramref name="maxAttempts"/> vezes,
    /// aguardando <paramref name="delay"/> entre tentativas. Retorna o resultado da
    /// última tentativa. O log de tentativa/falha é feito dentro deste método, portanto
    /// o chamador não precisa repetir mensagens de erro.
    /// </summary>
    private async Task<bool> RetryAsync(
        string label,
        int maxAttempts,
        TimeSpan delay,
        Func<Task<bool>> operation,
        CancellationToken ct,
        Func<bool>? shouldRetry = null)
    {
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            if (attempt > 1)
            {
                _log($"[WinProvision] \"{label}\": tentativa {attempt}/{maxAttempts} (aguardando {delay.TotalSeconds:0}s)…");
                await Task.Delay(delay, ct);
            }

            try
            {
                bool result = await operation();
                if (result) return true;

                if (shouldRetry is not null && !shouldRetry())
                {
                    _log($"[WinProvision] \"{label}\": falha definitiva; sem nova tentativa automática.");
                    break;
                }

                if (attempt < maxAttempts)
                    _log($"[WinProvision] \"{label}\": falhou (tentativa {attempt}/{maxAttempts}), tentando novamente…");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (shouldRetry is not null && !shouldRetry())
                {
                    _log($"[WinProvision] \"{label}\": erro definitivo ({ex.Message}); sem nova tentativa automática.");
                    break;
                }
                if (attempt < maxAttempts)
                    _log($"[WinProvision] \"{label}\": erro ({ex.Message}) na tentativa {attempt}/{maxAttempts}, tentando novamente…");
                else
                    _log($"[WinProvision] \"{label}\": erro ({ex.Message}) — sem mais tentativas.");
            }
        }

        return false;
    }

    private static bool IsTransientWingetFailure(WingetExecutionResult result)
    {
        if (result.FailureReason is WingetFailureReason.DownloadError or WingetFailureReason.CatalogError)
            return true;

        string output = result.Output ?? string.Empty;
        return output.Contains("timeout", StringComparison.OrdinalIgnoreCase)
            || output.Contains("timed out", StringComparison.OrdinalIgnoreCase)
            || output.Contains("tempo esgotado", StringComparison.OrdinalIgnoreCase)
            || output.Contains("network", StringComparison.OrdinalIgnoreCase)
            || output.Contains("rede", StringComparison.OrdinalIgnoreCase)
            || output.Contains("temporarily unavailable", StringComparison.OrdinalIgnoreCase)
            || output.Contains("temporariamente indisponível", StringComparison.OrdinalIgnoreCase)
            || output.Contains("another installation is in progress", StringComparison.OrdinalIgnoreCase)
            || output.Contains("outra instalação está em andamento", StringComparison.OrdinalIgnoreCase)
            || output.Contains("0x800706BA", StringComparison.OrdinalIgnoreCase)
            || output.Contains("0x8001010A", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTransientProfileException(Exception exception) => exception switch
    {
        HttpRequestException { StatusCode: null } => true,
        HttpRequestException { StatusCode: { } status } when status == System.Net.HttpStatusCode.RequestTimeout
            || status == System.Net.HttpStatusCode.TooManyRequests
            || (int)status >= 500 => true,
        _ => exception is IOException or TimeoutException or TaskCanceledException
    };

    private static bool IsStoreProductId(string id) => id.Length == 9 && id.All(char.IsLetterOrDigit);

    private static string ResolvePackageSource(ProfileAppRef appRef) =>
        appRef.Source ?? (IsStoreProductId(appRef.Id) ? "msstore" : "winget");

    private static string PackageIdentityKey(ProfileAppRef appRef) =>
        $"{ResolvePackageSource(appRef)}\u001f{appRef.Id}";

    private static string PackageCheckpointKey(ProfileAppRef appRef) =>
        $"package:{ResolvePackageSource(appRef)}:{appRef.Id}:{appRef.PinnedVersion ?? "latest"}";

    internal static string OfficeIdentityKey(ProfileAppRef appRef)
    {
        string options = JsonSerializer.Serialize(appRef.OfficeOptions, WinProvisionJsonOptions.Profile);
        return AutoRunCheckpointStore.Fingerprint(options);
    }

    private sealed record PackageInstallResult(bool Success, bool WingetUnavailable, string Source,
        string Method, TimeSpan Elapsed, string? Error, int? ExitCode, bool InstallerStarted, string FailureReason);

    private sealed record OfficeInstallResult(bool Success, TimeSpan Elapsed, string? Error,
        int? ExitCode, bool InstallerStarted, string FailureReason);

    private sealed record AutoItemResult(string Name, string Id, string Source, string Method,
        bool Success, TimeSpan? Elapsed, string? Error, int? ExitCode, bool InstallerStarted, string? FailureReason);

    private static string FormatElapsed(TimeSpan elapsed) => elapsed.TotalMinutes >= 1
        ? $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds}s"
        : $"{elapsed.TotalSeconds:0.0}s";

    private static string DescribeFailure(string? reason, string? details)
    {
        string value = reason ?? string.Empty;
        if (value.Equals(nameof(WingetFailureReason.ElevationCanceled), StringComparison.OrdinalIgnoreCase))
            return "UAC recusado pelo usuário";
        if (value.Equals(nameof(WingetFailureReason.BlockedByPolicy), StringComparison.OrdinalIgnoreCase)
            || ContainsPolicyBlock(details))
            return "bloqueado por política de TI; o fallback não contorna essa restrição";
        if (value.Equals(nameof(WingetFailureReason.ElevationRequired), StringComparison.OrdinalIgnoreCase))
            return "permissão de administrador necessária";
        if (value.Equals(nameof(WingetFailureReason.ElevationProhibited), StringComparison.OrdinalIgnoreCase))
            return "o instalador não permite elevação";
        if (value.Contains("desconhecido", StringComparison.OrdinalIgnoreCase))
            return "resultado incerto; não será repetido automaticamente";
        return string.IsNullOrWhiteSpace(details) ? (reason ?? "falha do instalador") : Truncate(details, 180)!;
    }

    private static bool ContainsPolicyBlock(string? details) => !string.IsNullOrWhiteSpace(details)
        && (details.Contains("blocked by policy", StringComparison.OrdinalIgnoreCase)
            || details.Contains("bloqueado por política", StringComparison.OrdinalIgnoreCase)
            || details.Contains("app locker", StringComparison.OrdinalIgnoreCase)
            || details.Contains("applocker", StringComparison.OrdinalIgnoreCase)
            || details.Contains("windows defender application control", StringComparison.OrdinalIgnoreCase)
            || details.Contains("wdac", StringComparison.OrdinalIgnoreCase)
            || details.Contains("0x800704EC", StringComparison.OrdinalIgnoreCase));

    private static string? Truncate(string? value, int maxLength) => string.IsNullOrWhiteSpace(value)
        ? null
        : value.Length <= maxLength ? value : value[..maxLength] + "…";

    private sealed class AutoExecutionGuard : IDisposable
    {
        private const uint EsContinuous = 0x80000000;
        private const uint EsSystemRequired = 0x00000001;
        private const uint EsDisplayRequired = 0x00000002;

        public AutoExecutionGuard()
        {
            SetThreadExecutionState(EsContinuous | EsSystemRequired | EsDisplayRequired);
        }

        public void Dispose()
        {
            SetThreadExecutionState(EsContinuous);
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint SetThreadExecutionState(uint esFlags);
    }
}
