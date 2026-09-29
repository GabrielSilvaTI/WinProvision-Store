using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using WinProvision.Core.Models;

namespace WinProvision.Core.Services;

/// <summary>
/// Fila global de operações (instalar/atualizar/remover) em andamento, consumida pelo
/// painel flutuante (OperationsQueuePanel). É singleton via DI para que
/// qualquer página/janela (HomePage, AppDetailsOverlay, PackagesPage...) possa enfileirar
/// uma operação e o mesmo painel reflita tudo em tempo real.
/// </summary>
public class OperationsQueueService : INotifyPropertyChanged
{
    private readonly OperationHistoryService? _historyService;

    public ObservableCollection<OperationItem> Operations { get; } = [];

    public event PropertyChangedEventHandler? PropertyChanged;

    public OperationsQueueService(OperationHistoryService? historyService = null)
    {
        _historyService = historyService;
        Operations.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(TotalCount));
            OnPropertyChanged(nameof(CompletedCount));
            OnPropertyChanged(nameof(HasOperations));
            OnPropertyChanged(nameof(HeaderText));
            OnPropertyChanged(nameof(HasFailedOperations));
        };
    }

    public int TotalCount => Operations.Count;

    public int CompletedCount => Operations.Count(o => o.IsFinished);

    public bool HasOperations => Operations.Count > 0;

    public bool HasFailedOperations => Operations.Any(o => o.State == OperationState.Failed);

    public string HeaderText
    {
        get
        {
            int total = TotalCount;
            if (total == 0) return "Operações";
            return $"{CompletedCount} de {total} operações concluídas";
        }
    }

    public OperationItem Enqueue(string appName, OperationKind kind, string? iconUrl = null)
    {
        var item = new OperationItem(appName, kind, iconUrl);
        item.PropertyChanged += Item_PropertyChanged;
        item.DismissRequested += Item_DismissRequested;
        Operations.Add(item);
        return item;
    }

    private void Item_DismissRequested(OperationItem _) => ClearFinished();

    public void Remove(OperationItem item)
    {
        item.PropertyChanged -= Item_PropertyChanged;
        item.DismissRequested -= Item_DismissRequested;
        Operations.Remove(item);
        item.Dispose();
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(CompletedCount));
        OnPropertyChanged(nameof(HasOperations));
        OnPropertyChanged(nameof(HeaderText));
        OnPropertyChanged(nameof(HasFailedOperations));
    }

    private void Item_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(OperationItem.IsFinished) && sender is OperationItem item)
        {
            OnPropertyChanged(nameof(CompletedCount));
            OnPropertyChanged(nameof(HeaderText));
            OnPropertyChanged(nameof(HasFailedOperations));
            _historyService?.Record(item);
        }
        else if (e.PropertyName == nameof(OperationItem.State))
        {
            OnPropertyChanged(nameof(HasFailedOperations));
        }
    }

    /// <summary>Remove da lista as operações já finalizadas (concluídas, com falha ou canceladas).</summary>
    public void ClearFinished()
    {
        foreach (var item in Operations.Where(o => o.IsFinished).ToList())
        {
            Remove(item);
        }
    }

    /// <summary>Remove da lista apenas as operações concluídas com êxito.</summary>
    public void ClearSuccessful()
    {
        foreach (var item in Operations.Where(o => o.State == OperationState.Completed).ToList())
        {
            Remove(item);
        }
    }

    /// <summary>Cancela todas as operações em andamento ou na fila.</summary>
    public void CancelAll()
    {
        foreach (var item in Operations.Where(o => !o.IsFinished).ToList())
        {
            if (item.CanCancel)
            {
                item.CancelCommand.Execute(null);
            }
        }
    }

    private void OnPropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
