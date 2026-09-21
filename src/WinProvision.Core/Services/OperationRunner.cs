using System.Text.RegularExpressions;
using WinProvision.Core.Models;
using WinProvision.Core.Models.Office;
using WinProvision.Core.Services.Office;

namespace WinProvision.Core.Services;

/// <summary>
/// Enfileira e executa uma operação de instalação/remoção via winget, atualizando o
/// <see cref="OperationItem"/> correspondente (status, progresso, cancelamento) que o
/// painel flutuante (estilo UnigetUI) exibe em tempo real.
/// </summary>
public static partial class OperationRunner
{
    private static Func<string, Action<string>?, CancellationToken, string?, Action<InstallProgressUpdate>?, string, Task<WingetExecutionResult>>? _installHandler;

    public static void ConfigureInstallHandler(
        Func<string, Action<string>?, CancellationToken, string?, Action<InstallProgressUpdate>?, string, Task<WingetExecutionResult>> installHandler)
    {
        _installHandler = installHandler ?? throw new ArgumentNullException(nameof(installHandler));
    }

    /// <summary>
    /// Instala usando o handler configurado (API COM do WinGet, com fallback interno para a
    /// CLI). Sem handler configurado, usa o <see cref="WingetExecutor"/> diretamente. Usado
    /// também pelo modo /auto, para que ele não rode sempre o winget.exe.
    /// </summary>
    public static Task<WingetExecutionResult> InstallWithConfiguredHandlerAsync(
        WingetExecutor executor,
        string appId,
        Action<string>? onLogReceived,
        CancellationToken cancellationToken,
        string? installLocation = null,
        Action<InstallProgressUpdate>? onProgress = null,
        string source = "winget")
    {
        return _installHandler is null
            ? executor.InstallAppAsync(appId, onLogReceived, cancellationToken, installLocation, source)
            : _installHandler(appId, onLogReceived, cancellationToken, installLocation, onProgress, source);
    }

    public static async Task<WingetExecutionResult> RunInstallAsync(
        OperationsQueueService queue,
        WingetExecutor executor,
        string appId,
        string appName,
        string? iconUrl = null,
        InstalledAppsService? installedAppsService = null,
        string? installLocation = null,
        string source = "winget")
    {
        var item = queue.Enqueue(appName, OperationKind.Install, iconUrl);
        item.State = OperationState.Running;
        item.StatusText = "Preparando...";

        try
        {
            var onLogReceived = new Action<string>(line => ReportProgress(item, line));
            var onProgress = new Action<InstallProgressUpdate>(update => ReportInstallProgress(item, update));
            var result = _installHandler is null
                ? await executor.InstallAppAsync(
                    appId,
                    onLogReceived,
                    item.CancellationTokenSource.Token,
                    installLocation,
                    source)
                : await _installHandler(
                    appId,
                    onLogReceived,
                    item.CancellationTokenSource.Token,
                    installLocation,
                    onProgress,
                    source);

            if (result.Success)
            {
                item.IsIndeterminate = false;
                item.Progress = 100;
            }
            item.State = result.Success
                ? OperationState.Completed
                : item.CancellationTokenSource.IsCancellationRequested
                    ? OperationState.Canceled
                    : OperationState.Failed;

            if (result.Success)
            {
                installedAppsService?.MarkInstalled(appId);
            }
            else if (item.State == OperationState.Failed)
            {
                item.StatusText = WingetErrorTranslator.ToMessage(result.FailureReason, "instalar", appName);
            }

            return result;
        }
        catch (Exception)
        {
            item.State = item.CancellationTokenSource.IsCancellationRequested
                ? OperationState.Canceled
                : OperationState.Failed;
            throw;
        }
    }

    /// <summary>
    /// Mesmo padrão de <see cref="RunInstallAsync"/>, só que para atualização de um pacote
    /// já instalado (winget update) — usado pela tela Atualizações. OperationKind.Update já
    /// existia no painel de fila (rótulo "Atualizando"), então isso só reaproveita a mesma
    /// exibição sem precisar de nada novo lá.
    /// </summary>
    public static async Task<WingetExecutionResult> RunUpdateAsync(
        OperationsQueueService queue,
        WingetExecutor executor,
        string appId,
        string appName,
        string? iconUrl = null,
        string source = "winget")
    {
        var item = queue.Enqueue(appName, OperationKind.Update, iconUrl);
        item.State = OperationState.Running;
        item.StatusText = "Preparando atualização...";

        try
        {
            var result = await executor.UpdateAppAsync(
                appId,
                onLogReceived: line => ReportProgress(item, line),
                cancellationToken: item.CancellationTokenSource.Token,
                source: source);

            item.Progress = 100;
            item.State = result.Success
                ? OperationState.Completed
                : item.CancellationTokenSource.IsCancellationRequested
                    ? OperationState.Canceled
                    : OperationState.Failed;

            if (!result.Success && item.State == OperationState.Failed)
            {
                item.StatusText = WingetErrorTranslator.ToMessage(result.FailureReason, "atualizar", appName);
            }

            return result;
        }
        catch (Exception)
        {
            item.State = item.CancellationTokenSource.IsCancellationRequested
                ? OperationState.Canceled
                : OperationState.Failed;
            throw;
        }
    }

    /// <summary>
    /// Mesmo padrão de <see cref="RunInstallAsync"/>, só que para remoção: enfileira no
    /// painel flutuante, executa via WingetExecutor.UninstallAppAsync (winget uninstall
    /// silencioso) e, em caso de sucesso, marca o app como não-instalado no cache
    /// compartilhado (InstalledAppsService) para o botão da UI voltar a "Instalar".
    /// </summary>
    public static async Task<WingetExecutionResult> RunUninstallAsync(
        OperationsQueueService queue,
        WingetExecutor executor,
        string appId,
        string appName,
        string? iconUrl = null,
        InstalledAppsService? installedAppsService = null)
    {
        var item = queue.Enqueue(appName, OperationKind.Uninstall, iconUrl);
        item.State = OperationState.Running;
        item.StatusText = "Preparando remoção...";

        try
        {
            var result = await executor.UninstallAppAsync(
                appId,
                onLogReceived: line => ReportProgress(item, line),
                cancellationToken: item.CancellationTokenSource.Token);

            item.Progress = 100;
            item.State = result.Success
                ? OperationState.Completed
                : item.CancellationTokenSource.IsCancellationRequested
                    ? OperationState.Canceled
                    : OperationState.Failed;

            if (result.Success)
            {
                installedAppsService?.MarkUninstalled(appId);
            }
            else if (item.State == OperationState.Failed)
            {
                // Sobrescreve a última linha crua do winget (ReportProgress deixou em
                // item.StatusText) por uma mensagem fixa em pt-BR — sem isso, o painel de
                // fila fica mostrando o texto do winget tal como ele imprimiu, que às vezes
                // sai em inglês e às vezes em português dependendo da mensagem específica.
                item.StatusText = WingetErrorTranslator.ToMessage(result.FailureReason, "desinstalar", appName);
            }

            return result;
        }
        catch (Exception)
        {
            item.State = item.CancellationTokenSource.IsCancellationRequested
                ? OperationState.Canceled
                : OperationState.Failed;
            throw;
        }
    }

    /// <summary>
    /// Mesmo padrão de enfileiramento do RunInstallAsync, só que para o pipeline do
    /// Office (ODT + configuration.xml) em vez do winget. Sem parsing de percentual —
    /// o setup.exe do ODT não imprime progresso incremental, então o item fica
    /// indeterminado até terminar.
    /// </summary>
    public static async Task<bool> RunOfficeInstallAsync(
        OperationsQueueService queue,
        OfficeDeploymentToolService officeService,
        OfficeInstallRequest request)
    {
        var item = queue.Enqueue(request.Plan.DisplayName, OperationKind.Install);
        item.State = OperationState.Running;
        item.StatusText = "Preparando...";

        try
        {
            bool success = await officeService.RunConfigureAsync(
                request,
                onStatus: line => item.StatusText = line,
                cancellationToken: item.CancellationTokenSource.Token);

            item.Progress = 100;
            item.State = success
                ? OperationState.Completed
                : item.CancellationTokenSource.IsCancellationRequested
                    ? OperationState.Canceled
                    : OperationState.Failed;

            return success;
        }
        catch (Exception)
        {
            item.State = item.CancellationTokenSource.IsCancellationRequested
                ? OperationState.Canceled
                : OperationState.Failed;
            throw;
        }
    }

    /// <summary>
    /// Mesmo padrão de RunOfficeInstallAsync, para o pipeline de remoção (ODT +
    /// configuration.xml com &lt;Remove&gt;/RemoveAll, mais limpeza opcional da edição
    /// Microsoft Store).
    /// </summary>
    public static async Task<bool> RunOfficeRemoveAsync(
        OperationsQueueService queue,
        OfficeDeploymentToolService officeService,
        OfficeRemoveRequest request,
        string label)
    {
        var item = queue.Enqueue(label, OperationKind.Uninstall);
        item.State = OperationState.Running;
        item.StatusText = "Preparando...";

        try
        {
            bool success = await officeService.RunRemoveAsync(
                request,
                onStatus: line => item.StatusText = line,
                cancellationToken: item.CancellationTokenSource.Token);

            item.Progress = 100;
            item.State = success
                ? OperationState.Completed
                : item.CancellationTokenSource.IsCancellationRequested
                    ? OperationState.Canceled
                    : OperationState.Failed;

            return success;
        }
        catch (Exception)
        {
            item.State = item.CancellationTokenSource.IsCancellationRequested
                ? OperationState.Canceled
                : OperationState.Failed;
            throw;
        }
    }

    /// <summary>Enfileira uma verificação/aplicação de atualizações do Click-to-Run (OfficeC2RClient.exe /update user).</summary>
    public static async Task<bool> RunOfficeUpdateCheckAsync(
        OperationsQueueService queue,
        OfficeDeploymentToolService officeService,
        bool silent = true)
    {
        var item = queue.Enqueue("Verificar atualizações do Office", OperationKind.Update);
        item.State = OperationState.Running;
        item.StatusText = "Preparando...";

        try
        {
            bool success = await officeService.RunUpdateNowAsync(
                silent,
                onStatus: line => item.StatusText = line,
                cancellationToken: item.CancellationTokenSource.Token);

            item.Progress = 100;
            item.State = success
                ? OperationState.Completed
                : item.CancellationTokenSource.IsCancellationRequested
                    ? OperationState.Canceled
                    : OperationState.Failed;

            return success;
        }
        catch (Exception)
        {
            item.State = item.CancellationTokenSource.IsCancellationRequested
                ? OperationState.Canceled
                : OperationState.Failed;
            throw;
        }
    }

    /// <summary>Enfileira a aplicação isolada da política &lt;Updates Enabled="TRUE|FALSE"/&gt;, sem reinstalar nada.</summary>
    public static async Task<bool> RunOfficeSetAutoUpdateAsync(
        OperationsQueueService queue,
        OfficeDeploymentToolService officeService,
        bool enabled)
    {
        var item = queue.Enqueue(enabled ? "Ativar atualizações automáticas do Office" : "Desativar atualizações automáticas do Office", OperationKind.Update);
        item.State = OperationState.Running;
        item.StatusText = "Preparando...";

        try
        {
            bool success = await officeService.RunSetAutoUpdateAsync(
                enabled,
                onStatus: line => item.StatusText = line,
                cancellationToken: item.CancellationTokenSource.Token);

            item.Progress = 100;
            item.State = success
                ? OperationState.Completed
                : item.CancellationTokenSource.IsCancellationRequested
                    ? OperationState.Canceled
                    : OperationState.Failed;

            return success;
        }
        catch (Exception)
        {
            item.State = item.CancellationTokenSource.IsCancellationRequested
                ? OperationState.Canceled
                : OperationState.Failed;
            throw;
        }
    }

    private static void ReportProgress(OperationItem item, string logLine)
    {
        if (string.IsNullOrWhiteSpace(logLine))
        {
            return;
        }

        item.StatusText = logLine.Trim();

        // O winget imprime o progresso do download/instalação como "NN%" em várias
        // linhas da barra de progresso do console; quando encontramos um percentual,
        // saímos do modo indeterminado e passamos a mostrar o valor real.
        var match = PercentRegex().Match(logLine);
        if (match.Success && int.TryParse(match.Groups[1].Value, out int percent))
        {
            item.IsIndeterminate = false;
            item.Progress = Math.Clamp(percent, 0, 100);
        }
    }

    private static void ReportInstallProgress(OperationItem item, InstallProgressUpdate update)
    {
        switch (update.Phase)
        {
            case InstallProgressPhase.Downloading when update.Percent is int percent:
                item.IsIndeterminate = false;
                item.Progress = Math.Clamp(percent, 0, 100);
                item.StatusText = $"Baixando... {item.Progress:0}%";
                break;
            case InstallProgressPhase.Preparing:
                item.IsIndeterminate = true;
                item.StatusText = "Verificando e preparando a instalação...";
                break;
            case InstallProgressPhase.Installing when update.Percent is int installPercent && installPercent > 0:
                item.IsIndeterminate = false;
                item.Progress = Math.Clamp(installPercent, 0, 100);
                item.StatusText = $"Instalando... {item.Progress:0}%";
                break;
            case InstallProgressPhase.Installing:
                item.IsIndeterminate = true;
                item.StatusText = "Instalando...";
                break;
        }
    }

    [GeneratedRegex(@"(\d{1,3})\s?%")]
    private static partial Regex PercentRegex();
}
