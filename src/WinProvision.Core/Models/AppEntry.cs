using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using WinProvision.Core.Models.Office;

namespace WinProvision.Core.Models;

/// <summary>
/// Modelo do aplicativo listado no apps.json da loja.
/// Campos vêm do manifesto do winget-pkgs (locale/instalador) e são
/// enriquecidos pela engine de indexação (Score, RegionTags, GitHubStars).
/// </summary>
public class AppEntry : INotifyPropertyChanged
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("publisher")]
    public string Publisher { get; set; } = string.Empty;

    [JsonPropertyName("publisherUrl")]
    public string? PublisherUrl { get; set; }

    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("moniker")]
    public string? Moniker { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("homepage")]
    public string? Homepage { get; set; }

    [JsonPropertyName("packageUrl")]
    public string? PackageUrl { get; set; }

    [JsonPropertyName("license")]
    public string? License { get; set; }

    [JsonPropertyName("licenseUrl")]
    public string? LicenseUrl { get; set; }

    [JsonPropertyName("releaseNotesUrl")]
    public string? ReleaseNotesUrl { get; set; }

    [JsonPropertyName("tags")]
    public List<string> Tags { get; set; } = [];

    [JsonPropertyName("architectures")]
    public List<string> Architectures { get; set; } = [];

    /// <summary>
    /// Score de relevância calculado (0-100). Ver ScoringEngine para a fórmula.
    /// </summary>
    [JsonPropertyName("score")]
    public int Score { get; set; }

    /// <summary>
    /// Tags de região com apelo identificado (ex.: ["BR"]). Vazio = sem apelo regional detectado.
    /// </summary>
    [JsonPropertyName("regionTags")]
    public List<string> RegionTags { get; set; } = [];

    /// <summary>
    /// Indica se foi possível localizar um repositório GitHub para enriquecer o score
    /// com estrelas/forks/atividade.
    /// </summary>
    [JsonPropertyName("hasGitHubMetrics")]
    public bool HasGitHubMetrics { get; set; }

    [JsonPropertyName("gitHubStars")]
    public int? GitHubStars { get; set; }

    /// <summary>
    /// Preenchido em tempo de execução pelo IconService. Não vem do apps.json.
    /// </summary>
    [JsonIgnore]
    public string IconUrl { get; set; } = string.Empty;

    /// <summary>
    /// Presente só quando este item representa um plano de Office (ex.: criado pelo
    /// botão "Adicionar ao catálogo" da OfficePage), em vez de um pacote winget comum.
    /// Null = app winget normal, instalado via <see cref="Services.WingetExecutor"/>;
    /// não-null = a tela de Pacotes/Instalar deve rodar o pipeline do ODT
    /// (<see cref="Services.Office.OfficeDeploymentToolService"/>) com estes parâmetros
    /// em vez de chamar o winget. Serializado normalmente no perfil (.json) — é o que
    /// permite exportar/importar apps winget e planos de Office no mesmo arquivo.
    /// </summary>
    [JsonPropertyName("office")]
    public OfficeInstallOptions? Office { get; set; }

    private long? _installerSizeBytes;

    /// <summary>
    /// Tamanho estimado do instalador, em bytes. Não existe um campo de tamanho no
    /// schema oficial do winget-pkgs (não há "InstallerSize" no manifesto) — este
    /// valor vem de uma checagem HTTP (HEAD/Range) feita pelo Indexer na
    /// sincronização diária, direto contra a(s) InstallerUrl do manifesto (ver
    /// InstallerSizeResolver). Nem todo pacote resolve (URL pode bloquear HEAD/Range,
    /// redirecionar de forma atípica, etc.) — null significa "não foi possível
    /// estimar", não "instalador vazio". Quando ausente aqui, a tela de Pacotes ainda
    /// tenta um fallback ao vivo via WingetExecutor.GetPackageInstallerSizeAsync.
    /// </summary>
    [JsonPropertyName("installerSizeBytes")]
    public long? InstallerSizeBytes
    {
        get => _installerSizeBytes;
        set
        {
            if (_installerSizeBytes == value) return;
            _installerSizeBytes = value;
            OnPropertyChanged();
        }
    }

    private bool _isInstalled;

    /// <summary>
    /// Preenchido em tempo de execução pelo InstalledAppsService (via "winget export").
    /// Não vem do apps.json. Notifica mudança para o botão Instalar/Abrir do card e do
    /// painel de detalhes trocarem automaticamente via binding.
    /// </summary>
    [JsonIgnore]
    public bool IsInstalled
    {
        get => _isInstalled;
        set
        {
            if (_isInstalled == value)
                return;

            _isInstalled = value;
            OnPropertyChanged();
        }
    }

    private bool _isInstalling;

    /// <summary>
    /// Ligado enquanto uma instalação está em andamento para este app (ver
    /// OperationRunner.RunInstallAsync). Usado pelo botão de ação do card/detalhes
    /// para trocar o rótulo para "Instalando" e bloquear novos cliques sem depender
    /// de IsEnabled (que mudaria a cor do botão via o estado "disabled" padrão).
    /// </summary>
    [JsonIgnore]
    public bool IsInstalling
    {
        get => _isInstalling;
        set
        {
            if (_isInstalling == value)
                return;

            _isInstalling = value;
            OnPropertyChanged();
        }
    }

    private bool _isSelectedForInstall;

    /// <summary>
    /// Estado do CheckBox de seleção na tela Pacotes (ver PackagesPage). Não vem do
    /// apps.json nem do perfil (.json) — é só o estado transitório de "marcado pra
    /// instalar" usado pelo botão geral "Instalar" da barra de ferramentas, ao estilo
    /// do UnigetUI, em vez de um botão de instalar por item.
    /// </summary>
    [JsonIgnore]
    public bool IsSelectedForInstall
    {
        get => _isSelectedForInstall;
        set
        {
            if (_isSelectedForInstall == value)
                return;

            _isSelectedForInstall = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}