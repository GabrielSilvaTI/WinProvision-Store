using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.Win32;
using Microsoft.Management.Deployment;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Storage.Streams;
using WinProvision.Core.Services;
using WinProvision.Core.Services.Office;

namespace WinProvision.Store.Services;

public sealed record InstalledPackage(
    string Name,
    string Id,
    string Version,
    string Source,
    string Scope,
    string IconUrl,
    bool IsOffice = false,
    bool IsSystemComponent = false);

public sealed class InstalledPackageClassifier(OfficeInstalledProductsDetector detector)
{
    public bool IsOffice(InstalledPackage package)
    {
        bool microsoftOfficeSignal = package.Id.Contains("Microsoft.Office", StringComparison.OrdinalIgnoreCase);
        bool detectedOfficeRow = detector.HasAnyInstallation()
            && (package.Name.Contains("Office", StringComparison.OrdinalIgnoreCase)
                || package.Id.Contains("Office", StringComparison.OrdinalIgnoreCase));
        return microsoftOfficeSignal || detectedOfficeRow;
    }

    public static bool IsSystemComponent(InstalledPackage package)
    {
        string value = $"{package.Name} {package.Id}".ToLowerInvariant();
        return value.Contains("visual c++")
            || value.Contains("redistributable")
            || value.Contains(".net")
            || value.Contains("desktop runtime")
            || value.Contains("webview2")
            || value.Contains("app installer")
            || value.Contains("microsoft.winget")
            || value.Contains("winprovision");
    }
}

public sealed class InstalledPackagesService
{
    private static readonly TimeSpan ComTimeout = TimeSpan.FromSeconds(5);
    private readonly InstalledAppIconResolver _iconResolver;

    public InstalledPackagesService(InstalledAppIconResolver iconResolver)
    {
        _iconResolver = iconResolver;
    }

    public async Task<IReadOnlyList<InstalledPackage>> ListAsync(CancellationToken cancellationToken = default)
    {
        if (!WinGetFactoryHelper.IsComDisabled)
        {
            try
            {
                var comTask = Task.Run(ListCom, cancellationToken);
                var completed = await Task.WhenAny(comTask, Task.Delay(ComTimeout, cancellationToken));
                if (completed == comTask)
                {
                    var comPackages = await comTask;
                    if (comPackages.Count > 0)
                        return comPackages;

                    WinGetFactoryHelper.ForceDisableComForSession("catálogo COM local retornou zero pacotes");
                    WinGetDiagnosticLog.Write("INSTALLED LIST FALLBACK motivo=COM retornou zero pacotes");
                }

                else
                {
                    WinGetFactoryHelper.ForceDisableComForSession("lista COM excedeu o timeout");
                    WinGetDiagnosticLog.Write("INSTALLED LIST FALLBACK motivo=COM timeout");
                }
            }
            catch (Exception ex)
            {
                WinGetFactoryHelper.DisableComForSession(ex);
                WinGetFactoryHelper.ForceDisableComForSession("falha ao listar pacotes via COM");
                WinGetDiagnosticLog.Write($"INSTALLED LIST FALLBACK motivo=COM exception={ex}");
            }
        }
        else
        {
            WinGetDiagnosticLog.Write("INSTALLED LIST FALLBACK motivo=COM desabilitado na sessão");
        }

        return await ListCliAsync(cancellationToken);
    }

    private static async Task<IReadOnlyList<InstalledPackage>> ListCom()
    {
        var manager = WinGetFactoryHelper.CreateResilientPackageManager();
        var catalogReference = manager.GetLocalPackageCatalog(LocalPackageCatalog.InstalledPackages);
        catalogReference.InstalledPackageInformationOnly = true;
        var connectResult = await catalogReference.ConnectAsync().AsTask().ConfigureAwait(false);
        if (connectResult.Status != ConnectResultStatus.Ok || connectResult.PackageCatalog is null)
            throw new InvalidOperationException($"Falha ao conectar ao catálogo local: {connectResult.Status}.");

        var options = WinGetFactoryHelper.CreateFindPackagesOptions();
        var selector = WinGetFactoryHelper.CreatePackageMatchFilter();
        selector.Field = PackageMatchField.Name;
        selector.Option = PackageFieldMatchOption.ContainsCaseInsensitive;
        selector.Value = string.Empty;
        options.Selectors.Add(selector);

        var findResult = await connectResult.PackageCatalog.FindPackagesAsync(options)
            .AsTask().ConfigureAwait(false);
        var result = new List<InstalledPackage>();
        foreach (var match in findResult.Matches.ToArray())
        {
            var package = match.CatalogPackage;
            var installed = package.InstalledVersion;
            if (installed is null)
                continue;

            string source = installed.PackageCatalog?.Info?.Name ?? string.Empty;
            result.Add(new InstalledPackage(
                package.Name,
                package.Id,
                installed.Version,
                source,
                string.Empty,
                string.Empty));
        }
        WinGetDiagnosticLog.Write($"INSTALLED LIST COM count={result.Count}");
        return result;
    }

    private static async Task<IReadOnlyList<InstalledPackage>> ListCliAsync(CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo
        {
            FileName = "winget.exe",
            Arguments = "list --disable-interactivity --accept-source-agreements",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8
        };
        using var process = Process.Start(info) ?? throw new InvalidOperationException("winget.exe não encontrado.");
        string output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var rows = ParseCliOutput(output);
        WinGetDiagnosticLog.Write($"INSTALLED LIST FALLBACK count={rows.Count}");
        return rows;
    }

    public static IReadOnlyList<InstalledPackage> ParseCliOutput(string output)
    {
        var lines = output.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        int separator = Array.FindIndex(lines, line => line.Trim().Length >= 3 && line.Trim().All(c => c == '-'));
        if (separator < 1) return [];
        string header = lines[separator - 1];
        var headerMatches = Regex.Matches(header, @"\S+").Cast<Match>().ToArray();
        if (headerMatches.Length < 3) return [];

        int nameStart = 0;
        int idStart = FindHeaderStart(headerMatches, "Id", "ID", "Identificação");
        int versionStart = FindHeaderStart(headerMatches, "Version", "Versão");
        int availableStart = FindHeaderStart(headerMatches, "Available", "Disponível");
        int sourceStart = FindHeaderStart(headerMatches, "Source", "Origem");
        if (idStart < 0 || versionStart < 0) return [];

        var result = new List<InstalledPackage>();
        foreach (string line in lines[(separator + 1)..])
        {
            string trimmedLine = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmedLine)
                || trimmedLine.StartsWith("No installed", StringComparison.OrdinalIgnoreCase)
                || Regex.IsMatch(trimmedLine, @"^\d+\s+(updates?|atualizaç(?:ão|ões)|packages?|pacotes?)\b", RegexOptions.IgnoreCase)
                || Regex.IsMatch(trimmedLine, @"(updates?|atualizaç(?:ão|ões))\s+(available|disponíve(?:l|is))", RegexOptions.IgnoreCase))
                continue;

            string name = Slice(line, nameStart, idStart).Trim();
            string id = Slice(line, idStart, versionStart).Trim();
            int versionEnd = availableStart >= 0
                ? availableStart
                : sourceStart >= 0 ? sourceStart : line.Length;
            string version = Slice(line, versionStart, versionEnd).Trim();
            string source = sourceStart >= 0 ? Slice(line, sourceStart, line.Length).Trim() : string.Empty;
            if (string.IsNullOrWhiteSpace(id)
                || id.Equals("Id", StringComparison.OrdinalIgnoreCase)
                || id.Equals("Identificação", StringComparison.OrdinalIgnoreCase)
                || id.Equals("No", StringComparison.OrdinalIgnoreCase))
                continue;

            result.Add(new InstalledPackage(name, id, version, source, "", ""));
        }
        return result;

        static int FindHeaderStart(IReadOnlyList<Match> matches, params string[] names)
        {
            var match = matches.FirstOrDefault(m => names.Any(name =>
                m.Value.Equals(name, StringComparison.OrdinalIgnoreCase)));
            return match?.Index ?? -1;
        }

        static string Slice(string value, int start, int end)
        {
            if (start < 0 || start >= value.Length || end <= start)
                return string.Empty;
            return value[start..Math.Min(end, value.Length)];
        }
    }

    public Task<IReadOnlyList<InstalledPackage>> ResolveIconsAsync(IEnumerable<InstalledPackage> packages, CancellationToken cancellationToken = default) => _iconResolver.ResolveAsync(packages, cancellationToken);
}
