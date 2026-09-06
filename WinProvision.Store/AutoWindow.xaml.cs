using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Wpf.Ui.Controls;
using WinProvision.Core.Models;
using WinProvision.Core.Services;

namespace WinProvision.Store;

public partial class AutoWindow : FluentWindow
{
    private readonly AutoInstallCliService _cliService;
    private readonly AutoWindowViewModel _viewModel = new();
    private readonly TaskCompletionSource _closedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public AutoWindow(AutoInstallCliService cliService)
    {
        _cliService = cliService;
        DataContext = _viewModel;
        InitializeComponent();
        WindowState = WindowState.Maximized;

        // Quando o pipeline termina, o ViewModel conta 10s e avisa por aqui —
        // fecha a janela sozinha, sem esperar o clique em "Fechar". O fechamento
        // do PowerShell/console pai acontece depois, em App.xaml.cs, assim que
        // WaitForCloseAsync() liberar (ver NativeConsole.CloseParentConsole).
        _viewModel.AutoCloseElapsed += (_, _) =>
        {
            if (IsVisible) Close();
        };
    }

    public async Task<AutoInstallExitCode> RunAsync(string profileSource, Action<string>? log = null, CancellationToken ct = default)
    {
        ProfileManifest manifest;
        var progress = new Progress<AutoInstallStageEvent>(_viewModel.ApplyEvent);
        try
        {
            manifest = await _cliService.ImportManifestAsync(profileSource, ct);
        }
        catch
        {
            // Mesmo quando o perfil não pode ser importado, a UI deve terminar em um
            // estado coerente em vez de permanecer eternamente em 0%.
            _viewModel.BuildPlan(Array.Empty<AutoInstallStageInfo>(), null);
            var fallbackExitCode = await _cliService.RunAsync(profileSource, log, progress, ct);
            _viewModel.Finish(fallbackExitCode);
            return fallbackExitCode;
        }

        _viewModel.BuildPlan(AutoInstallCliService.PlanStages(manifest), manifest.Name);

        AutoInstallExitCode exitCode;
        try
        {
            exitCode = await _cliService.RunManifestAsync(manifest, profileSource, log, progress, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            log?.Invoke($"[WinProvision] ERRO inesperado: {ex.Message}");
            exitCode = AutoInstallExitCode.UnexpectedError;
        }

        _viewModel.Finish(exitCode);
        return exitCode;
    }

    public Task WaitForCloseAsync() => _closedTcs.Task;

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.Dispose();
        _closedTcs.TrySetResult();
        base.OnClosed(e);
    }
}
