using System.Runtime.Versioning;
using WinProvision.Core.Models.Provisioning;
using WinProvision.Core.Services;
using WinProvision.Core.Services.Backup;

namespace WinProvision.Core.Services.Provisioning;

/// <summary>
/// Ponta de entrada da linha de comando (<c>WinProvision.Store.exe /Provision
/// caminho\para\perfil.json</c>) — lê um perfil de provisionamento (.json) exportado pela tela
/// "Provisionamento" e aplica todos os ajustes que ele descreve (tema, barra de tarefas, plano
/// de energia, nome da máquina) sem nenhum prompt/janela, pra uso em provisionamento
/// automatizado (ex.: chamado de dentro de uma task sequence).
///
/// Mesma estrutura do <see cref="AutoInstallCliService"/> (modo /auto): não depende de nenhuma
/// peça de UI, roda com a MainWindow nunca sendo criada (ver App.xaml.cs) e reporta progresso
/// via o delegate de log (Console por padrão, ou um CliFileLogger.Log).
/// </summary>
[SupportedOSPlatform("windows")]
public class ProvisionCliService
{
    private readonly ProvisioningService _provisioningService;
    private readonly BackupAutoSyncService _backupSyncService;

    private Action<string> _log = Console.WriteLine;

    public ProvisionCliService(ProvisioningService provisioningService, BackupAutoSyncService backupSyncService)
    {
        _provisioningService = provisioningService;
        _backupSyncService = backupSyncService;
    }

    /// <summary>
    /// Executa o perfil de ponta a ponta.
    /// </summary>
    /// <param name="profilePath">Caminho local do perfil .json, ou uma URL http(s) direta pro conteúdo (ex.: link "raw" de Gist).</param>
    /// <param name="log">
    /// Sink de log opcional — recebe cada linha já formatada. Se omitido, cai de volta pra
    /// Console.WriteLine. Passe <see cref="CliFileLogger"/>.Log aqui pra também gravar em arquivo.
    /// </param>
    /// <returns>Código de saída detalhado — ver <see cref="ProvisioningExitCode"/>.</returns>
    public async Task<ProvisioningExitCode> RunAsync(string profilePath, Action<string>? log = null, CancellationToken ct = default)
    {
        _log = log ?? Console.WriteLine;

        bool isUrl = ProfileSourceReader.IsHttpUrl(profilePath);

        if (!isUrl && !File.Exists(profilePath))
        {
            _log($"[WinProvision] ERRO: perfil de provisionamento não encontrado em '{profilePath}'.");
            return ProvisioningExitCode.ProfileNotFound;
        }

        _log(isUrl
            ? $"[WinProvision] Baixando perfil de provisionamento: {profilePath}"
            : $"[WinProvision] Lendo perfil de provisionamento: {profilePath}");

        ProvisioningManifest manifest;
        try
        {
            manifest = await _provisioningService.ImportAsync(profilePath, ct);
        }
        catch (Exception ex)
        {
            _log($"[WinProvision] ERRO ao ler o perfil: {ex.Message}");
            return ProvisioningExitCode.ProfileReadError;
        }

        try
        {
            string profileLabel = manifest.Name ?? Path.GetFileNameWithoutExtension(profilePath);
            _log($"[WinProvision] Perfil de provisionamento \"{profileLabel}\" carregado.");

            var result = await _provisioningService.ApplyAsync(manifest, _log, ct);

            if (result.Steps.Count == 0)
            {
                _log("[WinProvision] Nada a fazer (perfil não define nenhum ajuste).");
                return ProvisioningExitCode.Success;
            }

            int succeeded = result.Steps.Count(s => s.Success);
            int failed = result.Steps.Count - succeeded;

            // Sincroniza o backup (local + Gist, se conectado) explicitamente aqui, sem
            // esperar o debounce de BackupAutoSyncService — o processo CLI encerra
            // (Shutdown) logo após este método retornar, então esperar o debounce padrão
            // faria o backup nunca rodar. Melhor-esforço: uma falha aqui (ex.: sem
            // internet) nunca deve mudar o código de saída desta execução.
            try
            {
                await _backupSyncService.RunSyncAsync(ct);
            }
            catch
            {
                // ignorado — mesma filosofia de melhor-esforço do próprio BackupAutoSyncService
            }

            _log(failed == 0
                ? $"[WinProvision] Concluído: {succeeded} ajuste(s) aplicado(s) com sucesso."
                : $"[WinProvision] Concluído com falhas: {succeeded} sucesso(s), {failed} falha(s).");

            if (result.RestartRequired)
            {
                _log("[WinProvision] AVISO: reinicie o Windows para que todos os ajustes (ex.: nome da máquina) tenham efeito.");
            }

            return failed == 0 ? ProvisioningExitCode.Success : ProvisioningExitCode.CompletedWithFailures;
        }
        catch (Exception ex)
        {
            _log($"[WinProvision] ERRO inesperado: {ex.Message}");
            return ProvisioningExitCode.UnexpectedError;
        }
    }
}
