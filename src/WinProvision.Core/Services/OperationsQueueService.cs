using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using WinProvision.Core.Models;

namespace WinProvision.Core.Services;

/// <summary>
/// Fila global de operações (instalar/atualizar/remover) em andamento, consumida pelo
/// painel flutuante estilo UnigetUI (OperationsQueuePanel). É singleton via DI para que
/// qualquer página/janela (HomePage, AppDetailsOverlay, PackagesPage...) possa enfileirar
/// uma operação e o mesmo painel reflita tudo em tempo real.
/// </summary>
public class OperationsQueueService : INotifyPropertyChanged
{
    public ObservableCollection<OperationItem> Operations { get; } = [];

    public event PropertyChangedEventHandler? PropertyChanged;

    public OperationsQueueService()
    {
        Operations.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(TotalCount));
            OnPropertyChanged(nameof(CompletedCount));
            OnPropertyChanged(nameof(HasOperations));
        };
    }

    public int TotalCount => Operations.Count;

    public int CompletedCount => Operations.Count(o => o.IsFinished);

    public bool HasOperations => Operations.Count > 0;

    public OperationItem Enqueue(string appName, OperationKind kind, string? iconUrl = null)
    {
        var item = new OperationItem(appName, kind, iconUrl);
        item.PropertyChanged += Item_PropertyChanged;
        Operations.Add(item);
        return item;
    }

    private void Item_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(OperationItem.IsFinished))
        {
            OnPropertyChanged(nameof(CompletedCount));
        }
    }

    /// <summary>Remove da lista as operações já finalizadas (concluídas, com falha ou canceladas).</summary>
    public void ClearFinished()
    {
        foreach (var item in Operations.Where(o => o.IsFinished).ToList())
        {
            item.PropertyChanged -= Item_PropertyChanged;
            Operations.Remove(item);
            item.Dispose();
        }
    }

    private void OnPropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
