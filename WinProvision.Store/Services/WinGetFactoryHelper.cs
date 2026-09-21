using System;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Management.Deployment;
using WinRT;

namespace WinProvision.Store.Services;

// Ativação resiliente do PackageManager do WinGet, incluindo o caso de
// processo elevado sem MSIX (RegFree-WinRT), que crasha tanto no
// StandardFactory quanto, às vezes, no ElevatedFactory.
public static class WinGetFactoryHelper
{
    private const int RpcServerUnavailable = unchecked((int)0x800706BA);
    private const int ClassNotRegistered = unchecked((int)0x80040154);
    private const int ServerExecFailure = unchecked((int)0x80080005);   // CO_E_SERVER_EXEC_FAILURE
    private const int AccessDenied = unchecked((int)0x80070005);        // E_ACCESSDENIED
    private const int RpcDisconnected = unchecked((int)0x80010108);     // RPC_E_DISCONNECTED
    private const int RpcServerFault = unchecked((int)0x80010105);      // RPC_E_SERVERFAULT
    private const int RpcCallFailed = unchecked((int)0x800706BE);       // RPC_S_CALL_FAILED

    // Estado da COM = circuit breaker com recuperação (antes era uma flag permanente por sessão:
    // uma única falha de ativação, por exemplo o App Installer ainda não provisionado no primeiro
    // logon / Windows Sandbox, mantinha TODAS as instalações no winget.exe até fechar o app).
    // Agora a COM só fica indisponível por um período curto e crescente (30s, 60s, 2min... até 5min),
    // é reavaliada sozinha e qualquer sucesso zera o estado.
    private static readonly TimeSpan BreakerBaseCooldown = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan BreakerMaxCooldown = TimeSpan.FromMinutes(5);
    private static long _breakerOpenUntilTicks;
    private static int _consecutiveActivationFailures;
    private static string? _disabledReason;
    private static WinGetMode _mode = ReadModeFromEnvironment();

    [Flags]
    private enum CLSCTX : uint
    {
        CLSCTX_LOCAL_SERVER = 0x4,
        // Valor real confirmado em wtypesbase.h / windows-rs: 0x4000000.
        // (Documentação enviada trazia 0x00200000, incorreto.)
        CLSCTX_ALLOW_LOWER_TRUST_REGISTRATION = 0x4000000
    }

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(
        ref Guid rclsid,
        IntPtr pUnkOuter,
        CLSCTX dwClsContext,
        ref Guid riid,
        out IntPtr ppv);

    // CLSID/IID do PackageManager (runtime class + interface nativa), não de
    // uma "fábrica" COM à parte, essa não existe. Confirmados via stack trace
    // real de crash do UniGetUI (issue #4750, Devolutions/UniGetUI):
    // "WinGet COM activation failed for CLSID c53a4f16-787e-42a4-b304-29effb4bf597
    //  (IID b375e3b9-f2e0-5c93-87a7-b67497f7e593, AllowLowerTrustRegistration=True)"
    private static readonly Guid ClsidPackageManager = new("C53A4F16-787E-42A4-B304-29EFFB4BF597");
    private static readonly Guid IidPackageManager = new("B375E3B9-F2E0-5C93-87A7-B67497F7E593");

    // Release CLSIDs: microsoft/winget-cli ComClsids.h,
    // commit 5b62860167520b1503b3880d5a026809eb07c6f4.
    // The interface IIDs are the CsWinRT projections generated from PackageManager.idl;
    // the same CLSID/IID mapping is used by Devolutions/UniGetUI ClassesDefinition.cs,
    // commit 5e8b14e102780e05a72dd79afc9e20f584da8d12.
    private static readonly Guid ClsidFindPackagesOptions = new("572DED96-9C60-4526-8F92-EE7D91D38C1A");
    private static readonly Guid IidFindPackagesOptions = new("A5270EDD-7DA7-57A3-BACE-F2593553561F");
    private static readonly Guid ClsidInstallOptions = new("1095F097-EB96-453B-B4E6-1613637F3B14");
    private static readonly Guid IidInstallOptions = new("6EE9DB69-AB48-5E72-A474-33A924CD23B3");
    private static readonly Guid ClsidPackageMatchFilter = new("D02C9DAF-99DC-429C-B503-4E504E4AB000");
    private static readonly Guid IidPackageMatchFilter = new("D981ECA3-4DE5-5AD7-967A-698C7D60FC3B");
    private static readonly Guid ClsidCreateCompositePackageCatalogOptions = new("526534B8-7E46-47C8-8416-B1685C327D37");
    private static readonly Guid IidCreateCompositePackageCatalogOptions = new("21ABAA76-089D-51C5-A745-C85EEFE70116");
    private static readonly Guid ClsidUninstallOptions = new("E1D9A11E-9F85-4D87-9C17-2B93143ADB8D");
    private static readonly Guid IidUninstallOptions = new("3EBC67F0-8339-594B-8A42-F90B69D02BBE");

    /// <summary>Modo de operação (env WINPROVISION_WINGET_MODE ou argumento /winget-mode=auto|com|cli).</summary>
    public static WinGetMode Mode => _mode;

    /// <summary>False no modo COM-only: nenhuma falha da COM pode cair silenciosamente no winget.exe.</summary>
    public static bool CliFallbackAllowed => _mode != WinGetMode.ComOnly;

    /// <summary>True quando as operações devem usar o CLI (modo cli, ou COM temporariamente indisponível).</summary>
    public static bool IsComDisabled =>
        _mode == WinGetMode.CliOnly ||
        (_mode == WinGetMode.Auto &&
         Environment.TickCount64 < Interlocked.Read(ref _breakerOpenUntilTicks));

    public static string DisabledReason =>
        _mode == WinGetMode.CliOnly
            ? "modo CLI forçado (WINPROVISION_WINGET_MODE=cli ou /winget-mode=cli)"
            : Volatile.Read(ref _disabledReason) ?? "desconhecido";

    public static void ConfigureMode(string[] args)
    {
        foreach (var arg in args)
        {
            var separator = arg.IndexOf('=');
            if (separator <= 0 ||
                !arg[..separator].TrimStart('/', '-').Equals("winget-mode", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (TryParseMode(arg[(separator + 1)..], out var mode))
            {
                _mode = mode;
            }
        }

        WinGetDiagnosticLog.Write($"WINGET MODE={_mode} (auto=COM com fallback CLI, com=só COM, cli=só winget.exe)");
    }

    private static WinGetMode ReadModeFromEnvironment() =>
        TryParseMode(Environment.GetEnvironmentVariable("WINPROVISION_WINGET_MODE"), out var mode)
            ? mode
            : WinGetMode.Auto;

    private static bool TryParseMode(string? value, out WinGetMode mode)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "com": mode = WinGetMode.ComOnly; return true;
            case "cli": mode = WinGetMode.CliOnly; return true;
            case "auto": mode = WinGetMode.Auto; return true;
            default: mode = WinGetMode.Auto; return false;
        }
    }

    /// <summary>Falha de ativação/servidor: a COM inteira está indisponível (não é erro de um pacote).</summary>
    public static bool IsActivationFailure(Exception exception) =>
        exception.HResult is RpcServerUnavailable or ClassNotRegistered or ServerExecFailure or AccessDenied;

    /// <summary>Falha provavelmente passageira (servidor COM reiniciou, RPC caiu): vale repetir com nova ativação.</summary>
    public static bool IsTransient(Exception exception) =>
        exception is WinGetComPreflightException { IsTransient: true } ||
        exception.HResult is RpcServerUnavailable or RpcDisconnected or RpcServerFault or RpcCallFailed;

    public static void DisableComForSession(Exception exception)
    {
        if (!IsActivationFailure(exception))
        {
            return;
        }

        OpenBreaker(
            $"0x{exception.HResult:X8} tipo={exception.GetType().FullName} mensagem=\"{exception.Message}\"");
    }

    public static void ForceDisableComForSession(string reason) => OpenBreaker(reason);

    private static void OpenBreaker(string reason)
    {
        var failures = Interlocked.Increment(ref _consecutiveActivationFailures);
        var cooldownTicks = Math.Min(
            BreakerBaseCooldown.Ticks << Math.Min(failures - 1, 4),
            BreakerMaxCooldown.Ticks);
        var cooldown = TimeSpan.FromTicks(cooldownTicks);
        Interlocked.Exchange(
            ref _breakerOpenUntilTicks,
            Environment.TickCount64 + (long)cooldown.TotalMilliseconds);
        Volatile.Write(ref _disabledReason, reason);
        WinGetDiagnosticLog.Write(
            $"COM INDISPONÍVEL: motivo={reason} falhas-consecutivas={failures} " +
            $"nova-tentativa-em={cooldown.TotalSeconds:0}s");
    }

    /// <summary>Chamado após qualquer ativação/conexão COM bem-sucedida: zera o breaker.</summary>
    public static void ReportComSuccess()
    {
        var hadFailures = Interlocked.Exchange(ref _consecutiveActivationFailures, 0) != 0;
        var wasOpen = Interlocked.Exchange(ref _breakerOpenUntilTicks, 0) != 0;
        if (hadFailures || wasOpen)
        {
            WinGetDiagnosticLog.Write("COM RECUPERADA: ativação bem-sucedida; voltando a usar a API COM");
        }
    }

    /// <summary>
    /// Autoteste em segundo plano na abertura do app: ativa o PackageManager e conecta ao catálogo.
    /// Aquece o servidor COM (a primeira ativação é a mais lenta) e deixa no log, logo no início,
    /// se a COM está saudável ou por que não está.
    /// </summary>
    public static async Task ProbeAsync(CancellationToken cancellationToken = default)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var packageManager = CreateResilientPackageManager();
            var reference = packageManager.GetPredefinedPackageCatalog(
                PredefinedPackageCatalog.OpenWindowsCatalog);
            reference.AcceptSourceAgreements = true;

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(90));
            var connect = await reference.ConnectAsync().AsTask(timeout.Token).ConfigureAwait(false);

            WinGetDiagnosticLog.Write(
                $"COM PROBE status={connect.Status} elapsed={stopwatch.Elapsed} mode={_mode}");
            if (connect.Status == ConnectResultStatus.Ok)
            {
                ReportComSuccess();
            }

            WinGetDiagnosticLog.WriteComServerInfo();
        }
        catch (Exception ex)
        {
            DisableComForSession(ex);
            WinGetDiagnosticLog.Write(
                $"COM PROBE falhou HRESULT=0x{ex.HResult:X8} elapsed={stopwatch.Elapsed} " +
                $"mode={_mode} exception={ex}");
        }
    }

    public static PackageManager CreateResilientPackageManager() =>
        CreateInstance<PackageManager>(ClsidPackageManager, IidPackageManager);

    public static FindPackagesOptions CreateFindPackagesOptions() =>
        CreateInstance<FindPackagesOptions>(ClsidFindPackagesOptions, IidFindPackagesOptions);

    public static InstallOptions CreateInstallOptions() =>
        CreateInstance<InstallOptions>(ClsidInstallOptions, IidInstallOptions);

    public static PackageMatchFilter CreatePackageMatchFilter() =>
        CreateInstance<PackageMatchFilter>(ClsidPackageMatchFilter, IidPackageMatchFilter);

    public static CreateCompositePackageCatalogOptions CreateCompositePackageCatalogOptions() =>
        CreateInstance<CreateCompositePackageCatalogOptions>(
            ClsidCreateCompositePackageCatalogOptions,
            IidCreateCompositePackageCatalogOptions);

    public static UninstallOptions CreateUninstallOptions() =>
        CreateInstance<UninstallOptions>(ClsidUninstallOptions, IidUninstallOptions);

    public static T CreateInstance<T>(Guid clsid, Guid iid)
    {
        IntPtr pUnknown = IntPtr.Zero;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            const CLSCTX ctx = CLSCTX.CLSCTX_LOCAL_SERVER | CLSCTX.CLSCTX_ALLOW_LOWER_TRUST_REGISTRATION;

            WinGetDiagnosticLog.Write(
                $"COM ACTIVATION type={typeof(T).Name} strategy=CoCreateInstance " +
                $"CLSID={clsid} IID={iid} CLSCTX=0x{(uint)ctx:X8}");
            int hr = CoCreateInstance(ref clsid, IntPtr.Zero, ctx, ref iid, out pUnknown);
            WinGetDiagnosticLog.Write(
                $"COM ACTIVATION type={typeof(T).Name} CoCreateInstance " +
                $"HRESULT=0x{hr:X8} elapsed={stopwatch.Elapsed}");
            Marshal.ThrowExceptionForHR(hr);

            var instance = MarshalGeneric<T>.FromAbi(pUnknown);
            WinGetDiagnosticLog.Write(
                $"COM ACTIVATION type={typeof(T).Name} FromAbi=success elapsed={stopwatch.Elapsed}");
            return instance;
        }
        catch (Exception ex)
        {
            WinGetDiagnosticLog.Write(
                $"COM ACTIVATION type={typeof(T).Name} failed " +
                $"HRESULT=0x{ex.HResult:X8} elapsed={stopwatch.Elapsed} exception={ex}");
            throw;
        }
        finally
        {
            // CoCreateInstance e FromAbi fazem AddRef cada um. Um Release evita vazamento.
            if (pUnknown != IntPtr.Zero)
            {
                Marshal.Release(pUnknown);
            }
        }
    }
}

public enum WinGetMode
{
    /// <summary>Padrão: COM primeiro; CLI só quando a COM não puder cumprir a operação.</summary>
    Auto,

    /// <summary>Diagnóstico: nunca usa o winget.exe; falhas da COM aparecem com o erro real.</summary>
    ComOnly,

    /// <summary>Contorno: nunca usa a COM.</summary>
    CliOnly
}

/// <summary>Falha ANTES de a instalação ser entregue ao servidor COM (conexão, catálogo, pacote).</summary>
public sealed class WinGetComPreflightException(string message, bool isTransient = false)
    : InvalidOperationException(message)
{
    public bool IsTransient { get; } = isTransient;
}
