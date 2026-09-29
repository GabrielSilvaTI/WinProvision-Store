using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WinProvision.Core.Models;
using WinProvision.Core.Services;
using Wpf.Ui.Controls;

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
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is false)
                OutputModalHost.Visibility = Visibility.Collapsed;
        };
    }

    private void ClosePanelButton_Click(object sender, RoutedEventArgs e) => Visibility = Visibility.Collapsed;

    private void Root_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;

        if (OutputModalHost.Visibility == Visibility.Visible)
            OutputModalHost.Visibility = Visibility.Collapsed;
        else
            Visibility = Visibility.Collapsed;
        e.Handled = true;
    }

    private void ClearFinished_Click(object sender, RoutedEventArgs e) => Queue?.ClearFinished();

    private void BulkMenuButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement button) return;

        var menu = new ContextMenu();

        var clearSuccessItem = new System.Windows.Controls.MenuItem
        {
            Header = "Limpar operações com êxito",
            Icon = new SymbolIcon { Symbol = SymbolRegular.Checkmark24, FontSize = 16 }
        };
        clearSuccessItem.Click += (_, _) => Queue?.ClearSuccessful();
        menu.Items.Add(clearSuccessItem);

        var clearFinishedItem = new System.Windows.Controls.MenuItem
        {
            Header = "Limpar todas as concluídas",
            Icon = new SymbolIcon { Symbol = SymbolRegular.Dismiss24, FontSize = 16 }
        };
        clearFinishedItem.Click += (_, _) => Queue?.ClearFinished();
        menu.Items.Add(clearFinishedItem);

        menu.Items.Add(new Separator());

        var cancelAllItem = new System.Windows.Controls.MenuItem
        {
            Header = "Cancelar todas as operações",
            Icon = new SymbolIcon { Symbol = SymbolRegular.Stop24, FontSize = 16 }
        };
        cancelAllItem.Click += (_, _) => Queue?.CancelAll();
        menu.Items.Add(cancelAllItem);

        menu.PlacementTarget = button;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void ItemMoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement button || button.Tag is not OperationItem item) return;

        var menu = new ContextMenu();

        var viewOutputItem = new System.Windows.Controls.MenuItem
        {
            Header = "Ver saída do terminal",
            Icon = new SymbolIcon { Symbol = SymbolRegular.WindowConsole20, FontSize = 16 }
        };
        viewOutputItem.Click += (_, _) => ShowOutputModal(item);
        menu.Items.Add(viewOutputItem);

        var copyLogItem = new System.Windows.Controls.MenuItem
        {
            Header = "Copiar saída do terminal",
            Icon = new SymbolIcon { Symbol = SymbolRegular.Copy24, FontSize = 16 }
        };
        copyLogItem.Click += (_, _) =>
        {
            try
            {
                Clipboard.SetText(item.GetFullLog());
            }
            catch { }
        };
        menu.Items.Add(copyLogItem);

        menu.Items.Add(new Separator());

        if (item.CanCancel)
        {
            var cancelItem = new System.Windows.Controls.MenuItem
            {
                Header = "Cancelar operação",
                Icon = new SymbolIcon { Symbol = SymbolRegular.Stop24, FontSize = 16 }
            };
            cancelItem.Click += (_, _) => item.CancelCommand.Execute(null);
            menu.Items.Add(cancelItem);
        }

        var removeItem = new System.Windows.Controls.MenuItem
        {
            Header = "Remover da lista",
            Icon = new SymbolIcon { Symbol = SymbolRegular.Delete24, FontSize = 16 }
        };
        removeItem.Click += (_, _) => Queue?.Remove(item);
        menu.Items.Add(removeItem);

        menu.PlacementTarget = button;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void LiveLineButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement button && button.Tag is OperationItem item)
        {
            ShowOutputModal(item);
        }
    }

    private void ShowOutputModal(OperationItem item)
    {
        OutputModalTitle.Text = $"Saída da Operação: {item.AppName}";
        OutputModalTextBox.Text = item.GetFullLog();
        OutputModalHost.Visibility = Visibility.Visible;
        OutputModalTextBox.Focus();
    }

    private void CloseOutputModal_Click(object sender, RoutedEventArgs e)
    {
        OutputModalHost.Visibility = Visibility.Collapsed;
    }

    private void CopyOutputLog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(OutputModalTextBox.Text);
        }
        catch { }
    }
}
