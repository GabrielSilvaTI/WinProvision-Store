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
/// Camada/via usada para a operação em andamento (ver WinGetService: COM -&gt; API própria ->
/// winget.exe). Não é mostrada como texto pro usuário final — só orienta a cor da barra de
/// progresso (ver GradientProgressBar.xaml). "Unknown" = ainda não identificada (cor padrão).
/// </summary>
public enum WingetMethod
{
    Unknown,
    ComApi,
    OwnApi,
    WingetExe
}

/// <summary>
/// Representa uma operação (instalar/atualizar/remover) exibida no painel de fila,
/// com nome do app, linha de status (ex.: URL/etapa atual),
/// progresso (determinado ou indeterminado) e um comando de cancelamento.
/// </summary>
public class OperationItem : INotifyPropertyChanged, IDisposable
{
    private string _statusText = "Na fila...";
    private string _liveLine = "Na fila...";
    private double _progress;
    private bool _isIndeterminate = true;
    private OperationState _state = OperationState.Queued;
    private bool _canCancel = true;
    private WingetMethod _method = WingetMethod.Unknown;
    private string? _detailText;
    private readonly List<string> _logLines = [];
    private readonly object _logLock = new();

    public OperationItem(string appName, OperationKind kind, string? iconUrl = null)
    {
        AppName = appName;
        Kind = kind;
        IconUrl = iconUrl;
        CancelCommand = new RelayCommand(_ => Cancel(), _ => CanCancel);
        ActionCommand = new RelayCommand(_ =>
        {
            if (IsFinished)
                RequestDismiss();
            else
                Cancel();
        });
    }

    public Guid Id { get; } = Guid.NewGuid();
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

    public string OperationTitle => $"{AppName} — {Kind switch
    {
        OperationKind.Install => "Instalação",
        OperationKind.Update => "Atualização",
        OperationKind.Uninstall => "Desinstalação",
        _ => "Operação"
    }}";

    /// <summary>Título da operação (ex.: 'Atualizando Boto3...').</summary>
    public string DisplayTitle => State switch
    {
        OperationState.Running => $"{KindLabel} {AppName}...",
        OperationState.Queued => $"{KindLabel} {AppName}...",
        OperationState.Completed => Kind switch
        {
            OperationKind.Install => $"{AppName} instalado com sucesso",
            OperationKind.Update => $"{AppName} atualizado com sucesso",
            OperationKind.Uninstall => $"{AppName} removido com sucesso",
            _ => $"{AppName} concluído"
        },
        OperationState.Failed => $"Falha ao {KindVerb} {AppName}",
        OperationState.Canceled => $"{KindLabel} {AppName} cancelado",
        _ => $"{AppName}"
    };

    public string KindVerb => Kind switch
    {
        OperationKind.Install => "instalar",
        OperationKind.Update => "atualizar",
        OperationKind.Uninstall => "remover",
        _ => "processar"
    };

    public string ButtonText => IsFinished ? "Fechar" : "Cancelar";

    /// <summary>Linha ao vivo exibida logo abaixo do título (ex.: 'Downloading boto3-1.43.102-py3-none-any.whl.metadata (6.6 kB)').</summary>
    public string LiveLine
    {
        get => _liveLine;
        set => SetField(ref _liveLine, value);
    }

    /// <summary>Resumo curto para a interface; logs detalhados permanecem em DetailText.</summary>
    public string UserFacingStatusText => State switch
    {
        OperationState.Queued => "Na fila",
        OperationState.Running => string.IsNullOrWhiteSpace(StatusText) ? $"{KindLabel}…" : StatusText,
        OperationState.Completed => Kind switch
        {
            OperationKind.Install => "Instalado",
            OperationKind.Update => "Atualizado",
            OperationKind.Uninstall => "Removido",
            _ => "Concluído"
        },
        OperationState.Failed => Kind switch
        {
            OperationKind.Install => "Falha na instalação",
            OperationKind.Update => "Falha na atualização",
            OperationKind.Uninstall => "Falha na remoção",
            _ => "Falha na operação"
        },
        OperationState.Canceled => "Cancelado",
        _ => string.Empty
    };

    public string StatusText
    {
        get => _statusText;
        set
        {
            if (SetField(ref _statusText, value))
            {
                if (string.IsNullOrWhiteSpace(_liveLine) || _liveLine == "Na fila...")
                {
                    LiveLine = value;
                }
            }
        }
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

    /// <summary>Camada atual (COM/API própria/winget.exe), usada só pra colorir a barra de progresso.</summary>
    public WingetMethod Method
    {
        get => _method;
        set => SetField(ref _method, value);
    }

    /// <summary>
    /// Última linha técnica recebida (ex.: "A API COM falhou (...); usando a API própria..."),
    /// guardada só pro tooltip de debug — o <see cref="StatusText"/> mostrado ao usuário fica
    /// limpo, sem mencionar a via.
    /// </summary>
    public string? DetailText
    {
        get => _detailText;
        set => SetField(ref _detailText, value);
    }

    public void AppendLog(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        lock (_logLock)
        {
            _logLines.Add(line.TrimEnd());
        }
        LiveLine = line.Trim();
    }

    public string GetFullLog()
    {
        lock (_logLock)
        {
            return _logLines.Count > 0
                ? string.Join(Environment.NewLine, _logLines)
                : (DetailText ?? StatusText ?? "Nenhum log disponível.");
        }
    }

    public event Action<OperationItem>? DismissRequested;
    public void RequestDismiss() => DismissRequested?.Invoke(this);

    public OperationState State
    {
        get => _state;
        set
        {
            if (SetField(ref _state, value))
            {
                CanCancel = value is OperationState.Queued or OperationState.Running;

                // Ao encerrar, substitui a última etapa técnica (ex.: "Preparando...")
                // pelo resultado real. Os detalhes completos continuam disponíveis no log.
                if (value == OperationState.Completed)
                {
                    LiveLine = Kind switch
                    {
                        OperationKind.Install => "Instalação concluída.",
                        OperationKind.Update => "Atualização concluída.",
                        OperationKind.Uninstall => "Remoção concluída.",
                        _ => "Operação concluída."
                    };
                }
                else if (value == OperationState.Failed)
                {
                    LiveLine = Kind switch
                    {
                        OperationKind.Install => "Falha na instalação.",
                        OperationKind.Update => "Falha na atualização.",
                        OperationKind.Uninstall => "Falha na remoção.",
                        _ => "Falha na operação."
                    };
                }
                else if (value == OperationState.Canceled)
                {
                    LiveLine = "Operação cancelada.";
                }

                OnPropertyChanged(nameof(IsFinished));
                OnPropertyChanged(nameof(UserFacingStatusText));
                OnPropertyChanged(nameof(DisplayTitle));
                OnPropertyChanged(nameof(ButtonText));
            }
        }
    }

    public bool IsFinished => State is OperationState.Completed or OperationState.Failed or OperationState.Canceled;

    public ICommand ActionCommand { get; }

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
