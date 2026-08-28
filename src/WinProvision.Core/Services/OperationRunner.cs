using System.Text.RegularExpressions;
using WinProvision.Core.Models;

namespace WinProvision.Core.Services;

/// <summary>
/// Enfileira e executa uma operação de instalação/remoção via winget, atualizando o
/// <see cref="OperationItem"/> correspondente (status, progresso, cancelamento) que o
/// painel flutuante (estilo UnigetUI) exibe em tempo real.
/// </summary>
public static partial class OperationRunner
{
    public static async Task<WingetExecutionResult> RunInstallAsync(
        OperationsQueueService queue,
        WingetExecutor executor,
        string appId,
        string appName,
        string? iconUrl = null,
        InstalledAppsService? installedAppsService = null)
    {
        var item = queue.Enqueue(appName, OperationKind.Install, iconUrl);
        item.State = OperationState.Running;
        item.StatusText = "Preparando...";

        try
        {
            var result = await executor.InstallAppAsync(
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
                installedAppsService?.MarkInstalled(appId);
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

    [GeneratedRegex(@"(\d{1,3})\s?%")]
    private static partial Regex PercentRegex();
}
