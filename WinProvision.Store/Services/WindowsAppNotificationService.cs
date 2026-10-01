using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace WinProvision.Store.Services;

/// <summary>Notificações nativas do Windows para operações que terminam sem janela aberta.</summary>
public sealed class WindowsAppNotificationService
{
    private bool _registered;
    private bool _activationHandlerRegistered;
    private Action? _onNotificationInvoked;

    public bool TryRegister(Action? onNotificationInvoked = null)
    {
        try
        {
            var manager = AppNotificationManager.Default;
            _onNotificationInvoked = onNotificationInvoked;

            // A documentação do Windows App SDK exige o handler antes do Register.
            // Isso também permite trazer a janela para frente quando o usuário clica
            // no resumo de uma execução em segundo plano.
            if (!_activationHandlerRegistered)
            {
                manager.NotificationInvoked += OnNotificationInvoked;
                _activationHandlerRegistered = true;
            }

            manager.Register();
            _registered = true;
            WinProvisionLog.Write("APP NOTIFICATIONS registered=true");
            return true;
        }
        catch (Exception ex)
        {
            WinProvisionLog.Write($"APP NOTIFICATIONS registration failed type={ex.GetType().Name} message={ex.Message}");
            return false;
        }
    }

    private void OnNotificationInvoked(
        AppNotificationManager sender,
        AppNotificationActivatedEventArgs args)
    {
        WinProvisionLog.Write("APP NOTIFICATIONS activated=true");
        try
        {
            _onNotificationInvoked?.Invoke();
        }
        catch (Exception ex)
        {
            WinProvisionLog.Write($"APP NOTIFICATIONS activation failed type={ex.GetType().Name} message={ex.Message}");
        }
    }

    public void ShowBackgroundUpdateSummary(int updated, int failed)
    {
        if (!_registered)
            return;

        string message = updated == 0 && failed == 0
            ? "Seus aplicativos já estão atualizados."
            : failed == 0
                ? $"{updated} aplicativo(s) atualizado(s) com sucesso."
                : $"{updated} atualizado(s); {failed} não puderam ser atualizados. Abra o app para ver os detalhes.";

        try
        {
            var notification = new AppNotificationBuilder()
                .AddText("WinProvision Store")
                .AddText("Atualização em segundo plano concluída")
                .AddText(message)
                .BuildNotification();
            AppNotificationManager.Default.Show(notification);
            WinProvisionLog.Write($"APP NOTIFICATIONS background-update updated={updated} failed={failed}");
        }
        catch (Exception ex)
        {
            WinProvisionLog.Write($"APP NOTIFICATIONS show failed type={ex.GetType().Name} message={ex.Message}");
        }
    }

    public void ShowBackgroundUpdateFailure()
    {
        if (!_registered)
            return;

        try
        {
            var notification = new AppNotificationBuilder()
                .AddText("WinProvision Store")
                .AddText("Não foi possível concluir as atualizações em segundo plano")
                .AddText("Abra o app e tente verificar as atualizações novamente.")
                .BuildNotification();
            AppNotificationManager.Default.Show(notification);
            WinProvisionLog.Write("APP NOTIFICATIONS background-update failure=true");
        }
        catch (Exception ex)
        {
            WinProvisionLog.Write($"APP NOTIFICATIONS failure notification failed type={ex.GetType().Name} message={ex.Message}");
        }
    }

    public void Unregister()
    {
        if (!_registered)
            return;

        try
        {
            var manager = AppNotificationManager.Default;
            manager.Unregister();
            if (_activationHandlerRegistered)
            {
                manager.NotificationInvoked -= OnNotificationInvoked;
                _activationHandlerRegistered = false;
            }
            WinProvisionLog.Write("APP NOTIFICATIONS unregistered=true");
        }
        catch (Exception ex)
        {
            WinProvisionLog.Write($"APP NOTIFICATIONS unregister failed type={ex.GetType().Name} message={ex.Message}");
        }
        finally
        {
            _registered = false;
        }
    }
}
