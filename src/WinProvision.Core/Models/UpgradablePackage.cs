using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace WinProvision.Core.Models;

/// <summary>
/// Um item da lista de "Atualizações": um pacote já instalado que tem uma versão mais nova
/// disponível. É montado em tempo de execução pelo catálogo local COM do WinGet, com fallback
/// e/ou para a saída de <c>winget upgrade</c> (ver <c>WinGetService.GetUpgradablePackagesAsync</c>).
/// </summary>
public class UpgradablePackage : INotifyPropertyChanged
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string CurrentVersion { get; init; } = string.Empty;
    public string AvailableVersion { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public string SourceLabel => Source.Trim().ToLowerInvariant() switch
    {
        "winget" => "WinGet",
        "msstore" => "Microsoft Store",
        "" => "Origem local",
        _ => Source.Trim()
    };

    /// <summary>
    /// Preenchido pela tela (UpdatesPage), cruzando o Id com o catálogo já carregado
    /// (StoreService), só pra mostrar o mesmo ícone que aparece em Visão Geral/Pacotes.
    /// Fica vazio quando o app não está no catálogo do WinProvision — a UI trata isso
    /// mostrando um placeholder.
    /// </summary>
    public string IconUrl { get; set; } = string.Empty;

    private bool _isSelectedForUpdate;

    /// <summary>Estado do CheckBox de seleção na tela Atualizações (ver UpdatesPage).</summary>
    public bool IsSelectedForUpdate
    {
        get => _isSelectedForUpdate;
        set
        {
            if (_isSelectedForUpdate == value)
                return;

            _isSelectedForUpdate = value;
            OnPropertyChanged();
        }
    }

    private bool _isUpdating;

    /// <summary>Ligado enquanto a atualização deste item está em andamento (ver OperationRunner.RunUpdateAsync).</summary>
    public bool IsUpdating
    {
        get => _isUpdating;
        set
        {
            if (_isUpdating == value)
                return;

            _isUpdating = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
