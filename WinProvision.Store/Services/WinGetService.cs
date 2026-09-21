using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Management.Deployment;
using WinProvision.Core.Models;
using WinProvision.Core.Services;

namespace WinProvision.Store.Services;

/// <summary>
/// Serviço para interação com o WinGet usando a API COM, com fallback para a CLI.
/// </summary>
public sealed class WinGetService
{
    // Tempo máximo até o PRIMEIRO callback de progresso (o vigia é cancelado no primeiro callback).
    // A primeira ativação do servidor COM + atualização de fonte pode passar de 1 minuto em máquina
    // recém-provisionada ou rede lenta; 60s cancelava a COM cedo demais e forçava o winget.exe.
    private static readonly TimeSpan ComProgressTimeout = TimeSpan.FromSeconds(180);
    private const int MaxPreflightAttempts = 3;
    private static readonly TimeSpan ProgressUpdateInterval = TimeSpan.FromMilliseconds(200);
    private readonly WingetExecutor _wingetExecutor;

    private sealed class ComInstallTimeoutException(string message) : TimeoutException(message);

    /// <summary>Marca o instante em que a instalação foi entregue ao servidor COM (a partir daí não se repete).</summary>
    private sealed class InstallAttemptState
    {
        public volatile bool Started;
    }

    /// <summary>
    /// IProgress&lt;T&gt; que chama o handler de forma síncrona, no thread que invoca Report.
    /// Progress&lt;T&gt; normal capturaria o SynchronizationContext do Task.Run (que é nulo) e
    /// enfileiraria cada Report no ThreadPool, sem garantir ordem entre callbacks sucessivos.
    /// </summary>
    private sealed class SynchronousProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }

    public WinGetService(WingetExecutor wingetExecutor)
    {
        _wingetExecutor = wingetExecutor;
    }

    public record WinGetPackageMatch(
        string PackageId,
        string Name,
        string Version,
        string Publisher,
        string Source);

    public async Task<IReadOnlyList<WinGetPackageMatch>> SearchAsync(
        string query,
        CancellationToken cancellationToken = default)
    {
        if (WinGetFactoryHelper.IsComDisabled)
        {
            WinGetDiagnosticLog.Write($"COM DESATIVADO NA SESSÃO: motivo={WinGetFactoryHelper.DisabledReason}");
            return await SearchCliFallbackAsync(query, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            return await Task.Run(
                () => SearchApiAsync(query, cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            WinGetFactoryHelper.DisableComForSession(ex);
            if (!WinGetFactoryHelper.CliFallbackAllowed)
            {
                WinGetDiagnosticLog.Write($"SEARCH COM-ONLY exception={ex}");
                throw;
            }

            WinGetDiagnosticLog.Write(
                $"SEARCH FALLBACK exception={ex.GetType().FullName} " +
                $"hresult=0x{ex.HResult:X8} message=\"{ex.Message}\" stack={ex}");
            WinGetDiagnosticLog.Write("FALLBACK PARA CLI: motivo=COM search exception");
            Trace.WriteLine($"WinGet COM search failed; falling back to winget.exe: {ex}");
            return await SearchCliFallbackAsync(query, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<WingetExecutionResult> InstallAsync(
        string packageId,
        Action<string>? onLogReceived = null,
        CancellationToken cancellationToken = default,
        string? installLocation = null,
        Action<InstallProgressUpdate>? onProgress = null,
        string source = "winget")
    {
        WinGetDiagnosticLog.Write(
            $"INSTALL ENTER packageId=\"{packageId}\" source={source} mode={WinGetFactoryHelper.Mode} " +
            $"comDisabled={WinGetFactoryHelper.IsComDisabled} thread={Environment.CurrentManagedThreadId}");

        if (WinGetFactoryHelper.IsComDisabled)
        {
            WinGetDiagnosticLog.Write($"FALLBACK PARA CLI: motivo={WinGetFactoryHelper.DisabledReason}");
            onLogReceived?.Invoke("API COM indisponível; usando winget.exe como fallback.");
            return await _wingetExecutor.InstallAppAsync(
                packageId, onLogReceived, cancellationToken, installLocation, source).ConfigureAwait(false);
        }

        var state = new InstallAttemptState();
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                // COM (InstallApiAsync) resolve o pacote pelo Id nos catálogos predefinidos
                // (OpenWindowsCatalog e, para source=msstore, MicrosoftStore).
                return await Task.Run(
                    () => InstallApiAsync(
                        packageId, onLogReceived, onProgress, cancellationToken, installLocation, source, state),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Falha de ativação/identidade ANTES de a instalação ser entregue ao servidor COM: tenta a
                // próxima estratégia (lower-trust -> empacotado) antes de desistir da COM.
                if (!state.Started && WinGetFactoryHelper.TryAdvanceStrategy(ex))
                {
                    WinGetDiagnosticLog.Write(
                        $"INSTALL COM repetindo com a estratégia {WinGetFactoryHelper.CurrentStrategy} " +
                        $"(falha anterior 0x{ex.HResult:X8})");
                    continue;
                }

                WinGetFactoryHelper.DisableComForSession(ex);
                WinGetDiagnosticLog.Write(
                    $"INSTALL COM FALHOU attempt={attempt}/{MaxPreflightAttempts} started={state.Started} " +
                    $"exception={ex.GetType().FullName} hresult=0x{ex.HResult:X8} " +
                    $"message=\"{ex.Message}\" stack={ex}");

                var noProgressTimeout = ex is ComInstallTimeoutException;

                // Instalação JÁ entregue ao servidor COM: nunca reexecuta sozinha (evita instalar
                // duas vezes / competir com um instalador ainda rodando). Exceção: timeout antes do
                // primeiro progresso, quando nada foi baixado ainda.
                if (state.Started && !noProgressTimeout)
                {
                    onLogReceived?.Invoke(
                        $"A API COM falhou durante a instalação ({ex.Message}); não será repetida automaticamente.");
                    return new WingetExecutionResult
                    {
                        Success = false,
                        ExitCode = ex.HResult,
                        Output = $"Falha na API COM durante a instalação: {ex.Message}",
                        FailureReason = WingetFailureReason.InternalError
                    };
                }

                // Falha passageira antes de começar (servidor COM reiniciando, RPC caiu): tenta de novo
                // com uma nova ativação antes de desistir da COM.
                if (attempt < MaxPreflightAttempts &&
                    !noProgressTimeout &&
                    WinGetFactoryHelper.IsTransient(ex) &&
                    !WinGetFactoryHelper.IsComDisabled)
                {
                    var delay = TimeSpan.FromSeconds(attempt * 2);
                    WinGetDiagnosticLog.Write($"INSTALL COM nova tentativa em {delay.TotalSeconds:0}s");
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (!WinGetFactoryHelper.CliFallbackAllowed)
                {
                    WinGetDiagnosticLog.Write("MODO COM-ONLY: fallback para winget.exe bloqueado");
                    return new WingetExecutionResult
                    {
                        Success = false,
                        ExitCode = ex.HResult,
                        Output = $"Falha na API COM (modo COM-only, sem fallback): {ex.Message}",
                        FailureReason = WingetFailureReason.InternalError
                    };
                }

                WinGetDiagnosticLog.Write(
                    $"FALLBACK PARA CLI: motivo=COM install exception ({ex.GetType().Name} 0x{ex.HResult:X8})");
                Trace.WriteLine($"WinGet COM install failed; falling back to winget.exe: {ex}");
                onLogReceived?.Invoke("API COM indisponível; usando winget.exe como fallback.");
                return await _wingetExecutor.InstallAppAsync(
                    packageId,
                    onLogReceived,
                    cancellationToken,
                    installLocation,
                    source).ConfigureAwait(false);
            }
        }
    }

    private static async Task<IReadOnlyList<WinGetPackageMatch>> SearchApiAsync(
        string query,
        CancellationToken cancellationToken)
    {
        Trace.WriteLine("WinGet COM search: creating resilient PackageManager.");
        var packageManager = WinGetFactoryHelper.CreateResilientPackageManager();
        var packageCatalogReference = packageManager.GetPredefinedPackageCatalog(
            PredefinedPackageCatalog.OpenWindowsCatalog);
        packageCatalogReference.AcceptSourceAgreements = true;
        var connectResult = await packageCatalogReference.ConnectAsync()
            .AsTask(cancellationToken).ConfigureAwait(false);
        if (connectResult.Status != ConnectResultStatus.Ok)
        {
            Trace.WriteLine($"WinGet COM search connection failed: {connectResult.Status}.");
            throw new InvalidOperationException(
                $"Falha ao conectar ao catálogo do WinGet: {connectResult.Status}.");
        }

        var packageCatalog = connectResult.PackageCatalog
            ?? throw new InvalidOperationException("O catálogo do WinGet não foi conectado.");

        var findOptions = WinGetFactoryHelper.CreateFindPackagesOptions();
        var filter = WinGetFactoryHelper.CreatePackageMatchFilter();
        filter.Field = PackageMatchField.Name;
        filter.Option = PackageFieldMatchOption.ContainsCaseInsensitive;
        filter.Value = query;
        findOptions.Filters.Add(filter);

        var findResult = await packageCatalog.FindPackagesAsync(findOptions)
            .AsTask(cancellationToken).ConfigureAwait(false);
        var results = new List<WinGetPackageMatch>();

        foreach (var match in findResult.Matches.ToArray())
        {
            var package = match.CatalogPackage;
            var version = package.DefaultInstallVersion;

            results.Add(new WinGetPackageMatch(
                package.Id,
                package.Name,
                version?.Version ?? string.Empty,
                version?.Publisher ?? string.Empty,
                packageCatalog.Info?.Name ?? "Windows Package Manager"));
        }

        return results;
    }

    private static async Task<IReadOnlyList<WinGetPackageMatch>> SearchCliFallbackAsync(
        string query,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "winget.exe",
            Arguments = $"search \"{query}\" --accept-source-agreements",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        WingetCliAudit.Launch(startInfo.FileName, startInfo.Arguments);
        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return Array.Empty<WinGetPackageMatch>();
        }

        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken)
            .ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        var lines = output
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line[(line.LastIndexOf('\r') + 1)..].TrimEnd('\r'))
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToArray();
        var separatorIndex = Array.FindIndex(lines, IsTableSeparator);
        if (separatorIndex < 0)
        {
            return Array.Empty<WinGetPackageMatch>();
        }

        var headerIndex = separatorIndex - 1;
        while (headerIndex >= 0 && string.IsNullOrWhiteSpace(lines[headerIndex]))
        {
            headerIndex--;
        }

        if (headerIndex < 0)
        {
            return Array.Empty<WinGetPackageMatch>();
        }

        var columnStarts = new[] { lines[headerIndex].TakeWhile(char.IsWhiteSpace).Count() }
            .Concat(Regex.Matches(lines[headerIndex], @"\s{2,}")
                .Select(match => match.Index + match.Length))
            .ToArray();
        if (columnStarts.Length < 4)
        {
            return Array.Empty<WinGetPackageMatch>();
        }

        var results = new List<WinGetPackageMatch>();
        foreach (var line in lines[(separatorIndex + 1)..])
        {
            var columns = ReadTableColumns(line, columnStarts);
            if (columns is null || string.IsNullOrWhiteSpace(columns.Value.Id))
            {
                continue;
            }

            results.Add(new WinGetPackageMatch(
                columns.Value.Id,
                columns.Value.Name,
                columns.Value.Version,
                string.Empty,
                columns.Value.Source));
        }

        return results;
    }

    private static bool IsTableSeparator(string line) =>
        Regex.IsMatch(line, @"^\s*-{3,}\s*$");

    private static (string Name, string Id, string Version, string Source)? ReadTableColumns(
        string line,
        int[] columnStarts)
    {
        if (columnStarts.Length < 4)
        {
            return null;
        }

        var fields = Regex.Split(line.Trim(), @"\s{2,}")
            .Where(field => !string.IsNullOrWhiteSpace(field))
            .ToArray();
        if (fields.Length < 4)
        {
            Trace.WriteLine($"WinGet CLI row discarded: expected at least four columns: {line}");
            return null;
        }

        var source = fields[^1];
        var version = fields[2];
        var id = fields[1];
        var name = fields[0];
        // Match is optional and may contain spaces (for example, "Tag: office");
        // it is intentionally ignored because Source is always the final field.
        if (id.Contains(' ', StringComparison.Ordinal))
        {
            Trace.WriteLine($"WinGet CLI row discarded: package ID contains spaces: {line}");
            return null;
        }

        if (id.Contains('…', StringComparison.Ordinal) || id.Contains("...", StringComparison.Ordinal))
        {
            Trace.WriteLine($"WinGet CLI row discarded: package ID is truncated: {line}");
            return null;
        }

        return (name, id, version, source);
    }

    private async Task<WingetExecutionResult> InstallApiAsync(
        string packageId,
        Action<string>? onLogReceived,
        Action<InstallProgressUpdate>? onProgress,
        CancellationToken cancellationToken,
        string? installLocation,
        string source,
        InstallAttemptState attemptState)
    {
        var stopwatch = Stopwatch.StartNew();
        WinGetDiagnosticLog.Write("INSTALL COM stage=begin");

        // Callbacks da UI nunca devem derrubar o pipeline COM (uma exceção aqui viraria "falha da COM").
        var userOnProgress = onProgress;
        if (userOnProgress is not null)
        {
            onProgress = update =>
            {
                try { userOnProgress(update); }
                catch (Exception ex) { WinGetDiagnosticLog.Write($"INSTALL COM onProgress lançou {ex.GetType().Name}: {ex.Message}"); }
            };
        }

        var userOnLog = onLogReceived;
        if (userOnLog is not null)
        {
            onLogReceived = line =>
            {
                try { userOnLog(line); }
                catch (Exception ex) { WinGetDiagnosticLog.Write($"INSTALL COM onLog lançou {ex.GetType().Name}: {ex.Message}"); }
            };
        }
        Trace.WriteLine("WinGet COM install: creating resilient PackageManager.");
        var packageManager = WinGetFactoryHelper.CreateResilientPackageManager();
        WinGetDiagnosticLog.Write($"INSTALL COM stage=packageManager-created elapsed={stopwatch.Elapsed}");
        Trace.WriteLine($"WinGet COM install: PackageManager created in {stopwatch.Elapsed}.");
        // OpenWindowsCatalog = fonte "winget". Apps da Microsoft Store (source=msstore) ficam no
        // catálogo MicrosoftStore; se não achar lá, ainda tenta o catálogo winget.
        var catalogKinds = string.Equals(source, "msstore", StringComparison.OrdinalIgnoreCase)
            ? new[] { PredefinedPackageCatalog.MicrosoftStore, PredefinedPackageCatalog.OpenWindowsCatalog }
            : new[] { PredefinedPackageCatalog.OpenWindowsCatalog };

        CatalogPackage? matchedPackage = null;
        var connectedAny = false;
        string? lastConnectFailure = null;
        for (var i = 0; i < catalogKinds.Length && matchedPackage is null; i++)
        {
            var kind = catalogKinds[i];
            var packageCatalogReference = packageManager.GetPredefinedPackageCatalog(kind);
            packageCatalogReference.AcceptSourceAgreements = true;
            var connectStarted = stopwatch.Elapsed;
            var connectResult = await packageCatalogReference.ConnectAsync()
                .AsTask(cancellationToken).ConfigureAwait(false);
            WinGetDiagnosticLog.Write(
                $"INSTALL COM stage=connect-completed catalog={kind} status={connectResult.Status} " +
                $"elapsed={stopwatch.Elapsed} connect={stopwatch.Elapsed - connectStarted}");
            if (connectResult.Status != ConnectResultStatus.Ok || connectResult.PackageCatalog is null)
            {
                lastConnectFailure = $"Falha ao conectar ao catálogo do WinGet ({kind}): {connectResult.Status}.";
                continue;
            }

            connectedAny = true;
            WinGetFactoryHelper.ReportComSuccess();
            var packageCatalog = connectResult.PackageCatalog;

            var findOptions = WinGetFactoryHelper.CreateFindPackagesOptions();
            var filter = WinGetFactoryHelper.CreatePackageMatchFilter();
            filter.Field = PackageMatchField.Id;
            filter.Option = PackageFieldMatchOption.Equals;
            filter.Value = packageId;
            findOptions.Filters.Add(filter);

            var findStarted = stopwatch.Elapsed;
            var findResult = await packageCatalog.FindPackagesAsync(findOptions)
                .AsTask(cancellationToken).ConfigureAwait(false);
            matchedPackage = findResult.Matches.ToArray().FirstOrDefault()?.CatalogPackage;
            WinGetDiagnosticLog.Write(
                $"INSTALL COM stage=package-lookup-completed catalog={kind} found={matchedPackage is not null} " +
                $"elapsed={stopwatch.Elapsed} lookup={stopwatch.Elapsed - findStarted}");
        }

        if (!connectedAny)
        {
            throw new WinGetComPreflightException(
                lastConnectFailure ?? "Falha ao conectar ao catálogo do WinGet.",
                isTransient: true);
        }

        if (matchedPackage is null)
        {
            // Não é falha de instalação: o índice COM pode estar defasado. Deixa o chamador decidir
            // (modo auto tenta o CLI; modo COM-only mostra este erro).
            throw new WinGetComPreflightException($"Pacote não encontrado no catálogo COM: {packageId}");
        }

        var installOptions = WinGetFactoryHelper.CreateInstallOptions();
        installOptions.PackageInstallMode = PackageInstallMode.Silent;
        installOptions.AcceptPackageAgreements = true;
        installOptions.PreferredInstallLocation = installLocation;

        onProgress?.Invoke(new InstallProgressUpdate(InstallProgressPhase.Preparing));
        attemptState.Started = true;
        WinGetDiagnosticLog.Write($"INSTALL COM stage=install-dispatched elapsed={stopwatch.Elapsed}");
        var installOperation = packageManager.InstallPackageAsync(matchedPackage, installOptions);
        using var operationCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var watchdogCts = new CancellationTokenSource();
        using var heartbeatCts = new CancellationTokenSource();
        var lastProgress = Stopwatch.GetTimestamp();
        var lastProgressState = PackageInstallProgressState.Queued;
        var lastDownloadProgress = 0d;
        var lastInstallationProgress = 0d;
        var firstProgress = 0;
        var progressCallbacks = 0;
        var lastLoggedPercent = -1;
        var watchdogTimedOut = 0;
        var lastUiUpdate = TimeSpan.MinValue;
        var hasUiUpdate = false;
        var lastUiPercent = -1;
        var lastUiState = (PackageInstallProgressState)(-1);
        var progressLock = new object();
        IProgress<InstallProgress> progress = new SynchronousProgress<InstallProgress>(value =>
        {
            var state = value.State;
            var downloadProgress = value.DownloadProgress;
            var installationProgress = value.InstallationProgress;
            lastProgressState = state;
            lastDownloadProgress = downloadProgress;
            lastInstallationProgress = installationProgress;
            Volatile.Write(ref lastProgress, Stopwatch.GetTimestamp());

            if (Interlocked.Exchange(ref firstProgress, 1) == 0)
            {
                WinGetDiagnosticLog.Write(
                    $"INSTALL COM stage=first-progress state={state} " +
                    $"download={downloadProgress} install={installationProgress} " +
                    $"dispatcher=false elapsed={stopwatch.Elapsed}");
                Trace.WriteLine(
                    $"WinGet COM install: first progress callback at {stopwatch.Elapsed}.");
                watchdogCts.Cancel();
            }

            var callbackNumber = Interlocked.Increment(ref progressCallbacks);
            var percentForLog = state == PackageInstallProgressState.Downloading
                ? (int)Math.Clamp(Math.Round(downloadProgress * 100), 0, 100)
                : (int)Math.Clamp(Math.Round(installationProgress * 100), 0, 100);
            if (callbackNumber <= 5 ||
                (percentForLog >= 0 && percentForLog / 10 > lastLoggedPercent / 10))
            {
                Interlocked.Exchange(ref lastLoggedPercent, percentForLog);
                WinGetDiagnosticLog.Write(
                    $"INSTALL COM progress callback={callbackNumber} state={state} " +
                    $"download={downloadProgress} install={installationProgress} " +
                    $"dispatcher=false elapsed={stopwatch.Elapsed}");
            }

            var now = stopwatch.Elapsed;
            lock (progressLock)
            {
                if (state is PackageInstallProgressState.Downloading or
                    PackageInstallProgressState.Installing)
                {
                    lastUiState = state;
                    var isDownloading = state == PackageInstallProgressState.Downloading;

                    // DownloadProgress fica em 0 em alguns catálogos até o primeiro byte
                    // chegar; BytesDownloaded/BytesRequired cobrem esse intervalo.
                    var progressValue = isDownloading
                        ? (downloadProgress > 0 || value.BytesRequired <= 0
                            ? downloadProgress
                            : (double)value.BytesDownloaded / value.BytesRequired)
                        : installationProgress;
                    var percent = (int)Math.Clamp(
                        Math.Round(progressValue * 100, MidpointRounding.AwayFromZero),
                        0,
                        100);
                    var phase = isDownloading
                        ? InstallProgressPhase.Downloading
                        : InstallProgressPhase.Installing;

                    if (isDownloading && percent >= 100)
                    {
                        if (lastUiPercent != 100)
                        {
                            lastUiPercent = 100;
                            lastUiUpdate = now;
                            hasUiUpdate = true;
                            onProgress?.Invoke(new InstallProgressUpdate(
                                InstallProgressPhase.Preparing));
                        }
                    }
                    else if (percent != lastUiPercent &&
                        (!hasUiUpdate ||
                         now - lastUiUpdate >= ProgressUpdateInterval))
                    {
                        lastUiPercent = percent;
                        lastUiUpdate = now;
                        hasUiUpdate = true;
                        onProgress?.Invoke(new InstallProgressUpdate(phase, percent));
                    }
                }
                else if (state != lastUiState)
                {
                    lastUiState = state;
                    lastUiUpdate = now;
                    hasUiUpdate = true;
                    switch (state)
                    {
                        case PackageInstallProgressState.Queued:
                            onProgress?.Invoke(new InstallProgressUpdate(
                                InstallProgressPhase.Preparing));
                            break;
                        case PackageInstallProgressState.PostInstall:
                            onProgress?.Invoke(new InstallProgressUpdate(
                                InstallProgressPhase.Installing));
                            break;
                        case PackageInstallProgressState.Finished:
                            onProgress?.Invoke(new InstallProgressUpdate(
                                InstallProgressPhase.Installing, 100));
                            break;
                    }
                }
            }
        });

        var watchdog = MonitorComProgressAsync(
            watchdogCts.Token,
            operationCts,
            () => Stopwatch.GetElapsedTime(Volatile.Read(ref lastProgress)),
            () => Interlocked.Exchange(ref watchdogTimedOut, 1),
            stopwatch);
        WinGetDiagnosticLog.WriteComServerInfo();
        var heartbeat = MonitorComHeartbeatAsync(
            heartbeatCts.Token,
            () => (lastProgressState, lastDownloadProgress, lastInstallationProgress),
            () => Stopwatch.GetElapsedTime(Volatile.Read(ref lastProgress)),
            stopwatch);
        InstallResult installResult;
        try
        {
            installResult = await installOperation.AsTask(operationCts.Token, progress)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            Volatile.Read(ref watchdogTimedOut) == 1 &&
            !cancellationToken.IsCancellationRequested)
        {
            throw new ComInstallTimeoutException(
                $"A API COM não reportou progresso por {ComProgressTimeout.TotalSeconds:0} segundos.");
        }
        finally
        {
            watchdogCts.Cancel();
            heartbeatCts.Cancel();
            await watchdog.ConfigureAwait(false);
            await heartbeat.ConfigureAwait(false);
        }

        var output = $"Resultado da API COM: {installResult.Status}.";
        WinGetDiagnosticLog.Write(
            $"INSTALL COM stage=result status={installResult.Status} " +
            $"reboot={installResult.RebootRequired} elapsed={stopwatch.Elapsed}");
        Trace.WriteLine($"WinGet COM install result: {installResult.Status}.");
        Trace.WriteLine($"WinGet COM install completed in {stopwatch.Elapsed}.");
        if (installResult.RebootRequired)
        {
            output += " Reinicialização necessária.";
        }

        if (installResult.Status != InstallResultStatus.Ok)
        {
            int installerErrorCode = unchecked((int)installResult.InstallerErrorCode);
            int extendedErrorCode = installResult.ExtendedErrorCode?.HResult ?? 0;
            WinGetDiagnosticLog.Write(
                $"INSTALL COM failure status={installResult.Status} " +
                $"installerErrorCode=0x{installerErrorCode:X8} ({installerErrorCode}) " +
                $"extendedErrorCode=0x{extendedErrorCode:X8} ({extendedErrorCode})");

            if ((installerErrorCode == 740 || extendedErrorCode == unchecked((int)0x800702E4)) &&
                WinGetFactoryHelper.CliFallbackAllowed)
            {
                WinGetDiagnosticLog.Write(
                    "FALLBACK PARA CLI: motivo=COM install requer elevação");
                onLogReceived?.Invoke(
                    "A instalação requer privilégios de administrador; usando o fallback do winget.");
                return await _wingetExecutor.InstallAppAsync(
                    packageId,
                    onLogReceived,
                    cancellationToken,
                    installLocation,
                    source).ConfigureAwait(false);
            }

            var failureReason = MapInstallFailure(installResult.Status);
            if (installResult.InstallerErrorCode == 0 && installResult.ExtendedErrorCode is { } extendedError)
            {
                output += $" Erro estendido: {extendedError.Message}";
            }

            Trace.WriteLine(output);
            return new WingetExecutionResult
            {
                Success = false,
                ExitCode = installerErrorCode,
                Output = output,
                FailureReason = failureReason
            };
        }

        onLogReceived?.Invoke(
            installResult.RebootRequired
                ? "Instalação concluída via API COM; reinicialização necessária."
                : "Instalação concluída via API COM do WinGet.");
        return new WingetExecutionResult
        {
            Success = true,
            ExitCode = 0,
            Output = output
        };
    }

    private static async Task MonitorComProgressAsync(
        CancellationToken watchdogCancellationToken,
        CancellationTokenSource operationCts,
        Func<TimeSpan> elapsedSinceProgress,
        Action onTimeout,
        Stopwatch stopwatch)
    {
        try
        {
            while (!watchdogCancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), watchdogCancellationToken)
                    .ConfigureAwait(false);
                if (elapsedSinceProgress() < ComProgressTimeout)
                {
                    continue;
                }

                Trace.WriteLine(
                    $"WinGet COM install: no progress for {ComProgressTimeout.TotalSeconds:0} seconds at {stopwatch.Elapsed}; cancelling.");
                onTimeout();
                operationCts.Cancel();
                return;
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static async Task MonitorComHeartbeatAsync(
        CancellationToken cancellationToken,
        Func<(PackageInstallProgressState State, double Download, double Install)> getProgress,
        Func<TimeSpan> elapsedSinceProgress,
        Stopwatch stopwatch)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken)
                    .ConfigureAwait(false);
                var progress = getProgress();
                WinGetDiagnosticLog.Write(
                    $"INSTALL COM heartbeat state={progress.State} " +
                    $"download={progress.Download} install={progress.Install} " +
                    $"sinceLastCallback={elapsedSinceProgress()} elapsed={stopwatch.Elapsed}");
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static WingetFailureReason MapInstallFailure(InstallResultStatus status) =>
        status switch
        {
            InstallResultStatus.BlockedByPolicy => WingetFailureReason.BlockedByPolicy,
            InstallResultStatus.NoApplicableInstallers => WingetFailureReason.NoApplicableInstallers,
            InstallResultStatus.PackageAgreementsNotAccepted =>
                WingetFailureReason.PackageAgreementsNotAccepted,
            InstallResultStatus.DownloadError => WingetFailureReason.DownloadError,
            InstallResultStatus.InstallError => WingetFailureReason.InstallError,
            InstallResultStatus.CatalogError => WingetFailureReason.CatalogError,
            InstallResultStatus.InternalError => WingetFailureReason.InternalError,
            _ => WingetFailureReason.Unknown
        };
}
