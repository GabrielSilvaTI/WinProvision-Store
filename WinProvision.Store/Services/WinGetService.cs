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
    private readonly WinProvisionApiService _apiService;
    private readonly WingetBootstrapper _bootstrapper;
    private int _postProvisionHandled;

    private sealed class ComInstallTimeoutException(string message) : TimeoutException(message);

    /// <summary>
    /// Qual operação COM disparar em <see cref="RunComPackageOperationAsync"/>:
    /// <see cref="Install"/> chama PackageManager.InstallPackageAsync (instalação "fresh");
    /// <see cref="Upgrade"/> chama PackageManager.UpgradePackageAsync no mesmo CatalogPackage
    /// (atualização de uma instalação existente) — mesmo InstallOptions, mesmo InstallResult/
    /// InstallProgress, só o método da COM que muda.
    /// </summary>
    private enum ComOperationKind { Install, Upgrade }

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

    public WinGetService(
        WingetExecutor wingetExecutor,
        WinProvisionApiService apiService,
        WingetBootstrapper bootstrapper)
    {
        _wingetExecutor = wingetExecutor;
        _apiService = apiService;
        _bootstrapper = bootstrapper;
    }

    /// <summary>
    /// Preparação da abertura do app (UI e /auto): PRIMEIRO provisiona o winget e suas
    /// dependências, SÓ DEPOIS aquece/testa a API COM. Antes o autoteste da COM rodava em
    /// paralelo, sem o App Installer, e abria o breaker com 0x80040154 pra sessão inteira.
    /// Nunca lança.
    /// </summary>
    public async Task PrepareAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await EnsureWingetProvisionedAsync(WinProvisionLog.Write, cancellationToken).ConfigureAwait(false);

            // SYSTEM nunca usa a COM (ver InstallAsync); o autoteste só geraria ruído.
            if (!WinProvisionApiService.IsRunningAsSystem())
            {
                await WinGetFactoryHelper.ProbeAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Encerramento do app: nada a fazer.
        }
        catch (Exception ex)
        {
            WinProvisionLog.Write($"PREPARE falhou {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Passo ZERO de qualquer operação: garante o winget provisionado antes de COM, API
    /// própria ou winget.exe. Falha NÃO aborta: a API própria não depende do winget, e o que
    /// exigir o winget de verdade (winget.exe, ODT via winget) falha com mensagem própria.
    /// </summary>
    private async Task EnsureWingetProvisionedAsync(Action<string>? onLog, CancellationToken cancellationToken)
    {
        WingetBootstrapResult result;
        try
        {
            result = await _bootstrapper.EnsureOnceAsync(onLog, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            WinProvisionLog.Write($"WINGET PROVISION exceção {ex.GetType().Name}: {ex.Message}");
            return;
        }

        if (!result.IsUsable)
        {
            WinProvisionLog.Write($"WINGET PROVISION falhou: {result.ErrorMessage}");
            onLog?.Invoke(
                "Winget indisponível e não foi possível provisioná-lo; seguindo com a API própria da WinProvision Store.");
            return;
        }

        WinProvisionLog.Write($"WINGET PROVISION status={result.Status} exe={WingetLocator.ExecutablePath}");

        // Só na PRIMEIRA vez que o winget aparece nesta sessão: falhas de COM anteriores
        // (App Installer ausente) deixam de valer, então a COM ganha uma tentativa limpa.
        if (result.Status == WingetBootstrapStatus.Bootstrapped &&
            Interlocked.Exchange(ref _postProvisionHandled, 1) == 0)
        {
            WinGetFactoryHelper.ResetAfterProvisioning("winget provisionado nesta sessão");
        }
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
        await EnsureWingetProvisionedAsync(WinProvisionLog.Write, cancellationToken).ConfigureAwait(false);

        if (WinGetFactoryHelper.IsComDisabled)
        {
            WinProvisionLog.Write($"COM DESATIVADO NA SESSÃO: motivo={WinGetFactoryHelper.DisabledReason}");
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
                WinProvisionLog.Write($"SEARCH COM-ONLY exception={ex}");
                throw;
            }

            WinProvisionLog.Write(
                $"SEARCH FALLBACK exception={ex.GetType().FullName} " +
                $"hresult=0x{ex.HResult:X8} message=\"{ex.Message}\" stack={ex}");
            WinProvisionLog.Write("FALLBACK PARA CLI: motivo=COM search exception");
            Trace.WriteLine($"WinGet COM search failed; falling back to winget.exe: {ex}");
            return await SearchCliFallbackAsync(query, cancellationToken).ConfigureAwait(false);
        }
    }

    public Task<WingetExecutionResult> InstallAsync(
        string packageId,
        Action<string>? onLogReceived = null,
        CancellationToken cancellationToken = default,
        string? installLocation = null,
        Action<InstallProgressUpdate>? onProgress = null,
        string source = "winget")
        => RunInstallOrUpgradeAsync(
            ComOperationKind.Install, packageId, onLogReceived, cancellationToken, installLocation, onProgress, source);

    /// <summary>
    /// Atualiza um pacote já instalado. Mesma cadeia de fallback e mesma robustez de
    /// <see cref="InstallAsync"/> (COM com retry/watchdog/heartbeat -&gt; API própria -&gt; CLI),
    /// trocando só a chamada COM final: <see cref="RunComPackageOperationAsync"/> despacha para
    /// PackageManager.UpgradePackageAsync em vez de InstallPackageAsync quando
    /// <see cref="ComOperationKind.Upgrade"/>. A API própria entra como nível 2 chamando o mesmo
    /// <see cref="WinProvisionApiService.TryInstallAsync"/> do install — o catálogo próprio só
    /// conhece a versão mais recente de cada pacote, então "instalar" e "atualizar pra última
    /// versão" são a mesma operação do ponto de vista dela (baixa e roda o instalador mais novo
    /// silenciosamente; pra Inno/NSIS/MSI isso atualiza em cima da instalação existente, sem
    /// desinstalar/reinstalar do zero).
    /// </summary>
    public Task<WingetExecutionResult> UpdateAsync(
        string packageId,
        Action<string>? onLogReceived = null,
        CancellationToken cancellationToken = default,
        string source = "winget")
        => RunInstallOrUpgradeAsync(
            ComOperationKind.Upgrade, packageId, onLogReceived, cancellationToken, null, null, source);

    /// <summary>
    /// Corpo comum de <see cref="InstallAsync"/> e <see cref="UpdateAsync"/> — mesma ordem fixa
    /// pros dois: 0) provisiona o winget + dependências; 1) COM (conforme o cenário); 2) API
    /// própria; 3) winget.exe. Sem o passo 0 nem o 1 nem o 3 funcionam.
    /// </summary>
    private async Task<WingetExecutionResult> RunInstallOrUpgradeAsync(
        ComOperationKind opKind,
        string packageId,
        Action<string>? onLogReceived,
        CancellationToken cancellationToken,
        string? installLocation,
        Action<InstallProgressUpdate>? onProgress,
        string source)
    {
        await EnsureWingetProvisionedAsync(onLogReceived, cancellationToken).ConfigureAwait(false);

        string logTag = opKind == ComOperationKind.Install ? "INSTALL" : "UPDATE";
        string actionLower = opKind == ComOperationKind.Install ? "instalação" : "atualização";

        WinProvisionLog.Write(
            $"{logTag} ENTER packageId=\"{packageId}\" source={source} mode={WinGetFactoryHelper.Mode} " +
            $"comDisabled={WinGetFactoryHelper.IsComDisabled} thread={Environment.CurrentManagedThreadId}");

        // Gatilhos que pulam a API COM inteiramente: execução como SYSTEM (sem desktop
        // interativo, COM/UAC nem funcionam) e reelevação já sabidamente falha nesta sessão
        // (usuário recusou o UAC antes; tentar a COM de novo só atrasa e provavelmente vai
        // pedir UAC de novo pro fallback CLI). Nos dois casos, a API própria da WinProvision
        // Store entra DIRETO, sem passar pela COM.
        if (WinProvisionApiService.IsRunningAsSystem() || WinProvisionElevationState.HasFailedThisSession)
        {
            var bypassReason = WinProvisionApiService.IsRunningAsSystem() ? "SYSTEM" : "reelevação-falhou-antes";
            WinProvisionLog.Write($"{logTag} BYPASS COM: motivo={bypassReason}");
            onLogReceived?.Invoke("Pulando API COM do WinGet; usando a API própria da WinProvision Store.");
            return await RunApiThenCliFallbackAsync(
                opKind, packageId, onLogReceived, cancellationToken, installLocation, source, bypassReason)
                .ConfigureAwait(false);
        }

        if (WinGetFactoryHelper.IsComDisabled)
        {
            WinProvisionLog.Write($"FALLBACK PARA CLI: motivo={WinGetFactoryHelper.DisabledReason}");
            onLogReceived?.Invoke("API COM indisponível; usando a API própria da WinProvision Store.");
            return await RunApiThenCliFallbackAsync(
                opKind, packageId, onLogReceived, cancellationToken, installLocation, source,
                $"COM-desativada-sessão({WinGetFactoryHelper.DisabledReason})").ConfigureAwait(false);
        }

        // Só serve pra sinalizar a via atual pro OperationRunner (ver ReportProgress) e colorir a
        // barra de progresso; as mensagens de falha/pulo da COM já existiam antes disso e cobriam
        // só o caminho de erro — sem isso, um install/update que dá certo de primeira na COM nunca
        // reportava nada em onLogReceived até a linha final de sucesso, perto demais do fim pra
        // colorir a barra durante a operação inteira.
        onLogReceived?.Invoke("Comunicando com a API COM do WinGet...");

        var state = new InstallAttemptState();
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                // COM (RunComPackageOperationAsync) resolve o pacote pelo Id nos catálogos
                // predefinidos (OpenWindowsCatalog e, para source=msstore, MicrosoftStore).
                return await Task.Run(
                    () => RunComPackageOperationAsync(
                        opKind, packageId, onLogReceived, onProgress, cancellationToken, installLocation, source, state),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Falha de ativação/identidade ANTES de a operação ser entregue ao servidor COM: tenta a
                // próxima estratégia (lower-trust -> empacotado) antes de desistir da COM.
                if (!state.Started && WinGetFactoryHelper.TryAdvanceStrategy(ex))
                {
                    WinProvisionLog.Write(
                        $"{logTag} COM repetindo com a estratégia {WinGetFactoryHelper.CurrentStrategy} " +
                        $"(falha anterior 0x{ex.HResult:X8})");
                    continue;
                }

                WinGetFactoryHelper.DisableComForSession(ex);
                WinProvisionLog.Write(
                    $"{logTag} COM FALHOU attempt={attempt}/{MaxPreflightAttempts} started={state.Started} " +
                    $"exception={ex.GetType().FullName} hresult=0x{ex.HResult:X8} " +
                    $"message=\"{ex.Message}\" stack={ex}");

                var noProgressTimeout = ex is ComInstallTimeoutException;

                // Operação JÁ entregue ao servidor COM: nunca reexecuta sozinha (evita
                // instalar/atualizar duas vezes / competir com um instalador ainda rodando).
                // Exceção: timeout antes do primeiro progresso, quando nada foi baixado ainda.
                if (state.Started && !noProgressTimeout)
                {
                    onLogReceived?.Invoke(
                        $"A API COM falhou durante a {actionLower} ({ex.Message}); não será repetida automaticamente.");
                    return new WingetExecutionResult
                    {
                        Success = false,
                        ExitCode = ex.HResult,
                        Output = $"Falha na API COM durante a {actionLower}: {ex.Message}",
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
                    WinProvisionLog.Write($"{logTag} COM nova tentativa em {delay.TotalSeconds:0}s");
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (!WinGetFactoryHelper.CliFallbackAllowed)
                {
                    WinProvisionLog.Write("MODO COM-ONLY: fallback para winget.exe bloqueado");
                    return new WingetExecutionResult
                    {
                        Success = false,
                        ExitCode = ex.HResult,
                        Output = $"Falha na API COM (modo COM-only, sem fallback): {ex.Message}",
                        FailureReason = WingetFailureReason.InternalError
                    };
                }

                WinProvisionLog.Write(
                    $"FALLBACK PARA API PRÓPRIA: motivo=COM {opKind} exception ({ex.GetType().Name} 0x{ex.HResult:X8})");
                Trace.WriteLine($"WinGet COM {opKind} failed; falling back to WinProvision API: {ex}");
                onLogReceived?.Invoke("API COM indisponível; usando a API própria da WinProvision Store.");
                return await RunApiThenCliFallbackAsync(
                    opKind,
                    packageId,
                    onLogReceived,
                    cancellationToken,
                    installLocation,
                    source,
                    $"COM-exception({ex.GetType().Name} 0x{ex.HResult:X8})").ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Nível 2 da cadeia de fallback, despachado por <paramref name="opKind"/>: instalação usa
    /// <see cref="TryWinProvisionApiThenCliAsync"/> (fallback final winget.exe install);
    /// atualização usa <see cref="TryApiUpdateThenCliAsync"/> (fallback final winget.exe update).
    /// </summary>
    private Task<WingetExecutionResult> RunApiThenCliFallbackAsync(
        ComOperationKind opKind,
        string packageId,
        Action<string>? onLogReceived,
        CancellationToken cancellationToken,
        string? installLocation,
        string source,
        string reason)
        => opKind == ComOperationKind.Install
            ? TryWinProvisionApiThenCliAsync(packageId, onLogReceived, cancellationToken, installLocation, source, reason)
            : TryApiUpdateThenCliAsync(packageId, onLogReceived, cancellationToken, source, reason);

    /// <summary>
    /// Tenta atualizar via <see cref="WinProvisionApiService.TryInstallAsync"/> (mesmo endpoint
    /// usado para instalar — ver comentário em <see cref="UpdateAsync"/>); só recorre ao
    /// winget.exe (<see cref="_wingetExecutor"/>) se a API própria também não resolver. Mesmo
    /// papel de <see cref="TryWinProvisionApiThenCliAsync"/>, só que pro lado da atualização.
    /// </summary>
    private async Task<WingetExecutionResult> TryApiUpdateThenCliAsync(
        string packageId,
        Action<string>? onLogReceived,
        CancellationToken cancellationToken,
        string source,
        string reason)
    {
        WinProvisionLog.Write($"UPDATE API-PROPRIA tentativa packageId=\"{packageId}\" motivo={reason}");

        WinProvisionInstallResult apiResult;
        try
        {
            apiResult = await _apiService.TryInstallAsync(
                packageId,
                onLogReceived is null ? null : new Progress<string>(onLogReceived),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A API própria nunca deve derrubar o pipeline: qualquer exceção inesperada (rede
            // fora do ar, R2 indisponível, etc.) é tratada como "não resolveu" e cai pro
            // winget.exe normalmente, igual a um WinProvisionInstallOutcome de falha.
            WinProvisionLog.Write(
                $"UPDATE API-PROPRIA exceção packageId=\"{packageId}\" {ex.GetType().Name}: {ex.Message}");
            apiResult = new WinProvisionInstallResult(
                WinProvisionInstallOutcome.DownloadFailed, Message: ex.Message);
        }

        if (apiResult.Outcome == WinProvisionInstallOutcome.Success)
        {
            WinProvisionLog.Write(
                $"UPDATE API-PROPRIA sucesso packageId=\"{packageId}\" exitCode={apiResult.ExitCode}");
            onLogReceived?.Invoke("Atualização concluída via API própria da WinProvision Store.");
            return new WingetExecutionResult
            {
                Success = true,
                ExitCode = apiResult.ExitCode ?? 0,
                Output = "Atualizado via API própria da WinProvision Store."
            };
        }

        WinProvisionLog.Write(
            $"UPDATE API-PROPRIA falhou packageId=\"{packageId}\" outcome={apiResult.Outcome} " +
            $"msg=\"{apiResult.Message}\" — caindo pro winget.exe (nível 3)");
        onLogReceived?.Invoke(
            $"API própria não conseguiu atualizar ({apiResult.Outcome}); usando winget.exe como último recurso.");
        return await _wingetExecutor.UpdateAppAsync(
            packageId, onLogReceived, cancellationToken, source).ConfigureAwait(false);
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
            FileName = WingetLocator.ExecutablePath,
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

    private async Task<WingetExecutionResult> RunComPackageOperationAsync(
        ComOperationKind opKind,
        string packageId,
        Action<string>? onLogReceived,
        Action<InstallProgressUpdate>? onProgress,
        CancellationToken cancellationToken,
        string? installLocation,
        string source,
        InstallAttemptState attemptState)
    {
        var stopwatch = Stopwatch.StartNew();
        // Rótulo usado em toda esta operação COM: "INSTALL COM" numa instalação nova,
        // "UPDATE COM" numa atualização — antes ficava fixo em "INSTALL COM" mesmo
        // durante updates, o que deixava o log de atualização com linhas do tipo
        // "INSTALL COM stage=install-dispatched" (confuso: parecia instalação, era update).
        string comTag = opKind == ComOperationKind.Install ? "INSTALL COM" : "UPDATE COM";
        WinProvisionLog.Write($"{comTag} stage=begin");

        // Callbacks da UI nunca devem derrubar o pipeline COM (uma exceção aqui viraria "falha da COM").
        var userOnProgress = onProgress;
        if (userOnProgress is not null)
        {
            onProgress = update =>
            {
                try { userOnProgress(update); }
                catch (Exception ex) { WinProvisionLog.Write($"{comTag} onProgress lançou {ex.GetType().Name}: {ex.Message}"); }
            };
        }

        var userOnLog = onLogReceived;
        if (userOnLog is not null)
        {
            onLogReceived = line =>
            {
                try { userOnLog(line); }
                catch (Exception ex) { WinProvisionLog.Write($"{comTag} onLog lançou {ex.GetType().Name}: {ex.Message}"); }
            };
        }
        Trace.WriteLine("WinGet COM install: creating resilient PackageManager.");
        var packageManager = WinGetFactoryHelper.CreateResilientPackageManager();
        WinProvisionLog.Write($"{comTag} stage=packageManager-created elapsed={stopwatch.Elapsed}");
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
            WinProvisionLog.Write(
                $"{comTag} stage=connect-completed catalog={kind} status={connectResult.Status} " +
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
            WinProvisionLog.Write(
                $"{comTag} stage=package-lookup-completed catalog={kind} found={matchedPackage is not null} " +
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
        WinProvisionLog.Write($"{comTag} stage=install-dispatched elapsed={stopwatch.Elapsed}");
        var installOperation = opKind == ComOperationKind.Install
            ? packageManager.InstallPackageAsync(matchedPackage, installOptions)
            : packageManager.UpgradePackageAsync(matchedPackage, installOptions);
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
                WinProvisionLog.Write(
                    $"{comTag} stage=first-progress state={state} " +
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
                WinProvisionLog.Write(
                    $"{comTag} progress callback={callbackNumber} state={state} " +
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
        WinProvisionLog.WriteComServerInfo();
        var heartbeat = MonitorComHeartbeatAsync(
            comTag,
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
        WinProvisionLog.Write(
            $"{comTag} stage=result status={installResult.Status} " +
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
            WinProvisionLog.Write(
                $"{comTag} failure status={installResult.Status} " +
                $"installerErrorCode=0x{installerErrorCode:X8} ({installerErrorCode}) " +
                $"extendedErrorCode=0x{extendedErrorCode:X8} ({extendedErrorCode})");

            bool requiresElevation =
                installerErrorCode == 740 || extendedErrorCode == unchecked((int)0x800702E4);

            // O instalador chegou a rodar e devolveu o próprio código de erro: repetir por outro
            // caminho executaria o mesmo instalador de novo (risco de instalação dupla/parcial).
            bool installerFailedItself = installerErrorCode != 0 && !requiresElevation;

            // Política bloqueando o pacote: contornar pela API própria seria burlar a política.
            bool blockedByPolicy = installResult.Status == InstallResultStatus.BlockedByPolicy;

            // Qualquer outra falha da COM (download, dependências do pacote, catálogo, erro
            // interno, sem instalador aplicável...) cai pra API própria e depois pro winget.exe.
            // Antes só a elevação caía; o resto voltava como falha final, sem fallback.
            if (WinGetFactoryHelper.CliFallbackAllowed && !installerFailedItself && !blockedByPolicy)
            {
                string reason = requiresElevation
                    ? "COM-requer-elevação"
                    : $"COM-status-{installResult.Status}(0x{extendedErrorCode:X8})";

                WinProvisionLog.Write($"FALLBACK PARA API PRÓPRIA: motivo={reason}");
                string actionLower = opKind == ComOperationKind.Install ? "instalação" : "atualização";
                onLogReceived?.Invoke(requiresElevation
                    ? $"A {actionLower} requer privilégios de administrador; usando a API própria da WinProvision Store."
                    : $"A API COM falhou ({installResult.Status}); usando a API própria da WinProvision Store.");
                return await RunApiThenCliFallbackAsync(
                    opKind,
                    packageId,
                    onLogReceived,
                    cancellationToken,
                    installLocation,
                    source,
                    reason).ConfigureAwait(false);
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

        string actionCap = opKind == ComOperationKind.Install ? "Instalação" : "Atualização";
        onLogReceived?.Invoke(
            installResult.RebootRequired
                ? $"{actionCap} concluída via API COM; reinicialização necessária."
                : $"{actionCap} concluída via API COM do WinGet.");
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
        string comTag,
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
                WinProvisionLog.Write(
                    $"{comTag} heartbeat state={progress.State} " +
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

    /// <summary>
    /// Nível 2 da cadeia de fallback (COM -&gt; API própria -&gt; CLI): tenta instalar via
    /// <see cref="WinProvisionApiService"/> (catálogo próprio hospedado no R2); só recorre
    /// ao winget.exe (<see cref="_wingetExecutor"/>) se a API própria também não resolver
    /// (pacote fora do catálogo próprio, sem installer compatível, hash divergente, etc.).
    /// Chamado a partir de todo ponto onde a API COM do WinGet é pulada ou falha, e do
    /// bypass direto de SYSTEM/reelevação-falhada no início de <see cref="InstallAsync"/>.
    /// </summary>
    private async Task<WingetExecutionResult> TryWinProvisionApiThenCliAsync(
        string packageId,
        Action<string>? onLogReceived,
        CancellationToken cancellationToken,
        string? installLocation,
        string source,
        string reason)
    {
        WinProvisionLog.Write($"INSTALL API-PROPRIA tentativa packageId=\"{packageId}\" motivo={reason}");

        WinProvisionInstallResult apiResult;
        try
        {
            apiResult = await _apiService.TryInstallAsync(
                packageId,
                onLogReceived is null ? null : new Progress<string>(onLogReceived),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A API própria nunca deve derrubar o pipeline: qualquer exceção inesperada
            // (rede fora do ar, R2 indisponível, etc.) é tratada como "não resolveu" e cai
            // pro winget.exe normalmente, igual a um WinProvisionInstallOutcome de falha.
            WinProvisionLog.Write(
                $"INSTALL API-PROPRIA exceção packageId=\"{packageId}\" {ex.GetType().Name}: {ex.Message}");
            apiResult = new WinProvisionInstallResult(
                WinProvisionInstallOutcome.DownloadFailed, Message: ex.Message);
        }

        if (apiResult.Outcome == WinProvisionInstallOutcome.Success)
        {
            WinProvisionLog.Write(
                $"INSTALL API-PROPRIA sucesso packageId=\"{packageId}\" exitCode={apiResult.ExitCode}");
            onLogReceived?.Invoke("Instalação concluída via API própria da WinProvision Store.");
            return new WingetExecutionResult
            {
                Success = true,
                ExitCode = apiResult.ExitCode ?? 0,
                Output = "Instalado via API própria da WinProvision Store."
            };
        }

        WinProvisionLog.Write(
            $"INSTALL API-PROPRIA falhou packageId=\"{packageId}\" outcome={apiResult.Outcome} " +
            $"msg=\"{apiResult.Message}\" — caindo pro winget.exe (nível 3)");
        onLogReceived?.Invoke(
            $"API própria não conseguiu instalar ({apiResult.Outcome}); usando winget.exe como último recurso.");
        return await _wingetExecutor.InstallAppAsync(
            packageId, onLogReceived, cancellationToken, installLocation, source).ConfigureAwait(false);
    }
}
