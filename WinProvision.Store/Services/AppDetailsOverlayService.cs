using WinProvision.Core.Models;
using WinProvision.Core.Models.Office;

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
    public event Action<AppEntry, string, string>? UpdateRequested;

    public void Show(AppEntry app) => Requested?.Invoke(app);

    public void Show(OfficeStoreOffer offer)
    {
        ArgumentNullException.ThrowIfNull(offer);
        var planScreenshots = OfficePlanCatalog.ByProductId(offer.OdtProductId)?.Screenshots;
        var screenshots = offer.Screenshots is { Count: > 0 }
            ? offer.Screenshots
            : planScreenshots;
        Show(new AppEntry
        {
            Id = offer.StoreProductId,
            Source = "msstore",
            Name = offer.DisplayName,
            Publisher = "Microsoft Corporation",
            Version = "Microsoft Store",
            Description = offer.Description,
            Homepage = $"https://apps.microsoft.com/detail/{offer.StoreProductId}",
            StoreIconUrl = offer.IconUrl,
            IconUrl = offer.IconUrl ?? string.Empty,
            StoreBannerUrl = offer.BannerUrl,
            StoreScreenshotUrls = screenshots?.ToList(),
            Tags = ["Microsoft Store", "Microsoft 365", "Office"],
        });
    }

    public void ShowUpdate(AppEntry app, string currentVersion, string availableVersion) =>
        UpdateRequested?.Invoke(app, currentVersion, availableVersion);
}
