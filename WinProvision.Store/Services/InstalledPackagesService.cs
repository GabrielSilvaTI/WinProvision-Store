using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using System.Runtime.InteropServices;
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

    // Termos que identificam runtimes, frameworks e componentes internos do Windows
    // — nunca "apps" no sentido que o usuário reconheceria, e desinstalar manualmente
    // costuma quebrar outros programas que dependem deles.
    private static readonly string[] SystemKeywords =
    [
        "visual c++", "redistributable", ".net", "desktop runtime", "webview2",
        "app installer", "microsoft.winget", "winprovision", "vclibs",
        "ui.xaml", "windows app runtime", "windows app sdk", "net native",
        "direct x", "directx", "security intelligence", "subsystem for linux",
        "edge update", "edge webview"
    ];

    // Nomes de família (PackageFamilyName sem o sufixo hash do publisher) dos apps
    // embutidos/de sistema do Windows mais comuns — Calculadora, Bloco de Notas etc.
    // Só aparecem no catálogo COM completo (LocalPackageCatalog.InstalledPackages),
    // nunca no Painel de Programas clássico, e não tem sentido oferecer pra
    // desinstalar por aqui. Lista não exaustiva, dá pra estender conforme aparecerem
    // mais casos.
    private static readonly string[] InboxAppFamilyPrefixes =
    [
        "Microsoft.WindowsCalculator", "Microsoft.WindowsNotepad", "Microsoft.Windows.Photos",
        "Microsoft.WindowsStore", "Microsoft.WindowsCamera", "Microsoft.WindowsSoundRecorder",
        "Microsoft.WindowsAlarms", "Microsoft.WindowsMaps", "Microsoft.WindowsFeedbackHub",
        "Microsoft.Getstarted", "Microsoft.MicrosoftStickyNotes",
        "Microsoft.Windows.CloudExperienceHost", "Microsoft.Windows.ShellExperienceHost",
        "Microsoft.Windows.StartMenuExperienceHost", "Microsoft.Windows.SecHealthUI",
        "Microsoft.SecHealthUI", "Microsoft.AAD.BrokerPlugin", "Microsoft.AccountsControl",
        "Microsoft.LockApp", "Microsoft.CredDialogHost", "Microsoft.ECApp",
        "Microsoft.Win32WebViewHost", "Microsoft.XboxGameCallableUI", "Microsoft.XboxIdentityProvider",
        "Microsoft.PPIProjection", "Microsoft.Windows.CapturePicker", "Microsoft.Windows.NarratorQuickStart",
        "Microsoft.Windows.ParentalControls", "Microsoft.Windows.PeopleExperienceHost",
        "Microsoft.Windows.PinningConfirmationDialog", "Microsoft.Windows.PrintDialog",
        "Microsoft.549981C3F5F10", "MicrosoftWindows.Client.CBS", "MicrosoftWindows.Client.Core",
        "MicrosoftWindows.Client.WebExperience"
    ];

    // PackageFamilyName clássico: "Publicador.NomeDoApp_" + 13 caracteres em
    // base32 (0-9, a-h, j-k, m-n, p-t, v-z — sem i/l/o/u) identificando o publisher.
    // Nenhum PackageIdentifier do catálogo winget tem essa forma; só aparece quando
    // o catálogo COM devolve o nome de família de um pacote MSIX/AppX diretamente.
    private static readonly Regex PackageFamilyNamePattern =
        new(@"^[\w.]+_[0-9a-hj-km-np-tv-z]{13}$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <param name="hasArpEntry">
    /// True quando o pacote tem uma entrada clássica de desinstalação (Painel de
    /// Programas/registro) — a forma "de verdade" de gerenciar um app no Windows.
    /// </param>
    public static bool IsSystemComponent(InstalledPackage package, bool hasArpEntry)
    {
        string value = $"{package.Name} {package.Id}".ToLowerInvariant();
        if (SystemKeywords.Any(term => value.Contains(term, StringComparison.Ordinal)))
            return true;

        if (InboxAppFamilyPrefixes.Any(prefix => package.Id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            return true;

        // Componente da própria Microsoft sem nenhuma entrada clássica de
        // desinstalação (sem ícone, sem pasta de instalação navegável) — sobra do
        // catálogo COM completo (host interno, framework não listado acima etc.),
        // não um app que o usuário reconheceria ou instalou de propósito.
        if (!hasArpEntry
            && package.Id.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase)
            && PackageFamilyNamePattern.IsMatch(package.Id))
            return true;

        return false;
    }
}

public sealed class InstalledPackagesService
{
    private static readonly TimeSpan ComTimeout = TimeSpan.FromSeconds(30);
    private readonly WingetBootstrapper _bootstrapper;

    public InstalledPackagesService(WingetBootstrapper bootstrapper)
    {
        _bootstrapper = bootstrapper;
    }

    public async Task<IReadOnlyList<InstalledPackage>> ListAsync(CancellationToken cancellationToken = default)
    {
        // Passo zero: winget provisionado antes da COM ou do winget.exe. Falha não aborta;
        // a listagem cai no que estiver disponível, como antes.
        try
        {
            var provisioned = await _bootstrapper.EnsureOnceAsync(WinGetDiagnosticLog.Write, cancellationToken);
            if (!provisioned.IsUsable)
            {
                WinGetDiagnosticLog.Write($"INSTALLED LIST winget indisponível: {provisioned.ErrorMessage}");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            WinGetDiagnosticLog.Write($"INSTALLED LIST provisionamento falhou {ex.GetType().Name}: {ex.Message}");
        }

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
                    {
                        WinGetFactoryHelper.ReportComSuccess();
                        return comPackages;
                    }

                    // Zero pacotes é um resultado válido (máquina limpa / Windows Sandbox):
                    // usa o CLI só nesta chamada e mantém a COM ativa para as instalações.
                    WinGetDiagnosticLog.Write("INSTALLED LIST FALLBACK motivo=COM retornou zero pacotes (COM segue ativa)");
                }
                else
                {
                    // Timeout de listagem não invalida a ativação da COM.
                    WinGetDiagnosticLog.Write("INSTALLED LIST FALLBACK motivo=COM timeout (COM segue ativa)");
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Só desativa a COM da sessão em falha de ativação (RPC indisponível / classe
                // não registrada); o filtro de HRESULT fica dentro do DisableComForSession.
                WinGetFactoryHelper.DisableComForSession(ex);
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
            FileName = WingetLocator.ExecutablePath,
            Arguments = "list --disable-interactivity --accept-source-agreements",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8
        };
        global::WinProvision.Core.Services.WingetCliAudit.Launch(info.FileName, info.Arguments);
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

    public async Task<IReadOnlyList<InstalledPackage>> ResolveIconsAsync(
        IEnumerable<InstalledPackage> packages,
        CancellationToken cancellationToken = default)
    {
        var source = packages.ToArray();
        var uninstallEntriesTask = Task.Run(ReadUninstallEntries, cancellationToken);
        var appxIconsTask = Task.Run(ReadAppxIconIndex, cancellationToken);
        var shortcutsTask = Task.Run(ReadStartMenuShortcuts, cancellationToken);
        var appsFolderAumidsTask = Task.Run(ReadAppsFolderAumids, cancellationToken);

        var uninstallEntries = await uninstallEntriesTask;
        var appxIcons = await appxIconsTask;
        var shortcuts = await shortcutsTask;
        var aumids = await appsFolderAumidsTask;

        using var gate = new SemaphoreSlim(4);
        var resolved = new InstalledPackage[source.Length];
        int withIcon = 0;

        await Task.WhenAll(source.Select(async (package, index) =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                string? iconUrl = null;
                string method = "f";
                var failures = new List<string>();

                var entry = FindMatchingEntry(package, uninstallEntries);

                if (TryFindAppxIcon(package, appxIcons, out string? appxAsset))
                {
                    iconUrl = CopyAppxAssetToCache(appxAsset!, package.Id);
                    if (iconUrl is not null) method = "store-png";
                    else failures.Add("store-png:falha ao copiar asset PNG");
                }

                string? displayIcon = entry?.DisplayIcon;
                if (iconUrl is null && !string.IsNullOrWhiteSpace(displayIcon))
                {
                    iconUrl = ResolveLocalIcon(displayIcon, package.Id, out string reason);
                    if (iconUrl is not null) method = "a";
                    else failures.Add($"a:{reason}");
                }

                if (iconUrl is null)
                {
                    string? executable = FindMainExecutable(entry);
                    if (executable is not null)
                    {
                        iconUrl = ResolveLocalIcon(executable, package.Id, out string reason);
                        if (iconUrl is not null) method = "b";
                        else failures.Add($"b:{reason}");
                    }
                    else failures.Add("b:InstallLocation/UninstallString sem executável local");
                }

                if (iconUrl is null)
                {
                    var shortcut = FindBestShortcut(package.Name, entry?.DisplayName, shortcuts);
                    if (shortcut is not null)
                    {
                        iconUrl = ResolveShortcutIcon(shortcut, package.Id, out string reason);
                        if (iconUrl is not null) method = "c";
                        else failures.Add($"c:{reason}");
                    }
                    else failures.Add("c:nenhum atalho do Menu Iniciar correspondeu");
                }

                if (iconUrl is null && TryGetAppsFolderPath(package, aumids, out string appsFolderPath))
                {
                    iconUrl = ResolveShellItemIcon(appsFolderPath, package.Id, out string reason);
                    if (iconUrl is not null) method = "d";
                    else failures.Add($"d:{reason}");
                }
                else if (iconUrl is null)
                {
                    failures.Add("d:PackageFamilyName/AppId não derivado");
                }

                if (iconUrl is null)
                    failures.Add("f:ícone genérico");
                else Interlocked.Increment(ref withIcon);

                WinGetDiagnosticLog.Write(
                    $"INSTALLED ICON item=\"{package.Name}\" id=\"{package.Id}\" method={method} " +
                    $"status={(iconUrl is null ? "missing" : "ok")} " +
                    $"failures=\"{string.Join(" | ", failures)}\"");

                resolved[index] = package with
                {
                    IconUrl = iconUrl ?? IconService.DefaultIconPackUri,
                    IsSystemComponent = InstalledPackageClassifier.IsSystemComponent(package, entry is not null)
                };
            }
            finally { gate.Release(); }
        }));

        WinGetDiagnosticLog.Write($"INSTALLED ICON SUMMARY with={withIcon} without={source.Length - withIcon}");
        return resolved;
    }

    private static string? ResolveLocalIcon(string? iconReference, string id, out string reason)
    {
        reason = string.Empty;
        if (string.IsNullOrWhiteSpace(iconReference))
        {
            reason = "referência vazia";
            return null;
        }

        var (path, iconIndex) = ParseIconReference(iconReference);
        if (!File.Exists(path))
        {
            reason = $"arquivo não encontrado: {path}";
            return null;
        }

        string modified = File.GetLastWriteTimeUtc(path).Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string cacheKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"local-v6|{id}|{path}|{iconIndex}|{modified}"))).ToLowerInvariant();
        string cacheFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinProvisionStore", "Cache", "Icons");
        Directory.CreateDirectory(cacheFolder);
        string destination = Path.Combine(cacheFolder, $"arp-{cacheKey}.png");
        if (File.Exists(destination)) return destination;

        try
        {
            // Para executáveis, leia diretamente o grupo de ícones embutido no
            // arquivo. Isso não depende do estado do Explorer/Shell nem de uma
            // thread STA e é a fonte correta para DisplayIcon apontando para um
            // .exe (por exemplo, Code.exe).
            bool extracted = TryExtractIconResource(path, iconIndex, destination);
            if (!extracted && iconIndex != 0)
            {
                extracted = TryExtractIconResource(path, 0, destination);
            }
            if (!extracted)
            {
                extracted = TryExtractAssociatedIcon(path, destination);
            }
            if (!extracted)
            {
                extracted = TryExtractShellIcon(path, destination);
            }
            if (extracted)
                return destination;
            reason = iconIndex != 0
                ? $"ExtractIconEx não retornou o índice {iconIndex} e os fallbacks falharam"
                : "ExtractIconEx não retornou o grupo de ícones do executável";
        }
        catch (Exception ex)
        {
            reason = ex.Message;
        }

        return null;
    }

    private sealed record AppxIconEntry(string PackageFullName, string PackageFamilyName, string Name, string AssetPath);

    private static IReadOnlyList<AppxIconEntry> ReadAppxIconIndex()
    {
        var result = new List<AppxIconEntry>();
        const string applicationsPath = @"Software\Microsoft\Windows\CurrentVersion\Appx\AppxAllUserStore\Applications";

        foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var applications = root.OpenSubKey(applicationsPath);
                if (applications is null) continue;

                foreach (string fullName in applications.GetSubKeyNames())
                {
                    using var packageKey = applications.OpenSubKey(fullName);
                    string? manifest = packageKey?.GetValue("Path") as string;
                    if (string.IsNullOrWhiteSpace(manifest) || !File.Exists(manifest))
                        continue;

                    AddAppxIconEntry(result, fullName,
                        Directory.GetParent(Path.GetDirectoryName(manifest)!)?.FullName ?? string.Empty,
                        manifest);
                }
            }
            catch (Exception ex)
            {
                WinGetDiagnosticLog.Write($"INSTALLED APPX read failed view={view} error={ex.Message}");
            }
        }

        try
        {
            using var userRoot = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
            using var packages = userRoot.OpenSubKey(
                @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages");
            if (packages is not null)
            {
                foreach (string fullName in packages.GetSubKeyNames())
                {
                    using var packageKey = packages.OpenSubKey(fullName);
                    string? installLocation = packageKey?.GetValue("PackageRootFolder") as string;
                    if (string.IsNullOrWhiteSpace(installLocation))
                        continue;
                    string manifest = Path.Combine(installLocation, "AppxManifest.xml");
                    AddAppxIconEntry(result, fullName, installLocation, manifest);
                }
            }
        }
        catch (Exception ex)
        {
            WinGetDiagnosticLog.Write($"INSTALLED APPX per-user read failed error={ex.Message}");
        }

        return result
            .GroupBy(x => $"{x.PackageFullName}|{x.AssetPath}", StringComparer.OrdinalIgnoreCase)
            .Select(x => x.First())
            .ToArray();
    }

    private static void AddAppxIconEntry(
        ICollection<AppxIconEntry> result,
        string fullName,
        string installLocation,
        string manifest)
    {
        if (!Directory.Exists(installLocation))
            return;

        string packageName = fullName.Split('_')[0];
        string familyName = BuildPackageFamilyName(fullName);
        string displayName = ReadManifestDisplayName(manifest) ?? packageName;
        string? asset = FindBestAppxAsset(installLocation);
        if (asset is not null)
            result.Add(new AppxIconEntry(fullName, familyName, displayName, asset));
    }

    private static string? ReadManifestDisplayName(string manifestPath)
    {
        try
        {
            XNamespace foundation = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
            XDocument document = XDocument.Load(manifestPath, LoadOptions.None);
            return document.Root?.Element(foundation + "Properties")?.Element(foundation + "DisplayName")?.Value;
        }
        catch
        {
            return null;
        }
    }

    private static string? FindBestAppxAsset(string installLocation)
    {
        try
        {
            return Directory.EnumerateFiles(Path.Combine(installLocation, "Assets"), "*.png", SearchOption.AllDirectories)
                .Where(path =>
                {
                    string name = Path.GetFileName(path);
                    return Regex.IsMatch(name, "Logo|targetsize-256|targetsize-48|square150|square44",
                        RegexOptions.IgnoreCase)
                        && !Regex.IsMatch(name, "contrast|badge", RegexOptions.IgnoreCase);
                })
                .Select(path => new { Path = path, Size = ReadImageSize(path) })
                .OrderByDescending(file => file.Size.Width * file.Size.Height)
                .ThenByDescending(file => file.Size.Width)
                .ThenByDescending(file => new FileInfo(file.Path).Length)
                .Select(file => file.Path)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static string BuildPackageFamilyName(string packageFullName)
    {
        string[] parts = packageFullName.Split('_', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? $"{parts[0]}_{parts[^1]}" : packageFullName;
    }

    private static bool TryFindAppxIcon(
        InstalledPackage package,
        IReadOnlyList<AppxIconEntry> entries,
        out string? assetPath)
    {
        assetPath = null;
        string id = package.Id ?? string.Empty;
        string shortId = id.StartsWith("MSIX\\", StringComparison.OrdinalIgnoreCase)
            ? id["MSIX\\".Length..]
            : id;

        var candidates = entries.Where(entry =>
                entry.PackageFullName.Equals(shortId, StringComparison.OrdinalIgnoreCase)
                || entry.PackageFamilyName.Equals(shortId, StringComparison.OrdinalIgnoreCase)
                || entry.PackageFullName.StartsWith(shortId + "_", StringComparison.OrdinalIgnoreCase)
                || NormalizeAppName(entry.Name).Equals(NormalizeAppName(package.Name), StringComparison.Ordinal))
            .GroupBy(entry => entry.AssetPath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();

        if (candidates.Length != 1)
            return false;

        assetPath = candidates[0].AssetPath;
        return true;
    }

    private static string? CopyAppxAssetToCache(string assetPath, string id)
    {
        try
        {
            if (!File.Exists(assetPath)) return null;
            string modified = File.GetLastWriteTimeUtc(assetPath).Ticks.ToString(CultureInfo.InvariantCulture);
            string key = Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes($"appx-v2|{id}|{assetPath}|{modified}"))).ToLowerInvariant();
            string folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WinProvisionStore", "Cache", "Icons");
            Directory.CreateDirectory(folder);
            string destination = Path.Combine(folder, $"appx-{key}.png");
            if (!File.Exists(destination))
                File.Copy(assetPath, destination);
            return destination;
        }
        catch
        {
            return null;
        }
    }

    private static (int Width, int Height) ReadImageSize(string path)
    {
        try
        {
            using var image = System.Drawing.Image.FromFile(path);
            return (image.Width, image.Height);
        }
        catch
        {
            return (0, 0);
        }
    }

    private static string? ResolveShellItemIcon(string shellPath, string id, out string reason)
    {
        reason = string.Empty;
        string cacheKey = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes($"local-v3|{id}|{shellPath}|0"))).ToLowerInvariant();
        string cacheFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinProvisionStore", "Cache", "Icons");
        Directory.CreateDirectory(cacheFolder);
        string destination = Path.Combine(cacheFolder, $"shell-{cacheKey}.png");
        if (File.Exists(destination)) return destination;

        if (TryExtractShellIcon(shellPath, destination))
            return destination;

        reason = "IShellItemImageFactory não retornou ícone";
        return null;
    }

    private sealed record ShortcutEntry(string Name, string Path);

    /// <summary>
    /// Enumera os atalhos (.lnk) do Menu Iniciar (comum + do usuário atual) uma
    /// única vez por chamada de <see cref="ResolveIconsAsync"/> — igual ao registro
    /// de desinstalação, evitando reler o disco por pacote. É a fonte de ícone mais
    /// confiável depois do registro: quase todo app com interface gráfica tem um
    /// atalho aqui, resolvido pelo próprio instalador (independe de heurística sobre
    /// nomes de arquivo dentro da pasta de instalação).
    /// </summary>
    private static IReadOnlyList<ShortcutEntry> ReadStartMenuShortcuts()
    {
        var result = new List<ShortcutEntry>();
        string[] roots =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
            Environment.GetFolderPath(Environment.SpecialFolder.Programs)
        ];
        foreach (string root in roots)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) continue;
            try
            {
                foreach (string path in Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories))
                    result.Add(new ShortcutEntry(Path.GetFileNameWithoutExtension(path), path));
            }
            catch (Exception ex)
            {
                WinGetDiagnosticLog.Write($"INSTALLED SHORTCUTS read failed root=\"{root}\" error={ex.Message}");
            }
        }
        return result;
    }

    /// <summary>
    /// Usa o Shell do Windows para extrair todos os AUMIDs (AppUserModelId) dos aplicativos UWP
    /// e da Microsoft Store em cache para cruzamento exato com PackageFamilyName.
    /// </summary>
    private static IReadOnlyDictionary<string, string> ReadAppsFolderAumids()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            Guid folderId = new Guid("1e87508d-89c2-42f0-8a7e-645a0f50ca58"); // FOLDERID_AppsFolder
            Guid iidIShellItem = typeof(NativeMethods.IShellItem).GUID;
            int hr = NativeMethods.SHGetKnownFolderItem(ref folderId, 0, IntPtr.Zero, ref iidIShellItem, out NativeMethods.IShellItem? folderItem);

            if (hr == 0 && folderItem != null)
            {
                Guid bhidEnumItems = new Guid("94f60519-2850-4924-aa5a-d15e84868039"); // BHID_EnumItems
                Guid iidIEnumShellItems = typeof(NativeMethods.IEnumShellItems).GUID;

                folderItem.BindToHandler(IntPtr.Zero, ref bhidEnumItems, ref iidIEnumShellItems, out IntPtr enumPtr);
                if (enumPtr != IntPtr.Zero)
                {
                    var enumItems = (NativeMethods.IEnumShellItems)Marshal.GetObjectForIUnknown(enumPtr);
                    while (enumItems.Next(1, out NativeMethods.IShellItem child, out uint fetched) == 0 && fetched == 1)
                    {
                        child.GetDisplayName(NativeMethods.SIGDN.DESKTOPABSOLUTEPARSING, out IntPtr namePtr);
                        if (namePtr != IntPtr.Zero)
                        {
                            string? parsingName = Marshal.PtrToStringUni(namePtr);
                            Marshal.FreeCoTaskMem(namePtr);

                            if (!string.IsNullOrWhiteSpace(parsingName))
                            {
                                int slash = parsingName.LastIndexOf('\\');
                                string aumid = slash >= 0 ? parsingName[(slash + 1)..] : parsingName;

                                int bang = aumid.IndexOf('!');
                                if (bang > 0)
                                {
                                    string familyName = aumid[..bang];
                                    map.TryAdd(familyName, aumid);

                                    child.GetDisplayName(NativeMethods.SIGDN.NORMALDISPLAY, out IntPtr displayPtr);
                                    if (displayPtr != IntPtr.Zero)
                                    {
                                        string? displayName = Marshal.PtrToStringUni(displayPtr);
                                        Marshal.FreeCoTaskMem(displayPtr);
                                        if (!string.IsNullOrWhiteSpace(displayName))
                                        {
                                            map.TryAdd("NAME:" + NormalizeAppName(displayName), aumid);
                                        }
                                    }
                                }
                            }
                        }
                        Marshal.ReleaseComObject(child);
                    }
                    Marshal.ReleaseComObject(enumItems);
                    Marshal.Release(enumPtr);
                }
                Marshal.ReleaseComObject(folderItem);
            }
        }
        catch (Exception ex)
        {
            WinGetDiagnosticLog.Write($"APPSFOLDER AUMID read failed error={ex.Message}");
        }
        return map;
    }

    private static ShortcutEntry? FindBestShortcut(string packageName, string? displayName, IReadOnlyList<ShortcutEntry> shortcuts)
    {
        if (shortcuts.Count == 0) return null;

        ShortcutEntry? best = null;
        double bestScore = 0;
        double secondBestScore = 0;
        foreach (var shortcut in shortcuts)
        {
            double score = Math.Max(
                NameSimilarity(packageName, shortcut.Name),
                string.IsNullOrWhiteSpace(displayName) ? 0 : NameSimilarity(displayName, shortcut.Name));
            if (score > bestScore)
            {
                secondBestScore = bestScore;
                bestScore = score;
                best = shortcut;
            }
            else if (score > secondBestScore)
            {
                secondBestScore = score;
            }
        }

        const double minScore = 0.72;
        const double minMargin = 0.1;
        return best is not null && bestScore >= minScore && (bestScore - secondBestScore) >= minMargin
            ? best
            : null;
    }

    private static string? ResolveShortcutIcon(ShortcutEntry shortcut, string id, out string reason)
    {
        reason = string.Empty;
        if (!TryReadShortcutTarget(shortcut.Path, out string targetPath, out string iconLocation, out int iconIndex))
        {
            reason = "falha ao ler o atalho (.lnk)";
            return null;
        }

        // O atalho pode ter um ícone próprio (IconLocation) diferente do executável
        // alvo — ex.: apontando pra um .ico dedicado ou outro arquivo/índice de
        // recurso — que tem prioridade; só cai pro executável em si quando o atalho
        // não define um ícone próprio.
        string reference = !string.IsNullOrWhiteSpace(iconLocation)
            ? $"\"{iconLocation}\",{iconIndex}"
            : (!string.IsNullOrWhiteSpace(targetPath) ? $"\"{targetPath}\"" : string.Empty);

        if (string.IsNullOrWhiteSpace(reference))
        {
            reason = "atalho sem alvo nem ícone definidos";
            return null;
        }

        return ResolveLocalIcon(reference, id, out reason);
    }

    private static bool TryReadShortcutTarget(string lnkPath, out string targetPath, out string iconLocation, out int iconIndex)
    {
        targetPath = string.Empty;
        iconLocation = string.Empty;
        iconIndex = 0;
        object? linkObject = null;
        try
        {
            linkObject = new NativeMethods.ShellLink();
            var link = (NativeMethods.IShellLinkW)linkObject;
            var persistFile = (System.Runtime.InteropServices.ComTypes.IPersistFile)linkObject;
            persistFile.Load(lnkPath, 0);

            var targetBuffer = new StringBuilder(260);
            link.GetPath(targetBuffer, targetBuffer.Capacity, IntPtr.Zero, 0);
            targetPath = Environment.ExpandEnvironmentVariables(targetBuffer.ToString());

            var iconBuffer = new StringBuilder(260);
            link.GetIconLocation(iconBuffer, iconBuffer.Capacity, out int index);
            iconLocation = Environment.ExpandEnvironmentVariables(iconBuffer.ToString());
            iconIndex = index;
            return !string.IsNullOrWhiteSpace(targetPath) || !string.IsNullOrWhiteSpace(iconLocation);
        }
        catch (Exception ex)
        {
            WinGetDiagnosticLog.Write($"INSTALLED SHORTCUT resolve failed path=\"{lnkPath}\" error={ex.Message}");
            return false;
        }
        finally
        {
            if (linkObject is not null) Marshal.ReleaseComObject(linkObject);
        }
    }

    private static void SaveComposedIcon(BitmapSource source, string destination, Color background)
    {
        BitmapSource composed = ComposeIconCanvas(CropTransparentBounds(source), background);
        using var stream = File.Create(destination);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(composed));
        encoder.Save(stream);
    }

    private static BitmapSource BitmapSourceFromGdiBitmap(System.Drawing.Bitmap bitmap)
    {
        using var stream = new MemoryStream();
        bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
        stream.Position = 0;
        BitmapSource source = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        source.Freeze();
        return source;
    }

    private static BitmapSource ComposeIconCanvas(BitmapSource source, Color background)
    {
        const double size = 256;
        const double maxDimension = 220;
        double scale = Math.Min(maxDimension / source.PixelWidth, maxDimension / source.PixelHeight);
        double width = source.PixelWidth * scale;
        double height = source.PixelHeight * scale;
        var visual = new DrawingVisual();
        using (DrawingContext drawing = visual.RenderOpen())
        {
            if (background.A > 0)
            {
                drawing.DrawRoundedRectangle(
                    new SolidColorBrush(background),
                    null,
                    new System.Windows.Rect(0, 0, size, size),
                    42,
                    42);
            }

            drawing.DrawImage(source, new System.Windows.Rect((size - width) / 2, (size - height) / 2, width, height));
        }

        var rendered = new RenderTargetBitmap(256, 256, 96, 96, PixelFormats.Pbgra32);
        rendered.Render(visual);
        rendered.Freeze();
        return rendered;
    }

    private const byte TransparentAlphaThreshold = 16;

    private static BitmapSource CropTransparentBounds(BitmapSource source)
    {
        BitmapSource bitmap = source.Format == PixelFormats.Pbgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0);
        bitmap.Freeze();

        int stride = bitmap.PixelWidth * 4;
        byte[] pixels = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, stride, 0);

        int left = bitmap.PixelWidth;
        int top = bitmap.PixelHeight;
        int right = -1;
        int bottom = -1;
        for (int y = 0; y < bitmap.PixelHeight; y++)
        {
            for (int x = 0; x < bitmap.PixelWidth; x++)
            {
                if (pixels[y * stride + (x * 4) + 3] <= TransparentAlphaThreshold)
                    continue;

                left = Math.Min(left, x);
                top = Math.Min(top, y);
                right = Math.Max(right, x);
                bottom = Math.Max(bottom, y);
            }
        }

        if (right < left || bottom < top)
            return source;

        int padding = Math.Max(1, Math.Max(right - left + 1, bottom - top + 1) / 16);
        left = Math.Max(0, left - padding);
        top = Math.Max(0, top - padding);
        right = Math.Min(bitmap.PixelWidth - 1, right + padding);
        bottom = Math.Min(bitmap.PixelHeight - 1, bottom + padding);

        var cropped = new CroppedBitmap(bitmap, new Int32Rect(
            left,
            top,
            right - left + 1,
            bottom - top + 1));
        cropped.Freeze();
        return cropped;
    }

    private static UninstallEntry? FindMatchingEntry(InstalledPackage package, IReadOnlyList<UninstallEntry> entries)
    {
        if (TryGetArpKey(package.Id, out string? hive, out string? view, out string? key))
        {
            return entries.FirstOrDefault(e => e.Hive == hive && e.View == view
                && e.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        }

        // Vários instaladores (MSI, Squirrel, alguns .NET/Inno Setup) usam o mesmo
        // identificador tanto como PackageIdentifier do winget quanto como nome da
        // chave ARP (ex.: ProductCode "{GUID}") — casamento exato e inequívoco,
        // sempre prioritário sobre qualquer comparação por nome.
        if (!string.IsNullOrWhiteSpace(package.Id))
        {
            var keyMatches = entries.Where(e => e.Key.Equals(package.Id, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (keyMatches.Length == 1) return keyMatches[0];
        }

        // Source IDs do not identify one ARP key reliably. Only use a unique
        // display-name/version match; ambiguity must never select the wrong icon.
        var exactMatches = entries.Where(e =>
            e.DisplayName.Equals(package.Name, StringComparison.OrdinalIgnoreCase)
            && (string.IsNullOrWhiteSpace(package.Version)
                || e.DisplayVersion.Equals(package.Version, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        if (exactMatches.Length == 1)
            return exactMatches[0];

        // Mesmo teste "Contains" de sempre (num sentido ou no outro), só que em cima
        // do nome normalizado (sem acento/caixa/pontuação/ruído tipo "(x64)"/"™") em
        // vez do texto bruto — cobre tudo que o Contains bruto original já cobria e
        // mais alguns casos, sem mudar o critério de aceitação: continua exigindo
        // resultado único. É o caminho primário; a pontuação por token abaixo é só
        // o último recurso, reservado pra quando nem isso encontra nada.
        string normalizedPackageName = NormalizeAppName(package.Name);
        var containmentMatches = entries.Where(e =>
        {
            string normalizedEntryName = NormalizeAppName(e.DisplayName);
            if (normalizedPackageName.Length == 0 || normalizedEntryName.Length == 0) return false;
            bool contains = normalizedEntryName.Contains(normalizedPackageName, StringComparison.Ordinal)
                || normalizedPackageName.Contains(normalizedEntryName, StringComparison.Ordinal);
            if (!contains) return false;
            return string.IsNullOrWhiteSpace(package.Version)
                || string.IsNullOrWhiteSpace(e.DisplayVersion)
                || e.DisplayVersion.Equals(package.Version, StringComparison.OrdinalIgnoreCase);
        })
        // HKCU/HKLM 32-bit and 64-bit views can expose the same uninstall entry.
        // Treat identical physical sources as one candidate rather than making a
        // valid app look ambiguous just because both registry views were indexed.
        .DistinctBy(e => $"{e.Key}|{e.DisplayIcon}|{e.InstallLocation}", StringComparer.OrdinalIgnoreCase)
        .ToArray();
        if (containmentMatches.Length == 1)
            return containmentMatches[0];

        // Alguns pacotes MSIX/Store representam a mesma instalação clássica com
        // uma versão de pacote diferente da versão registrada pelo instalador.
        // Quando o nome normalizado aponta para uma única entrada, o executável
        // dessa entrada continua sendo uma fonte local válida; não descarte-o
        // apenas por essa diferença de formatação/versionamento.
        var nameOnlyMatches = entries.Where(e =>
        {
            string normalizedEntryName = NormalizeAppName(e.DisplayName);
            if (normalizedPackageName.Length == 0 || normalizedEntryName.Length == 0)
                return false;
            return normalizedEntryName.Contains(normalizedPackageName, StringComparison.Ordinal)
                || normalizedPackageName.Contains(normalizedEntryName, StringComparison.Ordinal);
        })
        .DistinctBy(e => $"{e.Key}|{e.DisplayIcon}|{e.InstallLocation}", StringComparer.OrdinalIgnoreCase)
        .ToArray();
        if (nameOnlyMatches.Length == 1)
            return nameOnlyMatches[0];

        // Nenhum casamento direto/por substring — último recurso: pontuação por
        // sobreposição de tokens (útil quando a ordem das palavras muda, ex.: nome
        // do winget "Adobe Acrobat Reader DC" vs DisplayName "Reader DC (Adobe)").
        // Mais permissivo que os passos acima, então só aceita quando o melhor
        // resultado está claramente à frente do segundo colocado — sinal mais fraco,
        // não vale arriscar o ícone errado num empate.
        UninstallEntry? bestEntry = null;
        double bestScore = 0;
        double secondBestScore = 0;
        foreach (var candidate in entries)
        {
            if (!string.IsNullOrWhiteSpace(package.Version)
                && !string.IsNullOrWhiteSpace(candidate.DisplayVersion)
                && !candidate.DisplayVersion.Equals(package.Version, StringComparison.OrdinalIgnoreCase))
                continue;

            double score = NameSimilarity(package.Name, candidate.DisplayName);
            if (score > bestScore)
            {
                secondBestScore = bestScore;
                bestScore = score;
                bestEntry = candidate;
            }
            else if (score > secondBestScore)
            {
                secondBestScore = score;
            }
        }

        const double minScore = 0.55;
        const double minMargin = 0.15;
        return bestEntry is not null && bestScore >= minScore && (bestScore - secondBestScore) >= minMargin
            ? bestEntry
            : null;
    }

    /// <summary>
    /// Similaridade [0,1] entre dois nomes de app, tolerando diferenças de
    /// acentuação, caixa, pontuação e ruído comum ("(x64)", "™" etc.). Quando a
    /// normalização ASCII zera algum dos dois lados (nomes em CJK, cirílico etc.),
    /// cai pra comparação bruta case-insensitive — igual ao comportamento anterior
    /// pra esses casos.
    /// </summary>
    private static double NameSimilarity(string rawA, string rawB)
    {
        if (string.IsNullOrWhiteSpace(rawA) || string.IsNullOrWhiteSpace(rawB)) return 0;
        if (rawA.Equals(rawB, StringComparison.OrdinalIgnoreCase)) return 1.0;

        string a = NormalizeAppName(rawA);
        string b = NormalizeAppName(rawB);
        if (a.Length == 0 || b.Length == 0)
        {
            return rawA.Contains(rawB, StringComparison.OrdinalIgnoreCase)
                || rawB.Contains(rawA, StringComparison.OrdinalIgnoreCase)
                ? 0.85
                : 0;
        }
        if (a == b) return 1.0;

        var tokensA = a.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        var tokensB = b.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        if (tokensA.Count == 0 || tokensB.Count == 0) return 0;

        int intersection = tokensA.Intersect(tokensB).Count();
        int union = tokensA.Union(tokensB).Count();
        double jaccard = union == 0 ? 0 : (double)intersection / union;
        bool containment = a.Contains(b, StringComparison.Ordinal) || b.Contains(a, StringComparison.Ordinal);
        return containment ? Math.Max(jaccard, 0.85) : jaccard;
    }

    private static string NormalizeAppName(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        string decomposed = value.Normalize(NormalizationForm.FormD);
        var withoutDiacritics = new StringBuilder(decomposed.Length);
        foreach (char c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                withoutDiacritics.Append(c);
        }

        string lowered = withoutDiacritics.ToString().Normalize(NormalizationForm.FormC).ToLowerInvariant();
        lowered = Regex.Replace(lowered, @"[™®©]", string.Empty);
        lowered = Regex.Replace(lowered, @"\b(x86|x64|32-?bit|64-?bit|win32|win64)\b", string.Empty, RegexOptions.IgnoreCase);
        lowered = Regex.Replace(lowered, @"[^a-z0-9]+", " ");
        return Regex.Replace(lowered, @"\s+", " ").Trim();
    }

    private static string? FindMainExecutable(UninstallEntry? entry)
    {
        if (entry is null) return null;
        if (!string.IsNullOrWhiteSpace(entry.InstallLocation))
        {
            string folder = Environment.ExpandEnvironmentVariables(entry.InstallLocation);
            if (Directory.Exists(folder))
            {
                string? candidate = PickBestExecutable(folder, entry.DisplayName);
                if (candidate is not null) return candidate;
            }
        }

        return TryParseExecutable(entry.UninstallString);
    }

    // Executáveis auxiliares comuns (instaladores, updaters, crash handlers,
    // sub-processos do Chromium/Electron etc.) que nunca são o ícone "principal"
    // do app e não devem competir com ele na pontuação por nome.
    private static readonly string[] ExecutableBlacklist =
    [
        "unins", "uninstall", "setup", "update", "updater", "crashpad", "crashhandler",
        "elevate", "elevation", "vcredist", "vc_redist", "redist", "dotnetfx",
        "watchdog", "reporter", "maintenancetool"
    ];

    // Pastas tipicamente enormes e irrelevantes pra achar o executável principal
    // (bibliotecas de terceiros, cache, locais/idiomas, controle de versão) — puladas
    // na busca recursiva por performance, sem custo de precisão real.
    private static readonly string[] SkipFolderNames =
    [
        "node_modules", ".git", "locales", "cache", "logs", "temp", "tmp"
    ];

    private static string? PickBestExecutable(string folder, string nameHint)
    {
        try
        {
            var candidates = EnumerateExecutablesSafe(folder)
                .Where(path => !IsBlacklistedExecutable(Path.GetFileNameWithoutExtension(path)))
                .Select(path => new FileInfo(path))
                .Where(fi => fi.Exists)
                .ToArray();
            if (candidates.Length == 0) return null;
            if (candidates.Length == 1) return candidates[0].FullName;

            string normalizedFolder = folder.TrimEnd('\\', '/');
            var scored = candidates
                .Select(fi => new
                {
                    File = fi,
                    NameScore = NameSimilarity(nameHint, Path.GetFileNameWithoutExtension(fi.Name)),
                    IsAtRoot = string.Equals(fi.DirectoryName?.TrimEnd('\\', '/'), normalizedFolder, StringComparison.OrdinalIgnoreCase)
                })
                .OrderByDescending(x => x.NameScore)
                .ThenByDescending(x => x.IsAtRoot)
                .ThenByDescending(x => x.File.Length)
                .ToArray();

            if (scored[0].NameScore >= 0.5)
                return scored[0].File.FullName;

            // Nenhum nome de executável parece com o app (comum em apps
            // Electron/Java cujo binário principal se chama "app"/"launcher"/o nome
            // do runtime) — melhor sinal restante é o maior executável na raiz da
            // instalação, que costuma ser o launcher/GUI principal em vez de um
            // helper ou utilitário de linha de comando.
            var rootCandidate = candidates
                .Where(fi => string.Equals(fi.DirectoryName?.TrimEnd('\\', '/'), normalizedFolder, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(fi => fi.Length)
                .FirstOrDefault();
            return (rootCandidate ?? candidates.OrderByDescending(fi => fi.Length).First()).FullName;
        }
        catch (Exception ex)
        {
            WinGetDiagnosticLog.Write($"INSTALLED EXE scan failed folder=\"{folder}\" error={ex.Message}");
            return null;
        }
    }

    private static bool IsBlacklistedExecutable(string fileNameWithoutExtension)
    {
        string lowered = fileNameWithoutExtension.ToLowerInvariant();
        return ExecutableBlacklist.Any(term => lowered.Contains(term, StringComparison.Ordinal));
    }

    /// <summary>
    /// Busca .exe recursivamente a partir de <paramref name="root"/>, tolerando
    /// subpastas inacessíveis (ao contrário de Directory.EnumerateFiles com
    /// SearchOption.AllDirectories, que aborta a enumeração inteira no primeiro
    /// UnauthorizedAccessException) e com limites de profundidade/quantidade pra
    /// nunca travar em instalações com árvores de arquivos enormes.
    /// </summary>
    private static IEnumerable<string> EnumerateExecutablesSafe(string root, int maxDepth = 4, int maxFiles = 3000)
    {
        int count = 0;
        var stack = new Stack<(string Dir, int Depth)>();
        stack.Push((root, 0));
        while (stack.Count > 0 && count < maxFiles)
        {
            (string dir, int depth) = stack.Pop();

            // GetFiles/GetDirectories (em vez de EnumerateFiles/EnumerateDirectories)
            // são avaliados inteiramente dentro do try — uma subpasta sem permissão
            // no meio da árvore não derruba os resultados já coletados de pastas
            // irmãs (o que aconteceria com a versão "lazy": a exceção surgiria no
            // meio da iteração, fora de qualquer try/catch local).
            string[] files;
            try { files = Directory.GetFiles(dir, "*.exe"); }
            catch { files = []; }

            foreach (string file in files)
            {
                yield return file;
                count++;
                if (count >= maxFiles) yield break;
            }

            if (depth >= maxDepth) continue;

            string[] subdirs;
            try { subdirs = Directory.GetDirectories(dir); }
            catch { subdirs = []; }

            foreach (string subdir in subdirs)
            {
                string name = Path.GetFileName(subdir);
                if (SkipFolderNames.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                stack.Push((subdir, depth + 1));
            }
        }
    }

    private static string? TryParseExecutable(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        string value = Environment.ExpandEnvironmentVariables(command.Trim());
        if (value.StartsWith('"'))
        {
            int end = value.IndexOf('"', 1);
            if (end > 1) return value[1..end];
        }
        int exeEnd = value.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exeEnd >= 0 ? value[..(exeEnd + 4)].Trim() : null;
    }

    private static bool TryGetAppsFolderPath(InstalledPackage package, IReadOnlyDictionary<string, string> aumids, out string path)
    {
        path = string.Empty;
        if (string.IsNullOrWhiteSpace(package.Id))
            return false;

        string value = package.Id;

        // Remove barras invertidas que possam vir como prefixos
        int separator = value.IndexOf('\\');
        if (separator >= 0)
            value = value[(separator + 1)..];

        int bang = value.IndexOf('!');
        // Se já tem o identificador completo (Family!AppId), usamos ele
        if (bang > 0 && bang < value.Length - 1)
        {
            string family = value[..bang];
            string appId = value[(bang + 1)..];
            path = $@"shell:AppsFolder\{family}!{appId}";
            return true;
        }

        // Match exato pelo dicionário indexado pelo PackageFamilyName (ex: Netflix, Spotify, etc.)
        if (aumids.TryGetValue(value, out string? exactAumid))
        {
            path = $@"shell:AppsFolder\{exactAumid}";
            return true;
        }

        // Match secundário via nome caso o App não relate o Family Name corretamente pelo catálogo local
        string normalizedName = "NAME:" + NormalizeAppName(package.Name);
        if (aumids.TryGetValue(normalizedName, out string? nameAumid))
        {
            path = $@"shell:AppsFolder\{nameAumid}";
            return true;
        }

        // Fallback genérico caso a listagem dinâmica tenha falhado
        path = $@"shell:AppsFolder\{value}!App";
        return true;
    }

    private static bool TryGetArpKey(string id, out string? hive, out string? view, out string? key)
    {
        hive = view = key = null;
        string[] parts = id.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 4 || !parts[0].Equals("ARP", StringComparison.OrdinalIgnoreCase))
            return false;
        hive = parts[1].Equals("User", StringComparison.OrdinalIgnoreCase) ? "HKCU" :
            parts[1].Equals("Machine", StringComparison.OrdinalIgnoreCase) ? "HKLM" : null;
        view = parts[2].Equals("X86", StringComparison.OrdinalIgnoreCase) ? "X86" :
            parts[2].Equals("X64", StringComparison.OrdinalIgnoreCase) ? "X64" : null;
        key = string.Join('\\', parts.Skip(3));
        return hive is not null && view is not null && !string.IsNullOrWhiteSpace(key);
    }

    private static IReadOnlyList<UninstallEntry> ReadUninstallEntries()
    {
        var result = new List<UninstallEntry>();
        foreach ((RegistryHive hive, string hiveName) in new[] { (RegistryHive.LocalMachine, "HKLM"), (RegistryHive.CurrentUser, "HKCU") })
            foreach ((RegistryView view, string viewName) in new[] { (RegistryView.Registry64, "X64"), (RegistryView.Registry32, "X86") })
            {
                try
                {
                    using var root = RegistryKey.OpenBaseKey(hive, view);
                    using var uninstall = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall");
                    if (uninstall is null) continue;
                    foreach (string keyName in uninstall.GetSubKeyNames())
                    {
                        using var key = uninstall.OpenSubKey(keyName);
                        string? displayName = key?.GetValue("DisplayName") as string;
                        string? displayIcon = key?.GetValue("DisplayIcon") as string;
                        string? installLocation = key?.GetValue("InstallLocation") as string;
                        string? uninstallString = key?.GetValue("UninstallString") as string;
                        if (!string.IsNullOrWhiteSpace(displayName))
                            result.Add(new UninstallEntry(hiveName, viewName, keyName,
                                displayName, key!.GetValue("DisplayVersion") as string ?? string.Empty,
                                string.IsNullOrWhiteSpace(displayIcon) ? string.Empty : NormalizeDisplayIcon(displayIcon),
                                installLocation ?? string.Empty, uninstallString ?? string.Empty));
                    }
                }
                catch (Exception ex)
                {
                    WinGetDiagnosticLog.Write($"INSTALLED ARP read failed hive={hiveName} view={viewName} error={ex.Message}");
                }
            }
        return result;
    }

    private static string NormalizeDisplayIcon(string value)
    {
        string result = Environment.ExpandEnvironmentVariables(value.Trim());
        if (result.StartsWith('"'))
        {
            int end = result.IndexOf('"', 1);
            if (end > 1)
            {
                string path = result[1..end];
                string suffix = result[(end + 1)..].Trim();
                return path + suffix;
            }
        }
        else
        {
            int comma = result.IndexOf(',');
            string path = comma > 0 ? result[..comma].Trim() : result;

            if (!File.Exists(path))
            {
                string candidate = path;
                while (candidate.Contains(' '))
                {
                    int separator = candidate.LastIndexOf(' ');
                    candidate = candidate[..separator].TrimEnd();
                    if (File.Exists(candidate))
                    {
                        result = candidate;
                        break;
                    }
                }
            }
            else
            {
                result = path + (comma > 0 ? result[comma..] : string.Empty);
            }
        }
        return result.Trim().Trim('"');
    }

    private static (string Path, int Index) ParseIconReference(string value)
    {
        string result = Environment.ExpandEnvironmentVariables(value.Trim());
        int index = 0;
        if (result.StartsWith('"'))
        {
            int endQuote = result.IndexOf('"', 1);
            if (endQuote > 1)
            {
                string suffix = result[(endQuote + 1)..].Trim();
                result = result[1..endQuote];
                _ = int.TryParse(suffix.TrimStart(','), out index);
            }
        }
        else
        {
            int comma = result.IndexOf(',');
            if (comma > 0)
            {
                _ = int.TryParse(result[(comma + 1)..].Trim(), out index);
                result = result[..comma].Trim();
            }
            else
            {
                int executableEnd = result.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
                if (executableEnd >= 0)
                    result = result[..(executableEnd + 4)].Trim().Trim('"');
            }
        }
        return (result.Trim().Trim('"'), index);
    }

    private static bool TryExtractIconResource(string path, int iconIndex, string destination)
    {
        if (!File.Exists(path)) return false;
        if (string.Equals(Path.GetExtension(path), ".ico", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                using var stream = File.OpenRead(path);
                var decoder = new IconBitmapDecoder(
                    stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                BitmapFrame? frame = decoder.Frames
                    .OrderByDescending(candidate => candidate.PixelWidth * candidate.PixelHeight)
                    .FirstOrDefault();
                if (frame is null) return false;
                SaveComposedIcon(frame, destination, Colors.Transparent);
                return true;
            }
            catch
            {
                return false;
            }
        }

        IntPtr largeIcon = IntPtr.Zero;
        IntPtr smallIcon = IntPtr.Zero;
        try
        {
            foreach (uint size in new uint[] { 256, 128, 64, 48, 32 })
            {
                IntPtr[] large = new IntPtr[1];
                uint[] iconIds = new uint[1];
                uint extracted = NativeMethods.PrivateExtractIcons(
                    path, iconIndex, size, size, large, iconIds, 1, 0);
                IntPtr iconHandle = large[0];
                if (extracted == 0 || iconHandle == IntPtr.Zero)
                    continue;

                try
                {
                    using var icon = System.Drawing.Icon.FromHandle(iconHandle);
                    using var bitmap = icon.ToBitmap();
                    SaveComposedIcon(BitmapSourceFromGdiBitmap(bitmap), destination, Colors.Transparent);
                    return true;
                }
                finally
                {
                    if (large[0] != IntPtr.Zero) NativeMethods.DestroyIcon(large[0]);
                }
            }

            return false;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (largeIcon != IntPtr.Zero) NativeMethods.DestroyIcon(largeIcon);
            if (smallIcon != IntPtr.Zero) NativeMethods.DestroyIcon(smallIcon);
        }
    }

    private static bool TryExtractAssociatedIcon(string path, string destination)
    {
        try
        {
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (icon is null) return false;
            using var bitmap = icon.ToBitmap();
            SaveComposedIcon(BitmapSourceFromGdiBitmap(bitmap), destination, Colors.Transparent);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryExtractShellIcon(string path, string destination)
    {
        if (!File.Exists(path)) return false;
        Guid iid = typeof(NativeMethods.IShellItemImageFactory).GUID;
        int hr = NativeMethods.SHCreateItemFromParsingName(path, IntPtr.Zero,
            ref iid, out var factory);
        if (hr < 0 || factory is null) return false;
        try
        {
            hr = factory.GetImage(new NativeMethods.Size(256, 256),
                NativeMethods.SIIGBF.BIGGERSIZEOK | NativeMethods.SIIGBF.ICONONLY,
                out IntPtr bitmap);
            if (hr < 0 || bitmap == IntPtr.Zero) return false;
            try
            {
                var source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                    bitmap, IntPtr.Zero, System.Windows.Int32Rect.Empty,
                    System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                SaveComposedIcon(source, destination, Colors.Transparent);
                return true;
            }
            finally { NativeMethods.DeleteObject(bitmap); }
        }
        finally { System.Runtime.InteropServices.Marshal.ReleaseComObject(factory); }
    }

    private sealed record UninstallEntry(
        string Hive,
        string View,
        string Key,
        string DisplayName,
        string DisplayVersion,
        string DisplayIcon,
        string InstallLocation,
        string UninstallString);

    private static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        internal static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, [System.Runtime.InteropServices.In] ref Guid riid, out IShellItemImageFactory item);

        [System.Runtime.InteropServices.DllImport("gdi32.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        internal static extern bool DeleteObject(IntPtr hObject);

        [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        internal static extern uint ExtractIconEx(string szFileName, int nIconIndex, out IntPtr phiconLarge, out IntPtr phiconSmall, uint nIcons);

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        internal static extern uint PrivateExtractIcons(
            string szFileName,
            int nIconIndex,
            uint cxIcon,
            uint cyIcon,
            [Out] IntPtr[] phicon,
            [Out] uint[] piconid,
            uint nIcons,
            uint flags);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        internal static extern bool DestroyIcon(IntPtr hIcon);

        [System.Runtime.InteropServices.DllImport("shell32.dll", ExactSpelling = true)]
        internal static extern int SHGetKnownFolderItem([System.Runtime.InteropServices.In] ref Guid rfid, uint flags, IntPtr hToken, [System.Runtime.InteropServices.In] ref Guid riid, out IShellItem item);

        internal enum SIIGBF : uint { BIGGERSIZEOK = 0x1, ICONONLY = 0x4 }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        internal struct Size(int cx, int cy) { public int cx = cx; public int cy = cy; }

        [System.Runtime.InteropServices.ComImport, System.Runtime.InteropServices.Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), System.Runtime.InteropServices.InterfaceType(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IShellItemImageFactory { int GetImage(Size size, SIIGBF flags, out IntPtr bitmap); }

        [System.Runtime.InteropServices.ComImport, System.Runtime.InteropServices.Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), System.Runtime.InteropServices.InterfaceType(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IShellItem
        {
            void BindToHandler(IntPtr pbc, [System.Runtime.InteropServices.In] ref Guid bhid, [System.Runtime.InteropServices.In] ref Guid riid, out IntPtr ppv);
            void GetParent(out IShellItem ppsi);
            void GetDisplayName(SIGDN sigdnName, out IntPtr ppszName);
            void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
            void Compare(IShellItem psi, uint hint, out int piOrder);
        }

        [System.Runtime.InteropServices.ComImport, System.Runtime.InteropServices.Guid("70629033-e363-4a28-a567-0db78006e6d7"), System.Runtime.InteropServices.InterfaceType(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IEnumShellItems
        {
            [System.Runtime.InteropServices.PreserveSig]
            int Next(uint celt, out IShellItem rgelt, out uint pceltFetched);
            void Skip(uint celt);
            void Reset();
            void Clone(out IEnumShellItems ppenum);
        }

        internal enum SIGDN : uint
        {
            NORMALDISPLAY = 0x00000000,
            DESKTOPABSOLUTEPARSING = 0x80028000,
            DESKTOPABSOLUTEEDITING = 0x8004c000
        }

        // CLSID/IID do ShellLink do shell32 — usado só pra ler (nunca criar/editar)
        // atalhos do Menu Iniciar e extrair alvo + ícone próprio (ver
        // TryReadShortcutTarget). Implementa IPersistFile (System.Runtime.InteropServices.ComTypes)
        // pra abrir o .lnk em modo leitura.
        [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
        internal class ShellLink { }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
        internal interface IShellLinkW
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cchMaxPath, IntPtr pfd, uint fFlags);
            void GetIDList(out IntPtr ppidl);
            void SetIDList(IntPtr pidl);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cchMaxName);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cchMaxPath);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cchMaxPath);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
            void GetHotkey(out short pwHotkey);
            void SetHotkey(short wHotkey);
            void GetShowCmd(out int piShowCmd);
            void SetShowCmd(int iShowCmd);
            void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cchIconPath, out int piIcon);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
            void Resolve(IntPtr hwnd, uint fFlags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
        }
    }
}
