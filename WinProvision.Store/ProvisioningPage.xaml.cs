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

namespace WinProvision.Store;

public partial class ProvisioningPage : Page
{
    private readonly ProvisioningService _provisioningService;
    private readonly ScheduledTempCleanerService _scheduledTempCleanerService;
    private readonly PackageCollectionService _packageCollectionService;
    private readonly ProfileService _profileService;
    private readonly GitHubBackupService _githubBackupService;
    // Guardados à parte (em vez de num controle de UI) porque o wallpaper é um arquivo, não um
    // valor editável — ficam aqui até o usuário exportar ou aplicar, e são preenchidos de volta
    // ao importar um perfil que já tenha wallpaper embutido.
    private string? _wallpaperFileName;
    private string? _wallpaperImageBase64;
    private string? _publishedBootstrapCommand;
    private string? _orchestratorLogFilePath;
    private bool _isPublishingBootstrap;

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
        InitializeProvisioningSearchEntries();
        Loaded += ProvisioningPage_Loaded;

        _provisioningService = App.Services.GetRequiredService<ProvisioningService>();
        _scheduledTempCleanerService = App.Services.GetRequiredService<ScheduledTempCleanerService>();
        _packageCollectionService = App.Services.GetRequiredService<PackageCollectionService>();
        _profileService = App.Services.GetRequiredService<ProfileService>();
        _githubBackupService = App.Services.GetRequiredService<GitHubBackupService>();
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

        _wheelAwareComboBoxes.Add(PowerPlanComboBox);
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
        string iconFile = ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Light
            ? "WinProvisionStore_Black.png"
            : "WinProvisionStore_White.png";
        OrchestratorIcon.Source = new BitmapImage(
            new Uri($"pack://application:,,,/Assets/{iconFile}", UriKind.Absolute));
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
        ApplyButton.Visibility = Visibility.Visible;
        RefreshProfileSummary();
    }

    private void InitializeProvisioningSearchEntries()
    {
        _provisioningSearchEntries.AddRange(
        [
            new("Tema do sistema", "Escolha o tema claro ou escuro do Windows.", "Personalização", ProvisioningCategory.Personalization, "aparência claro escuro", "Color24", ThemeComboBox),
            new("Alinhamento da barra de tarefas", "Posicione os ícones à esquerda ou no centro.", "Personalização", ProvisioningCategory.Personalization, "barra tarefas centralizar esquerda", "Settings24", TaskbarAlignmentComboBox),
            new("Caixa de pesquisa", "Defina como a pesquisa aparece na barra de tarefas.", "Personalização", ProvisioningCategory.Personalization, "barra tarefas pesquisa ícone ocultar", "Search24", TaskbarSearchBoxComboBox),
            new("Ocultar a barra de tarefas automaticamente", "Recolha a barra quando ela não estiver em uso.", "Personalização", ProvisioningCategory.Personalization, "auto ocultar recolher", "Desktop24", TaskbarAutoHideCheckBox),
            new("Papel de parede", "Escolha uma imagem para o plano de fundo da área de trabalho.", "Personalização", ProvisioningCategory.Personalization, "imagem fundo plano desktop área trabalho", "Desktop24", SelectWallpaperButton),
            new("Nome da máquina", "Defina o nome que será atribuído ao computador.", "Configurações avançadas", ProvisioningCategory.System, "computador pc hostname dispositivo", "Desktop24", MachineNameTextBox),
            new("Região", "Escolha a região do Windows.", "Configurações avançadas", ProvisioningCategory.System, "país localização brasil idioma", "Globe24", RegionComboBox),
            new("Plano de energia", "Selecione o perfil de energia do computador.", "Configurações avançadas", ProvisioningCategory.System, "bateria desempenho economia equilibrado", "Power24", PowerPlanComboBox),
            new("Desligar a tela", "Escolha após quanto tempo a tela será desligada.", "Configurações avançadas", ProvisioningCategory.System, "monitor vídeo tempo limite energia", "Desktop24", DisplayTimeoutAcComboBox),
            new("Suspender o computador", "Escolha após quanto tempo o PC entrará em suspensão.", "Configurações avançadas", ProvisioningCategory.System, "repouso dormir standby suspensão energia", "Power24", StandbyTimeoutAcComboBox),
            new("Ponto de restauração", "Crie um ponto de restauração antes de aplicar o perfil.", "Configurações avançadas", ProvisioningCategory.System, "sistema backup recuperação proteger", "Settings24", AutoCreateRestorePointCheckBox),
            new("Limpeza automática de temporários", "Agende a limpeza ao entrar no Windows.", "Configurações avançadas", ProvisioningCategory.System, "arquivos temporários logon inicialização agendar", "Broom24", AutoCleanTempOnLogonCheckBox),
            new("Informação OEM", "Edite o texto de identificação OEM do perfil.", "Perfil e JSON", ProvisioningCategory.Json, "fabricante identificação nome", "Person24", ProfileNameTextBox),
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
        _ = RefreshCleanTempTaskStatusAsync();
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
        Clipboard.SetText(JsonPreviewTextBox.Text);
        StatusText.Text = "JSON copiado para a área de transferência.";
    }

    private void OpenFullEditorButton_Click(object sender, RoutedEventArgs e)
    {
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
            && manifest.TaskbarAlignment is null or TaskbarAlignmentMode.NaoDefinido
            && manifest.TaskbarSearchBox is null or TaskbarSearchBoxMode.NaoDefinido
            && manifest.TaskbarAutoHide is not true
            && manifest.PowerPlan is null or PowerPlanMode.NaoDefinido
            && manifest.DisplayTimeoutOnAc is null
            && manifest.DisplayTimeoutOnDc is null
            && manifest.StandbyTimeoutOnAc is null
            && manifest.StandbyTimeoutOnDc is null
            && string.IsNullOrWhiteSpace(manifest.MachineName)
            && string.IsNullOrWhiteSpace(manifest.WallpaperImageBase64)
            && string.IsNullOrWhiteSpace(manifest.Region)
            && manifest.AutoCreateRestorePoint is not true
            && manifest.AutoCleanTempOnLogon is not true;

        if (isEmpty) return;

        _provisioningService.SetCurrent(manifest);
    }

    private void Field_Changed(object sender, RoutedEventArgs e)
    {
        if (!_uiLoaded) return;
        PushCurrentToService();
        UpdateDesktopPreview();
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
            changes.Add($"Tema: {theme}");

        if (GetSelectedContent(TaskbarAlignmentComboBox, "NaoDefinido") is { } alignment)
            changes.Add($"Alinhamento da barra de tarefas: {alignment}");

        if (GetSelectedContent(TaskbarSearchBoxComboBox, "NaoDefinido") is { } searchBox)
            changes.Add($"Caixa de pesquisa: {searchBox}");

        if (TaskbarAutoHideCheckBox.IsChecked is true)
            changes.Add("Barra de tarefas: ocultar automaticamente");

        if (_wallpaperFileName is { } wallpaperName)
            changes.Add($"Papel de parede: {wallpaperName}");

        if (GetSelectedContent(PowerPlanComboBox, "NaoDefinido") is { } powerPlan)
            changes.Add($"Plano de energia: {powerPlan}");

        if (GetSelectedMinutes(DisplayTimeoutAcComboBox) is { } displayAc)
            changes.Add($"Tela desliga: {FriendlyMinutesLabel(displayAc)}");
        if (GetSelectedMinutes(StandbyTimeoutAcComboBox) is { } standbyAc)
            changes.Add($"PC suspende: {FriendlyMinutesLabel(standbyAc)}");

        if (!string.IsNullOrWhiteSpace(MachineNameTextBox.Text))
            changes.Add($"Nome do PC: {MachineNameTextBox.Text.Trim()}");

        if (GetSelectedContent(RegionComboBox, "") is { } region)
            changes.Add($"Região: {region}");

        if (AutoCreateRestorePointCheckBox.IsChecked is true)
            changes.Add("Ponto de restauração: criar automaticamente ao aplicar");

        if (AutoCleanTempOnLogonCheckBox.IsChecked is true)
            changes.Add("Limpeza de arquivos temporários: agendar para cada logon");

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

    /// <summary>Lê o código de região (Tag = ISO 3166-1) do RegionComboBox — Tag vazio ("Não alterar") vira null.</summary>
    private static string? GetSelectedRegion(ComboBox? comboBox)
    {
        if (comboBox?.SelectedItem is not ComboBoxItem item) return null;
        string? tag = item.Tag as string;
        return string.IsNullOrEmpty(tag) ? null : tag;
    }

    /// <summary>Seleciona, no RegionComboBox, o item cujo Tag bate com o código ISO informado (null vira "Não alterar").</summary>
    private static void SelectRegion(ComboBox? comboBox, string? value)
    {
        if (comboBox is null) return;
        string tag = value ?? string.Empty;
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

    private ProvisioningManifest BuildManifestFromUi(string? name = null) => new()
    {
        Name = name ?? (string.IsNullOrWhiteSpace(ProfileNameTextBox.Text) ? null : ProfileNameTextBox.Text.Trim()),
        Creator = string.IsNullOrWhiteSpace(ProfileCreatorTextBox.Text) ? null : ProfileCreatorTextBox.Text.Trim(),
        Theme = GetSelectedEnum<SystemThemeMode>(ThemeComboBox),
        TaskbarAlignment = GetSelectedEnum<TaskbarAlignmentMode>(TaskbarAlignmentComboBox),
        TaskbarSearchBox = GetSelectedEnum<TaskbarSearchBoxMode>(TaskbarSearchBoxComboBox),
        TaskbarAutoHide = TaskbarAutoHideCheckBox.IsChecked,
        PowerPlan = GetSelectedEnum<PowerPlanMode>(PowerPlanComboBox),
        DisplayTimeoutOnAc = GetSelectedMinutes(DisplayTimeoutAcComboBox),
        DisplayTimeoutOnDc = GetSelectedMinutes(DisplayTimeoutAcComboBox),
        StandbyTimeoutOnAc = GetSelectedMinutes(StandbyTimeoutAcComboBox),
        StandbyTimeoutOnDc = GetSelectedMinutes(StandbyTimeoutAcComboBox),
        MachineName = string.IsNullOrWhiteSpace(MachineNameTextBox.Text) ? null : MachineNameTextBox.Text.Trim(),
        WallpaperFileName = _wallpaperFileName,
        WallpaperImageBase64 = _wallpaperImageBase64,
        Region = GetSelectedRegion(RegionComboBox),
        AutoCreateRestorePoint = AutoCreateRestorePointCheckBox.IsChecked,
        AutoCleanTempOnLogon = AutoCleanTempOnLogonCheckBox.IsChecked,
    };

    private void LoadManifestIntoUi(ProvisioningManifest manifest)
    {
        ProfileNameTextBox.Text = manifest.Name ?? string.Empty;
        ProfileCreatorTextBox.Text = manifest.Creator ?? string.Empty;
        SelectEnum(ThemeComboBox, manifest.Theme);
        SelectEnum(TaskbarAlignmentComboBox, manifest.TaskbarAlignment);
        SelectEnum(TaskbarSearchBoxComboBox, manifest.TaskbarSearchBox);
        TaskbarAutoHideCheckBox.IsChecked = manifest.TaskbarAutoHide;
        SelectEnum(PowerPlanComboBox, manifest.PowerPlan);
        SelectMinutes(DisplayTimeoutAcComboBox, manifest.DisplayTimeoutOnAc ?? manifest.DisplayTimeoutOnDc);
        SelectMinutes(StandbyTimeoutAcComboBox, manifest.StandbyTimeoutOnAc ?? manifest.StandbyTimeoutOnDc);
        MachineNameTextBox.Text = manifest.MachineName ?? string.Empty;
        SelectRegion(RegionComboBox, manifest.Region);
        AutoCreateRestorePointCheckBox.IsChecked = manifest.AutoCreateRestorePoint;
        AutoCleanTempOnLogonCheckBox.IsChecked = manifest.AutoCleanTempOnLogon;

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
        ApplyButton.IsEnabled = false;
        StatusText.Text = "Aplicando ajustes...";
        StatusText.ToolTip = null;

        try
        {
            var manifest = BuildManifestFromUi();
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
            }
            else
            {
                StatusText.Text = failedSteps.Count == 1
                    ? $"A configuração \"{failedSteps[0].Setting}\" não pôde ser aplicada."
                    : $"{failedSteps.Count} configurações não puderam ser aplicadas.";
                StatusText.ToolTip = string.Join(Environment.NewLine,
                    failedSteps.Select(step => $"{step.Setting}: {step.Message}"));
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
        }
        finally
        {
            ApplyButton.IsEnabled = true;
        }
    }

    private async Task RefreshCleanTempTaskStatusAsync()
    {
        try
        {
            bool isEnabled = await _scheduledTempCleanerService.IsEnabledAsync();
            CleanTempTaskStatusText.Text = isEnabled
                ? "Limpeza automática ativa."
                : "Limpeza automática inativa.";
            ScheduleCleanTempNowButton.Content = isEnabled ? "Atualizar agendamento" : "Agendar limpeza";
        }
        catch
        {
            CleanTempTaskStatusText.Text = "Status no sistema: Não foi possível verificar.";
        }
    }

    private async void ScheduleCleanTempNowButton_Click(object sender, RoutedEventArgs e)
    {
        ScheduleCleanTempNowButton.IsEnabled = false;
        StatusText.Text = "Agendando a limpeza automática...";

        try
        {
            var result = await _scheduledTempCleanerService.EnableAsync();
            if (result.Success)
            {
                StatusText.Text = "Limpeza automática agendada.";
                AutoCleanTempOnLogonCheckBox.IsChecked = true;
            }
            else
            {
                StatusText.Text = "Não foi possível agendar a limpeza. Tente novamente.";
            }
        }
        catch
        {
            StatusText.Text = "Não foi possível agendar a limpeza. Tente novamente.";
        }
        finally
        {
            ScheduleCleanTempNowButton.IsEnabled = true;
            await RefreshCleanTempTaskStatusAsync();
        }
    }


}
