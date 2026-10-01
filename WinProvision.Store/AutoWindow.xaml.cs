using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using WinProvision.Core.Models;
using WinProvision.Core.Services;
using Wpf.Ui.Controls;
using Wpf.Ui.TaskBar;

namespace WinProvision.Store;

public partial class AutoWindow : FluentWindow
{
    private readonly AutoInstallCliService _cliService;
    private readonly AutoWindowViewModel _viewModel = new();
    private readonly TaskCompletionSource _closedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _globalProgressShimmerRunning;
    private double _globalProgressShimmerTrackWidth;
    private AutoInstallExitCode? _exitCode;

    public int CurrentProgress => (int)_viewModel.GlobalProgress;

    public void ShowCloudTracking(string viewUrl)
    {
        CloudTrackingUrlTextBox.Text = viewUrl;
        CloudTrackingQrImage.Source = CreateQrImage(viewUrl);
        PurposePanel.Visibility = Visibility.Collapsed;
        CloudTrackingPanel.Visibility = Visibility.Visible;
    }

    public AutoWindow(AutoInstallCliService cliService)
    {
        _cliService = cliService;
        DataContext = _viewModel;
        InitializeComponent();
        WindowState = WindowState.Maximized;
        Loaded += AutoWindow_Loaded;
        GlobalProgressTrack.SizeChanged += GlobalProgressTrack_SizeChanged;
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;

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
            manifest = await _cliService.ImportManifestWithRetryAsync(profileSource, log, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            log?.Invoke($"[WinProvision] ERRO ao carregar o perfil: {ex.Message}");
            _viewModel.BuildPlan(Array.Empty<AutoInstallStageInfo>(), null);
            _exitCode = AutoInstallExitCode.ProfileReadError;
            _viewModel.Finish(AutoInstallExitCode.ProfileReadError);
            UpdateTaskbarProgress();
            return AutoInstallExitCode.ProfileReadError;
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

        _exitCode = exitCode;
        _viewModel.Finish(exitCode);
        UpdateTaskbarProgress();
        return exitCode;
    }

    public Task WaitForCloseAsync() => _closedTcs.Task;

    private void AutoWindow_Loaded(object sender, RoutedEventArgs e)
        => UpdateGlobalProgressShimmer(restart: true);

    private void GlobalProgressTrack_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (Math.Abs(e.NewSize.Width - _globalProgressShimmerTrackWidth) > 1)
            UpdateGlobalProgressShimmer(restart: true);
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(AutoWindowViewModel.GlobalProgress)) return;

        UpdateTaskbarProgress();
        if (_viewModel.GlobalProgress >= 100)
        {
            StopGlobalProgressShimmer();
            return;
        }

        UpdateGlobalProgressShimmer();
    }

    private void UpdateTaskbarProgress()
    {
        if (_viewModel.GlobalProgress <= 0)
        {
            TaskBarProgress.SetState(this, TaskBarProgressState.Indeterminate);
            return;
        }

        TaskBarProgressState state = _exitCode switch
        {
            AutoInstallExitCode.Success => TaskBarProgressState.Normal,
            null => TaskBarProgressState.Normal,
            _ => TaskBarProgressState.Error
        };
        int progress = (int)Math.Clamp(Math.Round(_viewModel.GlobalProgress), 0, 100);
        TaskBarProgress.SetValue(this, state, progress, 100);
    }

    private void UpdateGlobalProgressShimmer(bool restart = false)
    {
        double trackWidth = GlobalProgressTrack.ActualWidth;
        if (_viewModel.GlobalProgress >= 100 || trackWidth <= 0)
        {
            StopGlobalProgressShimmer();
            return;
        }

        GlobalProgressShimmer.Visibility = Visibility.Visible;
        if (_globalProgressShimmerRunning && !restart) return;

        _globalProgressShimmerTrackWidth = trackWidth;
        _globalProgressShimmerRunning = true;

        double travelDistance = trackWidth + GlobalProgressShimmer.Width;
        double durationMs = Math.Clamp(travelDistance / 780d * 1000d, 1000d, 2800d);
        var animation = new DoubleAnimation(
            -GlobalProgressShimmer.Width,
            trackWidth,
            TimeSpan.FromMilliseconds(durationMs))
        {
            RepeatBehavior = RepeatBehavior.Forever
        };

        GlobalProgressShimmerTransform.BeginAnimation(
            TranslateTransform.XProperty,
            animation,
            HandoffBehavior.SnapshotAndReplace);
    }

    private void StopGlobalProgressShimmer()
    {
        _globalProgressShimmerRunning = false;
        GlobalProgressShimmerTransform.BeginAnimation(TranslateTransform.XProperty, null);
        GlobalProgressShimmer.Visibility = Visibility.Collapsed;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void CopyCloudTrackingUrl_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(CloudTrackingUrlTextBox.Text))
            Clipboard.SetText(CloudTrackingUrlTextBox.Text);
    }

    private void OpenCloudTrackingUrl_Click(object sender, RoutedEventArgs e)
    {
        if (Uri.TryCreate(CloudTrackingUrlTextBox.Text, UriKind.Absolute, out var uri))
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }

    private static ImageSource CreateQrImage(string url)
    {
        const int quietZone = 4;
        bool[,] modules = CloudLogSessionId.EncodeQr(url);
        int moduleCount = modules.GetLength(0);
        double imageSize = moduleCount + quietZone * 2;
        var qrGeometry = new StreamGeometry();
        using (StreamGeometryContext geometryContext = qrGeometry.Open())
        {
            for (int row = 0; row < moduleCount; row++)
            {
                for (int column = 0; column < moduleCount; column++)
                {
                    if (!modules[row, column]) continue;

                    double left = column + quietZone;
                    double top = row + quietZone;
                    geometryContext.BeginFigure(new Point(left, top), isFilled: true, isClosed: true);
                    geometryContext.LineTo(new Point(left + 1, top), isStroked: false, isSmoothJoin: false);
                    geometryContext.LineTo(new Point(left + 1, top + 1), isStroked: false, isSmoothJoin: false);
                    geometryContext.LineTo(new Point(left, top + 1), isStroked: false, isSmoothJoin: false);
                }
            }
        }

        qrGeometry.Freeze();

        var drawing = new DrawingGroup();
        using (DrawingContext drawingContext = drawing.Open())
        {
            // A margem de quatro módulos é necessária para leitores de QR; o desenho vetorial
            // mantém os módulos nítidos mesmo quando o painel muda de escala.
            drawingContext.DrawRectangle(Brushes.White, null, new Rect(0, 0, imageSize, imageSize));
            drawingContext.DrawGeometry(new SolidColorBrush(Color.FromRgb(24, 24, 24)), null, qrGeometry);
        }

        drawing.Freeze();
        var image = new DrawingImage(drawing);
        image.Freeze();
        return image;
    }

    protected override void OnClosed(EventArgs e)
    {
        Loaded -= AutoWindow_Loaded;
        GlobalProgressTrack.SizeChanged -= GlobalProgressTrack_SizeChanged;
        _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        StopGlobalProgressShimmer();
        _viewModel.Dispose();
        TaskBarProgress.SetState(this, TaskBarProgressState.None);
        _closedTcs.TrySetResult();
        base.OnClosed(e);
    }
}
