using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using WinProvision.Core.Models;
using WinProvision.Core.Services;
using Wpf.Ui.Controls;

namespace WinProvision.Store;

public partial class AutoWindow : FluentWindow
{
    private readonly AutoInstallCliService _cliService;
    private readonly AutoWindowViewModel _viewModel = new();
    private readonly TaskCompletionSource _closedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _globalProgressShimmerRunning;
    private double _globalProgressShimmerTrackWidth;

    public int CurrentProgress => (int)_viewModel.GlobalProgress;

    public void ShowCloudTracking(string viewUrl)
    {
        CloudTrackingUrlTextBox.Text = viewUrl;
        CloudTrackingQrImage.Source = CreateQrBitmap(viewUrl);
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
            _viewModel.Finish(AutoInstallExitCode.ProfileReadError);
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

        _viewModel.Finish(exitCode);
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

        if (_viewModel.GlobalProgress >= 100)
        {
            StopGlobalProgressShimmer();
            return;
        }

        UpdateGlobalProgressShimmer();
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

    private static BitmapSource CreateQrBitmap(string url)
    {
        const int quietZone = 4;
        const int moduleScale = 6;
        bool[,] modules = CloudLogSessionId.EncodeQr(url);
        int moduleCount = modules.GetLength(0);
        int pixelSize = (moduleCount + quietZone * 2) * moduleScale;
        int stride = pixelSize * 4;
        byte[] pixels = new byte[stride * pixelSize];

        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 255;
            pixels[i + 1] = 255;
            pixels[i + 2] = 255;
            pixels[i + 3] = 255;
        }

        for (int row = 0; row < moduleCount; row++)
        {
            for (int column = 0; column < moduleCount; column++)
            {
                if (!modules[row, column]) continue;

                int startX = (column + quietZone) * moduleScale;
                int startY = (row + quietZone) * moduleScale;
                for (int y = startY; y < startY + moduleScale; y++)
                {
                    for (int x = startX; x < startX + moduleScale; x++)
                    {
                        int offset = y * stride + x * 4;
                        pixels[offset] = 0;
                        pixels[offset + 1] = 0;
                        pixels[offset + 2] = 0;
                    }
                }
            }
        }

        var bitmap = new WriteableBitmap(pixelSize, pixelSize, 96, 96, PixelFormats.Bgra32, null);
        bitmap.WritePixels(new Int32Rect(0, 0, pixelSize, pixelSize), pixels, stride, 0);
        bitmap.Freeze();
        return bitmap;
    }

    protected override void OnClosed(EventArgs e)
    {
        Loaded -= AutoWindow_Loaded;
        GlobalProgressTrack.SizeChanged -= GlobalProgressTrack_SizeChanged;
        _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        StopGlobalProgressShimmer();
        _viewModel.Dispose();
        _closedTcs.TrySetResult();
        base.OnClosed(e);
    }
}
