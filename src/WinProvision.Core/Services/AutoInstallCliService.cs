using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
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

        bool hasPackages = manifest.Apps.Any(a => a.OfficeOptions is null);
        bool hasOffice = manifest.Apps.Any(a => a.OfficeOptions is not null);

        if (hasPackages) stages.Add(StageCatalog[AutoInstallStage.PackagesAndApps]);
        if (hasOffice) stages.Add(StageCatalog[AutoInstallStage.MicrosoftOffice]);

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
                || provisioning.AutoInstallWindowsUpdates == true
                || provisioning.AutoCreateRestorePoint == true
                || provisioning.AutoCleanTempOnLogon == true;

            if (hasPersonalization) stages.Add(StageCatalog[AutoInstallStage.SystemPersonalization]);
            if (hasConfigurations) stages.Add(StageCatalog[AutoInstallStage.PredefinedConfigurations]);
        }

        return stages;
    }

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
    public async Task<AutoInstallExitCode> RunAsync(
        string profileSource,
        Action<string>? log = null,
        IProgress<AutoInstallStageEvent>? stageProgress = null,
        CancellationToken ct = default)
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
        if (isUrl)
        {
            // Para URLs, tentamos até ProfileDownloadRetryCount vezes antes de desistir —
            // no primeiro logon a rede pode ainda estar estabilizando (DHCP, DNS, VPN).
            ProfileManifest? downloaded = null;
            Exception? lastEx = null;
            for (int attempt = 1; attempt <= ProfileDownloadRetryCount; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                if (attempt > 1)
                {
                    _log($"[WinProvision] Tentando baixar o perfil novamente (tentativa {attempt}/{ProfileDownloadRetryCount}, aguardando {ProfileDownloadRetryDelay.TotalSeconds:0}s)…");
                    await Task.Delay(ProfileDownloadRetryDelay, ct);
                }
                try
                {
                    downloaded = await ImportManifestAsync(profileSource, ct);
                    break;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    lastEx = ex;
                    if (attempt < ProfileDownloadRetryCount)
                        _log($"[WinProvision] Falha ao baixar o perfil (tentativa {attempt}/{ProfileDownloadRetryCount}): {ex.Message}");
                }
            }

            if (downloaded is null)
            {
                _log($"[WinProvision] ERRO ao baixar o perfil após {ProfileDownloadRetryCount} tentativa(s): {lastEx?.Message}");
                return AutoInstallExitCode.ProfileReadError;
            }

            manifest = downloaded;
        }
        else
        {
            try
            {
                manifest = await ImportManifestAsync(profileSource, ct);
            }
            catch (Exception ex)
            {
                _log($"[WinProvision] ERRO ao ler o perfil: {ex.Message}");
                return AutoInstallExitCode.ProfileReadError;
            }
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

        // Checa o winget uma única vez antes de qualquer app/Office.
        if (manifest.Apps.Count > 0)
        {
            var bootstrapResult = await _wingetBootstrapper.EnsureWingetAsync(_log, ct);
            if (!bootstrapResult.IsUsable)
            {
                _log($"[WinProvision] ERRO: winget não está disponível e o bootstrap automático não conseguiu " +
                     $"deixá-lo funcional ({bootstrapResult.ErrorMessage}). Pulando {manifest.Apps.Count} " +
                     "item(ns) de app/Office — todos dependem do winget.");
                failed += manifest.Apps.Count;
                wingetUnavailable = true;

                if (stages.Contains(AutoInstallStage.PackagesAndApps)) Stage(AutoInstallStage.PackagesAndApps, AutoInstallStageState.Failed);
                if (stages.Contains(AutoInstallStage.MicrosoftOffice)) Stage(AutoInstallStage.MicrosoftOffice, AutoInstallStageState.Failed);
            }
        }

        List<AppEntry> catalog = new();
        if (!wingetUnavailable && manifest.Apps.Count > 0)
        {
            try { catalog = await _storeService.LoadCatalogAsync(false, ct); }
            catch { /* segue pelo Id */ }
        }

        if (!wingetUnavailable)
        {
            var packageApps = manifest.Apps.Where(a => a.OfficeOptions is null).ToList();
            var officeApps = manifest.Apps.Where(a => a.OfficeOptions is not null).ToList();

            if (packageApps.Count > 0)
            {
                // Progresso em degraus: cada app concluído preenche 1/N da barra (25% pra
                // 4 apps, e assim por diante). Não tentamos mais acompanhar o percentual
                // interno do winget (baseProgress + p/N) — a saída dele quando redirecionada
                // chega em rajadas, então aquele percentual só fazia a barra parecer travada
                // e pular de vez; o degrau por item concluído é o dado confiável que temos.
                // Enquanto o item atual instala, a barra fica no degrau anterior (a
                // AutoWindowViewModel mostra "indeterminado" só no primeiro item, com 0
                // concluído — ver AutoStageViewModel.IsIndeterminate).
                StageProgress(AutoInstallStage.PackagesAndApps, 0, $"Preparando {packageApps.Count} pacote(s)…");
                bool allSucceeded = true;
                for (int i = 0; i < packageApps.Count; i++)
                {
                    var appRef = packageApps[i];
                    string label = appRef.Name ?? appRef.Id;
                    if (i > 0) StageProgress(AutoInstallStage.PackagesAndApps, i * 100d / packageApps.Count, $"Instalando {label}…");
                    bool ok = await InstallWingetAsync(appRef, catalog, ct);
                    allSucceeded &= ok;
                    if (ok) succeeded++; else failed++;
                    StageProgress(AutoInstallStage.PackagesAndApps, (i + 1) * 100d / packageApps.Count, ok ? $"Instalado {label}" : $"Falhou: {label}");
                }
                Stage(AutoInstallStage.PackagesAndApps, allSucceeded ? AutoInstallStageState.Completed : AutoInstallStageState.Failed, 100);
            }

            if (officeApps.Count > 0)
            {
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
                    bool ok = await InstallOfficeAsync(appRef, appRef.OfficeOptions!, ct);
                    allSucceeded &= ok;
                    if (ok) succeeded++; else failed++;
                    StageProgress(AutoInstallStage.MicrosoftOffice, (i + 1) * 100d / officeApps.Count, ok ? $"Instalado {label}" : $"Falhou: {label}");
                }
                Stage(AutoInstallStage.MicrosoftOffice, allSucceeded ? AutoInstallStageState.Completed : AutoInstallStageState.Failed, 100);
            }
        }

        if (manifest.Provisioning is { } provisioning)
        {
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
        }

        try { await _backupSyncService.RunSyncAsync(ct); }
        catch { }

        _log(failed == 0
            ? $"[WinProvision] Concluído: {succeeded} item(ns) instalado(s)/aplicado(s) com sucesso."
            : $"[WinProvision] Concluído com falhas: {succeeded} sucesso(s), {failed} falha(s).");

        if (restartRequired)
            _log("[WinProvision] AVISO: reinicie o Windows para que todos os ajustes de provisionamento tenham efeito.");

        return wingetUnavailable
            ? AutoInstallExitCode.WingetUnavailable
            : failed == 0 ? AutoInstallExitCode.Success : AutoInstallExitCode.CompletedWithFailures;
    }

    private async Task<bool> InstallWingetAsync(ProfileAppRef appRef, List<AppEntry> catalog, CancellationToken ct, Action<double>? progress = null)
    {
        string displayName = catalog.FirstOrDefault(a => string.Equals(a.Id, appRef.Id, StringComparison.OrdinalIgnoreCase))?.Name
            ?? appRef.Id;

        _log($"[WinProvision] Instalando \"{displayName}\" ({appRef.Id})…");

        bool success = await RetryAsync(displayName, WingetRetryCount, WingetRetryDelay, async () =>
        {
            try
            {
                var result = await _wingetExecutor.InstallAppAsync(
                    appRef.Id,
                    onLogReceived: line => { LogLine(displayName, line); if (TryParsePercent(line, out var pct)) progress?.Invoke(pct); },
                    cancellationToken: ct);

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
                _log($"[WinProvision] \"{displayName}\": exceção ({ex.Message}).");
                return false;
            }
        }, ct);

        _log(success
            ? $"[WinProvision] \"{displayName}\": OK."
            : $"[WinProvision] \"{displayName}\": FALHOU após {WingetRetryCount} tentativa(s).");

        progress?.Invoke(100);
        return success;
    }

    private async Task<bool> InstallOfficeAsync(ProfileAppRef appRef, OfficeInstallOptions options, CancellationToken ct, Action<double>? progress = null)
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

        _log($"[WinProvision] Instalando \"{label}\" (Office/ODT)…");

        bool success = await RetryAsync(label, OfficeRetryCount, OfficeRetryDelay, async () =>
        {
            try
            {
                bool result = await _officeService.RunConfigureAsync(
                    request,
                    onStatus: line => { LogLine(label, line); if (TryParsePercent(line, out var pct)) progress?.Invoke(pct); },
                    cancellationToken: ct);

                if (!result)
                    _log($"[WinProvision] \"{label}\": instalação do Office retornou falha.");

                return result;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log($"[WinProvision] \"{label}\": exceção durante Office ({ex.Message}).");
                return false;
            }
        }, ct);

        _log(success
            ? $"[WinProvision] \"{label}\": OK."
            : $"[WinProvision] \"{label}\": FALHOU após {OfficeRetryCount} tentativa(s).");

        progress?.Invoke(100);
        return success;
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
            if (manifest.AutoInstallWindowsUpdates == true) count++;
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
        CancellationToken ct)
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

                if (attempt < maxAttempts)
                    _log($"[WinProvision] \"{label}\": falhou (tentativa {attempt}/{maxAttempts}), tentando novamente…");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (attempt < maxAttempts)
                    _log($"[WinProvision] \"{label}\": erro ({ex.Message}) na tentativa {attempt}/{maxAttempts}, tentando novamente…");
                else
                    _log($"[WinProvision] \"{label}\": erro ({ex.Message}) — sem mais tentativas.");
            }
        }

        return false;
    }
}
