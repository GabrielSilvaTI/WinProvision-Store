using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.DependencyInjection;
using WinProvision.Core.Models;
using WinProvision.Core.Models.Provisioning;
using WinProvision.Core.Services;
using WinProvision.Core.Services.Provisioning;

namespace WinProvision.Store;

public partial class ProvisioningPage : Page
{
    private readonly ProvisioningService _provisioningService;

    // Guardados à parte (em vez de num controle de UI) porque o wallpaper é um arquivo, não um
    // valor editável — ficam aqui até o usuário exportar ou aplicar, e são preenchidos de volta
    // ao importar um perfil que já tenha wallpaper embutido.
    private string? _wallpaperFileName;
    private string? _wallpaperImageBase64;

    // Evita empurrar estado pro serviço enquanto LoadManifestIntoUi está preenchendo os
    // controles programaticamente (cada SelectionChanged/TextChanged disparado durante a
    // carga geraria um push com o manifesto ainda pela metade) — só falso durante a carga.
    private bool _uiLoaded;

    public ProvisioningPage()
    {
        InitializeComponent();

        _provisioningService = App.Services.GetRequiredService<ProvisioningService>();

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
    }

    /// <summary>Alterna o menu lateral de "Provisionamento" entre a visão do Perfil (padrão) e uma seção específica.</summary>
    private void ShowSection(StackPanel sectionPanel, string title)
    {
        PersonalizationSectionPanel.Visibility = Visibility.Collapsed;
        UpdatesSectionPanel.Visibility = Visibility.Collapsed;
        AdvancedSectionPanel.Visibility = Visibility.Collapsed;
        JsonSectionPanel.Visibility = Visibility.Collapsed;
        sectionPanel.Visibility = Visibility.Visible;

        SectionTitleText.Text = title;
        ProfileOverviewPanel.Visibility = Visibility.Collapsed;
        SectionPanel.Visibility = Visibility.Visible;
    }

    private void ShowProfileOverview()
    {
        SectionPanel.Visibility = Visibility.Collapsed;
        ProfileOverviewPanel.Visibility = Visibility.Visible;
        RefreshProfileSummary();
    }

    private void PersonalizationNavCard_Click(object sender, RoutedEventArgs e) => ShowSection(PersonalizationSectionPanel, "Personalização");

    private void UpdatesNavCard_Click(object sender, RoutedEventArgs e) => ShowSection(UpdatesSectionPanel, "Atualizações");

    private void AdvancedNavCard_Click(object sender, RoutedEventArgs e) => ShowSection(AdvancedSectionPanel, "Configurações Avançadas");

    private void JsonNavCard_Click(object sender, RoutedEventArgs e) => ShowSection(JsonSectionPanel, "Visualização do JSON");

    private void BackToProfileButton_Click(object sender, RoutedEventArgs e) => ShowProfileOverview();

    /// <summary>
    /// Recalcula os cartões "Informações do Perfil"/"Resumo do Perfil" e a Visualização do
    /// JSON a partir do estado atual da UI — chamado sempre que algo muda (ver
    /// <see cref="PushCurrentToService"/>) e ao voltar da tela de uma seção pra visão do Perfil.
    /// </summary>
    private void RefreshProfileSummary(ProvisioningManifest? manifest = null)
    {
        manifest ??= BuildManifestFromUi();

        // Nome/Criador não são forçados aqui de volta pro TextBox — são os próprios TextBox
        // (ProfileNameTextBox/ProfileCreatorTextBox) que alimentam o manifesto, então
        // sobrescrever o texto a cada refresh atrapalharia o usuário digitando.
        ProfileCreatedAtText.Text = manifest.CreatedAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss");

        bool personalizationSet = (manifest.Theme is { } theme && theme != SystemThemeMode.NaoDefinido)
            || (manifest.TaskbarAlignment is { } align && align != TaskbarAlignmentMode.NaoDefinido)
            || (manifest.TaskbarSearchBox is { } search && search != TaskbarSearchBoxMode.NaoDefinido)
            || manifest.TaskbarAutoHide is true
            || !string.IsNullOrWhiteSpace(manifest.WallpaperImageBase64);
        bool updatesSet = manifest.AutoInstallWindowsUpdates is true;
        bool advancedSet = !string.IsNullOrWhiteSpace(manifest.MachineName)
            || !string.IsNullOrWhiteSpace(manifest.Region)
            || (manifest.PowerPlan is { } power && power != PowerPlanMode.NaoDefinido)
            || manifest.AutoCreateRestorePoint is true;

        int sectionsConfigured = (personalizationSet ? 1 : 0) + (updatesSet ? 1 : 0) + (advancedSet ? 1 : 0);

        int keysModified = 0;
        if (manifest.Theme is { } t && t != SystemThemeMode.NaoDefinido) keysModified++;
        if (manifest.TaskbarAlignment is { } ta && ta != TaskbarAlignmentMode.NaoDefinido) keysModified++;
        if (manifest.TaskbarSearchBox is { } ts && ts != TaskbarSearchBoxMode.NaoDefinido) keysModified++;
        if (manifest.TaskbarAutoHide is true) keysModified++;
        if (manifest.PowerPlan is { } pp && pp != PowerPlanMode.NaoDefinido) keysModified++;
        if (!string.IsNullOrWhiteSpace(manifest.MachineName)) keysModified++;
        if (!string.IsNullOrWhiteSpace(manifest.WallpaperImageBase64)) keysModified++;
        if (!string.IsNullOrWhiteSpace(manifest.Region)) keysModified++;
        if (manifest.AutoInstallWindowsUpdates is true) keysModified++;
        if (manifest.AutoCreateRestorePoint is true) keysModified++;

        var warnings = new List<string>();
        if (!string.IsNullOrWhiteSpace(manifest.MachineName) && manifest.MachineName.Length > 15)
        {
            warnings.Add("Nome da máquina excede 15 caracteres (limite NetBIOS) — pode ser truncado ao aplicar.");
        }

        SectionsConfiguredCountText.Text = sectionsConfigured.ToString();
        KeysModifiedCountText.Text = keysModified.ToString();
        WarningsCountText.Text = warnings.Count.ToString();
        ChangesDetectedText.Text = $"{keysModified} alteração(ões) detectada(s)";

        var friendlyChanges = BuildFriendlyChangesList();
        ChangesListItemsControl.ItemsSource = friendlyChanges;
        ChangesListItemsControl.Visibility = friendlyChanges.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        NoChangesText.Visibility = friendlyChanges.Count > 0 ? Visibility.Collapsed : Visibility.Visible;

        if (warnings.Count == 0)
        {
            ProfileValidIcon.Symbol = Wpf.Ui.Controls.SymbolRegular.CheckmarkCircle24;
            ProfileValidIcon.Foreground = (System.Windows.Media.Brush)FindResource("SystemFillColorSuccessBrush");
            ProfileValidText.Text = "Perfil válido";
        }
        else
        {
            ProfileValidIcon.Symbol = Wpf.Ui.Controls.SymbolRegular.Warning24;
            ProfileValidIcon.Foreground = (System.Windows.Media.Brush)FindResource("SystemFillColorCautionBrush");
            ProfileValidText.Text = string.Join(" ", warnings);
        }

        JsonPreviewTextBox.Text = BuildProfileJson(manifest);
    }

    /// <summary>Mesmo formato gravado por <see cref="ProvisioningService.ExportAsync"/> — um <see cref="ProfileManifest"/> com só a seção de provisionamento preenchida.</summary>
    private static string BuildProfileJson(ProvisioningManifest manifest)
    {
        var profile = new ProfileManifest { Name = manifest.Name, Provisioning = manifest };
        return JsonSerializer.Serialize(profile, WinProvisionJsonOptions.Default);
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
        bool isEmpty = manifest.Theme is null or SystemThemeMode.NaoDefinido
            && manifest.TaskbarAlignment is null or TaskbarAlignmentMode.NaoDefinido
            && manifest.TaskbarSearchBox is null or TaskbarSearchBoxMode.NaoDefinido
            && manifest.TaskbarAutoHide is not true
            && manifest.PowerPlan is null or PowerPlanMode.NaoDefinido
            && string.IsNullOrWhiteSpace(manifest.MachineName)
            && string.IsNullOrWhiteSpace(manifest.WallpaperImageBase64)
            && string.IsNullOrWhiteSpace(manifest.Region)
            && manifest.AutoInstallWindowsUpdates is not true
            && manifest.AutoCreateRestorePoint is not true;

        if (isEmpty) return;

        _provisioningService.SetCurrent(manifest);
    }

    private void Field_Changed(object sender, RoutedEventArgs e) => PushCurrentToService();

    /// <summary>
    /// Monta a lista simples e legível (um item por linha, em português) do que já foi
    /// alterado na tela — lida direto dos controles (mesmo texto amigável que o usuário já
    /// vê nos ComboBox/CheckBox), pra quem for exportar ou aplicar o perfil enxergar de cara
    /// o que está sendo levado, sem precisar interpretar o JSON bruto.
    /// </summary>
    private List<string> BuildFriendlyChangesList()
    {
        var changes = new List<string>();

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

        if (!string.IsNullOrWhiteSpace(MachineNameTextBox.Text))
            changes.Add($"Nome do PC: {MachineNameTextBox.Text.Trim()}");

        if (GetSelectedContent(RegionComboBox, "") is { } region)
            changes.Add($"Região: {region}");

        if (AutoInstallWindowsUpdatesCheckBox.IsChecked is true)
            changes.Add("Atualizações e drivers: instalar automaticamente ao aplicar");

        if (AutoCreateRestorePointCheckBox.IsChecked is true)
            changes.Add("Ponto de restauração: criar automaticamente ao aplicar");

        return changes;
    }

    /// <summary>
    /// Lê o texto amigável (Content) do item selecionado num ComboBox — retorna null quando
    /// nada foi de fato escolhido (Tag igual a <paramref name="unsetTag"/>, o valor de "Não
    /// alterar" de cada combo), pra ficar de fora da lista de alterações.
    /// </summary>
    private static string? GetSelectedContent(ComboBox comboBox, string unsetTag)
    {
        if (comboBox.SelectedItem is not ComboBoxItem item) return null;
        string? tag = item.Tag as string;
        if (tag == unsetTag) return null;
        return item.Content as string;
    }

    /// <summary>Lê o valor do enum selecionado num ComboBox montado com ComboBoxItem.Tag = nome do enum.</summary>
    private static TEnum GetSelectedEnum<TEnum>(ComboBox comboBox) where TEnum : struct, Enum
    {
        string? tag = (comboBox.SelectedItem as ComboBoxItem)?.Tag as string;
        return tag is not null && Enum.TryParse<TEnum>(tag, out var value) ? value : default;
    }

    /// <summary>Seleciona, num ComboBox montado com ComboBoxItem.Tag = nome do enum, o item cujo Tag bate com o valor.</summary>
    private static void SelectEnum<TEnum>(ComboBox comboBox, TEnum? value) where TEnum : struct, Enum
    {
        string tag = (value ?? default).ToString();
        var match = comboBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == tag);
        comboBox.SelectedItem = match ?? comboBox.Items.OfType<ComboBoxItem>().FirstOrDefault();
    }

    /// <summary>Lê o código de região (Tag = ISO 3166-1) do RegionComboBox — Tag vazio ("Não alterar") vira null.</summary>
    private static string? GetSelectedRegion(ComboBox comboBox)
    {
        string? tag = (comboBox.SelectedItem as ComboBoxItem)?.Tag as string;
        return string.IsNullOrEmpty(tag) ? null : tag;
    }

    /// <summary>Seleciona, no RegionComboBox, o item cujo Tag bate com o código ISO informado (null vira "Não alterar").</summary>
    private static void SelectRegion(ComboBox comboBox, string? value)
    {
        string tag = value ?? string.Empty;
        var match = comboBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == tag);
        comboBox.SelectedItem = match ?? comboBox.Items.OfType<ComboBoxItem>().FirstOrDefault();
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
        MachineName = string.IsNullOrWhiteSpace(MachineNameTextBox.Text) ? null : MachineNameTextBox.Text.Trim(),
        WallpaperFileName = _wallpaperFileName,
        WallpaperImageBase64 = _wallpaperImageBase64,
        Region = GetSelectedRegion(RegionComboBox),
        AutoInstallWindowsUpdates = AutoInstallWindowsUpdatesCheckBox.IsChecked,
        AutoCreateRestorePoint = AutoCreateRestorePointCheckBox.IsChecked,
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
        MachineNameTextBox.Text = manifest.MachineName ?? string.Empty;
        SelectRegion(RegionComboBox, manifest.Region);
        AutoInstallWindowsUpdatesCheckBox.IsChecked = manifest.AutoInstallWindowsUpdates;
        AutoCreateRestorePointCheckBox.IsChecked = manifest.AutoCreateRestorePoint;

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
            }
        }
        else
        {
            WallpaperPreviewImage.Source = null;
            WallpaperFileNameText.Text = "Nenhuma imagem selecionada.";
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
            StatusText.Text = $"Imagem \"{_wallpaperFileName}\" pronta — será incluída ao exportar, sincronizar ou aplicar o perfil.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Erro ao carregar imagem: {ex.Message}";
        }
    }

    private async void ImportButton_Click(object sender, RoutedEventArgs e)
    {
        var openFileDialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "JSON Profile (*.json)|*.json",
            Title = "Selecione o perfil de provisionamento"
        };

        if (openFileDialog.ShowDialog() != true) return;

        StatusText.Text = "Lendo perfil de provisionamento...";

        try
        {
            var manifest = await _provisioningService.ImportAsync(openFileDialog.FileName);
            LoadManifestIntoUi(manifest);

            // Importar já marca como "atual" (entra no próximo backup/sincronização),
            // mesmo antes de clicar em "Aplicar agora" — só não mexe no Windows ainda.
            _provisioningService.SetCurrent(manifest);

            StatusText.Text = $"Perfil \"{manifest.Name ?? System.IO.Path.GetFileNameWithoutExtension(openFileDialog.FileName)}\" importado. Revise os ajustes e clique em \"Aplicar agora\" para valer nesta máquina.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Erro ao importar: {ex.Message}";
        }
    }

    private async void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        var saveFileDialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "JSON Profile (*.json)|*.json",
            FileName = "provisionamento.json",
            Title = "Salvar Perfil de Provisionamento"
        };

        if (saveFileDialog.ShowDialog() != true) return;

        try
        {
            // Respeita o nome que o usuário já digitou em "Nome do Perfil" — só cai pro nome do
            // arquivo escolhido se o campo estiver vazio (perfil ainda sem nome próprio).
            var manifest = BuildManifestFromUi();
            manifest.Name ??= System.IO.Path.GetFileNameWithoutExtension(saveFileDialog.FileName);
            await _provisioningService.ExportAsync(manifest, saveFileDialog.FileName);

            // Exportar também marca como "atual" — sem isso, editar os campos aqui e
            // exportar nunca aparecia no backup/sincronização automática (só "Aplicar
            // agora" atualizava esse estado antes desta correção).
            _provisioningService.SetCurrent(manifest);

            StatusText.Text = $"Perfil exportado com sucesso em '{saveFileDialog.FileName}'. Também marcado como estado atual — já entra no próximo backup/sincronização.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Erro ao exportar: {ex.Message}";
        }
    }

    private async void ApplyButton_Click(object sender, RoutedEventArgs e)
    {
        ApplyButton.IsEnabled = false;
        StatusText.Text = "Aplicando ajustes...";

        try
        {
            var manifest = BuildManifestFromUi();
            var result = await _provisioningService.ApplyAsync(manifest);

            if (result.Steps.Count == 0)
            {
                StatusText.Text = "Nenhum ajuste selecionado — escolha ao menos uma opção diferente de \"Não alterar\".";
                return;
            }

            var summary = new StringBuilder();
            foreach (var step in result.Steps)
            {
                summary.AppendLine($"{(step.Success ? "✔" : "✘")} {step.Setting}: {step.Message}");
            }

            if (result.RestartRequired)
            {
                summary.AppendLine();
                summary.Append("Reinicie o Windows para que todos os ajustes tenham efeito.");
            }

            StatusText.Text = summary.ToString().TrimEnd();

            if (manifest.MachineName is not null)
            {
                CurrentMachineNameText.Text = $"Nome atual: {Environment.MachineName} (nome pendente: {manifest.MachineName})";
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Erro ao aplicar: {ex.Message}";
        }
        finally
        {
            ApplyButton.IsEnabled = true;
        }
    }
}
