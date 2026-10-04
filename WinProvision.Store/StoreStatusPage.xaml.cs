using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WinProvision.Core.Services;

namespace WinProvision.Store;

public partial class StoreStatusPage : Page
{
    private readonly WingetBootstrapper _wingetBootstrapper;
    private readonly WinProvisionApiService _apiService;
    private CancellationTokenSource? _checkCancellation;
    private string _diagnosticText = "";

    public StoreStatusPage(WingetBootstrapper wingetBootstrapper, WinProvisionApiService apiService)
    {
        InitializeComponent();
        _wingetBootstrapper = wingetBootstrapper;
        _apiService = apiService;
        Loaded += StoreStatusPage_Loaded;
        Unloaded += StoreStatusPage_Unloaded;
    }

    private async void StoreStatusPage_Loaded(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_diagnosticText))
            await RefreshAsync();
    }

    private void StoreStatusPage_Unloaded(object sender, RoutedEventArgs e) =>
        _checkCancellation?.Cancel();

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async Task RefreshAsync()
    {
        _checkCancellation?.Cancel();
        var operation = new CancellationTokenSource();
        _checkCancellation = operation;
        var ct = operation.Token;

        RefreshButton.IsEnabled = false;
        CopyButton.IsEnabled = false;
        RefreshButton.Content = "Verificando…";
        SetPendingState();
        var report = new List<string>
        {
            "WinProvision Store Status",
            $"Verificado em: {DateTimeOffset.Now:dd/MM/yyyy HH:mm:ss zzz}",
            $"Windows: {Environment.OSVersion.VersionString}",
            $"Aplicativo: {typeof(StoreStatusPage).Assembly.GetName().Version}"
        };

        try
        {
            await CheckWingetAsync(ct, report);
            var apiHealth = await _apiService.CheckCatalogHealthAsync(ct);
            ApplyApiHealth(apiHealth, report);
            LastCheckedText.Text = $"Verificado em {apiHealth.CheckedAt.ToLocalTime():dd/MM/yyyy HH:mm:ss}";
            _diagnosticText = string.Join(Environment.NewLine, report);
            CopyButton.IsEnabled = true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            LastCheckedText.Text = "Verificação cancelada";
        }
        catch (Exception ex)
        {
            LastCheckedText.Text = "A verificação terminou com erro";
            report.Add($"Erro geral: {ex.GetType().Name}: {ex.Message}");
            _diagnosticText = string.Join(Environment.NewLine, report);
            CopyButton.IsEnabled = true;
        }
        finally
        {
            if (ReferenceEquals(_checkCancellation, operation))
            {
                _checkCancellation = null;
                RefreshButton.IsEnabled = true;
                RefreshButton.Content = "Verificar novamente";
            }
            operation.Dispose();
        }
    }

    private async Task CheckWingetAsync(CancellationToken ct, List<string> report)
    {
        bool available;
        try
        {
            available = await _wingetBootstrapper.IsWingetAvailableAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            available = false;
            WingetDetailText.Text = $"Falha ao consultar o WinGet ({ex.GetType().Name}).";
        }

        if (!available)
        {
            SetStatus(WingetStatusText, "Indisponível", false);
            WingetVersionText.Text = "Versão: não encontrada";
            WingetSourcesText.Text = "Fontes: não consultadas";
            if (string.IsNullOrEmpty(WingetDetailText.Text))
                WingetDetailText.Text = "O WinGet não respondeu à verificação local.";
            report.Add("WinGet CLI: indisponível");
            return;
        }

        SetStatus(WingetStatusText, "Disponível", true);
        string executable = WingetLocator.ExecutablePath;
        report.Add($"WinGet executável: {executable}");
        try
        {
            var version = await RunWingetCommandAsync(executable, ["--version"], TimeSpan.FromSeconds(8), ct);
            string versionText = FirstNonEmptyLine(version.StandardOutput, version.StandardError);
            WingetVersionText.Text = $"Versão: {(version.ExitCode == 0 && versionText.Length > 0 ? versionText : "não informada")}";
            report.Add($"WinGet versão: {WingetVersionText.Text["Versão: ".Length..]}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            WingetVersionText.Text = "Versão: consulta não concluída";
            report.Add($"WinGet versão: erro {ex.GetType().Name}");
        }

        try
        {
            var sources = await RunWingetCommandAsync(executable, ["source", "list"], TimeSpan.FromSeconds(12), ct);
            string output = FirstNonEmptyLine(sources.StandardOutput, sources.StandardError);
            WingetSourcesText.Text = sources.ExitCode == 0
                ? "Fontes configuradas:" + Environment.NewLine + Truncate(sources.StandardOutput.Trim(), 1200)
                : $"Fontes: não foi possível listar (código {sources.ExitCode}).";
            report.Add("WinGet fontes:");
            report.Add(sources.ExitCode == 0 ? Truncate(sources.StandardOutput.Trim(), 3000) : Truncate(output, 1000));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            WingetSourcesText.Text = "Fontes: consulta excedeu o tempo ou falhou.";
            report.Add($"WinGet fontes: erro {ex.GetType().Name}");
        }

        if (string.IsNullOrEmpty(WingetDetailText.Text))
            WingetDetailText.Text = $"Executável: {executable}";
    }

    private void ApplyApiHealth(CatalogApiHealth health, List<string> report)
    {
        foreach (var source in health.Sources)
        {
            var statusText = source.Source == "WinGet" ? WingetApiStatusText : MsStoreApiStatusText;
            var detailsText = source.Source == "WinGet" ? WingetApiDetailsText : MsStoreApiDetailsText;
            SetStatus(statusText, source.Available ? "Disponível" : "Indisponível", source.Available);
            detailsText.Text = source.Available
                ? $"{source.AppCount:N0} aplicativos · catálogo de {source.GeneratedUtc?.ToLocalTime():dd/MM/yyyy HH:mm} · resposta em {source.ResponseTime?.TotalSeconds:F1}s"
                : source.Error ?? "Falha ao consultar o catálogo.";
            report.Add(source.Available
                ? $"API {source.Source}: disponível; {source.AppCount} apps; catálogo {source.GeneratedUtc:O}; resposta {source.ResponseTime?.TotalMilliseconds:F0} ms"
                : $"API {source.Source}: indisponível; {source.Error}");
        }

        var cache = health.Cache;
        SetStatus(CacheStatusText,
            !cache.Exists ? "Não encontrado" : cache.Valid ? "Válido" : "Inválido ou expirado",
            cache.Exists && cache.Valid);
        CacheDetailsText.Text = cache.UpdatedUtc is { } updated
            ? $"Última gravação: {updated.ToLocalTime():dd/MM/yyyy HH:mm:ss}. O cache de contingência é aceito por até 30 dias."
            : "O índice combinado ainda não foi gravado neste computador.";
        report.Add(cache.Exists
            ? $"Cache local: {(cache.Valid ? "válido e dentro da retenção" : "inválido, expirado ou ilegível")}; atualização {cache.UpdatedUtc:O}"
            : "Cache local: ausente");
    }

    private void SetPendingState()
    {
        SetStatus(WingetStatusText, "Verificando…", null);
        SetStatus(WingetApiStatusText, "Verificando…", null);
        SetStatus(MsStoreApiStatusText, "Verificando…", null);
        SetStatus(CacheStatusText, "Verificando…", null);
        WingetVersionText.Text = "Versão: consultando";
        WingetSourcesText.Text = "Fontes: consultando";
        WingetDetailText.Text = "";
        WingetApiDetailsText.Text = "";
        MsStoreApiDetailsText.Text = "";
        CacheDetailsText.Text = "";
    }

    private static void SetStatus(TextBlock target, string text, bool? healthy)
    {
        target.Text = text;
        string? resourceKey = healthy switch
        {
            true => "AppOperationSuccessBrush",
            false => "AppOperationFailureBrush",
            _ => null
        };
        target.Foreground = resourceKey is null
            ? (Brush)Application.Current.FindResource("TextFillColorPrimaryBrush")
            : (Brush)Application.Current.FindResource(resourceKey);
    }

    private static async Task<WingetCommandResult> RunWingetCommandAsync(
        string executable, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
            throw new InvalidOperationException("O processo do WinGet não iniciou.");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
        Task<string> stderr = process.StandardError.ReadToEndAsync(timeoutCts.Token);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
            return new WingetCommandResult(process.ExitCode, await stdout, await stderr);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            if (ct.IsCancellationRequested)
                throw;
            throw new TimeoutException("A consulta do WinGet excedeu o tempo limite.");
        }
    }

    private static string FirstNonEmptyLine(params string[] values) =>
        values.SelectMany(value => value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.Length > 0) ?? "";

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength] + "…";

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(_diagnosticText);
            LastCheckedText.Text = "Diagnóstico copiado";
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException or InvalidOperationException)
        {
            LastCheckedText.Text = "Não foi possível copiar o diagnóstico";
        }
    }

    private sealed record WingetCommandResult(int ExitCode, string StandardOutput, string StandardError);
}
