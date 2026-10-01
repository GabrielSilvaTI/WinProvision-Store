using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WinProvision.Core.Services;

public enum TemporaryFileCategory
{
    User,
    System,
}

public sealed record TemporaryFileCleanupEstimate(
    TemporaryFileCategory Category,
    string Path,
    long FileCount,
    long Bytes,
    long SkippedCount);

public sealed record TemporaryFileCleanupResult(
    long DeletedCount,
    long DeletedBytes,
    long SkippedCount,
    string? Error = null)
{
    public bool Success => Error is null;
}

/// <summary>
/// Analisa e remove somente arquivos antigos das pastas temporárias padrão do Windows.
/// Não segue junctions/symlinks, não toca em Downloads nem nos caches de outros aplicativos.
/// </summary>
public sealed class TemporaryFileCleanupService
{
    public const int DefaultMinimumFileAgeDays = 7;
    public static readonly TimeSpan MinimumFileAge = TimeSpan.FromDays(DefaultMinimumFileAgeDays);

    public Task<IReadOnlyList<TemporaryFileCleanupEstimate>> AnalyzeAsync(CancellationToken cancellationToken = default) =>
        AnalyzeAsync(DefaultMinimumFileAgeDays, cancellationToken);

    public Task<IReadOnlyList<TemporaryFileCleanupEstimate>> AnalyzeAsync(
        int minimumAgeDays,
        CancellationToken cancellationToken = default)
    {
        ValidateMinimumAgeDays(minimumAgeDays);
        return Task.Run<IReadOnlyList<TemporaryFileCleanupEstimate>>(() =>
        {
            DateTime cutoff = DateTime.UtcNow - TimeSpan.FromDays(minimumAgeDays);
            string userTemp = Path.GetFullPath(Path.GetTempPath());
            string systemTemp = Path.GetFullPath(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp"));

            var estimates = new List<TemporaryFileCleanupEstimate>
            {
                AnalyzeDirectory(TemporaryFileCategory.User, userTemp, cutoff, cancellationToken),
            };

            if (!string.Equals(userTemp, systemTemp, StringComparison.OrdinalIgnoreCase))
                estimates.Add(AnalyzeDirectory(TemporaryFileCategory.System, systemTemp, cutoff, cancellationToken));

            return estimates;
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<TemporaryFileCleanupResult>> CleanAsync(
        IEnumerable<TemporaryFileCategory> categories,
        CancellationToken cancellationToken = default) =>
        await CleanAsync(categories, DefaultMinimumFileAgeDays, cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<TemporaryFileCleanupResult>> CleanAsync(
        IEnumerable<TemporaryFileCategory> categories,
        int minimumAgeDays,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(categories);
        ValidateMinimumAgeDays(minimumAgeDays);
        var selected = new HashSet<TemporaryFileCategory>(categories);
        var results = new List<TemporaryFileCleanupResult>();
        DateTime cutoff = DateTime.UtcNow - TimeSpan.FromDays(minimumAgeDays);

        if (selected.Contains(TemporaryFileCategory.User))
        {
            string userTemp = Path.GetFullPath(Path.GetTempPath());
            results.Add(await CleanUserDirectoryAsync(userTemp, cutoff, cancellationToken));
        }

        if (selected.Contains(TemporaryFileCategory.System))
        {
            string systemTemp = Path.GetFullPath(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp"));
            results.Add(await CleanSystemDirectoryElevatedAsync(systemTemp, cutoff, cancellationToken));
        }

        return results;
    }

    private static TemporaryFileCleanupEstimate AnalyzeDirectory(
        TemporaryFileCategory category,
        string root,
        DateTime cutoff,
        CancellationToken cancellationToken)
    {
        long count = 0;
        long bytes = 0;
        long skipped = 0;
        if (!Directory.Exists(root))
            return new TemporaryFileCleanupEstimate(category, root, 0, 0, 0);
        try
        {
            if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
                return new TemporaryFileCleanupEstimate(category, root, 0, 0, 1);
        }
        catch (Exception ex) when (IsExpectedFileSystemException(ex))
        {
            return new TemporaryFileCleanupEstimate(category, root, 0, 0, 1);
        }

        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string directory = pending.Pop();
            IEnumerable<string> entries;
            try { entries = Directory.GetFileSystemEntries(directory); }
            catch (Exception ex) when (IsExpectedFileSystemException(ex)) { skipped++; continue; }

            try
            {
                foreach (string entry in entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        FileAttributes attributes = File.GetAttributes(entry);
                        if ((attributes & FileAttributes.ReparsePoint) != 0)
                        {
                            skipped++;
                            continue;
                        }

                        if ((attributes & FileAttributes.Directory) != 0)
                        {
                            pending.Push(entry);
                            continue;
                        }

                        var info = new FileInfo(entry);
                        if (info.LastWriteTimeUtc < cutoff)
                        {
                            count++;
                            bytes += Math.Max(0, info.Length);
                        }
                    }
                    catch (Exception ex) when (IsExpectedFileSystemException(ex)) { skipped++; }
                }
            }
            catch (Exception ex) when (IsExpectedFileSystemException(ex)) { skipped++; }
        }

        return new TemporaryFileCleanupEstimate(category, root, count, bytes, skipped);
    }

    private static async Task<TemporaryFileCleanupResult> CleanUserDirectoryAsync(
        string root,
        DateTime cutoff,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(root))
            return new TemporaryFileCleanupResult(0, 0, 0);
        try
        {
            if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
                return new TemporaryFileCleanupResult(0, 0, 1);
        }
        catch (Exception ex) when (IsExpectedFileSystemException(ex))
        {
            return new TemporaryFileCleanupResult(0, 0, 1);
        }

        long deleted = 0;
        long bytes = 0;
        long skipped = 0;
        foreach (string file in EnumerateOldFilesCore(root, cutoff, cancellationToken, () => skipped++))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var info = new FileInfo(file);
                long length = info.Length;
                info.Delete();
                deleted++;
                bytes += Math.Max(0, length);
            }
            catch (Exception ex) when (IsExpectedFileSystemException(ex)) { skipped++; }
            await Task.Yield();
        }

        RemoveEmptyDirectories(root, cutoff, cancellationToken, ref skipped);
        return new TemporaryFileCleanupResult(deleted, bytes, skipped);
    }

    private static async Task<TemporaryFileCleanupResult> CleanSystemDirectoryElevatedAsync(
        string root,
        DateTime cutoff,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(root))
            return new TemporaryFileCleanupResult(0, 0, 0);

        // O script só recebe a pasta Windows\Temp validada pelo próprio app e nunca apaga
        // arquivos recentes. O cmdlet ignora arquivos bloqueados e pontos de reparse.
        string escapedRoot = root.Replace("'", "''", StringComparison.Ordinal);
        string cutoffIso = cutoff.ToString("o", CultureInfo.InvariantCulture);
        string script = string.Join(" ",
        [
            "$ErrorActionPreference='Stop';",
            $"$root=[IO.Path]::GetFullPath('{escapedRoot}');",
            "$expected=[IO.Path]::GetFullPath((Join-Path $env:windir 'Temp'));",
            "if (-not [string]::Equals($root,$expected,[StringComparison]::OrdinalIgnoreCase)) { throw 'Pasta de destino inválida.' }; if (([IO.File]::GetAttributes($root) -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'A pasta temporária é um ponto de redirecionamento.' };",
            $"$cutoff=[DateTime]::Parse('{cutoffIso}',[Globalization.CultureInfo]::InvariantCulture,[Globalization.DateTimeStyles]::RoundtripKind).ToUniversalTime();",
            "$removed=0L; $bytes=0L; $skipped=0L; $stack=[Collections.Generic.Stack[string]]::new(); $stack.Push($root);",
            "while ($stack.Count -gt 0) { $dir=$stack.Pop(); try { $items=[IO.Directory]::GetFileSystemEntries($dir) } catch { $skipped++; continue };",
            "foreach ($path in $items) { try { $item=Get-Item -LiteralPath $path -Force -ErrorAction Stop; if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { $skipped++; continue };",
            "if ($item.PSIsContainer) { $stack.Push($path); continue }; if ($item.LastWriteTimeUtc -ge $cutoff) { continue };",
            "$length=$item.Length; try { Remove-Item -LiteralPath $path -Force -ErrorAction Stop; $removed++; $bytes += $length } catch { $skipped++ } } catch { $skipped++ } } };",
            "Write-Output ('WINPROVISION_RESULT={0};{1};{2}' -f $removed,$bytes,$skipped)"
        ]);

        string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        WingetExecutionResult elevated = await ElevatedProcessRunner.RunElevatedAsync(
            "powershell.exe",
            $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}",
            cancellationToken);
        if (!elevated.Success)
            return new TemporaryFileCleanupResult(0, 0, 0,
                string.IsNullOrWhiteSpace(elevated.Output) ? "A limpeza do diretório do Windows foi cancelada ou falhou." : elevated.Output.Trim());

        string? resultLine = elevated.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(line => line.StartsWith("WINPROVISION_RESULT=", StringComparison.Ordinal));
        if (resultLine is null)
            return new TemporaryFileCleanupResult(0, 0, 0, "O Windows não retornou o resumo da limpeza do diretório do sistema.");

        string[] values = resultLine["WINPROVISION_RESULT=".Length..].Split(';');
        if (values.Length != 3
            || !long.TryParse(values[0], CultureInfo.InvariantCulture, out long deleted)
            || !long.TryParse(values[1], CultureInfo.InvariantCulture, out long bytes)
            || !long.TryParse(values[2], CultureInfo.InvariantCulture, out long skipped))
            return new TemporaryFileCleanupResult(0, 0, 0, "O resumo da limpeza do Windows estava inválido.");

        return new TemporaryFileCleanupResult(deleted, bytes, skipped);
    }

    private static IEnumerable<string> EnumerateOldFilesCore(
        string root,
        DateTime cutoff,
        CancellationToken cancellationToken,
        Action onSkipped)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string directory = pending.Pop();
            IEnumerable<string> entries;
            try { entries = Directory.GetFileSystemEntries(directory); }
            catch (Exception ex) when (IsExpectedFileSystemException(ex)) { onSkipped(); continue; }
            foreach (string entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                bool isOldFile = false;
                try
                {
                    FileAttributes attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) { onSkipped(); continue; }
                    if ((attributes & FileAttributes.Directory) != 0) { pending.Push(entry); continue; }
                    isOldFile = File.GetLastWriteTimeUtc(entry) < cutoff;
                }
                catch (Exception ex) when (IsExpectedFileSystemException(ex)) { onSkipped(); }
                if (isOldFile) yield return entry;
            }
        }
    }

    private static void RemoveEmptyDirectories(string root, DateTime cutoff, CancellationToken cancellationToken, ref long skipped)
    {
        var directories = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string directory = pending.Pop();
            try
            {
                foreach (string child in Directory.EnumerateDirectories(directory))
                {
                    if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) { skipped++; continue; }
                    directories.Add(child);
                    pending.Push(child);
                }
            }
            catch (Exception ex) when (IsExpectedFileSystemException(ex)) { skipped++; }
        }

        for (int index = directories.Count - 1; index >= 0; index--)
        {
            try
            {
                if (Directory.GetLastWriteTimeUtc(directories[index]) < cutoff
                    && !Directory.EnumerateFileSystemEntries(directories[index]).Any())
                    Directory.Delete(directories[index]);
            }
            catch (Exception ex) when (IsExpectedFileSystemException(ex)) { skipped++; }
        }
    }

    private static bool IsExpectedFileSystemException(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or SecurityException or ArgumentException or NotSupportedException;

    private static void ValidateMinimumAgeDays(int minimumAgeDays)
    {
        if (minimumAgeDays is < 1 or > 365)
            throw new ArgumentOutOfRangeException(nameof(minimumAgeDays), "A idade mínima deve ficar entre 1 e 365 dias.");
    }
}
