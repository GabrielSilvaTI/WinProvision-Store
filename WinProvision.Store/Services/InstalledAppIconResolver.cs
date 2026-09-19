using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Windows.Management.Deployment;
using WinProvision.Core.Services;

namespace WinProvision.Store.Services;

public sealed class InstalledAppIconResolver
{
    private sealed record ResolverIndexes(
        IReadOnlyList<UninstallEntry> UninstallEntries,
        IReadOnlyDictionary<string, ShortcutTarget> StartMenuShortcuts,
        IReadOnlyDictionary<string, string> MsixApps);
    private sealed record ShortcutTarget(string Target, string IconLocation);

    private static async Task<ResolverIndexes> BuildIndexesAsync(CancellationToken cancellationToken)
    {
        var shortcuts = new Dictionary<string, ShortcutTarget>(StringComparer.OrdinalIgnoreCase);
        foreach (string root in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu)
        }.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                foreach (string path in Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories))
                {
                    string name = Path.GetFileNameWithoutExtension(path);
                    if (!shortcuts.ContainsKey(name) && TryReadShortcut(path, out var target))
                        shortcuts[name] = target;
                }
            }
            catch { }
        }

        var msix = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            try
            {
                var manager = new PackageManager();
                foreach (var package in manager.FindPackagesForUser(string.Empty))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string fullName = package.Id?.FullName ?? string.Empty;
                    string familyName = package.Id?.FamilyName ?? string.Empty;
                    foreach (var entry in package.GetAppListEntries().ToArray())
                    {
                        string? aumid = entry.AppInfo?.AppUserModelId;
                        if (!string.IsNullOrWhiteSpace(aumid))
                            msix[fullName] = aumid;
                    }
                    if (!string.IsNullOrWhiteSpace(familyName) && !msix.ContainsKey(fullName))
                        msix[fullName] = familyName;
                }
            }
            catch { }
        }
        return new ResolverIndexes(ReadUninstallEntries(), shortcuts, msix);
    }

    public async Task<IReadOnlyList<InstalledPackage>> ResolveAsync(
        IEnumerable<InstalledPackage> packages,
        CancellationToken cancellationToken = default)
    {
        var source = packages.ToArray();
        var indexes = await Task.Run(() => BuildIndexesAsync(cancellationToken), cancellationToken);
        using var gate = new SemaphoreSlim(4);
        var resolved = new InstalledPackage[source.Length];
        int withIcon = 0;
        await Task.WhenAll(source.Select(async (package, index) =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                string? iconUrl = null;
                string via = "generico";
                string file = string.Empty;
                string reason = string.Empty;
                var registryEntry = FindMatchingEntry(package, indexes.UninstallEntries);

                string? registryIcon = FindRegistryDefaultIcon(registryEntry);
                if (!string.IsNullOrWhiteSpace(registryIcon))
                {
                    iconUrl = ResolveLocalIcon(registryIcon, package.Id, out reason);
                    if (iconUrl is not null) { via = "registro-chave"; file = registryIcon; }
                }

                string? displayIcon = FindDisplayIcon(package, indexes.UninstallEntries);
                if (iconUrl is null && !string.IsNullOrWhiteSpace(displayIcon))
                {
                    iconUrl = ResolveLocalIcon(displayIcon, package.Id, out reason);
                    if (iconUrl is not null)
                    {
                        via = TryGetArpKey(package.Id, out _, out _, out _)
                            ? "displayicon" : "registro-nome";
                        file = displayIcon;
                    }
                }

                if (iconUrl is null)
                {
                    string? executable = FindMainExecutable(registryEntry);
                    if (executable is not null)
                    {
                        iconUrl = ResolveLocalIcon(executable, package.Id, out reason);
                        if (iconUrl is not null)
                        {
                            via = string.IsNullOrWhiteSpace(indexes.UninstallEntries
                                .FirstOrDefault(x => x.DisplayName.Equals(package.Name, StringComparison.OrdinalIgnoreCase))
                                ?.InstallLocation) ? "pasta-uninstall" : "installlocation";
                            file = executable;
                        }
                    }
                    else reason = "nenhum executável elegível";
                }

                if (iconUrl is null && TryGetStartMenuShortcut(package, indexes, out ShortcutTarget shortcut))
                {
                    string iconReference = string.IsNullOrWhiteSpace(shortcut.IconLocation)
                        ? shortcut.Target : shortcut.IconLocation;
                    iconUrl = ResolveLocalIcon(iconReference, package.Id, out reason);
                    if (iconUrl is not null) { via = "atalho"; file = iconReference; }
                }

                if (iconUrl is null && TryGetAppsFolderPath(package, indexes, out string appsFolderPath))
                {
                    iconUrl = ResolveShellItemIcon(appsFolderPath, package.Id, out reason);
                    if (iconUrl is not null) { via = "msix"; file = appsFolderPath; }
                }

                if (iconUrl is null)
                {
                    via = "generico";
                    reason = string.IsNullOrWhiteSpace(reason) ? "nenhuma fonte local elegível" : reason;
                }
                else Interlocked.Increment(ref withIcon);

                WinGetDiagnosticLog.Write(
                    $"ICON id=\"{package.Id}\" nome=\"{package.Name}\" via={via} " +
                    $"arquivo=\"{file}\" resultado={(iconUrl is null ? "fallback" : "ok")} motivo=\"{reason}\"");
                resolved[index] = package with
                {
                    IconUrl = iconUrl ?? IconService.DefaultIconPackUri,
                    IsSystemComponent = InstalledPackageClassifier.IsSystemComponent(package)
                };
            }
            finally { gate.Release(); }
        }));
        WinGetDiagnosticLog.Write($"ICON SUMMARY total={source.Length} ok={withIcon} fallback={source.Length - withIcon}");
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
        string cacheKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"installed-v4|{id}|{path}|{iconIndex}|{modified}"))).ToLowerInvariant();
        string cacheFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinProvision", "Store", "installed-icons");
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
            Encoding.UTF8.GetBytes($"installed-v4|{id}|{shellPath}|0"))).ToLowerInvariant();
        string cacheFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinProvision", "Store", "installed-icons");
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
        const double size = 128;
        const double maxDimension = 110;
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

        var rendered = new RenderTargetBitmap(128, 128, 96, 96, PixelFormats.Pbgra32);
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

    private static string? FindRegistryDefaultIcon(UninstallEntry? entry)
    {
        if (entry is null) return null;
        try
        {
            RegistryHive hive = entry.Hive == "HKCU" ? RegistryHive.CurrentUser : RegistryHive.LocalMachine;
            RegistryView view = entry.View == "X86" ? RegistryView.Registry32 : RegistryView.Registry64;
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var key = root.OpenSubKey(
                $@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{entry.Key}\DefaultIcon");
            return key?.GetValue(null) as string;
        }
        catch { return null; }
    }

    private static string? FindMainExecutable(UninstallEntry? entry)
    {
        if (entry is null) return null;
        if (!string.IsNullOrWhiteSpace(entry.InstallLocation))
        {
            string folder = Environment.ExpandEnvironmentVariables(entry.InstallLocation);
            if (Directory.Exists(folder))
            {
                string? candidate = Directory.EnumerateFiles(folder, "*.exe", SearchOption.TopDirectoryOnly)
                    .Where(IsEligibleExecutable)
                    .OrderBy(path => Path.GetFileName(path).Length)
                    .FirstOrDefault();
                if (candidate is not null) return candidate;
            }
        }

        string? uninstall = TryParseExecutable(entry.UninstallString);
        return uninstall is not null && IsEligibleExecutable(uninstall) ? uninstall : null;
    }

    private static bool IsEligibleExecutable(string path)
    {
        string full = Path.GetFullPath(path);
        string name = Path.GetFileNameWithoutExtension(full);
        string lower = full.ToLowerInvariant();
        return !name.Contains("unins", StringComparison.OrdinalIgnoreCase)
            && !name.Contains("setup", StringComparison.OrdinalIgnoreCase)
            && !lower.Contains(@"\system32\", StringComparison.OrdinalIgnoreCase)
            && !lower.Contains(@"\windows\", StringComparison.OrdinalIgnoreCase)
            && !lower.Contains(@"\installer\", StringComparison.OrdinalIgnoreCase);
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

    private static bool TryGetStartMenuShortcut(
        InstalledPackage package, ResolverIndexes indexes, out ShortcutTarget shortcut)
    {
        shortcut = null!;
        string? exact = indexes.StartMenuShortcuts
            .FirstOrDefault(x => x.Key.Equals(package.Name, StringComparison.OrdinalIgnoreCase)).Key;
        if (exact is not null)
        {
            shortcut = indexes.StartMenuShortcuts[exact];
            return true;
        }

        var candidates = indexes.StartMenuShortcuts
            .Where(x => x.Key.Contains(package.Name, StringComparison.OrdinalIgnoreCase)
                || package.Name.Contains(x.Key, StringComparison.OrdinalIgnoreCase))
            .Select(x => x.Value)
            .Take(2)
            .ToArray();
        if (candidates.Length == 1)
        {
            shortcut = candidates[0];
            return true;
        }
        return false;
    }

    private static bool TryReadShortcut(string path, out ShortcutTarget target)
    {
        target = null!;
        try
        {
            var link = (NativeMethods.IShellLinkW)new NativeMethods.ShellLink();
            var persist = (NativeMethods.IPersistFile)link;
            persist.Load(path, 0);
            var targetBuffer = new StringBuilder(32768);
            var iconBuffer = new StringBuilder(32768);
            link.GetPath(targetBuffer, targetBuffer.Capacity, IntPtr.Zero, 0);
            link.GetIconLocation(iconBuffer, iconBuffer.Capacity, out _);
            target = new ShortcutTarget(
                Environment.ExpandEnvironmentVariables(targetBuffer.ToString()),
                Environment.ExpandEnvironmentVariables(iconBuffer.ToString()));
            Marshal.ReleaseComObject(persist);
            Marshal.ReleaseComObject(link);
            return !string.IsNullOrWhiteSpace(target.Target) || !string.IsNullOrWhiteSpace(target.IconLocation);
        }
        catch { return false; }
    }

    private static bool TryGetAppsFolderPath(
        InstalledPackage package, ResolverIndexes indexes, out string path)
    {
        path = string.Empty;
        string value = package.Id;
        int separator = value.IndexOf('\\');
        if (separator >= 0) value = value[(separator + 1)..];
        int bang = value.IndexOf('!');
        string family = bang > 0 ? value[..bang] : DeriveFamilyName(value);
        if (string.IsNullOrWhiteSpace(family)) return false;
        string appId = bang > 0 && bang < value.Length - 1 ? value[(bang + 1)..] : string.Empty;
        string fullName = value;
        if (indexes.MsixApps.TryGetValue(fullName, out string? aumid))
            path = $@"shell:AppsFolder\{aumid}";
        else if (!string.IsNullOrWhiteSpace(appId))
            path = $@"shell:AppsFolder\{family}!{appId}";
        else
            return false;
        return true;
    }

    private static string DeriveFamilyName(string fullName)
    {
        string[] parts = fullName.Split('_');
        return parts.Length >= 2 ? $"{parts[0]}_{parts[^1]}" : string.Empty;
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
        [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
        internal class ShellLink { }
        [ComImport, Guid("000214F9-0000-0000-C000-000000000046"),
            InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IShellLinkW
        {
            int GetPath([Out] StringBuilder file, int maxPath, IntPtr findData, uint flags);
            int GetIDList(out IntPtr pidl);
            int SetIDList(IntPtr pidl);
            int GetDescription([Out] StringBuilder name, int maxName);
            int SetDescription(string name);
            int GetWorkingDirectory([Out] StringBuilder dir, int maxPath);
            int SetWorkingDirectory(string dir);
            int GetArguments([Out] StringBuilder args, int maxPath);
            int SetArguments(string args);
            int GetHotkey(out short hotkey);
            int SetHotkey(short hotkey);
            int GetShowCmd(out int showCmd);
            int SetShowCmd(int showCmd);
            int GetIconLocation([Out] StringBuilder iconPath, int maxPath, out int iconIndex);
            int SetIconLocation(string iconPath, int iconIndex);
        }
        [ComImport, Guid("0000010b-0000-0000-C000-000000000046"),
            InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IPersistFile
        {
            int GetClassID(out Guid classId);
            int IsDirty();
            int Load([MarshalAs(UnmanagedType.LPWStr)] string fileName, uint mode);
            int Save(string fileName, bool remember);
            int SaveCompleted(string fileName);
            int GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string fileName);
        }
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