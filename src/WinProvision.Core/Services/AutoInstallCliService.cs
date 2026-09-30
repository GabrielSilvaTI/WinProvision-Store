using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
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
/// O WinGet é preparado somente se o método de instalação escolhido precisar dele.
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

    // Office/ODT: 2 tentativas, aguarda 15s. O Click-to-Run precisa de mais tempo pra liberar
    // recursos antes de um segundo intento.
    private const int OfficeRetryCount = 2;
    private static readonly TimeSpan OfficeRetryDelay = TimeSpan.FromSeconds(15);

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

    /// <summary>Nomes de ajuste (ver ProvisioningService.ApplyAsync/Report) que contam como "System Personalization" na UI — os demais ajustes de provisionamento caem em "Pre-defined Configurations".</summary>
    private static readonly HashSet<string> PersonalizationSettings = new(StringComparer.Ordinal)
    {
        "Tema do sistema",
        "Alinhamento da barra de tarefas",
        "Ocultar automaticamente a barra de tarefas",
        "Caixa de pesquisa da barra de tarefas",
        "Papel de parede",
        "Região",
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

        int packageCount = manifest.Apps.Count(a => a.OfficeOptions is null);
        int officeCount = manifest.Apps.Count(a => a.OfficeOptions is not null);
        bool hasPackages = packageCount > 0;
        bool hasOffice = officeCount > 0;

        if (hasPackages)
            stages.Add(StageCatalog[AutoInstallStage.PackagesAndApps] with { WorkUnits = packageCount });
        if (hasOffice)
            stages.Add(StageCatalog[AutoInstallStage.MicrosoftOffice] with { WorkUnits = officeCount });

        if (manifest.Provisioning is { } provisioning)
        {
            bool hasPersonalization =
                provisioning.Theme is not null && provisioning.Theme != SystemThemeMode.NaoDefinido
                || provisioning.TaskbarAlignment is not null && provisioning.TaskbarAlignment != TaskbarAlignmentMode.NaoDefinido
                || provisioning.TaskbarAutoHide is not null
                || provisioning.TaskbarSearchBox is not null && provisioning.TaskbarSearchBox != TaskbarSearchBoxMode.NaoDefinido
                || !string.IsNullOrWhiteSpace(provisioning.WallpaperImageBase64)
                || !string.IsNullOrWhiteSpace(provisioning.Region);

            bool hasConfigurations =
                provisioning.PowerPlan is not null && provisioning.PowerPlan != PowerPlanMode.NaoDefinido
                || !string.IsNullOrWhiteSpace(provisioning.MachineName)
                || provisioning.AutoCreateRestorePoint == true
                || provisioning.AutoCleanTempOnLogon == true;

            if (hasPersonalization)
                stages.Add(StageCatalog[AutoInstallStage.SystemPersonalization] with
                {
                    WorkUnits = CountProvisioningSteps(provisioning, personalization: true)
                });
            if (hasConfigurations)
                stages.Add(StageCatalog[AutoInstallStage.PredefinedConfigurations] with
                {
                    WorkUnits = CountProvisioningSteps(provisioning, personalization: false)
                });
        }

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
        CancellationToken ct = default)
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
            return await RunManifestAsync(manifest, profileSource, _log, stageProgress, ct);
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

        return await _profileService.ImportAsync(profileSource, ct);
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
        CancellationToken ct = default)
    {
        _log = log ?? Console.WriteLine;
        return RunManifestCoreAsync(manifest, profileSource, stageProgress, ct);
    }

    private async Task<AutoInstallExitCode> RunManifestCoreAsync(
        ProfileManifest manifest,
        string profileSource,
        IProgress<AutoInstallStageEvent>? stageProgress,
        CancellationToken ct)
    {
        var totalTimer = Stopwatch.StartNew();
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
        var stages = PlanStages(manifest).Select(s => s.Stage).ToHashSet();

        void Stage(AutoInstallStage stage, AutoInstallStageState state, double progress = 0, string? detail = null)
            => stageProgress?.Report(new AutoInstallStageEvent(stage, state, Math.Clamp(progress, 0, 100), detail));

        void StageProgress(AutoInstallStage stage, double progress, string? detail = null)
            => Stage(stage, AutoInstallStageState.InProgress, progress, detail);

        var packageApps = manifest.Apps.Where(a => a.OfficeOptions is null).ToList();
        var officeApps = manifest.Apps.Where(a => a.OfficeOptions is not null).ToList();
        var itemResults = new List<AutoItemResult>(manifest.Apps.Count);

        if (packageApps.Count > 0)
        {
            var packageStageTimer = Stopwatch.StartNew();
            // O progresso interno do COM e da WinProvision API é confiável durante o
            // download. Cada pacote pesa igualmente; metade do item representa download
            // e metade instalação, evitando que 100% do download pareça instalação pronta.
            StageProgress(AutoInstallStage.PackagesAndApps, 0, $"Preparando {packageApps.Count} pacote(s)…");
            bool allSucceeded = true;
            for (int i = 0; i < packageApps.Count; i++)
            {
                var appRef = packageApps[i];
                string label = appRef.Name ?? appRef.Id;
                double itemProgress = 0;
                double lastReportedProgress = -1;
                InstallProgressPhase? lastReportedPhase = null;
                var progressThrottle = Stopwatch.StartNew();
                StageProgress(AutoInstallStage.PackagesAndApps, i * 100d / packageApps.Count, $"Preparando {label}…");

                void ReportPackageProgress(InstallProgressUpdate update)
                {
                    string phase = update.Phase switch
                    {
                        InstallProgressPhase.Downloading => "Baixando",
                        InstallProgressPhase.Installing => "Instalando",
                        _ => "Preparando"
                    };
                    double? phaseProgress = update.Phase switch
                    {
                        InstallProgressPhase.Downloading when update.Percent is double downloadPercent
                            => Math.Clamp(downloadPercent, 0, 100) * 0.5,
                        InstallProgressPhase.Installing when update.Percent is double installPercent
                            => 50 + Math.Clamp(installPercent, 0, 100) * 0.5,
                        InstallProgressPhase.Installing => 50,
                        _ => null
                    };

                    if (phaseProgress is double value)
                        itemProgress = Math.Max(itemProgress, value);

                    bool phaseChanged = lastReportedPhase != update.Phase;
                    if (!phaseChanged
                        && itemProgress - lastReportedProgress < 0.5
                        && progressThrottle.ElapsedMilliseconds < 120)
                        return;

                    lastReportedProgress = itemProgress;
                    lastReportedPhase = update.Phase;
                    progressThrottle.Restart();
                    string detail = update.Percent is double percent
                        ? $"{phase} {label} · {percent:0}%"
                        : $"{phase} {label}…";
                    StageProgress(AutoInstallStage.PackagesAndApps,
                        (i + itemProgress / 100d) * 100d / packageApps.Count,
                        detail);
                }

                var installResult = await InstallWingetAsync(appRef, ct, ReportPackageProgress);
                bool ok = installResult.Success;
                itemResults.Add(new AutoItemResult(label, appRef.Id, installResult.Source, installResult.Method,
                    ok, installResult.Elapsed, installResult.Error));
                allSucceeded &= ok;
                if (ok) succeeded++; else failed++;
                if (!ok && installResult.WingetUnavailable)
                    wingetUnavailable = true;
                StageProgress(AutoInstallStage.PackagesAndApps, (i + 1) * 100d / packageApps.Count, ok ? $"Instalado {label}" : $"Falhou: {label}");
            }
            Stage(AutoInstallStage.PackagesAndApps, allSucceeded ? AutoInstallStageState.Completed : AutoInstallStageState.Failed, 100);
            _log($"[WinProvision] Etapa de pacotes concluída em {FormatElapsed(packageStageTimer.Elapsed)}.");
        }

        if (officeApps.Count > 0)
        {
            var officeStageTimer = Stopwatch.StartNew();
            // O Click-to-Run não dá um percentual confiável durante a instalação (às
            // vezes fica mudo por minutos, às vezes pula direto pro fim), então não
            // tentamos degrau nenhum aqui — a etapa fica indeterminada (spinner/shimmer)
            // do início ao fim de cada item e só avança quando o item de fato termina.
            // Com 2+ itens de Office no perfil, o avanço por item concluído ainda
            // acontece (mesmo cálculo de degrau dos pacotes), só sem meio-termo dentro
            // de um item individual.
            StageProgress(AutoInstallStage.MicrosoftOffice, 0, $"Preparando {officeApps.Count} instalação(ões) do Office…");
            bool allSucceeded = true;
            for (int i = 0; i < officeApps.Count; i++)
            {
                var appRef = officeApps[i];
                string label = appRef.Name ?? appRef.OfficeOptions!.ProductId;
                if (i > 0) StageProgress(AutoInstallStage.MicrosoftOffice, i * 100d / officeApps.Count, $"Instalando {label}…");
                var officeResult = await InstallOfficeAsync(appRef, appRef.OfficeOptions!, ct);
                bool ok = officeResult.Success;
                itemResults.Add(new AutoItemResult(label, appRef.OfficeOptions!.ProductId, "Office", "ODT",
                    ok, officeResult.Elapsed, officeResult.Error));
                allSucceeded &= ok;
                if (ok) succeeded++; else failed++;
                StageProgress(AutoInstallStage.MicrosoftOffice, (i + 1) * 100d / officeApps.Count, ok ? $"Instalado {label}" : $"Falhou: {label}");
            }
            Stage(AutoInstallStage.MicrosoftOffice, allSucceeded ? AutoInstallStageState.Completed : AutoInstallStageState.Failed, 100);
            _log($"[WinProvision] Etapa Office concluída em {FormatElapsed(officeStageTimer.Elapsed)}.");
        }

        if (manifest.Provisioning is { } provisioning)
        {
            var provisioningTimer = Stopwatch.StartNew();
            _log("[WinProvision] Aplicando ajustes de provisionamento do sistema...");
            bool hasPersonalization = stages.Contains(AutoInstallStage.SystemPersonalization);
            bool hasConfigurations = stages.Contains(AutoInstallStage.PredefinedConfigurations);
            var active = new HashSet<AutoInstallStage>();
            int personalizationTotal = CountProvisioningSteps(provisioning, true);
            int configurationTotal = CountProvisioningSteps(provisioning, false);
            int personalizationDone = 0;
            int configurationDone = 0;

            if (hasPersonalization) StageProgress(AutoInstallStage.SystemPersonalization, 0, "Preparando personalização…");
            if (hasConfigurations) StageProgress(AutoInstallStage.PredefinedConfigurations, 0, "Preparando configurações do sistema…");

            void OnProvisioningStep(ProvisioningStepResult step)
            {
                var stage = PersonalizationSettings.Contains(step.Setting)
                    ? AutoInstallStage.SystemPersonalization
                    : AutoInstallStage.PredefinedConfigurations;
                active.Add(stage);
                if (stage == AutoInstallStage.SystemPersonalization)
                {
                    personalizationDone++;
                    StageProgress(stage, personalizationTotal == 0 ? 100 : personalizationDone * 100d / personalizationTotal, step.Setting);
                }
                else
                {
                    configurationDone++;
                    StageProgress(stage, configurationTotal == 0 ? 100 : configurationDone * 100d / configurationTotal, step.Setting);
                }
            }

            ProvisioningApplyResult result;
            try
            {
                result = await _provisioningService.ApplyAsync(provisioning, _log, ct, OnProvisioningStep);
            }
            catch
            {
                foreach (var stage in active) Stage(stage, AutoInstallStageState.Failed);
                throw;
            }

            succeeded += result.Steps.Count(s => s.Success);
            failed += result.Steps.Count(s => !s.Success);
            restartRequired = result.RestartRequired;

            if (hasPersonalization && !active.Contains(AutoInstallStage.SystemPersonalization))
                Stage(AutoInstallStage.SystemPersonalization, AutoInstallStageState.Completed, 100);
            if (hasConfigurations && !active.Contains(AutoInstallStage.PredefinedConfigurations))
                Stage(AutoInstallStage.PredefinedConfigurations, AutoInstallStageState.Completed, 100);

            foreach (var stage in active)
            {
                bool stageFailed = result.Steps.Any(s =>
                    (((PersonalizationSettings.Contains(s.Setting) && stage == AutoInstallStage.SystemPersonalization) ||
                      (!PersonalizationSettings.Contains(s.Setting) && stage == AutoInstallStage.PredefinedConfigurations)) && !s.Success));
                Stage(stage, stageFailed ? AutoInstallStageState.Failed : AutoInstallStageState.Completed, stageFailed ? Math.Max(0, stage == AutoInstallStage.SystemPersonalization ? personalizationDone * 100d / Math.Max(1, personalizationTotal) : configurationDone * 100d / Math.Max(1, configurationTotal)) : 100);
            }
            _log($"[WinProvision] Etapa de provisionamento concluída em {FormatElapsed(provisioningTimer.Elapsed)}.");
        }

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
                _log($"[WinProvision]   {(item.Success ? "OK" : "FALHOU")} | {item.Name} | ID={item.Id} | origem={item.Source} | método={item.Method} | tempo={FormatElapsed(item.Elapsed)}" +
                     (string.IsNullOrWhiteSpace(item.Error) ? string.Empty : $" | motivo={item.Error}"));
        }

        if (restartRequired)
            _log("[WinProvision] AVISO: reinicie o Windows para que todos os ajustes de provisionamento tenham efeito.");

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
        string source = appRef.Source ?? (IsStoreProductId(appRef.Id) ? "msstore" : "winget");

        _log($"[WinProvision] Instalando \"{displayName}\" ({appRef.Id})…");
        var installTimer = Stopwatch.StartNew();
        bool wingetWasUnavailable = false;
        bool retryableFailure = true;
        string method = "desconhecido";
        string? error = null;

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
                retryableFailure = IsTransientWingetFailure(result);
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
                retryableFailure = IsTransientException(ex);
                _log($"[WinProvision] \"{displayName}\": exceção ({ex.Message}).");
                return false;
            }
        }, ct, shouldRetry: () => retryableFailure);

        _log(success
            ? $"[WinProvision] \"{displayName}\": OK."
            : $"[WinProvision] \"{displayName}\": FALHOU em {FormatElapsed(installTimer.Elapsed)}.");
        _log($"[WinProvision] Tempo de \"{displayName}\": {FormatElapsed(installTimer.Elapsed)}.");

        progress?.Invoke(new InstallProgressUpdate(InstallProgressPhase.Installing, 100,
            Enum.TryParse<WingetMethod>(method, out var completedMethod) ? completedMethod : WingetMethod.Unknown));
        return new PackageInstallResult(success, wingetWasUnavailable, source, method, installTimer.Elapsed, Truncate(error, 240));
    }

    private async Task<OfficeInstallResult> InstallOfficeAsync(ProfileAppRef appRef, OfficeInstallOptions options, CancellationToken ct, Action<double>? progress = null)
    {
        var installTimer = Stopwatch.StartNew();
        string? error = null;
        var plan = OfficePlanCatalog.All.FirstOrDefault(p => string.Equals(p.ProductId, options.ProductId, StringComparison.OrdinalIgnoreCase));
        string label = appRef.Name ?? plan?.DisplayName ?? options.ProductId;

        if (plan is null)
        {
            _log($"[WinProvision] \"{label}\": FALHOU (ProductId '{options.ProductId}' não existe no catálogo desta versão do app).");
            return new OfficeInstallResult(false, installTimer.Elapsed, $"ProductId inválido: {options.ProductId}");
        }

        var additionalProducts = (options.AdditionalProductIds ?? []).Select(OfficePlanCatalog.ByProductId).ToArray();
        if (additionalProducts.Any(product => product is null))
        {
            _log($"[WinProvision] \"{label}\": um ou mais produtos adicionais não existem no catálogo desta versão do app.");
            return new OfficeInstallResult(false, installTimer.Elapsed, "Produtos Office adicionais inválidos");
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
            AutoUpdatesEnabled: options.AutoUpdatesEnabled,
            AdditionalProducts: additionalProducts.Cast<OfficePlan>().ToArray());

        if (OfficeConfigXmlBuilder.ValidateRequest(request) is { } validationError)
        {
            _log($"[WinProvision] \"{label}\": configuração Office inválida: {validationError}");
            return new OfficeInstallResult(false, installTimer.Elapsed, validationError);
        }

        _log($"[WinProvision] Instalando \"{label}\" (Office/ODT)…");

        bool officeRetryable = false;
        bool success = await RetryAsync(label, OfficeRetryCount, OfficeRetryDelay, async () =>
        {
            try
            {
                bool result = await _officeService.RunConfigureAsync(
                    request,
                    onStatus: line => { LogLine(label, line); if (TryParsePercent(line, out var pct)) progress?.Invoke(pct); },
                    cancellationToken: ct);

                officeRetryable = false;
                if (!result)
                {
                    error = "O instalador Office retornou falha";
                    _log($"[WinProvision] \"{label}\": instalação do Office retornou falha.");
                }

                return result;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log($"[WinProvision] \"{label}\": exceção durante Office ({ex.Message}).");
                error = ex.Message;
                officeRetryable = IsTransientException(ex);
                return false;
            }
        }, ct, shouldRetry: () => officeRetryable);

        _log(success
            ? $"[WinProvision] \"{label}\": OK."
            : $"[WinProvision] \"{label}\": FALHOU após {OfficeRetryCount} tentativa(s).");

        progress?.Invoke(100);
        return new OfficeInstallResult(success, installTimer.Elapsed, success ? null : Truncate(error, 240));
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
            if (manifest.Theme is { } theme && theme != SystemThemeMode.NaoDefinido) count++;
            if (manifest.TaskbarAlignment is { } alignment && alignment != TaskbarAlignmentMode.NaoDefinido) count++;
            if (manifest.TaskbarAutoHide is not null) count++;
            if (manifest.TaskbarSearchBox is { } search && search != TaskbarSearchBoxMode.NaoDefinido) count++;
            if (!string.IsNullOrWhiteSpace(manifest.WallpaperImageBase64)) count++;
            if (!string.IsNullOrWhiteSpace(manifest.Region)) count++;
        }
        else
        {
            if (manifest.PowerPlan is { } power && power != PowerPlanMode.NaoDefinido) count++;
            if (!string.IsNullOrWhiteSpace(manifest.MachineName)) count++;
            if (manifest.AutoCreateRestorePoint == true) count++;
            if (manifest.AutoCleanTempOnLogon == true) count++;
        }

        return count;
    }

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

    private static bool IsTransientException(Exception exception) =>
        exception is HttpRequestException or IOException or TimeoutException or TaskCanceledException;

    private static bool IsTransientProfileException(Exception exception) => exception switch
    {
        HttpRequestException { StatusCode: null } => true,
        HttpRequestException { StatusCode: { } status } when status == System.Net.HttpStatusCode.RequestTimeout
            || status == System.Net.HttpStatusCode.TooManyRequests
            || (int)status >= 500 => true,
        _ => exception is IOException or TimeoutException or TaskCanceledException
    };

    private static bool IsStoreProductId(string id) => id.Length == 9 && id.All(char.IsLetterOrDigit);

    private sealed record PackageInstallResult(bool Success, bool WingetUnavailable, string Source,
        string Method, TimeSpan Elapsed, string? Error);

    private sealed record OfficeInstallResult(bool Success, TimeSpan Elapsed, string? Error);

    private sealed record AutoItemResult(string Name, string Id, string Source, string Method,
        bool Success, TimeSpan Elapsed, string? Error);

    private static string FormatElapsed(TimeSpan elapsed) => elapsed.TotalMinutes >= 1
        ? $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds}s"
        : $"{elapsed.TotalSeconds:0.0}s";

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
