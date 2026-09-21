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
    private static int _comDisabled;
    private static string? _disabledReason;

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

    public static bool IsComDisabled => Volatile.Read(ref _comDisabled) != 0;

    /// <summary>Motivo real pelo qual a COM foi desativada na sessão (para o log de diagnóstico).</summary>
    public static string DisabledReason => Volatile.Read(ref _disabledReason) ?? "desconhecido";

    public static void DisableComForSession(Exception exception)
    {
        if (exception.HResult is not (RpcServerUnavailable or ClassNotRegistered) ||
            Interlocked.Exchange(ref _comDisabled, 1) != 0)
        {
            return;
        }

        Volatile.Write(ref _disabledReason,
            $"0x{exception.HResult:X8} {exception.GetType().Name}");
        WinGetDiagnosticLog.Write(
            $"COM DESATIVADO NA SESSÃO: motivo=0x{exception.HResult:X8} " +
            $"tipo={exception.GetType().FullName} mensagem=\"{exception.Message}\"");
    }

    public static void ForceDisableComForSession(string reason)
    {
        if (Interlocked.Exchange(ref _comDisabled, 1) == 0)
        {
            Volatile.Write(ref _disabledReason, reason);
            WinGetDiagnosticLog.Write($"COM DESATIVADO NA SESSÃO: motivo={reason}");
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
