using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace WinProvision.Store;

/// <summary>
/// Cria confirmações com dimensões e conteúdo padronizados usando o ContentDialog do WPF-UI.
/// </summary>
internal static class StoreConfirmationDialog
{
    public static Task<ContentDialogResult> ShowAsync(
        string title,
        string message,
        string confirmText,
        string cancelText,
        string? secondaryText = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(message);
        ArgumentException.ThrowIfNullOrWhiteSpace(confirmText);
        ArgumentException.ThrowIfNullOrWhiteSpace(cancelText);

        var body = new System.Windows.Controls.TextBlock
        {
            Text = message,
            Style = (Style)Application.Current.FindResource("StoreDialogMessageStyle")
        };

        var dialog = new ContentDialog
        {
            Title = title,
            Content = body,
            PrimaryButtonText = confirmText,
            CloseButtonText = cancelText,
            SecondaryButtonText = secondaryText ?? string.Empty,
            PrimaryButtonAppearance = ControlAppearance.Primary,
            SecondaryButtonAppearance = ControlAppearance.Secondary,
            CloseButtonAppearance = ControlAppearance.Secondary,
            DefaultButton = ContentDialogButton.Close,
            DialogWidth = (double)Application.Current.FindResource("StoreConfirmationDialogWidth"),
            DialogMaxWidth = (double)Application.Current.FindResource("StoreConfirmationDialogMaxWidth"),
            DialogMaxHeight = (double)Application.Current.FindResource("StoreConfirmationDialogMaxHeight")
        };

        var service = App.Services.GetRequiredService<IContentDialogService>();
        return service.ShowAsync(dialog, cancellationToken);
    }
}
