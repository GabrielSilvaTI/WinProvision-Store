using System.Windows;
using System.Windows.Controls;
using WinProvision.Core.Services;

namespace WinProvision.Store.Controls;

public partial class OperationsQueuePanel : UserControl
{
    public static readonly DependencyProperty QueueProperty = DependencyProperty.Register(
        nameof(Queue), typeof(OperationsQueueService), typeof(OperationsQueuePanel));

    public OperationsQueueService Queue
    {
        get => (OperationsQueueService)GetValue(QueueProperty);
        set => SetValue(QueueProperty, value);
    }

    public OperationsQueuePanel()
    {
        InitializeComponent();
    }

    private void ClearFinished_Click(object sender, RoutedEventArgs e) => Queue?.ClearFinished();
}
