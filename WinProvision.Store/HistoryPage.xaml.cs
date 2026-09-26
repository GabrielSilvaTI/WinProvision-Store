using System.Windows;
using System.Windows.Controls;
using WinProvision.Core.Services;

namespace WinProvision.Store;

public partial class HistoryPage : Page
{
    private readonly OperationHistoryService _historyService;

    public HistoryPage(OperationHistoryService historyService)
    {
        InitializeComponent();
        _historyService = historyService;
        HistoryList.ItemsSource = _historyService.Entries;
        UpdateEmptyState();
        _historyService.Entries.CollectionChanged += (_, _) => UpdateEmptyState();
    }

    private void UpdateEmptyState() =>
        EmptyText.Visibility = _historyService.Entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void ClearHistory_Click(object sender, RoutedEventArgs e) => _historyService.Clear();
}
