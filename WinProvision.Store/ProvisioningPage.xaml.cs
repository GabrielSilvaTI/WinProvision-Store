using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Microsoft.Extensions.DependencyInjection;
using WinProvision.Core.Models;
using WinProvision.Core.Models.Provisioning;
using WinProvision.Core.Services;
using WinProvision.Core.Services.Backup;
using WinProvision.Core.Services.Profile;
using WinProvision.Core.Services.Provisioning;
using WinProvision.Store.Converters;
using Wpf.Ui.Appearance;
using Wpf.Ui;
using Wpf.Ui.Controls;
using Wpf.Ui.Extensions;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = System.Windows.Controls.TextBox;
using ContentDialogResult = Wpf.Ui.Controls.ContentDialogResult;
using SimpleContentDialogCreateOptions = Wpf.Ui.SimpleContentDialogCreateOptions;

namespace WinProvision.Store;

public partial class ProvisioningPage : Page
{
    private readonly ProvisioningService _provisioningService;
    private readonly TemporaryFileCleanupService _temporaryFileCleanupService;
    private IReadOnlyList<TemporaryFileCleanupEstimate>? _lastTempCleanupEstimates;
    private readonly PackageCollectionService _packageCollectionService;
    private readonly ProfileService _profileService;
    private readonly GitHubBackupService _githubBackupService;
    private readonly IContentDialogService _contentDialogService;
    // Guardados à parte (em vez de num controle de UI) porque o wallpaper é um arquivo, não um
    // valor editável — ficam aqui até o usuário exportar ou aplicar, e são preenchidos de volta
    // ao importar um perfil que já tenha wallpaper embutido.
    private string? _wallpaperFileName;
    private string? _wallpaperImageBase64;
    private string? _publishedBootstrapCommand;
    private string? _orchestratorLogFilePath;
    private bool _isPublishingBootstrap;
    private readonly DispatcherTimer _profileEditDebounceTimer;

    // Evita empurrar estado pro serviço enquanto LoadManifestIntoUi está preenchendo os
    // controles programaticamente (cada SelectionChanged/TextChanged disparado durante a
    // carga geraria um push com o manifesto ainda pela metade) — só falso durante a carga.
    private bool _uiLoaded;

    // ComboBoxes que devem ter rolagem de rodinha nativa no dropdown (Popup/HWND separada)
    private readonly List<ComboBox> _wheelAwareComboBoxes = new();

    private sealed record ProvisioningSearchEntry(
        string Title,
        string Description,
        string Category,
        ProvisioningCategory Section,
        string SearchTerms,
        string Symbol,
        FrameworkElement Target);

    private enum ProvisioningCategory { Personalization, System, Json }

    private readonly List<ProvisioningSearchEntry> _provisioningSearchEntries = [];
    private readonly List<ProvisioningSearchEntry> _provisioningSearchResults = [];

    public ProvisioningPage()
    {
        InitializeComponent();
        CleanUserTempCheckBox.IsChecked = true;
        _profileEditDebounceTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _profileEditDebounceTimer.Tick += ProfileEditDebounceTimer_Tick;
        InitializeProvisioningSearchEntries();
        Loaded += ProvisioningPage_Loaded;

        _provisioningService = App.Services.GetRequiredService<ProvisioningService>();
        _temporaryFileCleanupService = App.Services.GetRequiredService<TemporaryFileCleanupService>();
        _packageCollectionService = App.Services.GetRequiredService<PackageCollectionService>();
        _profileService = App.Services.GetRequiredService<ProfileService>();
        _githubBackupService = App.Services.GetRequiredService<GitHubBackupService>();
        _contentDialogService = App.Services.GetRequiredService<IContentDialogService>();
        _packageCollectionService.Changed += PackageCollectionChanged;
        _profileService.ImportValidationChanged += ProfileImportValidationChanged;

        CurrentMachineNameText.Text = $"Nome atual: {Environment.MachineName}";

        // Se já existe um estado de provisionamento "atual" nesta sessão (aplicado antes,
        // ou importado/exportado noutra visita a esta página, ou restaurado de um backup),
        // preenche a UI com ele — sem isso, reabrir esta página sempre parecia "em branco"
        // mesmo com algo pronto para sincronizar.
        if (_provisioningService.Current is { } current)
        {
            LoadManifestIntoUi(current);
        }

        _uiLoaded = true;
        RefreshProfileSummary();
        UpdateDesktopPreview();

        _wheelAwareComboBoxes.Add(DisplayTimeoutAcComboBox);
        _wheelAwareComboBoxes.Add(StandbyTimeoutAcComboBox);
        InputManager.Current.PostProcessInput += GlobalPostProcessInput;
    }

    private void ProvisioningPage_Loaded(object sender, RoutedEventArgs e)
    {
        UpdateOrchestratorIcon();
        UpdateOrchestratorLogPathVisibility();
        if (_publishedBootstrapCommand is null)
        {
            BootstrapCommandTextBox.Text = _githubBackupService.IsConnected
                ? "Publique o perfil para gerar o comando do PowerShell."
                : "Conecte o GitHub em Conta e Sincronização para publicar o Bootstrap.";
            PublishBootstrapButton.IsEnabled = _githubBackupService.IsConnected && !_isPublishingBootstrap;
        }
    }

    private void UpdateOrchestratorIcon()
    {
        string iconResource = ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Light
            ? "assets/winprovisionstore_black.png"
            : "assets/winprovisionstore_white.png";
        OrchestratorIcon.Source = App.LoadAssetBitmap(iconResource);
    }

    /// <summary>Captura TODOS os eventos de entrada do thread WPF, inclusive os do Popup do
    /// ComboBox (que é uma HWND separada e por padrão não passa o MouseWheel pro ScrollViewer
    /// interno quando a janela principal tem um ScrollViewer próprio). É o mesmo comportamento
    /// das páginas do app (Configurações do Windows): apontou o mouse pra qualquer área do
    /// dropdown e girou a rodinha, rola só as opções — sem levar a página principal junto.</summary>
    private void GlobalPostProcessInput(object sender, ProcessInputEventArgs e)
    {
        if (e.StagingItem?.Input is not MouseWheelEventArgs wheel) return;
        ComboBox? target = null;
        foreach (var cb in _wheelAwareComboBoxes)
        {
            if (cb.IsDropDownOpen && cb.IsMouseOver) { target = cb; break; }
        }
        if (target is null) return;

        Popup? popup = target.Template?.FindName("PART_Popup", target) as Popup;
        ScrollViewer? sv = popup is null ? null : FindVisualChild<ScrollViewer>(popup);
        if (sv is null || sv.ScrollableHeight <= 0) return;

        wheel.Handled = true;
        double offset = sv.VerticalOffset - (wheel.Delta / 3.0);
        if (offset < 0) offset = 0;
        if (offset > sv.ScrollableHeight) offset = sv.ScrollableHeight;
        sv.ScrollToVerticalOffset(offset);
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T found) return found;
            if (FindVisualChild<T>(child) is { } nested) return nested;
        }
        return null;
    }

    /// <summary>Alterna o menu lateral de "Provisionamento" entre a visão do Perfil (padrão) e uma seção específica.</summary>
    private void ShowSection(StackPanel sectionPanel, string title, bool showApplyButton = true)
    {
        PersonalizationSectionPanel.Visibility = Visibility.Collapsed;
        AdvancedSectionPanel.Visibility = Visibility.Collapsed;
        JsonSectionPanel.Visibility = Visibility.Collapsed;
        OrchestratorSectionPanel.Visibility = Visibility.Collapsed;
        sectionPanel.Visibility = Visibility.Visible;

        SectionTitleText.Text = title;
        ProfileOverviewPanel.Visibility = Visibility.Collapsed;
        SectionPanel.Visibility = Visibility.Visible;
        ApplyButton.Visibility = showApplyButton ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowProfileOverview()
    {
        SectionPanel.Visibility = Visibility.Collapsed;
        ProfileOverviewPanel.Visibility = Visibility.Visible;
        ApplyButton.Visibility = Visibility.Collapsed;
        RefreshProfileSummary();
    }

    private void InitializeProvisioningSearchEntries()
    {
        _provisioningSearchEntries.AddRange(
        [
            new("Tema do Windows", "Escolha o tema claro ou escuro da interface do Windows.", "Personalização", ProvisioningCategory.Personalization, "aparência tema claro escuro sistema", "Color24", ThemeComboBox),
            new("Tema dos aplicativos", "Defina o modo claro ou escuro usado pelos aplicativos compatíveis.", "Personalização", ProvisioningCategory.Personalization, "aparência tema claro escuro programas apps", "Color24", AppsThemeComboBox),
            new("Cor de destaque", "Escolha uma cor fixa ou deixe o Windows selecioná-la pelo papel de parede.", "Personalização", ProvisioningCategory.Personalization, "cor destaque automática papel parede hexadecimal", "Color24", AccentColorModeComboBox),
            new("Alinhamento da barra de tarefas", "Posicione os ícones à esquerda ou no centro.", "Personalização", ProvisioningCategory.Personalization, "barra tarefas centralizar esquerda", "Settings24", TaskbarAlignmentComboBox),
            new("Caixa de pesquisa", "Defina como a pesquisa aparece na barra de tarefas.", "Personalização", ProvisioningCategory.Personalization, "barra tarefas pesquisa ícone ocultar", "Search24", TaskbarSearchBoxComboBox),
            new("Ocultar a barra de tarefas automaticamente", "Recolha a barra quando ela não estiver em uso.", "Personalização", ProvisioningCategory.Personalization, "auto ocultar recolher", "Desktop24", TaskbarAutoHideCheckBox),
            new("Papel de parede", "Escolha uma imagem para o plano de fundo da área de trabalho.", "Personalização", ProvisioningCategory.Personalization, "imagem fundo plano desktop área trabalho", "Desktop24", SelectWallpaperButton),
            new("Nome da máquina", "Defina o nome que será atribuído ao computador.", "Configurações avançadas", ProvisioningCategory.System, "computador pc hostname dispositivo", "Desktop24", MachineNameTextBox),
            new("Data e hora automáticas", "Sincronize o relógio pela fonte configurada no Windows.", "Configurações avançadas", ProvisioningCategory.System, "data hora sincronização automática ntp", "CalendarClock24", AutomaticTimeCheckBox),
            new("Fuso horário automático", "Ajuste o fuso horário conforme a localização do sistema.", "Configurações avançadas", ProvisioningCategory.System, "timezone localização automático horário", "Globe24", AutomaticTimeZoneCheckBox),
            new("Plano de energia", "Escolha como equilibrar autonomia e desempenho.", "Configurações avançadas", ProvisioningCategory.System, "bateria desempenho economia equilibrado", "Power24", PowerPlanOptionsPanel),
            new("Extensões de arquivos", "Escolha se o Explorador exibe extensões conhecidas.", "Configurações avançadas", ProvisioningCategory.System, "explorador arquivos tipos sufixo", "FolderOpen24", ShowFileExtensionsCheckBox),
            new("Arquivos ocultos", "Escolha se o Explorador exibe arquivos e pastas ocultos.", "Configurações avançadas", ProvisioningCategory.System, "explorador pastas arquivos hidden", "FolderOpen24", ShowHiddenFilesCheckBox),
            new("Página inicial do Explorador", "Escolha entre Este Computador e Acesso rápido.", "Configurações avançadas", ProvisioningCategory.System, "explorador pastas abertura início este computador acesso rápido", "FolderOpen24", OpenExplorerToThisPcCheckBox),
            new("Desligar a tela", "Escolha após quanto tempo a tela será desligada.", "Configurações avançadas", ProvisioningCategory.System, "monitor vídeo tempo limite energia", "Desktop24", DisplayTimeoutAcComboBox),
            new("Suspender o computador", "Escolha após quanto tempo o PC entrará em suspensão.", "Configurações avançadas", ProvisioningCategory.System, "repouso dormir standby suspensão energia", "Power24", StandbyTimeoutAcComboBox),
            new("Limpeza de arquivos temporários", "Analise e limpe arquivos temporários antigos com segurança.", "Configurações avançadas", ProvisioningCategory.System, "limpar espaço disco cache arquivos temporários", "Broom24", AnalyzeTempFilesButton),
            new("Informação OEM", "Edite o texto de identificação OEM do Windows.", "Configurações avançadas", ProvisioningCategory.System, "fabricante identificação nome sobre", "Person24", ProfileNameTextBox),
            new("Nome do perfil", "Edite o nome associado ao perfil.", "Perfil e JSON", ProvisioningCategory.Json, "criador autor perfil", "Person24", ProfileCreatorTextBox),
            new("Código JSON", "Consulte o JSON gerado ou abra o editor completo.", "Perfil e JSON", ProvisioningCategory.Json, "arquivo código visualizar copiar exportar", "Code24", JsonPreviewTextBox)
        ]);
    }

    private void ProvisioningSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        string query = NormalizeProvisioningSearchText(ProvisioningSearchBox.Text ?? string.Empty);
        bool isSearching = query.Length > 0;
        if (isSearching && SectionPanel.Visibility == Visibility.Visible)
        {
            ShowProfileOverview();
        }

        ProvisioningSearchResultsPanel.Visibility = isSearching ? Visibility.Visible : Visibility.Collapsed;
        ProvisioningCategoriesPanel.Visibility = isSearching ? Visibility.Collapsed : Visibility.Visible;
        ProvisioningSearchResultsList.Children.Clear();
        _provisioningSearchResults.Clear();
        ProvisioningSearchEmptyText.Visibility = Visibility.Collapsed;

        if (!isSearching)
        {
            ProvisioningSearchResultText.Visibility = Visibility.Collapsed;
            ProvisioningSearchResultText.Text = string.Empty;
            return;
        }

        string[] terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        _provisioningSearchResults.AddRange(_provisioningSearchEntries.Where(entry =>
        {
            string searchableText = NormalizeProvisioningSearchText($"{entry.Title} {entry.Description} {entry.Category} {entry.SearchTerms}");
            return terms.All(term => searchableText.Contains(term, StringComparison.Ordinal));
        }));

        foreach (ProvisioningSearchEntry entry in _provisioningSearchResults)
            ProvisioningSearchResultsList.Children.Add(CreateProvisioningSearchResultButton(entry));

        ProvisioningSearchEmptyText.Visibility = _provisioningSearchResults.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ProvisioningSearchResultText.Text = _provisioningSearchResults.Count switch
        {
            0 => "Nenhuma opção encontrada.",
            1 => "1 opção encontrada. Selecione para abrir essa configuração.",
            _ => $"{_provisioningSearchResults.Count} opções encontradas. Selecione uma para abrir a configuração correspondente."
        };
        ProvisioningSearchResultText.Visibility = Visibility.Visible;
    }

    private void ProvisioningSearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || _provisioningSearchResults.Count == 0)
        {
            return;
        }

        NavigateToProvisioningSearchResult(_provisioningSearchResults[0]);
        e.Handled = true;
    }

    private Wpf.Ui.Controls.Button CreateProvisioningSearchResultButton(ProvisioningSearchEntry entry)
    {
        var button = new Wpf.Ui.Controls.Button
        {
            Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary,
            Style = (Style)FindResource("SettingsSearchResultButtonStyle"),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Tag = entry,
            ToolTip = entry.Description
        };
        button.Click += ProvisioningSearchResultButton_Click;
        System.Windows.Automation.AutomationProperties.SetName(button, $"{entry.Title}, {entry.Category}");
        System.Windows.Automation.AutomationProperties.SetHelpText(button, entry.Description);

        var content = new Grid();
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new Wpf.Ui.Controls.SymbolIcon
        {
            Symbol = Enum.Parse<Wpf.Ui.Controls.SymbolRegular>(entry.Symbol),
            FontSize = 20,
            VerticalAlignment = VerticalAlignment.Center
        };
        icon.SetResourceReference(Control.ForegroundProperty, "TextFillColorSecondaryBrush");
        content.Children.Add(icon);

        var textStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        textStack.Children.Add(new TextBlock { Text = entry.Title, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        var description = new TextBlock { Text = entry.Description, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) };
        description.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        textStack.Children.Add(description);
        Grid.SetColumn(textStack, 2);
        content.Children.Add(textStack);

        var category = new TextBlock
        {
            Text = entry.Category,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 180
        };
        category.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        Grid.SetColumn(category, 4);
        content.Children.Add(category);
        button.Content = content;
        return button;
    }

    private void ProvisioningSearchResultButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Wpf.Ui.Controls.Button { Tag: ProvisioningSearchEntry entry })
            NavigateToProvisioningSearchResult(entry);
    }

    private void NavigateToProvisioningSearchResult(ProvisioningSearchEntry entry)
    {
        switch (entry.Section)
        {
            case ProvisioningCategory.Personalization:
                PersonalizationNavCard_Click(this, new RoutedEventArgs());
                break;
            case ProvisioningCategory.System:
                AdvancedNavCard_Click(this, new RoutedEventArgs());
                break;
            case ProvisioningCategory.Json:
                JsonNavCard_Click(this, new RoutedEventArgs());
                break;
        }

        Dispatcher.BeginInvoke(new Action(() =>
        {
            entry.Target.BringIntoView();
            entry.Target.Focus();
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private static string NormalizeProvisioningSearchText(string value)
    {
        string decomposed = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (char character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
                builder.Append(character);
        }

        return builder.ToString().Normalize(NormalizationForm.FormC).ToLowerInvariant();
    }

    private void PersonalizationNavCard_Click(object sender, RoutedEventArgs e)
    {
        ShowSection(PersonalizationSectionPanel, "Personalização");
        UpdateDesktopPreview();
    }

    private void AdvancedNavCard_Click(object sender, RoutedEventArgs e)
    {
        ShowSection(AdvancedSectionPanel, "Configurações Avançadas");
    }

    private void JsonNavCard_Click(object sender, RoutedEventArgs e) => ShowSection(JsonSectionPanel, "Visualização do JSON");

    private void OrchestratorNavCard_Click(object sender, RoutedEventArgs e)
    {
        ShowSection(OrchestratorSectionPanel, "WinProvision Orchestrator", showApplyButton: false);
    }

    private void OrchestratorOptions_Changed(object sender, RoutedEventArgs e)
    {
        if (!_uiLoaded) return;

        UpdateOrchestratorLogPathVisibility();
        _publishedBootstrapCommand = null;
        BootstrapCommandTextBox.Text = "As opções mudaram. Publique novamente para atualizar o script.";
        CopyBootstrapCommandButton.IsEnabled = false;
        PublishBootstrapButton.IsEnabled = _githubBackupService.IsConnected && !_isPublishingBootstrap;
    }

    private void UpdateOrchestratorLogPathVisibility()
    {
        if (OrchestratorLocalLogPathPanel is not null)
            OrchestratorLocalLogPathPanel.Visibility = OrchestratorLogsLocalRadio.IsChecked == true
                ? Visibility.Visible
                : Visibility.Collapsed;

        if (OrchestratorLogPathTextBox is not null)
            OrchestratorLogPathTextBox.Text = _orchestratorLogFilePath ?? "Selecione o destino do arquivo TXT";
    }

    private void ChooseOrchestratorLogPathButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Escolher destino do log do Orchestrator",
            FileName = "WinProvision-Orchestrator.txt",
            DefaultExt = ".txt",
            AddExtension = true,
            Filter = "Arquivo de texto (*.txt)|*.txt|Todos os arquivos (*.*)|*.*",
            OverwritePrompt = false,
            CheckPathExists = true
        };

        if (!string.IsNullOrWhiteSpace(_orchestratorLogFilePath))
        {
            dialog.InitialDirectory = Path.GetDirectoryName(_orchestratorLogFilePath);
            dialog.FileName = Path.GetFileName(_orchestratorLogFilePath);
        }

        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
            return;

        _orchestratorLogFilePath = dialog.FileName;
        UpdateOrchestratorLogPathVisibility();
        OrchestratorOptions_Changed(sender, e);
    }

    private void PackageCollectionChanged()
    {
        if (_uiLoaded)
            RefreshProfileSummary();
    }

    private void ProfileImportValidationChanged()
    {
        if (_uiLoaded)
            Dispatcher.Invoke(() => RefreshProfileSummary());
    }

    private void BackToProfileButton_Click(object sender, RoutedEventArgs e) => ShowProfileOverview();

    /// <summary>
    /// Atualiza os metadados e a visualização JSON do perfil a partir do estado atual da UI.
    /// </summary>
    private void RefreshProfileSummary(ProvisioningManifest? manifest = null)
    {
        manifest ??= BuildManifestFromUi();

        // Nome/Informação OEM não são forçados aqui de volta pro TextBox — são os próprios TextBox
        // (ProfileNameTextBox/ProfileCreatorTextBox) que alimentam o manifesto, então
        // sobrescrever o texto a cada refresh atrapalharia o usuário digitando.
        ProfileCreatedAtText.Text = manifest.CreatedAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss");

        var friendlyChanges = BuildFriendlyChangesList();
        ChangesDetectedText.Text = friendlyChanges.Count == 1
            ? "1 alteração detectada"
            : $"Alterações detectadas: {friendlyChanges.Count}";

        string profileJson = BuildProfileJson(manifest);
        if (!string.Equals(JsonPreviewTextBox.Text, profileJson, StringComparison.Ordinal))
            JsonPreviewTextBox.Text = profileJson;
        _publishedBootstrapCommand = null;
        BootstrapCommandTextBox.Text = _githubBackupService.IsConnected
            ? "Publique o perfil para gerar o comando do PowerShell."
            : "Conecte o GitHub em Conta e Sincronização para publicar o Bootstrap.";
        CopyBootstrapCommandButton.IsEnabled = false;
        PublishBootstrapButton.IsEnabled = _githubBackupService.IsConnected && !_isPublishingBootstrap;
    }

    /// <summary>
    /// Monta o perfil completo em tempo real: todos os aplicativos adicionados às abas
    /// de pacotes, planos do Office e a configuração de provisionamento atual. O JSON
    /// segue o mesmo critério da exportação do perfil completo.
    /// </summary>
    private string BuildProfileJson(ProvisioningManifest manifest)
    {
        var packageApps = _packageCollectionService.Tabs
            .SelectMany(tab => tab.Items)
            .GroupBy(app => app.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
        var profile = _profileService.BuildFromSelection(
            packageApps,
            manifest.Name,
            manifest);
        return JsonSerializer.Serialize(profile, WinProvisionJsonOptions.Profile);
    }

    private async void PublishBootstrapButton_Click(object sender, RoutedEventArgs e)
    {
        CommitPendingProfileEdit();

        if (!_githubBackupService.IsConnected)
        {
            StatusText.Text = "Conecte o GitHub em Conta e Sincronização para publicar o Bootstrap.";
            return;
        }

        string profileJson = JsonPreviewTextBox.Text;
        BootstrapDisplayMode displayMode = OrchestratorDisplayTerminalRadio.IsChecked == true
            ? BootstrapDisplayMode.Terminal
            : BootstrapDisplayMode.UserInterface;
        BootstrapLogMode logMode = OrchestratorLogsCloudRadio.IsChecked == true
            ? BootstrapLogMode.Cloud
            : BootstrapLogMode.Local;
        string? logFilePath = _orchestratorLogFilePath;
        if (logMode == BootstrapLogMode.Local && string.IsNullOrWhiteSpace(logFilePath))
        {
            StatusText.Text = "Escolha onde salvar o arquivo TXT antes de publicar.";
            BootstrapCommandTextBox.Text = "Selecione o destino do log local e publique novamente.";
            return;
        }
        _isPublishingBootstrap = true;
        PublishBootstrapButton.IsEnabled = false;
        CopyBootstrapCommandButton.IsEnabled = false;
        StatusText.Text = "Publicando o perfil e o script no Gist...";

        try
        {
            var result = await _githubBackupService.PublishBootstrapAsync(profileJson, displayMode, logMode, logFilePath);
            if (!result.Success || string.IsNullOrWhiteSpace(result.RawUrl))
            {
                StatusText.Text = result.ErrorMessage ?? "Não foi possível publicar o Bootstrap no Gist.";
                BootstrapCommandTextBox.Text = "A publicação falhou. Corrija o acesso ao GitHub e tente novamente.";
                return;
            }

            if (!string.Equals(JsonPreviewTextBox.Text, profileJson, StringComparison.Ordinal))
            {
                StatusText.Text = "O perfil mudou durante a publicação. Publique novamente para atualizar o Gist.";
                BootstrapCommandTextBox.Text = "Publique novamente para gerar um comando com o perfil atualizado.";
                return;
            }

            if (displayMode != (OrchestratorDisplayTerminalRadio.IsChecked == true
                    ? BootstrapDisplayMode.Terminal
                    : BootstrapDisplayMode.UserInterface)
                || logMode != (OrchestratorLogsCloudRadio.IsChecked == true
                    ? BootstrapLogMode.Cloud
                    : BootstrapLogMode.Local)
                || !string.Equals(logFilePath, _orchestratorLogFilePath, StringComparison.OrdinalIgnoreCase))
            {
                StatusText.Text = "As opções mudaram durante a publicação. Publique novamente para aplicar as escolhas atuais.";
                BootstrapCommandTextBox.Text = "Publique novamente para atualizar as opções do Orchestrator.";
                return;
            }

            _publishedBootstrapCommand = $"irm '{result.RawUrl.Replace("'", "''", StringComparison.Ordinal)}' | iex";
            BootstrapCommandTextBox.Text = _publishedBootstrapCommand;
            CopyBootstrapCommandButton.IsEnabled = true;
            StatusText.Text = $"Orchestrator publicado ({(displayMode == BootstrapDisplayMode.Terminal ? "Terminal" : "interface")}, logs {(logMode == BootstrapLogMode.Cloud ? "na nuvem" : "locais")}). Copie o comando para o FirstLogon do Schneegans.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Falha ao publicar o Bootstrap: {ex.Message}";
        }
        finally
        {
            _isPublishingBootstrap = false;
            PublishBootstrapButton.IsEnabled = _githubBackupService.IsConnected;
        }
    }

    private void CopyBootstrapCommandButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_publishedBootstrapCommand))
        {
            StatusText.Text = "Publique o perfil antes de copiar o comando.";
            return;
        }

        Clipboard.SetText(_publishedBootstrapCommand);
        StatusText.Text = "Comando do Bootstrap copiado para a área de transferência.";
    }

    private void CopyJsonButton_Click(object sender, RoutedEventArgs e)
    {
        CommitPendingProfileEdit();
        Clipboard.SetText(JsonPreviewTextBox.Text);
        StatusText.Text = "JSON copiado para a área de transferência.";
    }

    private void OpenFullEditorButton_Click(object sender, RoutedEventArgs e)
    {
        CommitPendingProfileEdit();
        var editor = new ProvisioningJsonEditorWindow(JsonPreviewTextBox.Text)
        {
            Owner = Window.GetWindow(this)
        };
        editor.ShowDialog();
    }

    /// <summary>
    /// Qualquer edição de campo (ComboBox, CheckBox, TextBox — ligados via XAML) chama isto,
    /// empurrando o estado atual da UI pro ProvisioningService SEM aplicar nada no Windows.
    /// Sem isso, editar aqui e clicar em "Sincronizar agora" nas Configurações (sem antes
    /// clicar em "Exportar" ou "Aplicar agora" nesta página) nunca via as mudanças — o
    /// provisionamento ficava "null" no backup mesmo com ajustes pendentes na tela.
    /// </summary>
    private void PushCurrentToService()
    {
        if (!_uiLoaded) return;

        var manifest = BuildManifestFromUi();

        // A visão de Perfil reflete a UI mesmo que o manifesto ainda esteja vazio (ex.: nada
        // preenchido ainda) — só o SetCurrent abaixo (que entra no backup/sincronização) é
        // condicionado a ter algo de fato preenchido.
        RefreshProfileSummary(manifest);

        // Um manifesto totalmente vazio (tudo "Não alterar", sem nome de máquina nem
        // wallpaper) não deve virar "estado atual" — senão a página marcaria
        // provisionamento como configurado só por ter sido aberta. TaskbarAutoHide
        // desmarcado (false) é o estado inicial do CheckBox, então não conta como
        // "alterado" sozinho — só marcado (true) conta.
        bool isEmpty = string.IsNullOrWhiteSpace(manifest.Name)
            && string.IsNullOrWhiteSpace(manifest.Creator)
            && manifest.Theme is null or SystemThemeMode.NaoDefinido
            && manifest.SystemTheme is null or SystemThemeMode.NaoDefinido
            && manifest.AppsTheme is null or SystemThemeMode.NaoDefinido
            && manifest.AccentColorMode is null or AccentColorMode.NaoDefinido
            && manifest.TaskbarAlignment is null or TaskbarAlignmentMode.NaoDefinido
            && manifest.TaskbarSearchBox is null or TaskbarSearchBoxMode.NaoDefinido
            && manifest.TaskbarAutoHide is not true
            && manifest.PowerPlan is null or PowerPlanMode.NaoDefinido
            && manifest.EnableAutomaticTime is not true
            && manifest.EnableAutomaticTimeZone is not true
            && manifest.ShowFileExtensions is null
            && manifest.ShowHiddenFiles is null
            && manifest.OpenExplorerToThisPc is null
            && manifest.DisplayTimeoutOnAc is null
            && manifest.DisplayTimeoutOnDc is null
            && manifest.StandbyTimeoutOnAc is null
            && manifest.StandbyTimeoutOnDc is null
            && string.IsNullOrWhiteSpace(manifest.MachineName)
            && string.IsNullOrWhiteSpace(manifest.WallpaperImageBase64);

        if (isEmpty) return;

        _provisioningService.SetCurrent(manifest);
    }

    private void Field_Changed(object sender, RoutedEventArgs e)
    {
        if (!_uiLoaded) return;
        ProvisioningInfoBar.IsOpen = false;

        // O perfil completo pode conter uma coleção grande de aplicativos. Serializá-lo e
        // recriar todo o editor JSON em cada tecla bloqueava o thread visual durante a
        // digitação. A alteração continua no controle imediatamente; apenas a sincronização
        // do estado e a prévia são agrupadas ao fim de uma pausa curta.
        if (sender is TextBox)
        {
            _profileEditDebounceTimer.Stop();
            _profileEditDebounceTimer.Start();
            return;
        }

        _profileEditDebounceTimer.Stop();
        PushCurrentToService();
        UpdateDesktopPreview();
    }

    private PowerPlanMode GetSelectedPowerPlan()
    {
        if (PowerPlanOptionsPanel.Children.OfType<RadioButton>().FirstOrDefault(option => option.IsChecked == true)?.Tag is string tag
            && Enum.TryParse(tag, out PowerPlanMode mode))
        {
            return mode;
        }

        return PowerPlanMode.NaoDefinido;
    }

    private void SelectPowerPlan(PowerPlanMode? value)
    {
        string tag = (value ?? PowerPlanMode.NaoDefinido).ToString();
        RadioButton? option = PowerPlanOptionsPanel.Children.OfType<RadioButton>()
            .FirstOrDefault(candidate => candidate.Tag as string == tag);
        if (option is not null)
            option.IsChecked = true;
    }

    private void AccentColorModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateAccentColorInputVisibility();
        Field_Changed(sender, e);
    }

    private void AccentColorHexTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateAccentColorPreview();
        Field_Changed(sender, e);
    }

    private void UpdateAccentColorInputVisibility()
    {
        if (AccentColorInputPanel is null || AccentColorModeComboBox is null)
            return;

        AccentColorInputPanel.Visibility = GetSelectedEnum<AccentColorMode>(AccentColorModeComboBox)
            == AccentColorMode.Personalizado
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    private void UpdateAccentColorPreview()
    {
        if (AccentColorPreview is null || AccentColorHexTextBox is null)
            return;

        try
        {
            if (ColorConverter.ConvertFromString(AccentColorHexTextBox.Text) is Color color)
            {
                AccentColorPreview.Background = new SolidColorBrush(color);
                AccentColorHexTextBox.ToolTip = null;
                return;
            }
        }
        catch (FormatException)
        {
            // A validação final acontece ao aplicar; enquanto digita, só preservamos a amostra anterior.
        }

        AccentColorHexTextBox.ToolTip = "Use o formato hexadecimal #RRGGBB.";
    }

    private void ProfileEditDebounceTimer_Tick(object? sender, EventArgs e)
    {
        _profileEditDebounceTimer.Stop();
        PushCurrentToService();
    }

    /// <summary>
    /// Confirma imediatamente uma edição pendente antes de uma ação que consome o JSON.
    /// Assim, copiar, abrir o editor ou publicar nunca usam uma prévia anterior.
    /// </summary>
    private void CommitPendingProfileEdit()
    {
        if (!_profileEditDebounceTimer.IsEnabled)
            return;

        _profileEditDebounceTimer.Stop();
        PushCurrentToService();
    }

    /// <summary>
    /// Monta a lista simples e legível (um item por linha, em português) do que já foi
    /// alterado na tela — lida direto dos controles (mesmo texto amigável que o usuário já
    /// vê nos ComboBox/CheckBox), pra quem for exportar ou aplicar o perfil enxergar de cara
    /// o que está sendo levado, sem precisar interpretar o JSON bruto.
    /// </summary>
    private List<string> BuildFriendlyChangesList()
    {
        var changes = new List<string>();

        if (!string.IsNullOrWhiteSpace(ProfileNameTextBox.Text))
            changes.Add($"Informação OEM: {ProfileNameTextBox.Text.Trim()}");

        if (!string.IsNullOrWhiteSpace(ProfileCreatorTextBox.Text))
            changes.Add($"Nome do Perfil: {ProfileCreatorTextBox.Text.Trim()}");

        if (GetSelectedContent(ThemeComboBox, "NaoDefinido") is { } theme)
            changes.Add($"Tema do Windows: {theme}");

        if (GetSelectedContent(AppsThemeComboBox, "NaoDefinido") is { } appsTheme)
            changes.Add($"Tema dos aplicativos: {appsTheme}");

        if (GetSelectedEnum<AccentColorMode>(AccentColorModeComboBox) is { } accentMode
            && accentMode != AccentColorMode.NaoDefinido)
        {
            changes.Add(accentMode == AccentColorMode.Automatico
                ? "Cor de destaque: automática pelo papel de parede"
                : $"Cor de destaque: {AccentColorHexTextBox.Text.Trim()}");
        }

        if (GetSelectedContent(TaskbarAlignmentComboBox, "NaoDefinido") is { } alignment)
            changes.Add($"Alinhamento da barra de tarefas: {alignment}");

        if (GetSelectedContent(TaskbarSearchBoxComboBox, "NaoDefinido") is { } searchBox)
            changes.Add($"Caixa de pesquisa: {searchBox}");

        if (TaskbarAutoHideCheckBox.IsChecked is true)
            changes.Add("Barra de tarefas: ocultar automaticamente");

        if (_wallpaperFileName is { } wallpaperName)
            changes.Add($"Papel de parede: {wallpaperName}");

        PowerPlanMode powerPlan = GetSelectedPowerPlan();
        if (powerPlan != PowerPlanMode.NaoDefinido)
            changes.Add($"Plano de energia: {PowerPlanDisplayName(powerPlan)}");

        if (GetSelectedMinutes(DisplayTimeoutAcComboBox) is { } displayAc)
            changes.Add($"Tela desliga: {FriendlyMinutesLabel(displayAc)}");
        if (GetSelectedMinutes(StandbyTimeoutAcComboBox) is { } standbyAc)
            changes.Add($"PC suspende: {FriendlyMinutesLabel(standbyAc)}");

        if (!string.IsNullOrWhiteSpace(MachineNameTextBox.Text))
            changes.Add($"Nome do PC: {MachineNameTextBox.Text.Trim()}");

        if (AutomaticTimeCheckBox.IsChecked is true)
            changes.Add("Data e hora: sincronizar automaticamente pela fonte configurada no Windows");
        if (AutomaticTimeZoneCheckBox.IsChecked is true)
            changes.Add("Fuso horário: ajustar automaticamente conforme a localização");
        if (ShowFileExtensionsCheckBox.IsChecked is bool showExtensions)
            changes.Add($"Extensões de arquivos: {(showExtensions ? "mostrar" : "ocultar")}");
        if (ShowHiddenFilesCheckBox.IsChecked is bool showHidden)
            changes.Add($"Arquivos ocultos: {(showHidden ? "mostrar" : "ocultar")}");
        if (OpenExplorerToThisPcCheckBox.IsChecked is bool openToThisPc)
            changes.Add($"Página inicial do Explorador: {(openToThisPc ? "Este Computador" : "Acesso rápido")}");

        return changes;
    }

    /// <summary>
    /// Lê o texto amigável (Content) do item selecionado num ComboBox — retorna null quando
    /// nada foi de fato escolhido (Tag igual a <paramref name="unsetTag"/>, o valor de "Não
    /// alterar" de cada combo), pra ficar de fora da lista de alterações.
    /// </summary>
    private static string? GetSelectedContent(ComboBox? comboBox, string unsetTag)
    {
        if (comboBox?.SelectedItem is not ComboBoxItem item) return null;
        string? tag = item.Tag as string;
        if (tag == unsetTag) return null;
        return item.Content as string;
    }

    /// <summary>Lê o valor do enum selecionado num ComboBox montado com ComboBoxItem.Tag = nome do enum.</summary>
    private static TEnum GetSelectedEnum<TEnum>(ComboBox? comboBox) where TEnum : struct, Enum
    {
        if (comboBox?.SelectedItem is not ComboBoxItem item) return default;
        string? tag = item.Tag as string;
        return tag is not null && Enum.TryParse<TEnum>(tag, out var value) ? value : default;
    }

    /// <summary>Seleciona, num ComboBox montado com ComboBoxItem.Tag = nome do enum, o item cujo Tag bate com o valor.</summary>
    private static void SelectEnum<TEnum>(ComboBox? comboBox, TEnum? value) where TEnum : struct, Enum
    {
        if (comboBox is null) return;
        string tag = (value ?? default).ToString();
        var match = comboBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == tag);
        comboBox.SelectedItem = match ?? comboBox.Items.OfType<ComboBoxItem>().FirstOrDefault();
    }

    /// <summary>Lê um número inteiro (minutos) de um ComboBox montado com ComboBoxItem.Tag = string de int.
    /// Tag "-1" ou parse impossível → null (não alterar). Tag "0" ou positivo → valor em minutos.</summary>
    private static int? GetSelectedMinutes(ComboBox? comboBox)
    {
        if (comboBox?.SelectedItem is not ComboBoxItem item) return null;
        string? tag = item.Tag as string;
        if (!int.TryParse(tag, out int value)) return null;
        if (value < 0) return null;
        return Math.Clamp(value, 0, 1440);
    }

    /// <summary>Seleciona, num ComboBox de timeout (minutos), o item cujo Tag bate com o valor.
    /// null → "Não alterar" (Tag=-1); 0 → "Nunca"; senão o valor correspondente (ou mais próximo).</summary>
    private static void SelectMinutes(ComboBox? comboBox, int? value)
    {
        if (comboBox is null) return;
        string tag = value is null ? "-1" : value.Value.ToString();
        var match = comboBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == tag);
        match ??= comboBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == "-1");
        comboBox.SelectedItem = match ?? comboBox.Items.OfType<ComboBoxItem>().FirstOrDefault();
    }

    private static string FriendlyMinutesLabel(int minutes)
    {
        return minutes switch
        {
            0 => "Nunca",
            60 => "1 hora",
            90 => "1 hora e meia",
            120 => "2 horas",
            180 => "3 horas",
            < 60 => $"{minutes} minuto{(minutes == 1 ? string.Empty : "s")}",
            _ => $"{minutes / 60} hora{(minutes / 60 == 1 ? string.Empty : "s")} e {minutes % 60} min"
        };
    }

    private static string PowerPlanDisplayName(PowerPlanMode powerPlan) => powerPlan switch
    {
        PowerPlanMode.Economia => "Economia de energia",
        PowerPlanMode.Equilibrado => "Equilibrado",
        PowerPlanMode.AltoDesempenho => "Alto desempenho",
        _ => "Não alterar",
    };

    private ProvisioningManifest BuildManifestFromUi(string? name = null) => new()
    {
        Name = name ?? (string.IsNullOrWhiteSpace(ProfileNameTextBox.Text) ? null : ProfileNameTextBox.Text.Trim()),
        Creator = string.IsNullOrWhiteSpace(ProfileCreatorTextBox.Text) ? null : ProfileCreatorTextBox.Text.Trim(),
        SystemTheme = GetSelectedEnum<SystemThemeMode>(ThemeComboBox),
        AppsTheme = GetSelectedEnum<SystemThemeMode>(AppsThemeComboBox),
        AccentColorMode = GetSelectedEnum<AccentColorMode>(AccentColorModeComboBox),
        AccentColor = GetSelectedEnum<AccentColorMode>(AccentColorModeComboBox) == AccentColorMode.Personalizado
            ? AccentColorHexTextBox.Text.Trim()
            : null,
        TaskbarAlignment = GetSelectedEnum<TaskbarAlignmentMode>(TaskbarAlignmentComboBox),
        TaskbarSearchBox = GetSelectedEnum<TaskbarSearchBoxMode>(TaskbarSearchBoxComboBox),
        TaskbarAutoHide = TaskbarAutoHideCheckBox.IsChecked,
        PowerPlan = GetSelectedPowerPlan(),
        EnableAutomaticTime = AutomaticTimeCheckBox.IsChecked == true ? true : null,
        EnableAutomaticTimeZone = AutomaticTimeZoneCheckBox.IsChecked == true ? true : null,
        ShowFileExtensions = ShowFileExtensionsCheckBox.IsChecked,
        ShowHiddenFiles = ShowHiddenFilesCheckBox.IsChecked,
        OpenExplorerToThisPc = OpenExplorerToThisPcCheckBox.IsChecked,
        DisplayTimeoutOnAc = GetSelectedMinutes(DisplayTimeoutAcComboBox),
        DisplayTimeoutOnDc = GetSelectedMinutes(DisplayTimeoutAcComboBox),
        StandbyTimeoutOnAc = GetSelectedMinutes(StandbyTimeoutAcComboBox),
        StandbyTimeoutOnDc = GetSelectedMinutes(StandbyTimeoutAcComboBox),
        MachineName = string.IsNullOrWhiteSpace(MachineNameTextBox.Text) ? null : MachineNameTextBox.Text.Trim(),
        WallpaperFileName = _wallpaperFileName,
        WallpaperImageBase64 = _wallpaperImageBase64,
    };

    private void LoadManifestIntoUi(ProvisioningManifest manifest)
    {
        bool wasUiLoaded = _uiLoaded;
        _uiLoaded = false;
        try
        {
        ProfileNameTextBox.Text = manifest.Name ?? string.Empty;
        ProfileCreatorTextBox.Text = manifest.Creator ?? string.Empty;
        SelectEnum(ThemeComboBox, manifest.SystemTheme ?? manifest.Theme);
        SelectEnum(AppsThemeComboBox, manifest.AppsTheme ?? manifest.Theme);
        SelectEnum(AccentColorModeComboBox, manifest.AccentColorMode);
        AccentColorHexTextBox.Text = manifest.AccentColor ?? "#0078D4";
        UpdateAccentColorInputVisibility();
        UpdateAccentColorPreview();
        SelectEnum(TaskbarAlignmentComboBox, manifest.TaskbarAlignment);
        SelectEnum(TaskbarSearchBoxComboBox, manifest.TaskbarSearchBox);
        TaskbarAutoHideCheckBox.IsChecked = manifest.TaskbarAutoHide;
        SelectPowerPlan(manifest.PowerPlan);
        AutomaticTimeCheckBox.IsChecked = manifest.EnableAutomaticTime == true;
        AutomaticTimeZoneCheckBox.IsChecked = manifest.EnableAutomaticTimeZone == true;
        ShowFileExtensionsCheckBox.IsChecked = manifest.ShowFileExtensions;
        ShowHiddenFilesCheckBox.IsChecked = manifest.ShowHiddenFiles;
        OpenExplorerToThisPcCheckBox.IsChecked = manifest.OpenExplorerToThisPc;
        SelectMinutes(DisplayTimeoutAcComboBox, manifest.DisplayTimeoutOnAc ?? manifest.DisplayTimeoutOnDc);
        SelectMinutes(StandbyTimeoutAcComboBox, manifest.StandbyTimeoutOnAc ?? manifest.StandbyTimeoutOnDc);
        MachineNameTextBox.Text = manifest.MachineName ?? string.Empty;

        _wallpaperFileName = manifest.WallpaperFileName;
        _wallpaperImageBase64 = manifest.WallpaperImageBase64;

        if (_wallpaperImageBase64 is { } base64)
        {
            try
            {
                ShowWallpaperPreview(Convert.FromBase64String(base64), _wallpaperFileName);
            }
            catch (FormatException)
            {
                WallpaperPreviewImage.Source = null;
                WallpaperFileNameText.Text = "Wallpaper incluído no perfil, mas o Base64 está corrompido.";
                ClearWallpaperButton.Visibility = Visibility.Collapsed;
            }
        }
        else
        {
            WallpaperPreviewImage.Source = null;
            WallpaperFileNameText.Text = "Nenhuma imagem selecionada.";
            ClearWallpaperButton.Visibility = Visibility.Collapsed;
        }

        UpdateDesktopPreview();
        }
        finally
        {
            _uiLoaded = wasUiLoaded;
        }
    }

    /// <summary>Monta um BitmapImage a partir dos bytes em memória — sem isso, o preview exigiria salvar um arquivo temporário só pra exibir.</summary>
    private void ShowWallpaperPreview(byte[] imageBytes, string? fileName)
    {
        var bitmap = new BitmapImage();
        using (var stream = new MemoryStream(imageBytes))
        {
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
        }
        bitmap.Freeze();

        WallpaperPreviewImage.Source = bitmap;
        WallpaperFileNameText.Text = fileName is null ? "Imagem carregada." : $"Selecionado: {fileName}";
        ClearWallpaperButton.Visibility = Visibility.Visible;
    }

    private void ClearWallpaperButton_Click(object sender, RoutedEventArgs e)
    {
        _wallpaperFileName = null;
        _wallpaperImageBase64 = null;
        WallpaperPreviewImage.Source = null;
        WallpaperFileNameText.Text = "Nenhuma imagem selecionada.";
        ClearWallpaperButton.Visibility = Visibility.Collapsed;
        PushCurrentToService();
        UpdateDesktopPreview();
        StatusText.Text = "Papel de parede restaurado para o padrão do tema.";
    }

    private async void SelectWallpaperButton_Click(object sender, RoutedEventArgs e)
    {
        var openFileDialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Imagens (*.jpg;*.jpeg;*.png)|*.jpg;*.jpeg;*.png",
            Title = "Selecione o papel de parede"
        };

        if (openFileDialog.ShowDialog() != true) return;

        try
        {
            byte[] imageBytes = await File.ReadAllBytesAsync(openFileDialog.FileName);

            _wallpaperFileName = Path.GetFileName(openFileDialog.FileName);
            _wallpaperImageBase64 = Convert.ToBase64String(imageBytes);

            ShowWallpaperPreview(imageBytes, _wallpaperFileName);
            PushCurrentToService();
            UpdateDesktopPreview();
            StatusText.Text = $"Imagem \"{_wallpaperFileName}\" pronta — será incluída ao exportar, sincronizar ou aplicar o perfil.";
        }
        catch
        {
            StatusText.Text = "Não foi possível carregar a imagem. Tente outro arquivo.";
        }
    }

    // ── Simulação Interativa da Área de Trabalho (Sandbox/VM Preview) ───────────────────
    private static BitmapImage? _cachedLightWallpaper;
    private static BitmapImage? _cachedDarkWallpaper;
    private static bool _wallpapersInitialized;

    private static void EnsureDefaultWallpapersLoaded()
    {
        if (_wallpapersInitialized) return;
        _wallpapersInitialized = true;

        try
        {
            const string lightPath = @"C:\Windows\Web\Wallpaper\Windows\img0.jpg";
            if (File.Exists(lightPath))
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(lightPath, UriKind.Absolute);
                bmp.EndInit();
                bmp.Freeze();
                _cachedLightWallpaper = bmp;
            }
        }
        catch { }

        try
        {
            const string darkPath = @"C:\Windows\Web\Wallpaper\Windows\img19.jpg";
            if (File.Exists(darkPath))
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(darkPath, UriKind.Absolute);
                bmp.EndInit();
                bmp.Freeze();
                _cachedDarkWallpaper = bmp;
            }
        }
        catch { }
    }

    /// <summary>
    /// Atualiza em tempo real a simulação gráfica da Área de Trabalho do Windows (Sandbox/VM Preview)
    /// com base no wallpaper customizado, tema claro/escuro, alinhamento e botões da barra de tarefas.
    /// </summary>
    private void UpdateDesktopPreview()
    {
        if (!_uiLoaded || DesktopPreviewWallpaper is null || ThemeComboBox is null || TaskbarAlignmentComboBox is null || TaskbarSearchBoxComboBox is null || TaskbarAutoHideCheckBox is null)
            return;

        EnsureDefaultWallpapersLoaded();

        var selectedTheme = GetSelectedEnum<SystemThemeMode>(ThemeComboBox);
        var selectedAlign = GetSelectedEnum<TaskbarAlignmentMode>(TaskbarAlignmentComboBox);
        var selectedSearch = GetSelectedEnum<TaskbarSearchBoxMode>(TaskbarSearchBoxComboBox);
        bool autoHide = TaskbarAutoHideCheckBox.IsChecked is true;

        // 1. Wallpaper
        if (WallpaperPreviewImage?.Source is BitmapImage customBmp && !string.IsNullOrWhiteSpace(_wallpaperImageBase64))
        {
            DesktopPreviewWallpaper.Source = customBmp;
            if (ClearWallpaperButton is not null) ClearWallpaperButton.Visibility = Visibility.Visible;
        }
        else
        {
            if (ClearWallpaperButton is not null) ClearWallpaperButton.Visibility = Visibility.Collapsed;
            if (selectedTheme == SystemThemeMode.Claro)
            {
                DesktopPreviewWallpaper.Source = _cachedLightWallpaper ?? _cachedDarkWallpaper;
            }
            else
            {
                // Escuro ou Não alterar (padrão escuro moderno do Windows 11)
                DesktopPreviewWallpaper.Source = _cachedDarkWallpaper ?? _cachedLightWallpaper;
            }
        }

        // 2. Cores da Barra de Tarefas (Tema Claro vs. Tema Escuro)
        bool isLight = selectedTheme == SystemThemeMode.Claro;
        if (isLight)
        {
            DesktopPreviewTaskbarTint.Background = new SolidColorBrush(Color.FromArgb(0xD9, 0xF3, 0xF3, 0xF3));
            var darkTextBrush = new SolidColorBrush(Color.FromRgb(0x1E, 0x29, 0x3B));
            var darkSecBrush = new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B));

            DesktopPreviewClockText.Foreground = darkTextBrush;
            DesktopPreviewDateText.Foreground = darkSecBrush;
            DesktopPreviewSearchIconOnly.Stroke = darkTextBrush;
            DesktopPreviewTrayChevron.Stroke = darkSecBrush;
            DesktopPreviewTrayWifi.Stroke = darkSecBrush;
            DesktopPreviewTrayWifiDot.Fill = darkSecBrush;
            DesktopPreviewTraySpeaker.Fill = darkSecBrush;
            DesktopPreviewTraySpeakerWaves.Stroke = darkSecBrush;
            DesktopPreviewTrayBattery.BorderBrush = darkSecBrush;
            DesktopPreviewTrayBatteryLevel.Background = darkSecBrush;
            DesktopPreviewTrayBatteryTerminal.Background = darkSecBrush;
        }
        else
        {
            DesktopPreviewTaskbarTint.Background = new SolidColorBrush(Color.FromArgb(0xBF, 0x20, 0x20, 0x20));
            var lightTextBrush = new SolidColorBrush(Color.FromRgb(0xF8, 0xFA, 0xFC));
            var lightSecBrush = new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8));

            DesktopPreviewClockText.Foreground = lightTextBrush;
            DesktopPreviewDateText.Foreground = lightSecBrush;
            DesktopPreviewSearchIconOnly.Stroke = lightTextBrush;
            DesktopPreviewTrayChevron.Stroke = lightSecBrush;
            DesktopPreviewTrayWifi.Stroke = lightSecBrush;
            DesktopPreviewTrayWifiDot.Fill = lightSecBrush;
            DesktopPreviewTraySpeaker.Fill = lightSecBrush;
            DesktopPreviewTraySpeakerWaves.Stroke = lightSecBrush;
            DesktopPreviewTrayBattery.BorderBrush = lightSecBrush;
            DesktopPreviewTrayBatteryLevel.Background = lightSecBrush;
            DesktopPreviewTrayBatteryTerminal.Background = lightSecBrush;
        }

        // 3. Alinhamento da Barra de Tarefas
        if (selectedAlign == TaskbarAlignmentMode.Esquerda)
        {
            DesktopPreviewTaskbarIcons.HorizontalAlignment = HorizontalAlignment.Left;
            DesktopPreviewTaskbarIcons.Margin = new Thickness(46, 0, 0, 0);
        }
        else
        {
            // Centro ou Não alterar (padrão centralizado do Windows 11)
            DesktopPreviewTaskbarIcons.HorizontalAlignment = HorizontalAlignment.Center;
            DesktopPreviewTaskbarIcons.Margin = new Thickness(0);
        }

        // 4. Caixa de Pesquisa
        switch (selectedSearch)
        {
            case TaskbarSearchBoxMode.Oculta:
                DesktopPreviewSearchBoxIcon.Visibility = Visibility.Collapsed;
                break;
            case TaskbarSearchBoxMode.ApenasIcone:
                DesktopPreviewSearchBoxIcon.Visibility = Visibility.Visible;
                break;
            case TaskbarSearchBoxMode.CaixaCompleta:
            default:
                DesktopPreviewSearchBoxIcon.Visibility = Visibility.Visible;
                break;
        }

        // 5. Ocultar automaticamente a barra de tarefas
        if (autoHide)
        {
            DesktopPreviewTaskbar.Visibility = Visibility.Collapsed;
            DesktopPreviewAutoHideIndicator.Visibility = Visibility.Visible;
        }
        else
        {
            DesktopPreviewTaskbar.Visibility = Visibility.Visible;
            DesktopPreviewAutoHideIndicator.Visibility = Visibility.Collapsed;
        }

        // 6. Relógio e Data atuais
        DesktopPreviewClockText.Text = DateTime.Now.ToString("HH:mm");
        DesktopPreviewDateText.Text = DateTime.Now.ToString("dd/MM/yyyy");

    }

    private async void ApplyButton_Click(object sender, RoutedEventArgs e)
    {
        ProvisioningInfoBar.IsOpen = false;
        var manifest = BuildManifestFromUi();
        string[] changes = BuildApplySummary(manifest);
        if (changes.Length == 0)
        {
            StatusText.Text = "Escolha ao menos uma opção para aplicar.";
            return;
        }

        var confirmation = await StoreConfirmationDialog.ShowAsync(
            "Confirmar provisionamento",
            "O WinProvision aplicará estas alterações:" + Environment.NewLine + Environment.NewLine
                + string.Join(Environment.NewLine, changes.Select(change => "• " + change)),
            "Aplicar alterações",
            "Revisar");
        if (confirmation != ContentDialogResult.Primary)
            return;

        ApplyButton.IsEnabled = false;
        StatusText.Text = "Aplicando ajustes...";
        StatusText.ToolTip = null;

        try
        {
            var result = await _provisioningService.ApplyAsync(manifest);

            if (result.Steps.Count == 0)
            {
                StatusText.Text = "Escolha ao menos uma opção para aplicar.";
                return;
            }

            var failedSteps = result.Steps.Where(step => !step.Success).ToList();
            if (failedSteps.Count == 0)
            {
                StatusText.Text = result.RestartRequired
                    ? "Tudo certo! As configurações foram aplicadas. Reinicie o Windows para concluir."
                    : "Tudo certo! As configurações foram aplicadas.";
            ShowProvisioningInfoBar("Provisionamento concluído", StatusText.Text, InfoBarSeverity.Success);
            }
            else
            {
                StatusText.Text = failedSteps.Count == 1
                    ? $"A configuração \"{failedSteps[0].Setting}\" não pôde ser aplicada."
                    : $"{failedSteps.Count} configurações não puderam ser aplicadas.";
                StatusText.ToolTip = string.Join(Environment.NewLine,
                    failedSteps.Select(step => $"{step.Setting}: {step.Message}"));
            ShowProvisioningInfoBar("Provisionamento parcial", StatusText.Text, InfoBarSeverity.Warning);
                await _contentDialogService.ShowSimpleDialogAsync(new SimpleContentDialogCreateOptions
                {
                    Title = "Provisionamento concluído com falhas",
                    Content = "As configurações abaixo precisam de atenção:" + Environment.NewLine + Environment.NewLine
                        + string.Join(Environment.NewLine, failedSteps.Select(step => $"• {step.Setting}: {step.Message}")),
                    CloseButtonText = "Fechar"
                });
            }

            if (manifest.MachineName is not null)
            {
                CurrentMachineNameText.Text = $"Nome atual: {Environment.MachineName} (nome pendente: {manifest.MachineName})";
            }
        }
        catch
        {
            StatusText.Text = "Não foi possível aplicar as configurações. Tente novamente.";
            StatusText.ToolTip = "Consulte os logs do aplicativo para ver os detalhes da falha.";
            ShowProvisioningInfoBar("Falha no provisionamento", StatusText.Text, InfoBarSeverity.Error);
            await _contentDialogService.ShowSimpleDialogAsync(new SimpleContentDialogCreateOptions
            {
                Title = "Falha no provisionamento",
                Content = "Não foi possível concluir a aplicação. Consulte os logs do aplicativo para ver os detalhes da falha.",
                CloseButtonText = "Fechar"
            });
        }
        finally
        {
            ApplyButton.IsEnabled = true;
        }
    }

    private string[] BuildApplySummary(ProvisioningManifest manifest)
    {
        var changes = new List<string>();
        SystemThemeMode? systemTheme = manifest.SystemTheme is { } configuredSystemTheme
            && configuredSystemTheme != SystemThemeMode.NaoDefinido
                ? configuredSystemTheme
                : manifest.Theme;
        SystemThemeMode? appsTheme = manifest.AppsTheme is { } configuredAppsTheme
            && configuredAppsTheme != SystemThemeMode.NaoDefinido
                ? configuredAppsTheme
                : manifest.Theme;
        if (systemTheme is { } system && system != SystemThemeMode.NaoDefinido) changes.Add($"Tema do Windows: {system}");
        if (appsTheme is { } apps && apps != SystemThemeMode.NaoDefinido) changes.Add($"Tema dos aplicativos: {apps}");
        if (manifest.AccentColorMode is { } accentMode && accentMode != AccentColorMode.NaoDefinido)
            changes.Add(accentMode == AccentColorMode.Automatico
                ? "Cor de destaque: Automática (definida pelo papel de parede)"
                : $"Cor de destaque: {manifest.AccentColor ?? "#0078D4"}");
        if (manifest.TaskbarAlignment is { } alignment && alignment != TaskbarAlignmentMode.NaoDefinido) changes.Add($"Alinhamento da barra de tarefas: {alignment}");
        if (manifest.TaskbarSearchBox is { } search && search != TaskbarSearchBoxMode.NaoDefinido) changes.Add($"Pesquisa da barra de tarefas: {search}");
        if (manifest.TaskbarAutoHide is bool autoHide) changes.Add($"Ocultação automática da barra: {(autoHide ? "Ativada" : "Desativada")}");
        if (manifest.PowerPlan is { } powerPlan && powerPlan != PowerPlanMode.NaoDefinido) changes.Add($"Plano de energia: {PowerPlanDisplayName(powerPlan)}");
        if (manifest.EnableAutomaticTime == true) changes.Add("Data e hora: sincronização automática pelo Windows");
        if (manifest.EnableAutomaticTimeZone == true) changes.Add("Fuso horário: automático conforme a localização");
        if (manifest.ShowFileExtensions is bool showExtensions) changes.Add($"Extensões de arquivos: {(showExtensions ? "Mostrar" : "Ocultar")}");
        if (manifest.ShowHiddenFiles is bool showHidden) changes.Add($"Arquivos ocultos: {(showHidden ? "Mostrar" : "Ocultar")}");
        if (manifest.OpenExplorerToThisPc is bool openToThisPc) changes.Add($"Página inicial do Explorador: {(openToThisPc ? "Este Computador" : "Acesso rápido")}");
        if (manifest.MachineName is not null) changes.Add($"Nome do computador: {manifest.MachineName} (exige reinicialização)");
        if (manifest.WallpaperImageBase64 is not null) changes.Add($"Papel de parede: {manifest.WallpaperFileName ?? "imagem selecionada"}");
        if (manifest.DisplayTimeoutOnAc is int displayAc) changes.Add($"Desligar tela na tomada: {FormatMinutes(displayAc)}");
        if (manifest.DisplayTimeoutOnDc is int displayDc) changes.Add($"Desligar tela na bateria: {FormatMinutes(displayDc)}");
        if (manifest.StandbyTimeoutOnAc is int standbyAc) changes.Add($"Suspender na tomada: {FormatMinutes(standbyAc)}");
        if (manifest.StandbyTimeoutOnDc is int standbyDc) changes.Add($"Suspender na bateria: {FormatMinutes(standbyDc)}");
        return changes.ToArray();
    }

    private static string FormatMinutes(int minutes) => minutes == 0 ? "Nunca" : $"{minutes} min";

    private void TempCleanupSelection_Changed(object sender, RoutedEventArgs e)
    {
        _lastTempCleanupEstimates = null;
        if (ProvisioningInfoBar is not null)
            ProvisioningInfoBar.IsOpen = false;
        if (CleanTempResultText is not null)
            CleanTempResultText.Visibility = Visibility.Collapsed;
    }

    private int MinimumTemporaryFileAgeDays
    {
        get
        {
            double value = TempFileMinimumAgeDaysNumberBox?.Value ?? TemporaryFileCleanupService.DefaultMinimumFileAgeDays;
            if (!double.IsFinite(value))
                value = TemporaryFileCleanupService.DefaultMinimumFileAgeDays;
            return (int)Math.Clamp(Math.Round(value), 1, 365);
        }
    }

    private void TempFileMinimumAgeDays_ValueChanged(object sender, NumberBoxValueChangedEventArgs e)
    {
        if (!_uiLoaded || TempFileMinimumAgeDaysNumberBox is null)
            return;

        _lastTempCleanupEstimates = null;
        CleanTempResultText.Visibility = Visibility.Collapsed;
        CleanTempAgeHintText.Text = $"Serão considerados arquivos com mais de {FormatTemporaryAgeDays(MinimumTemporaryFileAgeDays)}.";
        CleanTempAnalysisText.Text = "Selecione Analisar para atualizar a estimativa com este período.";
        ProvisioningInfoBar.IsOpen = false;
    }

    private void ShowProvisioningInfoBar(string title, string message, InfoBarSeverity severity)
    {
        ProvisioningInfoBar.Title = title;
        ProvisioningInfoBar.Message = message;
        ProvisioningInfoBar.Severity = severity;
        ProvisioningInfoBar.IsOpen = true;
    }

    private async void AnalyzeTempFilesButton_Click(object sender, RoutedEventArgs e)
    {
        ProvisioningInfoBar.IsOpen = false;
        AnalyzeTempFilesButton.IsEnabled = false;
        CleanTempAnalysisText.Text = "Analisando arquivos temporários…";
        CleanTempResultText.Visibility = Visibility.Collapsed;
        try
        {
            _lastTempCleanupEstimates = await _temporaryFileCleanupService.AnalyzeAsync(MinimumTemporaryFileAgeDays);
            CleanTempAnalysisText.Text = FormatTempCleanupEstimates(_lastTempCleanupEstimates);
            StatusText.Text = "Análise de arquivos temporários concluída.";
        }
        catch (OperationCanceledException)
        {
            CleanTempAnalysisText.Text = "Análise cancelada.";
        }
        catch (Exception ex)
        {
            CleanTempAnalysisText.Text = "Não foi possível analisar os diretórios temporários.";
            CleanTempAnalysisText.ToolTip = ex.Message;
            ShowProvisioningInfoBar("Falha na análise", "Não foi possível analisar os diretórios temporários. Consulte os detalhes ao lado da análise.", InfoBarSeverity.Error);
        }
        finally { AnalyzeTempFilesButton.IsEnabled = true; }
    }

    private async void CleanTempFilesButton_Click(object sender, RoutedEventArgs e)
    {
        ProvisioningInfoBar.IsOpen = false;
        var categories = new List<TemporaryFileCategory>();
        if (CleanUserTempCheckBox.IsChecked == true) categories.Add(TemporaryFileCategory.User);
        if (CleanSystemTempCheckBox.IsChecked == true) categories.Add(TemporaryFileCategory.System);
        if (categories.Count == 0)
        {
            StatusText.Text = "Selecione ao menos uma categoria para limpar.";
            return;
        }

        CleanTempFilesButton.IsEnabled = false;
        AnalyzeTempFilesButton.IsEnabled = false;
        try
        {
            int minimumAgeDays = MinimumTemporaryFileAgeDays;
            var confirmation = await StoreConfirmationDialog.ShowAsync(
                "Limpar arquivos temporários?",
                $"Serão analisadas as categorias selecionadas. Apenas arquivos com mais de {FormatTemporaryAgeDays(minimumAgeDays)} serão removidos; itens em uso serão ignorados."
                    + (categories.Contains(TemporaryFileCategory.System) ? Environment.NewLine + Environment.NewLine + "A pasta temporária do Windows requer permissão de administrador." : string.Empty),
                "Limpar arquivos",
                "Cancelar");
            if (confirmation != ContentDialogResult.Primary) return;

            CleanTempAnalysisText.Text = "Analisando arquivos temporários…";
            _lastTempCleanupEstimates = await _temporaryFileCleanupService.AnalyzeAsync(minimumAgeDays);
            var selectedEstimates = _lastTempCleanupEstimates.Where(item => categories.Contains(item.Category)).ToArray();
            long files = selectedEstimates.Sum(item => item.FileCount);
            long bytes = selectedEstimates.Sum(item => item.Bytes);
            CleanTempAnalysisText.Text = FormatTempCleanupEstimates(_lastTempCleanupEstimates);
            if (files == 0)
            {
                StatusText.Text = $"Nenhum arquivo com mais de {FormatTemporaryAgeDays(minimumAgeDays)} foi encontrado nas categorias selecionadas.";
                ShowProvisioningInfoBar("Nenhum arquivo encontrado", StatusText.Text, InfoBarSeverity.Informational);
                return;
            }

            CleanTempAnalysisText.Text = $"Encontrados {files:N0} arquivo(s), aproximadamente {FormatTempBytes(bytes)}. Iniciando a limpeza…";
            CleanTempFilesButton.Content = "Limpando…";
            var results = await _temporaryFileCleanupService.CleanAsync(categories, minimumAgeDays);
            long removed = results.Sum(item => item.DeletedCount);
            long recoveredBytes = results.Sum(item => item.DeletedBytes);
            long skipped = results.Sum(item => item.SkippedCount);
            string[] errors = results.Where(item => item.Error is not null).Select(item => item.Error!).ToArray();
            CleanTempResultText.Text = errors.Length > 0
                ? string.Join(Environment.NewLine, errors)
                : $"Removidos {removed:N0} arquivo(s), liberando aproximadamente {FormatTempBytes(recoveredBytes)}. {skipped:N0} item(ns) foram ignorados (em uso ou sem acesso).";
            CleanTempResultText.Visibility = Visibility.Visible;
            StatusText.Text = errors.Length > 0 ? "A limpeza foi concluída parcialmente." : "Limpeza de temporários concluída.";
            ShowProvisioningInfoBar(
                errors.Length > 0 ? "Limpeza parcial" : "Limpeza concluída",
                errors.Length > 0 ? "Alguns diretórios não puderam ser limpos. Consulte o resumo da operação." : CleanTempResultText.Text,
                errors.Length > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success);
            _lastTempCleanupEstimates = await _temporaryFileCleanupService.AnalyzeAsync(minimumAgeDays);
            CleanTempAnalysisText.Text = FormatTempCleanupEstimates(_lastTempCleanupEstimates);
        }
        catch (Exception ex)
        {
            CleanTempResultText.Text = "A limpeza não pôde ser concluída. " + ex.Message;
            CleanTempResultText.Visibility = Visibility.Visible;
            StatusText.Text = "Falha na limpeza de arquivos temporários.";
            ShowProvisioningInfoBar("Falha na limpeza", "Não foi possível concluir a limpeza de arquivos temporários.", InfoBarSeverity.Error);
        }
        finally
        {
            CleanTempFilesButton.Content = "Limpar selecionados";
            CleanTempFilesButton.IsEnabled = true;
            AnalyzeTempFilesButton.IsEnabled = true;
        }
    }

    private static string FormatTempCleanupEstimates(IEnumerable<TemporaryFileCleanupEstimate> estimates)
    {
        TemporaryFileCleanupEstimate[] items = estimates.ToArray();
        return string.Join(Environment.NewLine, items.Select(item =>
            $"{(item.Category == TemporaryFileCategory.User ? "Este usuário" : "Windows")}: {item.FileCount:N0} arquivo(s), {FormatTempBytes(item.Bytes)} estimados"
            + (item.SkippedCount > 0 ? $"; {item.SkippedCount:N0} item(ns) sem acesso" : string.Empty)));
    }

    private static string FormatTempBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = Math.Max(0, bytes);
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value:N1} {units[unit]}";
    }

    private static string FormatTemporaryAgeDays(int days) => days == 1 ? "1 dia" : $"{days} dias";


}
