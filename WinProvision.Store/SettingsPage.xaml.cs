using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using WinProvision.Core.Models;
using WinProvision.Core.Services;
using WinProvision.Core.Services.Backup;
using WinProvision.Store.Services;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace WinProvision.Store;

public partial class SettingsPage : Page
{
    private readonly CacheService _cacheService;
    private readonly AppUpdateService _updateService;
    private readonly InstallationPreferencesService _installationPreferences;
    private readonly ApplicationPreferencesService _applicationPreferences;
    private readonly PackageCollectionService _collectionService;
    private readonly LocalBackupService _backupService;
    private bool _initializingPreferences = true;
    private bool _initializingInstallMethod = true;
    private AppUpdateCheckResult? _pendingUpdate;
    private readonly List<SettingSearchEntry> _searchEntries = [];
    private readonly List<SettingSearchEntry> _searchResults = [];

    private sealed record SettingSearchEntry(
        string Title,
        string Description,
        string Category,
        string CategoryKey,
        string SearchTerms,
        string Symbol,
        Border Target);

    private readonly Dictionary<string, (string Title, StackPanel Panel)> _categories;

    public SettingsPage()
    {
        InitializeComponent();

        _cacheService = App.Services.GetRequiredService<CacheService>();
        _updateService = App.Services.GetRequiredService<AppUpdateService>();
        _installationPreferences = App.Services.GetRequiredService<InstallationPreferencesService>();
        _applicationPreferences = App.Services.GetRequiredService<ApplicationPreferencesService>();
        _collectionService = App.Services.GetRequiredService<PackageCollectionService>();
        _backupService = App.Services.GetRequiredService<LocalBackupService>();

        _categories = new Dictionary<string, (string Title, StackPanel Panel)>(StringComparer.OrdinalIgnoreCase)
        {
            ["General"] = ("Preferências gerais", PanelGeneral),
            ["Interface"] = ("Interface e inicialização", PanelInterface),
            ["Operations"] = ("Armazenamento e cache", PanelOperations),
            ["Backup"] = ("Backup e restauração", PanelBackup)
        };

        InitializeSearchEntries();

        InstallMethodComboBox.SelectedItem = InstallMethodComboBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => Enum.TryParse<PackageInstallMethod>(item.Tag?.ToString(), out var method)
                                    && method == _installationPreferences.PreferredMethod)
            ?? InstallMethodComboBox.Items[0];
        UpdateInstallMethodDescription(_installationPreferences.PreferredMethod);
        _initializingInstallMethod = false;

        LaunchAtStartupSwitch.IsChecked = _applicationPreferences.LaunchAtStartup;
        _initializingPreferences = false;
        RefreshThemeButtonsUi();
        VersionText.Text = $"Versão {GetApplicationVersion()}";

        ApplicationThemeManager.Changed += ApplicationThemeManager_Changed;
        Unloaded += (_, _) =>
        {
            ApplicationThemeManager.Changed -= ApplicationThemeManager_Changed;
        };
    }

    private static string GetApplicationVersion()
    {
        var version = Assembly.GetEntryAssembly()?.GetName().Version;
        return version is null ? "desconhecida" : version.ToString(3);
    }

    // ─── Navegação e Categorias ──────────────────────────────────────────────

    private void Category_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && _categories.TryGetValue(tag, out var cat))
        {
            NavigateToCategory(cat.Title, cat.Panel);
        }
    }

    private void CategoryCard_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Space)
            || sender is not FrameworkElement { Tag: string tag }
            || !_categories.TryGetValue(tag, out var category))
        {
            return;
        }

        NavigateToCategory(category.Title, category.Panel);
        e.Handled = true;
    }

    private void NavigateToCategory(string title, StackPanel activePanel)
    {
        HomepageScrollViewer.Visibility = Visibility.Collapsed;
        SubCategoryScrollViewer.Visibility = Visibility.Visible;
        BackButton.Visibility = Visibility.Visible;
        SettingsTitleTextBlock.Text = title;

        foreach (var item in _categories.Values)
        {
            item.Panel.Visibility = ReferenceEquals(item.Panel, activePanel) ? Visibility.Visible : Visibility.Collapsed;
        }

        SubCategoryScrollViewer.ScrollToTop();
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        SubCategoryScrollViewer.Visibility = Visibility.Collapsed;
        HomepageScrollViewer.Visibility = Visibility.Visible;
        BackButton.Visibility = Visibility.Collapsed;
        SettingsTitleTextBlock.Text = "Configurações do WinProvision Store";

        foreach (var item in _categories.Values)
        {
            item.Panel.Visibility = Visibility.Collapsed;
        }
    }

    private void InitializeSearchEntries()
    {
        _searchEntries.AddRange(
        [
            new("Método de instalação preferencial", "Escolha o método automático, WinGet COM, WinProvision API ou WinGet CLI.", "Preferências gerais", "General", "método instalar instalação pacote automático api com winget cli rota", "Settings24", InstallMethodCard),
            new("Verificação de atualizações", "Procure uma versão mais recente do WinProvision Store.", "Preferências gerais", "General", "atualização atualizar versão github verificar update", "ArrowSync24", AppUpdateCard),
            new("Exportar configurações", "Salve as preferências atuais em um arquivo JSON.", "Preferências gerais", "General", "exportar salvar configurações json", "ArrowExportLtr24", PrivacyCard),
            new("Importar configurações", "Carregue preferências de um arquivo JSON.", "Preferências gerais", "General", "importar carregar configurações json", "ArrowImport24", PrivacyCard),
            new("Abrir pasta de logs", "Acesse os arquivos de log do aplicativo.", "Preferências gerais", "General", "log logs pasta diagnóstico arquivos", "DocumentText24", PrivacyCard),
            new("Restaurar padrões", "Volte às preferências originais do aplicativo.", "Preferências gerais", "General", "restaurar padrões redefinir reset configurações", "ArrowRotateCounterclockwise24", PrivacyCard),
            new("Tema do aplicativo", "Alterne entre os temas claro, escuro e do sistema.", "Interface e inicialização", "Interface", "tema aparência claro escuro sistema cor", "Color24", ThemeCard),
            new("Iniciar com o Windows", "Abra o WinProvision automaticamente ao entrar nesta conta.", "Interface e inicialização", "Interface", "inicialização iniciar login windows automático", "Power24", StartupCard),
            new("Limpar cache local de instaladores", "Remova os instaladores temporários para liberar espaço em disco.", "Armazenamento e cache", "Operations", "cache armazenamento espaço disco temporários instaladores limpar", "Broom24", CacheCard),
            new("Exportar backup local", "Salve a lista de aplicativos instalados em um arquivo JSON.", "Backup e restauração", "Backup", "backup exportar salvar aplicativos instalados lista json", "Save24", BackupExportCard),
            new("Restaurar de arquivo JSON", "Selecione um arquivo de backup para usar na Coleção de Pacotes.", "Backup e restauração", "Backup", "backup restaurar importar arquivo json coleção reinstalar", "ArrowRotateCounterclockwise24", BackupRestoreCard)
        ]);
    }

    private void SettingsSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        string query = NormalizeSearchText(SettingsSearchBox.Text ?? string.Empty);

        if (!string.IsNullOrEmpty(query) && SubCategoryScrollViewer.Visibility == Visibility.Visible)
        {
            BackButton_Click(sender, e);
        }

        SettingsSearchResultsList.Children.Clear();
        _searchResults.Clear();
        bool isSearching = !string.IsNullOrWhiteSpace(query);
        SettingsCategoryListPanel.Visibility = isSearching ? Visibility.Collapsed : Visibility.Visible;
        SettingsSearchResultsPanel.Visibility = isSearching ? Visibility.Visible : Visibility.Collapsed;
        SettingsSearchEmptyText.Visibility = Visibility.Collapsed;

        if (!isSearching)
        {
            SettingsSearchResultText.Visibility = Visibility.Collapsed;
            SettingsSearchResultText.Text = string.Empty;
            return;
        }

        string[] queryTerms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        _searchResults.AddRange(_searchEntries.Where(entry =>
        {
            string searchableText = NormalizeSearchText($"{entry.Title} {entry.Description} {entry.Category} {entry.SearchTerms}");
            return queryTerms.All(term => searchableText.Contains(term, StringComparison.Ordinal));
        }));

        foreach (SettingSearchEntry entry in _searchResults)
        {
            SettingsSearchResultsList.Children.Add(CreateSearchResultButton(entry));
        }

        if (_searchResults.Count == 0)
        {
            SettingsSearchResultText.Text = "Nenhuma opção encontrada.";
            SettingsSearchEmptyText.Visibility = Visibility.Visible;
        }
        else
        {
            SettingsSearchResultText.Text = _searchResults.Count == 1
                ? "1 opção encontrada. Selecione para abrir essa configuração."
                : $"{_searchResults.Count} opções encontradas. Selecione uma para abrir a configuração correspondente.";
        }

        SettingsSearchResultText.Visibility = Visibility.Visible;
    }

    private Wpf.Ui.Controls.Button CreateSearchResultButton(SettingSearchEntry entry)
    {
        var button = new Wpf.Ui.Controls.Button
        {
            Appearance = ControlAppearance.Secondary,
            Style = (Style)FindResource("SettingsSearchResultButtonStyle"),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Tag = entry,
            ToolTip = entry.Description
        };
        button.Click += SettingsSearchResultButton_Click;
        System.Windows.Automation.AutomationProperties.SetName(button, $"{entry.Title}, {entry.Category}");
        System.Windows.Automation.AutomationProperties.SetHelpText(button, entry.Description);

        var content = new Grid();
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new SymbolIcon
        {
            Symbol = Enum.Parse<SymbolRegular>(entry.Symbol),
            FontSize = 20,
            VerticalAlignment = VerticalAlignment.Center
        };
        icon.SetResourceReference(Control.ForegroundProperty, "TextFillColorSecondaryBrush");
        content.Children.Add(icon);

        var textStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        textStack.Children.Add(new System.Windows.Controls.TextBlock { Text = entry.Title, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        var description = new System.Windows.Controls.TextBlock
        {
            Text = entry.Description,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 3, 0, 0)
        };
        description.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        textStack.Children.Add(description);
        Grid.SetColumn(textStack, 2);
        content.Children.Add(textStack);

        var category = new System.Windows.Controls.TextBlock
        {
            Text = entry.Category,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 180
        };
        category.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        Grid.SetColumn(category, 4);
        content.Children.Add(category);
        button.Content = content;
        return button;
    }

    private void SettingsSearchResultButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Wpf.Ui.Controls.Button { Tag: SettingSearchEntry entry })
        {
            NavigateToSearchResult(entry);
        }
    }

    private void NavigateToSearchResult(SettingSearchEntry entry)
    {
        if (!_categories.TryGetValue(entry.CategoryKey, out var category))
        {
            return;
        }

        NavigateToCategory(category.Title, category.Panel);
        Dispatcher.BeginInvoke(new Action(() =>
        {
            entry.Target.BringIntoView();
            entry.Target.Focus();
        }), DispatcherPriority.Loaded);
    }

    private void SettingsSearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || _searchResults.Count == 0)
        {
            return;
        }

        NavigateToSearchResult(_searchResults[0]);
        e.Handled = true;
    }

    private static string NormalizeSearchText(string value)
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

    // ─── Tema ────────────────────────────────────────────────────────────────

    private void RefreshThemeButtonsUi()
    {
        LightThemeButton.Appearance = _applicationPreferences.Theme == AppThemePreference.Light
            ? ControlAppearance.Primary : ControlAppearance.Secondary;
        DarkThemeButton.Appearance = _applicationPreferences.Theme == AppThemePreference.Dark
            ? ControlAppearance.Primary : ControlAppearance.Secondary;
        SystemThemeButton.Appearance = _applicationPreferences.Theme == AppThemePreference.System
            ? ControlAppearance.Primary : ControlAppearance.Secondary;
    }

    private void ApplicationThemeManager_Changed(ApplicationTheme currentApplicationTheme, System.Windows.Media.Color systemAccent)
    {
        Dispatcher.BeginInvoke(RefreshThemeButtonsUi);
    }

    private void LightThemeButton_Click(object sender, RoutedEventArgs e)
    {
        _applicationPreferences.SetTheme(AppThemePreference.Light);
        RefreshThemeButtonsUi();
        StatusText.Text = "Tema claro aplicado.";
    }

    private void DarkThemeButton_Click(object sender, RoutedEventArgs e)
    {
        _applicationPreferences.SetTheme(AppThemePreference.Dark);
        RefreshThemeButtonsUi();
        StatusText.Text = "Tema escuro aplicado.";
    }

    private void SystemThemeButton_Click(object sender, RoutedEventArgs e)
    {
        _applicationPreferences.SetTheme(AppThemePreference.System);
        RefreshThemeButtonsUi();
        StatusText.Text = "Tema do Windows aplicado automaticamente.";
    }

    private void LaunchAtStartupSwitch_Click(object sender, RoutedEventArgs e)
    {
        if (_initializingPreferences) return;
        bool enabled = LaunchAtStartupSwitch.IsChecked == true;
        try
        {
            _applicationPreferences.SetLaunchAtStartup(enabled);
            StatusText.Text = enabled
                ? "O WinProvision será iniciado com esta conta do Windows."
                : "A inicialização automática foi desativada.";
        }
        catch (Exception ex)
        {
            _initializingPreferences = true;
            LaunchAtStartupSwitch.IsChecked = !enabled;
            _initializingPreferences = false;
            StatusText.Text = $"Não foi possível alterar a inicialização automática: {ex.Message}";
        }
    }

    // ─── Método de Instalação e Cache ────────────────────────────────────────

    private void InstallMethodComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializingInstallMethod || InstallMethodComboBox.SelectedItem is not ComboBoxItem { Tag: string tag }
            || !Enum.TryParse(tag, out PackageInstallMethod method))
            return;

        try
        {
            _installationPreferences.SetPreferredMethod(method);
            UpdateInstallMethodDescription(method);
            StatusText.Text = "Método de instalação salvo.";
        }
        catch
        {
            StatusText.Text = "Não foi possível salvar a preferência. Tente novamente.";
        }
    }

    private void UpdateInstallMethodDescription(PackageInstallMethod method)
    {
        InstallMethodDescriptionText.Text = method switch
        {
            PackageInstallMethod.ComApi => "Usa somente o WinGet COM.",
            PackageInstallMethod.WinProvisionApi => "Usa somente a WinProvision API.",
            PackageInstallMethod.WingetCli => "Usa somente o WinGet CLI.",
            _ => "Seleciona a rota disponível e usa fallback quando necessário."
        };
    }

    private async void ClearCacheButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await _cacheService.ClearAsync();
            // Os ícones do Office e outras imagens remotas usam um cache WPF
            // independente dos caches de catálogo e do IconService.
            Converters.AsyncImage.ClearCache();
            StatusText.Text = "Caches locais de busca e instalação limpos.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Erro ao limpar cache: {ex.Message}";
        }
    }

    // ─── Atualizador do Aplicativo ───────────────────────────────────────────

    private async void CheckUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        CheckUpdateButton.IsEnabled = false;
        UpdateAvailablePanel.Visibility = Visibility.Collapsed;
        _pendingUpdate = null;
        UpdateSubtitleText.Text = "Procurando atualizações no GitHub...";

        try
        {
            var result = await _updateService.CheckForUpdateAsync();

            if (!result.Success)
            {
                UpdateSubtitleText.Text = result.Error;
                return;
            }

            if (!result.UpdateAvailable)
            {
                UpdateSubtitleText.Text = result.LatestVersion is not null
                    ? $"Você já está na versão mais recente ({result.CurrentVersion?.ToString(3)})."
                    : "Seu build já é o mais recente publicado no canal nightly.";
                return;
            }

            _pendingUpdate = result;
            UpdateSubtitleText.Text = $"Versão instalada: {result.CurrentVersion?.ToString(3)}";
            UpdateAvailableText.Text = result.LatestVersion is not null
                ? $"Nova versão disponível: {result.LatestVersion.ToString(3)}"
                : $"Novo build nightly disponível ({result.ReleaseLabel}).";
            UpdateAvailablePanel.Visibility = Visibility.Visible;
        }
        catch
        {
            UpdateSubtitleText.Text = "Não foi possível verificar atualizações. Tente novamente.";
        }
        finally
        {
            CheckUpdateButton.IsEnabled = true;
        }
    }

    private async void InstallUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingUpdate is null) return;

        CheckUpdateButton.IsEnabled = false;
        InstallUpdateButton.IsEnabled = false;
        UpdateProgressBar.Visibility = Visibility.Visible;

        var progress = new Progress<double>(fraction =>
        {
            UpdateProgressBar.Value = fraction;
        });

        try
        {
            string setupPath = await _updateService.DownloadInstallerAsync(_pendingUpdate, progress);
            AppUpdateService.ScheduleSilentInstallAndRestart(setupPath);
            await Task.Delay(TimeSpan.FromMilliseconds(500));
            Application.Current.Shutdown();
        }
        catch
        {
            UpdateProgressBar.Visibility = Visibility.Collapsed;
            CheckUpdateButton.IsEnabled = true;
            InstallUpdateButton.IsEnabled = true;
            StatusText.Text = "Falha ao baixar e aplicar atualização.";
        }
    }

    // ─── Backup e Restauração ────────────────────────────────────────────────

    private async void ExportBackup_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Salvar backup de pacotes",
            Filter = "Arquivo JSON (*.json)|*.json",
            FileName = $"WinProvision-Backup-{DateTime.Now:yyyyMMdd}.json"
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            var tab = _collectionService.ActiveTab;
            var list = tab?.Items.Select(app => new
            {
                Id = app.Id,
                Name = app.Name,
                Version = app.Version,
                Source = app.Source,
                ManagerName = "WinGet"
            }).ToList() ?? [];

            var bundleObj = new { export_version = 3.0, packages = list };
            string json = JsonSerializer.Serialize(bundleObj, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(dialog.FileName, json);
            StatusText.Text = $"Backup salvo com sucesso em \"{Path.GetFileName(dialog.FileName)}\".";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Erro ao criar backup: {ex.Message}";
        }
    }

    private void ImportBackup_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Selecionar arquivo de backup",
            Filter = "Arquivo JSON (*.json)|*.json"
        };

        if (dialog.ShowDialog() == true)
        {
            StatusText.Text = $"Arquivo \"{Path.GetFileName(dialog.FileName)}\" carregado. Importe na aba Coleção de Pacotes.";
        }
    }

    // ─── Exportar / Importar Configurações e Logs ────────────────────────────

    private void ExportSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Exportar configurações do WinProvision",
            Filter = "Configurações WinProvision (*.json)|*.json",
            FileName = "winprovision-settings.json"
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            var payload = new
            {
                exportedAt = DateTimeOffset.UtcNow,
                theme = _applicationPreferences.Theme.ToString(),
                launchAtStartup = _applicationPreferences.LaunchAtStartup,
                preferredMethod = _installationPreferences.PreferredMethod.ToString()
            };
            File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
            StatusText.Text = "Configurações exportadas com sucesso.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Erro ao exportar: {ex.Message}";
        }
    }

    private void ImportSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Importar configurações",
            Filter = "Configurações WinProvision (*.json)|*.json"
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(dialog.FileName));
            var root = doc.RootElement;
            if (root.TryGetProperty("theme", out var tProp) && Enum.TryParse<AppThemePreference>(tProp.GetString(), out var theme))
                _applicationPreferences.SetTheme(theme);
            if (root.TryGetProperty("preferredMethod", out var mProp) && Enum.TryParse<PackageInstallMethod>(mProp.GetString(), out var method))
            {
                _installationPreferences.SetPreferredMethod(method);
                InstallMethodComboBox.SelectedItem = InstallMethodComboBox.Items
                    .OfType<ComboBoxItem>()
                    .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), method.ToString(), StringComparison.OrdinalIgnoreCase));
                UpdateInstallMethodDescription(method);
            }
            if (root.TryGetProperty("launchAtStartup", out var startupProp)
                && startupProp.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                bool launchAtStartup = startupProp.GetBoolean();
                _applicationPreferences.SetLaunchAtStartup(launchAtStartup);
                LaunchAtStartupSwitch.IsChecked = launchAtStartup;
            }

            RefreshThemeButtonsUi();
            StatusText.Text = "Configurações importadas com sucesso.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Erro ao importar: {ex.Message}";
        }
    }

    private void OpenLogsButton_Click(object sender, RoutedEventArgs e)
    {
        string logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinProvisionStore", "logs");
        Directory.CreateDirectory(logDir);
        Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = $"\"{logDir}\"", UseShellExecute = true });
    }

    private void ResetPreferencesButton_Click(object sender, RoutedEventArgs e)
    {
        _applicationPreferences.SetTheme(AppThemePreference.System);
        _applicationPreferences.SetLaunchAtStartup(false);
        LaunchAtStartupSwitch.IsChecked = false;
        _installationPreferences.SetPreferredMethod(PackageInstallMethod.Automatic);
        RefreshThemeButtonsUi();
        InstallMethodComboBox.SelectedIndex = 0;
        UpdateInstallMethodDescription(PackageInstallMethod.Automatic);
        StatusText.Text = "Configurações restauradas para os padrões originais.";
    }
}
