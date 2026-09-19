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
    public InstalledPackagesService()
    {
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

    public async Task<IReadOnlyList<InstalledPackage>> ResolveIconsAsync(
        IEnumerable<InstalledPackage> packages,
        CancellationToken cancellationToken = default)
    {
        var source = packages.ToArray();
        var uninstallEntries = await Task.Run(ReadUninstallEntries, cancellationToken);
        using var gate = new SemaphoreSlim(4);
        var resolved = new InstalledPackage[source.Length];
        int withIcon = 0;
        await Task.WhenAll(source.Select(async (package, index) =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                string? iconUrl = null;
                string method = "d";
                var failures = new List<string>();

                string? displayIcon = FindDisplayIcon(package, uninstallEntries);
                if (!string.IsNullOrWhiteSpace(displayIcon))
                {
                    iconUrl = ResolveLocalIcon(displayIcon, package.Id, out string reason);
                    if (iconUrl is not null) method = "a";
                    else failures.Add($"a:{reason}");
                }

                if (iconUrl is null)
                {
                    var entry = FindMatchingEntry(package, uninstallEntries);
                    string? executable = FindMainExecutable(entry);
                    if (executable is not null)
                    {
                        iconUrl = ResolveLocalIcon(executable, package.Id, out string reason);
                        if (iconUrl is not null) method = "b";
                        else failures.Add($"b:{reason}");
                    }
                    else failures.Add("b:InstallLocation/UninstallString sem executável local");
                }

                if (iconUrl is null && TryGetAppsFolderPath(package, out string appsFolderPath))
                {
                    iconUrl = ResolveShellItemIcon(appsFolderPath, package.Id, out string reason);
                    if (iconUrl is not null) method = "c";
                    else failures.Add($"c:{reason}");
                }
                else if (iconUrl is null)
                {
                    failures.Add("c:PackageFamilyName/AppId não derivado");
                }

                if (iconUrl is null)
                    failures.Add("d:ícone genérico");
                else Interlocked.Increment(ref withIcon);

                WinGetDiagnosticLog.Write(
                    $"INSTALLED ICON item=\"{package.Name}\" id=\"{package.Id}\" method={method} " +
                    $"status={(iconUrl is null ? "missing" : "ok")} " +
                    $"failures=\"{string.Join(" | ", failures)}\"");
                resolved[index] = package with
                {
                    IconUrl = iconUrl ?? IconService.DefaultIconPackUri,
                    IsSystemComponent = InstalledPackageClassifier.IsSystemComponent(package)
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
        string cacheKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"local-v3|{id}|{path}|{iconIndex}|{modified}"))).ToLowerInvariant();
        string cacheFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinProvisionStore", "Cache", "Icons");
        Directory.CreateDirectory(cacheFolder);
        string destination = Path.Combine(cacheFolder, $"arp-{cacheKey}.png");
        if (File.Exists(destination)) return destination;

        try
        {
            bool extracted = iconIndex != 0
                ? TryExtractIconResource(path, iconIndex, destination)
                : TryExtractShellIcon(path, destination);
            if (extracted)
                return destination;
            reason = iconIndex != 0
                ? $"ExtractIconEx não retornou o índice {iconIndex}"
                : "IShellItemImageFactory não retornou ícone";
        }
        catch (Exception ex)
        {
            reason = ex.Message;
        }

        return null;
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
                if (pixels[y * stride + (x * 4) + 3] == 0)
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

        // Source IDs do not identify one ARP key reliably. Only use a unique
        // display-name/version match; ambiguity must never select the wrong icon.
        var exactMatches = entries.Where(e =>
            e.DisplayName.Equals(package.Name, StringComparison.OrdinalIgnoreCase)
            && (string.IsNullOrWhiteSpace(package.Version)
                || e.DisplayVersion.Equals(package.Version, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        if (exactMatches.Length == 1)
            return exactMatches[0];

        var nameMatches = entries.Where(e =>
            (e.DisplayName.Contains(package.Name, StringComparison.OrdinalIgnoreCase)
             || package.Name.Contains(e.DisplayName, StringComparison.OrdinalIgnoreCase))
            && (string.IsNullOrWhiteSpace(package.Version)
                || string.IsNullOrWhiteSpace(e.DisplayVersion)
                || e.DisplayVersion.Equals(package.Version, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        var matches = nameMatches.Length == 1 ? nameMatches : exactMatches;
        return matches.Length == 1 ? matches[0] : null;
    }

    private static string? FindDisplayIcon(InstalledPackage package, IReadOnlyList<UninstallEntry> entries) =>
        FindMatchingEntry(package, entries)?.DisplayIcon;

    private static string? FindMainExecutable(UninstallEntry? entry)
    {
        if (entry is null) return null;
        if (!string.IsNullOrWhiteSpace(entry.InstallLocation))
        {
            string folder = Environment.ExpandEnvironmentVariables(entry.InstallLocation);
            if (Directory.Exists(folder))
            {
                string? candidate = Directory.EnumerateFiles(folder, "*.exe", SearchOption.TopDirectoryOnly)
                    .OrderBy(path => Path.GetFileName(path).Contains("unins", StringComparison.OrdinalIgnoreCase))
                    .ThenBy(path => Path.GetFileName(path).Contains("setup", StringComparison.OrdinalIgnoreCase))
                    .FirstOrDefault();
                if (candidate is not null) return candidate;
            }
        }

        return TryParseExecutable(entry.UninstallString);
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

    private static bool TryGetAppsFolderPath(InstalledPackage package, out string path)
    {
        path = string.Empty;
        string value = package.Id;
        int separator = value.IndexOf('\\');
        if (separator >= 0) value = value[(separator + 1)..];
        int bang = value.IndexOf('!');
        if (bang <= 0 || bang == value.Length - 1) return false;
        string family = value[..bang];
        string appId = value[(bang + 1)..];
        path = $@"shell:AppsFolder\{family}!{appId}";
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
                using var icon = new System.Drawing.Icon(path);
                using var bitmap = icon.ToBitmap();
                IntPtr bitmapHandle = bitmap.GetHbitmap();
                try
                {
                    var source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                        bitmapHandle, IntPtr.Zero, System.Windows.Int32Rect.Empty,
                        System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
                    source.Freeze();
                    source = ComposeIconCanvas(CropTransparentBounds(source), Colors.Transparent);
                    using var stream = File.Create(destination);
                    var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(source));
                    encoder.Save(stream);
                    return true;
                }
                finally
                {
                    NativeMethods.DeleteObject(bitmapHandle);
                }
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
            uint extracted = NativeMethods.ExtractIconEx(path, iconIndex, out largeIcon, out smallIcon, 1);
            IntPtr iconHandle = largeIcon != IntPtr.Zero ? largeIcon : smallIcon;
            if (extracted == 0 || iconHandle == IntPtr.Zero) return false;

            using var icon = System.Drawing.Icon.FromHandle(iconHandle);
            using var bitmap = icon.ToBitmap();
            IntPtr bitmapHandle = bitmap.GetHbitmap();
            var source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                bitmapHandle, IntPtr.Zero, System.Windows.Int32Rect.Empty,
                System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
            NativeMethods.DeleteObject(bitmapHandle);
            source.Freeze();
            source = ComposeIconCanvas(CropTransparentBounds(source), Colors.Transparent);
            using var stream = File.Create(destination);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(source));
            encoder.Save(stream);
            return true;
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
                source = ComposeIconCanvas(CropTransparentBounds(source), Colors.Transparent);
                using var stream = File.Create(destination);
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(source));
                encoder.Save(stream);
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
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        internal static extern bool DestroyIcon(IntPtr hIcon);
        internal enum SIIGBF : uint { BIGGERSIZEOK = 0x1, ICONONLY = 0x4 }
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        internal struct Size(int cx, int cy) { public int cx = cx; public int cy = cy; }
        [System.Runtime.InteropServices.ComImport, System.Runtime.InteropServices.Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), System.Runtime.InteropServices.InterfaceType(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IShellItemImageFactory { int GetImage(Size size, SIIGBF flags, out IntPtr bitmap); }
    }
}
