using WinProvision.Core.Models;

namespace WinProvision.Store.Services;

/// <summary>
/// Ponte entre quem pede para ver os detalhes de um app (ex.: HomePage, ao clicar num
/// cartão) e o overlay que efetivamente os mostra (AppDetailsOverlay, hospedado direto
/// no MainWindow — ver MainWindow.xaml/.xaml.cs). Singleton registrado em App.xaml.cs.
///
/// Existe pra que a tela de Detalhes deixe de ser uma janela separada (FluentWindow com
/// ShowDialog): antes disso, abrir "Detalhes" criava uma janela nova de verdade (com seu
/// próprio botão de fechar no chrome do Windows, de área de clique minúscula e fácil de
/// errar). Agora é um painel dentro da própria janela principal — sem processo/guia novo
/// e com um botão de fechar do tamanho que quisermos.
/// </summary>
public sealed class AppDetailsOverlayService
{
    public event Action<AppEntry>? Requested;

    public void Show(AppEntry app) => Requested?.Invoke(app);
}
