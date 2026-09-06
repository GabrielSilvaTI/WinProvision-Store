using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows.Input;

namespace WinProvision.Core.Models;

public enum OperationKind
{
    Install,
    Update,
    Uninstall
}

public enum OperationState
{
    Queued,
    Running,
    Completed,
    Failed,
    Canceled
}

/// <summary>
/// Representa uma operação (instalar/atualizar/remover) exibida no painel de fila,
/// no estilo do UnigetUI: nome do app, linha de status (ex.: URL/etapa atual),
/// progresso (determinado ou indeterminado) e um comando de cancelamento.
/// </summary>
public class OperationItem : INotifyPropertyChanged, IDisposable
{
    private string _statusText = "Na fila...";
    private double _progress;
    private bool _isIndeterminate = true;
    private OperationState _state = OperationState.Queued;
    private bool _canCancel = true;

    public OperationItem(string appName, OperationKind kind, string? iconUrl = null)
    {
        AppName = appName;
        Kind = kind;
        IconUrl = iconUrl;
        CancelCommand = new RelayCommand(_ => Cancel(), _ => CanCancel);
    }

    public string AppName { get; }
    public OperationKind Kind { get; }
    public string? IconUrl { get; }

    public CancellationTokenSource CancellationTokenSource { get; } = new();

    public string KindLabel => Kind switch
    {
        OperationKind.Install => "Instalando",
        OperationKind.Update => "Atualizando",
        OperationKind.Uninstall => "Removendo",
        _ => "Processando"
    };

    public string StatusText
    {
        get => _statusText;
        set => SetField(ref _statusText, value);
    }

    /// <summary>Progresso de 0 a 100. Ignorado enquanto <see cref="IsIndeterminate"/> for true.</summary>
    public double Progress
    {
        get => _progress;
        set => SetField(ref _progress, value);
    }

    public bool IsIndeterminate
    {
        get => _isIndeterminate;
        set => SetField(ref _isIndeterminate, value);
    }

    public OperationState State
    {
        get => _state;
        set
        {
            if (SetField(ref _state, value))
            {
                CanCancel = value is OperationState.Queued or OperationState.Running;
                OnPropertyChanged(nameof(IsFinished));
            }
        }
    }

    public bool IsFinished => State is OperationState.Completed or OperationState.Failed or OperationState.Canceled;

    public bool CanCancel
    {
        get => _canCancel;
        private set
        {
            if (SetField(ref _canCancel, value))
            {
                (CancelCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    public ICommand CancelCommand { get; }

    private void Cancel()
    {
        if (!CanCancel) return;

        try
        {
            CancellationTokenSource.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Já finalizado/descartado - nada a fazer.
        }

        StatusText = "Cancelando...";
    }

    public void Dispose() => CancellationTokenSource.Dispose();

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged(string? propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

/// <summary>ICommand simples baseado em delegates, para não trazer uma dependência extra de MVVM só para isso.</summary>
public class RelayCommand(Action<object?> execute, Predicate<object?>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => canExecute?.Invoke(parameter) ?? true;

    public void Execute(object? parameter) => execute(parameter);

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
