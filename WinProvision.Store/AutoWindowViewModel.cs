using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Microsoft.Win32;
using WinProvision.Core.Services;
using WinProvision.Store.Models;

namespace WinProvision.Store;

public sealed class AutoWindowViewModel : INotifyPropertyChanged, IDisposable
{
    private double _globalProgress;
    private string _globalProgressText = "0%";
    private string _profileName = "Preparing your workspace";
    private string _statusText = "Preparing…";
    private bool _isFinished;
    private bool _hasFailed;
    private readonly DispatcherTimer _timer;
    private readonly EventHandler _timerTickHandler;

    // Fecha a janela (e o console pai) sozinha alguns segundos depois que o
    // pipeline termina, sem depender do usuário clicar em "Fechar".
    private const int AutoCloseSeconds = 10;
    private readonly DispatcherTimer _autoCloseTimer;
    private readonly EventHandler _autoCloseTickHandler;
    private int _autoCloseSecondsRemaining;
    private bool _isAutoClosing;

    public ObservableCollection<AutoStageViewModel> Stages { get; } = [];
    public AutoSystemInfo SysInfo { get; } = AutoSystemInfo.Create();

    public double GlobalProgress { get => _globalProgress; private set { if (Math.Abs(_globalProgress - value) < 0.01) return; _globalProgress = value; OnPropertyChanged(); } }
    public string GlobalProgressText { get => _globalProgressText; private set { _globalProgressText = value; OnPropertyChanged(); } }
    public string ProfileName { get => _profileName; private set { _profileName = value; OnPropertyChanged(); } }
    public string StatusText { get => _statusText; private set { _statusText = value; OnPropertyChanged(); } }
    public bool IsFinished { get => _isFinished; private set { _isFinished = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanClose)); } }
    public bool HasFailed { get => _hasFailed; private set { _hasFailed = value; OnPropertyChanged(); } }
    public bool CanClose => true;

    public bool IsAutoClosing
    {
        get => _isAutoClosing;
        private set { _isAutoClosing = value; OnPropertyChanged(); OnPropertyChanged(nameof(FooterMessage)); OnPropertyChanged(nameof(CloseButtonText)); }
    }

    public int AutoCloseSecondsRemaining
    {
        get => _autoCloseSecondsRemaining;
        private set { _autoCloseSecondsRemaining = value; OnPropertyChanged(); OnPropertyChanged(nameof(FooterMessage)); OnPropertyChanged(nameof(CloseButtonText)); }
    }

    public string FooterMessage => IsAutoClosing
        ? $"Concluído. Fechando automaticamente em {AutoCloseSecondsRemaining}s..."
        : "Por favor, mantenha seu computador ligado.";

    public string CloseButtonText => IsAutoClosing
        ? $"Fechar ({AutoCloseSecondsRemaining}s)"
        : "Fechar";

    /// <summary>Disparado quando a contagem regressiva chega a zero — quem tiver a
    /// referência da Window (AutoWindow.xaml.cs) é responsável por fechá-la de fato.</summary>
    public event EventHandler? AutoCloseElapsed;

    public AutoWindowViewModel()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        _timerTickHandler = (_, _) =>
        {
            var now = DateTime.Now;
            foreach (var stage in Stages)
                stage.UpdateElapsed(now);
        };
        _timer.Tick += _timerTickHandler;

        _autoCloseTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _autoCloseTickHandler = (_, _) =>
        {
            AutoCloseSecondsRemaining--;
            if (AutoCloseSecondsRemaining > 0) return;

            _autoCloseTimer.Stop();
            AutoCloseElapsed?.Invoke(this, EventArgs.Empty);
        };
        _autoCloseTimer.Tick += _autoCloseTickHandler;
    }

    public void BuildPlan(IEnumerable<AutoInstallStageInfo> plan, string? profileName)
    {
        Stages.Clear();
        int i = 1;
        foreach (var stage in plan)
            Stages.Add(new AutoStageViewModel(stage, i++));

        ProfileName = string.IsNullOrWhiteSpace(profileName) ? "Perfil padrão" : profileName;
        GlobalProgress = 0;
        GlobalProgressText = "0%";
        StatusText = Stages.Count == 0 ? "Nada a configurar" : "Preparando...";
        IsFinished = false;
        HasFailed = false;
        _timer.Start();
    }

    public void ApplyEvent(AutoInstallStageEvent evt)
    {
        var item = Stages.FirstOrDefault(x => x.Stage == evt.Stage);
        if (item is null) return;

        item.Status = evt.State;
        item.Progress = evt.State switch
        {
            AutoInstallStageState.Completed => 100,
            AutoInstallStageState.Failed => Math.Max(item.Progress, evt.Progress),
            _ => evt.Progress
        };

        if (!string.IsNullOrWhiteSpace(evt.Detail))
            item.CurrentDetail = evt.Detail!;

        if (evt.State is AutoInstallStageState.Completed or AutoInstallStageState.Failed)
            item.UpdateElapsed(DateTime.Now);

        int totalWorkUnits = Stages.Sum(x => x.WorkUnits);
        double overall = totalWorkUnits == 0
            ? 100
            : Stages.Sum(x => x.Progress * x.WorkUnits) / totalWorkUnits;
        GlobalProgress = Math.Clamp(overall, 0, 100);
        GlobalProgressText = $"{GlobalProgress:0.0}%";
        HasFailed |= evt.State == AutoInstallStageState.Failed;
        StatusText = evt.State switch
        {
            AutoInstallStageState.InProgress => BuildProgressStatus(item, evt.Detail),
            AutoInstallStageState.Failed => "Concluído com avisos",
            AutoInstallStageState.Completed => HasFailed ? "Concluído com avisos" : "Aplicando sua configuração",
            _ => "Preparando..."
        };
    }

    private string BuildProgressStatus(AutoStageViewModel item, string? detail)
    {
        int stageIndex = Stages.IndexOf(item) + 1;
        string stageProgress = Stages.Count == 0 ? string.Empty : $"Etapa {stageIndex} de {Stages.Count}";
        string currentAction = string.IsNullOrWhiteSpace(detail) ? item.Title : detail.Trim();
        return $"{stageProgress}  •  {currentAction}";
    }

    public void Finish(AutoInstallExitCode exitCode)
    {
        foreach (var stage in Stages)
        {
            if (stage.Status is AutoInstallStageState.Completed) stage.Progress = 100;
        }
        IsFinished = true;
        HasFailed |= exitCode != AutoInstallExitCode.Success;
        GlobalProgress = 100;
        GlobalProgressText = "100%";
        StatusText = exitCode == AutoInstallExitCode.Success ? "Concluído" : "Concluído com avisos";
        _timer.Stop();

        // Todos os estágios terminaram (com sucesso ou com avisos): dispara a
        // contagem regressiva que fecha a UI (e o PowerShell/console pai que a
        // chamou) sozinha, sem exigir clique no usuário.
        AutoCloseSecondsRemaining = AutoCloseSeconds;
        IsAutoClosing = true;
        _autoCloseTimer.Start();
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Tick -= _timerTickHandler;

        _autoCloseTimer.Stop();
        _autoCloseTimer.Tick -= _autoCloseTickHandler;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class AutoSystemInfo
{
    public string WindowsVersion { get; init; } = "Windows";
    public string Architecture { get; init; } = RuntimeInformation.OSArchitecture switch
    {
        System.Runtime.InteropServices.Architecture.X64 => "x64",
        System.Runtime.InteropServices.Architecture.Arm64 => "ARM64",
        System.Runtime.InteropServices.Architecture.X86 => "x86",
        System.Runtime.InteropServices.Architecture.Arm => "ARM",
        _ => RuntimeInformation.OSArchitecture.ToString()
    };
    public string ProcessorName { get; init; } = "Processador desconhecido";
    public string RamSize { get; init; } = "Indisponível";
    public string DiskSpace { get; init; } = "Indisponível";

    public static AutoSystemInfo Create()
    {
        int buildNumber = GetWindowsBuildNumber();
        string productName = ReadRegistry64("ProductName")
                             ?? ReadRegistry32("ProductName")
                             ?? ReadRegistry("ProductName")
                             ?? "Windows";
        string displayVersion = ReadRegistry64("DisplayVersion")
                                ?? ReadRegistry32("DisplayVersion")
                                ?? ReadRegistry("DisplayVersion")
                                ?? string.Empty;

        // A Microsoft mantém "Windows 10" em ProductName em algumas instalações de
        // Windows 11. O build é a fonte confiável para distinguir as duas famílias.
        string normalizedProduct = NormalizeWindowsProductName(productName, buildNumber);
        if (string.IsNullOrWhiteSpace(displayVersion))
            displayVersion = InferDisplayVersion(buildNumber);

        string windows = string.IsNullOrWhiteSpace(displayVersion)
            ? normalizedProduct
            : $"{normalizedProduct} {displayVersion}";
        if (buildNumber > 0)
            windows = $"{windows} · build {buildNumber}";

        return new AutoSystemInfo
        {
            WindowsVersion = windows.Trim(),
            ProcessorName = ReadProcessorName(),
            RamSize = GetInstalledRam(),
            DiskSpace = GetSystemDriveSpace()
        };
    }

    private static int GetWindowsBuildNumber()
    {
        try
        {
            string? build = ReadRegistry64("CurrentBuildNumber") ?? ReadRegistry64("CurrentBuild") ?? ReadRegistry("CurrentBuildNumber") ?? ReadRegistry("CurrentBuild");
            if (int.TryParse(build, out int registryBuild) && registryBuild > 0)
                return registryBuild;
        }
        catch
        {
            // Fallback below.
        }

        return Environment.OSVersion.Version.Build;
    }

    private static string NormalizeWindowsProductName(string productName, int buildNumber)
    {
        if (buildNumber >= 22000 && productName.Contains("Windows 10", StringComparison.OrdinalIgnoreCase))
            return productName.Replace("Windows 10", "Windows 11", StringComparison.OrdinalIgnoreCase);

        return productName;
    }

    private static string InferDisplayVersion(int buildNumber) => buildNumber switch
    {
        >= 26200 and < 27000 => "25H2",
        >= 26100 and < 26200 => "24H2",
        >= 22631 and < 26100 => "23H2",
        >= 22621 and < 22631 => "22H2",
        >= 22000 and < 22621 => "21H2",
        _ => string.Empty
    };

    private static string? ReadRegistry64(string name)
    {
        try
        {
            using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                .OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", writable: false);
            return key?.GetValue(name)?.ToString();
        }
        catch
        {
            return null;
        }
    }

    private static string ReadProcessorName()
    {
        string? value = ReadProcessorName(RegistryView.Registry64)
                        ?? ReadProcessorName(RegistryView.Registry32);
        if (!string.IsNullOrWhiteSpace(value))
            return NormalizeWhitespace(value);

        var fallback = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER");
        return string.IsNullOrWhiteSpace(fallback)
            ? "Modelo do processador indisponível"
            : NormalizeWhitespace(fallback);
    }

    private static string? ReadProcessorName(RegistryView view)
    {
        try
        {
            using var cpu = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view)
                .OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0", writable: false);
            return cpu?.GetValue("ProcessorNameString")?.ToString();
        }
        catch
        {
            return null;
        }
    }

    private static string GetInstalledRam()
    {
        try
        {
            if (GetPhysicallyInstalledSystemMemory(out ulong totalKb) && totalKb > 0)
                return FormatBytes(totalKb * 1024UL);
        }
        catch
        {
            // Algumas VMs não expõem a memória instalada pelo firmware.
        }

        try
        {
            var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            if (GlobalMemoryStatusEx(ref status) && status.TotalPhysicalMemory > 0)
                return FormatBytes(status.TotalPhysicalMemory);
        }
        catch
        {
            // Keep the card usable even if the native query is unavailable.
        }

        return "Indisponível";
    }

    private static string GetSystemDriveSpace()
    {
        try
        {
            string root = Path.GetPathRoot(Environment.SystemDirectory)
                          ?? Path.GetPathRoot(Environment.CurrentDirectory)
                          ?? "C:\\";
            var drive = new DriveInfo(root);
            if (drive.IsReady)
            {
                string name = drive.Name.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return $"{name} • {FormatBytes((ulong)drive.AvailableFreeSpace)} livres de {FormatBytes((ulong)drive.TotalSize)}";
            }
        }
        catch
        {
            // Keep the UI deterministic if the drive query is unavailable.
        }

        return "Indisponível";
    }

    private static string? ReadRegistry(string name)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion",
                writable: false);
            return key?.GetValue(name)?.ToString();
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadRegistry32(string name)
    {
        try
        {
            using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32)
                .OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", writable: false);
            return key?.GetValue(name)?.ToString();
        }
        catch
        {
            return null;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPhysicallyInstalledSystemMemory(out ulong totalMemoryInKilobytes);

    [DllImport("kernel32.dll", EntryPoint = "GlobalMemoryStatusEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysicalMemory;
        public ulong AvailablePhysicalMemory;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    private static string FormatBytes(ulong bytes)
    {
        if (bytes == 0) return "0 GB";

        const double gib = 1024d * 1024d * 1024d;
        const double tib = gib * 1024d;

        return bytes >= tib
            ? $"{bytes / tib:0.0} TB"
            : $"{bytes / gib:0.0} GB";
    }

    private static string NormalizeWhitespace(string value)
        => string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
