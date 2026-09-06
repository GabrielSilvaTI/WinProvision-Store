using System.ComponentModel;
using System.Runtime.CompilerServices;
using WinProvision.Core.Services;

namespace WinProvision.Store.Models;

public sealed class AutoStageViewModel : INotifyPropertyChanged
{
    private AutoInstallStageState _status = AutoInstallStageState.Pending;
    private string _executionTime = "";
    private double _progress;
    private string _currentDetail = "";
    private DateTime? _startedAt;

    public AutoStageViewModel(AutoInstallStageInfo info, int number)
    {
        Stage = info.Stage;
        Title = info.Title;
        Description = info.Description;
        Number = number.ToString("00");
    }

    public AutoInstallStage Stage { get; }
    public string Number { get; }
    public string Title { get; }
    public string Description { get; }

    public double Progress
    {
        get => _progress;
        set
        {
            if (Math.Abs(_progress - value) < 0.01) return;
            _progress = Math.Clamp(value, 0, 100);
            OnPropertyChanged();
            OnPropertyChanged(nameof(ProgressText));
            OnPropertyChanged(nameof(IsIndeterminate));
        }
    }

    /// <summary>
    /// True enquanto a etapa está rodando e ainda nenhum item terminou (0%) — a barra
    /// mostra o shimmer deslizante em vez de um preenchimento vazio parado. Assim que o
    /// primeiro item conclui (Progress > 0), vira preenchimento em degraus (1 item
    /// concluído / total de itens da etapa) e para de ser indeterminada.
    /// </summary>
    public bool IsIndeterminate => Status == AutoInstallStageState.InProgress && Progress <= 0.01;

    public string CurrentDetail
    {
        get => _currentDetail;
        set { if (_currentDetail == value) return; _currentDetail = value; OnPropertyChanged(); }
    }

    public string ProgressText => Status == AutoInstallStageState.InProgress && Progress > 0
        ? $"{Math.Round(Progress):0}%"
        : StatusText;

    public AutoInstallStageState Status
    {
        get => _status;
        set
        {
            if (_status == value) return;
            var previous = _status;
            _status = value;
            if (value == AutoInstallStageState.InProgress && _startedAt is null)
                _startedAt = DateTime.Now;
            if (previous == AutoInstallStageState.InProgress && value is (AutoInstallStageState.Completed or AutoInstallStageState.Failed) && _startedAt is { } started)
                ExecutionTime = FormatElapsed(DateTime.Now - started);
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(ProgressText));
            OnPropertyChanged(nameof(IsIndeterminate));
        }
    }

    public string ExecutionTime
    {
        get => _executionTime;
        set { if (_executionTime == value) return; _executionTime = value; OnPropertyChanged(); }
    }

    public void UpdateElapsed(DateTime now)
    {
        if (Status == AutoInstallStageState.InProgress && _startedAt is { } started)
            ExecutionTime = FormatElapsed(now - started);
    }

    private static string FormatElapsed(TimeSpan elapsed)
        => elapsed.TotalHours >= 1 ? elapsed.ToString(@"hh\:mm\:ss") : elapsed.ToString(@"mm\:ss");

    public string StatusText => Status switch
    {
        AutoInstallStageState.InProgress => "Em execução",
        AutoInstallStageState.Completed => "Concluído",
        AutoInstallStageState.CompletedWithWarnings => "Concluído com avisos",
        AutoInstallStageState.Failed => "Falhou",
        _ => "Aguardando"
    };

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
