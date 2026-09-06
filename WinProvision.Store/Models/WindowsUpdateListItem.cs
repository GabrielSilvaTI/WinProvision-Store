using System.ComponentModel;
using System.Runtime.CompilerServices;
using WinProvision.Core.Models.Provisioning;

namespace WinProvision.Store.Models;

/// <summary>
/// Wrapper de UI em torno de <see cref="WindowsUpdateItem"/> (imutável, sem notificação) — junta
/// o índice na última busca de <see cref="Core.Services.Provisioning.WindowsUpdateService"/>
/// (necessário pra instalar, já que o serviço trabalha com os objetos COM originais, não com o
/// DTO) e o estado de seleção/instalação exibido na aba Atualizações &amp; Drivers.
/// </summary>
public class WindowsUpdateListItem(WindowsUpdateItem item, int index) : INotifyPropertyChanged
{
    public int Index { get; } = index;

    public string Title => item.Title;

    public bool IsDriver => item.IsDriver;

    public string TypeLabel => item.IsDriver ? "Driver" : "Atualização";

    public string KbLabel => item.KbArticleIds.Count == 0
        ? string.Empty
        : "KB" + string.Join(", KB", item.KbArticleIds);

    public string SizeLabel => item.MaxDownloadSizeBytes <= 0
        ? string.Empty
        : $"{item.MaxDownloadSizeBytes / (1024.0 * 1024.0):0.#} MB";

    private bool _isSelected;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            OnPropertyChanged();
        }
    }

    private bool _isInstalling;

    public bool IsInstalling
    {
        get => _isInstalling;
        set
        {
            if (_isInstalling == value) return;
            _isInstalling = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
