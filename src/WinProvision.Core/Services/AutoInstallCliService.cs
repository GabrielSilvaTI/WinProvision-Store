using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WinProvision.Core.Models;
using WinProvision.Core.Models.Office;
using WinProvision.Core.Services.Office;
using WinProvision.Core.Services.Profile;

namespace WinProvision.Core.Services;

/// <summary>
/// Ponta de entrada da instalação via linha de comando (ex.: <c>WinProvision.Store.exe /auto
/// caminho\para\perfil.json</c>) — lê um perfil (.json) exportado pela tela Pacotes e instala
/// tudo que ele descreve (apps winget + planos de Office) sem nenhum prompt/diálogo, prá uso
/// em provisionamento automatizado (ex.: chamado de dentro de uma task sequence, ou de dentro
/// do WinProvision principal via Process.Start).
///
/// Não depende de nenhuma peça de UI (OperationsQueueService/janelas) de propósito — isso
/// roda com a MainWindow nunca sendo criada (ver App.xaml.cs), então tudo aqui fala direto
/// com WingetExecutor/OfficeDeploymentToolService e reporta progresso via o delegate de log
/// (que por padrão só escreve no Console, mas quem chamar pode passar o próprio sink —
/// ex.: um CliFileLogger.Log, ou até um callback que empurra as linhas pra UI do
/// WinProvision principal).
/// </summary>
public class AutoInstallCliService
{
    private readonly ProfileService _profileService;
    private readonly StoreService _storeService;
    private readonly WingetExecutor _wingetExecutor;
    private readonly OfficeDeploymentToolService _officeService;

    private Action<string> _log = Console.WriteLine;

    public AutoInstallCliService(
        ProfileService profileService,
        StoreService storeService,
        WingetExecutor wingetExecutor,
        OfficeDeploymentToolService officeService)
    {
        _profileService = profileService;
        _storeService = storeService;
        _wingetExecutor = wingetExecutor;
        _officeService = officeService;
    }

    /// <summary>
    /// Executa o perfil de ponta a ponta.
    /// </summary>
    /// <param name="profilePath">Caminho do perfil .json a instalar.</param>
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
    public async Task<AutoInstallExitCode> RunAsync(string profilePath, Action<string>? log = null, CancellationToken ct = default)
    {
        _log = log ?? Console.WriteLine;

        if (!File.Exists(profilePath))
        {
            _log($"[WinProvision] ERRO: perfil não encontrado em '{profilePath}'.");
            return AutoInstallExitCode.ProfileNotFound;
        }

        _log($"[WinProvision] Lendo perfil: {profilePath}");

        ProfileManifest manifest;
        try
        {
            manifest = await _profileService.ImportAsync(profilePath, ct);
        }
        catch (Exception ex)
        {
            _log($"[WinProvision] ERRO ao ler o perfil: {ex.Message}");
            return AutoInstallExitCode.ProfileReadError;
        }

        try
        {
            return await RunManifestAsync(manifest, profilePath, ct);
        }
        catch (Exception ex)
        {
            _log($"[WinProvision] ERRO inesperado: {ex.Message}");
            return AutoInstallExitCode.UnexpectedError;
        }
    }

    private async Task<AutoInstallExitCode> RunManifestAsync(ProfileManifest manifest, string profilePath, CancellationToken ct)
    {
        string profileLabel = manifest.Name ?? Path.GetFileNameWithoutExtension(profilePath);
        _log($"[WinProvision] Perfil \"{profileLabel}\" — {manifest.Apps.Count} item(ns) a instalar.");

        if (manifest.Apps.Count == 0)
        {
            _log("[WinProvision] Nada a fazer (perfil vazio).");
            return AutoInstallExitCode.Success;
        }

        // Carrega o catálogo remoto uma vez só, só pra resolver o nome bonito dos apps
        // winget no log — se falhar (sem rede, por ex.), segue instalando pelo Id mesmo.
        List<AppEntry> catalog;
        try
        {
            catalog = await _storeService.LoadCatalogAsync(false, ct);
        }
        catch
        {
            catalog = new List<AppEntry>();
        }

        int succeeded = 0;
        int failed = 0;

        foreach (var appRef in manifest.Apps)
        {
            bool ok = appRef.OfficeOptions is { } officeOptions
                ? await InstallOfficeAsync(appRef, officeOptions, ct)
                : await InstallWingetAsync(appRef, catalog, ct);

            if (ok) succeeded++; else failed++;
        }

        _log(failed == 0
            ? $"[WinProvision] Concluído: {succeeded} item(ns) instalado(s) com sucesso."
            : $"[WinProvision] Concluído com falhas: {succeeded} sucesso(s), {failed} falha(s).");

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
