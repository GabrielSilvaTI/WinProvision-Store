using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.DependencyInjection;
using WinProvision.Core.Models;
using WinProvision.Core.Models.Provisioning;
using WinProvision.Core.Services;
using WinProvision.Core.Services.Backup;
using WinProvision.Core.Services.Provisioning;
using WinProvision.Store.Converters;

namespace WinProvision.Store;

public partial class ProvisioningPage : Page
{
    private const string DefaultBootstrapScriptUrl = "https://pub-166b41912a994dbe86583ba10596d673.r2.dev/Store/WinProvision_Store_Bootstrap.ps1";
    private const string StableExecutableUrl = "https://github.com/GabrielSilvaTI/WinProvision-Store/releases/latest/download/WinProvision.Store.exe";
    private readonly ProvisioningService _provisioningService;
    private readonly CliPresetsService _cliPresetsService;
    private readonly GitHubBackupService _backupService;
    private readonly ScheduledTempCleanerService _scheduledTempCleanerService;
    private readonly PackageCollectionService _packageCollectionService;
    private readonly IconService _iconService;

    // Guardados à parte (em vez de num controle de UI) porque o wallpaper é um arquivo, não um
    // valor editável — ficam aqui até o usuário exportar ou aplicar, e são preenchidos de volta
    // ao importar um perfil que já tenha wallpaper embutido.
    private string? _wallpaperFileName;
    private string? _wallpaperImageBase64;

    // Evita empurrar estado pro serviço enquanto LoadManifestIntoUi está preenchendo os
    // controles programaticamente (cada SelectionChanged/TextChanged disparado durante a
    // carga geraria um push com o manifesto ainda pela metade) — só falso durante a carga.
    private bool _uiLoaded;

    // Manipulação de ícones e menu de contexto da simulação do Desktop
    private bool _showDesktopIcons = true;
    private bool _autoArrange = false;
    private bool _alignToGrid = true;
    private Border? _draggedElement;
    private Point _dragStartMouse;
    private double _dragStartLeft;
    private double _dragStartTop;
    private bool _isDragging;
    private Border? _selectedIcon;
    
    // Gerenciamento de ícones adicionados pelo usuário (app shortcuts)
    private readonly List<Border> _userAddedDesktopIcons = new();

    // ComboBoxes que devem ter rolagem de rodinha nativa no dropdown (Popup/HWND separada)
    private readonly List<ComboBox> _wheelAwareComboBoxes = new();

    public ProvisioningPage()
    {
        InitializeComponent();

        _provisioningService = App.Services.GetRequiredService<ProvisioningService>();
        _cliPresetsService = App.Services.GetRequiredService<CliPresetsService>();
        _backupService = App.Services.GetRequiredService<GitHubBackupService>();
        _scheduledTempCleanerService = App.Services.GetRequiredService<ScheduledTempCleanerService>();
        _packageCollectionService = App.Services.GetRequiredService<PackageCollectionService>();
        _iconService = App.Services.GetRequiredService<IconService>();
        BootstrapScriptUrlTextBox.Text = DefaultBootstrapScriptUrl;

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

        // Preenche o gerador de CLI com o perfil/webhook salvos em Configurações → Conta,
        // se os campos ainda estiverem vazios (nunca sobrescreve o que o usuário já digitou
        // nesta sessão). Também escuta mudanças salvas em Configurações enquanto esta
        // página (Singleton) já está aberta.
        ApplyCliPresetsIfEmpty();
        _cliPresetsService.Changed += () => Dispatcher.BeginInvoke(ApplyCliPresetsIfEmpty);

        _wheelAwareComboBoxes.Add(PowerPlanComboBox);
        _wheelAwareComboBoxes.Add(DisplayTimeoutAcComboBox);
        _wheelAwareComboBoxes.Add(StandbyTimeoutAcComboBox);
        InputManager.Current.PostProcessInput += GlobalPostProcessInput;
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
    private void ShowSection(StackPanel sectionPanel, string title)
    {
        PersonalizationSectionPanel.Visibility = Visibility.Collapsed;
        AdvancedSectionPanel.Visibility = Visibility.Collapsed;
        JsonSectionPanel.Visibility = Visibility.Collapsed;
        CliSectionPanel.Visibility = Visibility.Collapsed;
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

    private void CliNavCard_Click(object sender, RoutedEventArgs e)
    {
        ShowSection(CliSectionPanel, "Gerador de Comando CLI Nativo");
        UpdateCliCommandPreview();
    }

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
        bool advancedSet = !string.IsNullOrWhiteSpace(manifest.MachineName)
            || !string.IsNullOrWhiteSpace(manifest.Region)
            || (manifest.PowerPlan is { } power && power != PowerPlanMode.NaoDefinido)
            || manifest.DisplayTimeoutOnAc is not null
            || manifest.DisplayTimeoutOnDc is not null
            || manifest.StandbyTimeoutOnAc is not null
            || manifest.StandbyTimeoutOnDc is not null
            || manifest.AutoCreateRestorePoint is true
            || manifest.AutoCleanTempOnLogon is true;

        int sectionsConfigured = (personalizationSet ? 1 : 0) + (advancedSet ? 1 : 0);

        int keysModified = 0;
        if (!string.IsNullOrWhiteSpace(manifest.Name)) keysModified++;
        if (!string.IsNullOrWhiteSpace(manifest.Creator)) keysModified++;
        if (manifest.DisplayTimeoutOnAc is not null) keysModified++;
        if (manifest.DisplayTimeoutOnDc is not null) keysModified++;
        if (manifest.StandbyTimeoutOnAc is not null) keysModified++;
        if (manifest.StandbyTimeoutOnDc is not null) keysModified++;
        if (manifest.Theme is { } t && t != SystemThemeMode.NaoDefinido) keysModified++;
        if (manifest.TaskbarAlignment is { } ta && ta != TaskbarAlignmentMode.NaoDefinido) keysModified++;
        if (manifest.TaskbarSearchBox is { } ts && ts != TaskbarSearchBoxMode.NaoDefinido) keysModified++;
        if (manifest.TaskbarAutoHide is true) keysModified++;
        if (manifest.PowerPlan is { } pp && pp != PowerPlanMode.NaoDefinido) keysModified++;
        if (!string.IsNullOrWhiteSpace(manifest.MachineName)) keysModified++;
        if (!string.IsNullOrWhiteSpace(manifest.WallpaperImageBase64)) keysModified++;
        if (!string.IsNullOrWhiteSpace(manifest.Region)) keysModified++;
        if (manifest.AutoCreateRestorePoint is true) keysModified++;
        if (manifest.AutoCleanTempOnLogon is true) keysModified++;

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

    private void CliField_Changed(object sender, RoutedEventArgs e) => UpdateCliCommandPreview();

    private void BootstrapField_Changed(object sender, TextChangedEventArgs e) => UpdateBootstrapCommandPreview();

    private void UpdateBootstrapCommandPreview()
    {
        if (BootstrapCommandPreviewTextBox is null) return;
        string scriptUrl = BootstrapScriptUrlTextBox.Text.Trim();
        BootstrapCommandPreviewTextBox.Text = scriptUrl.Length == 0
            ? "powershell.exe -NoProfile -ExecutionPolicy Bypass -Command \"<URL-do-bootstrap>\""
            : $"powershell.exe -NoProfile -ExecutionPolicy Bypass -Command \"iwr -useb '{scriptUrl}' -OutFile $env:TEMP\\bootstrap.ps1; & $env:TEMP\\bootstrap.ps1\"";
    }

    private string BuildBootstrapScript()
    {
        string profileUrl = CliProfilePathTextBox.Text.Trim();
        if (!Uri.TryCreate(profileUrl, UriKind.Absolute, out var profileUri)
            || profileUri.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException("O perfil do Bootstrap precisa ser uma URL HTTP(S), como a URL raw do Gist.");
        }

        string webhook = CliWebhookCheckBox.IsChecked == true
            ? CliWebhookUrlPasswordBox.Password.Trim()
            : string.Empty;

        var script = new StringBuilder();
        script.AppendLine("$ErrorActionPreference = 'Stop'");
        script.AppendLine($"$ExeUrl = '{PowerShellLiteral(StableExecutableUrl)}'");
        script.AppendLine($"$ProfileUrl = '{PowerShellLiteral(profileUrl)}'");
        script.AppendLine($"$WebhookUrl = '{PowerShellLiteral(webhook)}'");
        script.AppendLine("$InstallDir = Join-Path $env:SystemDrive 'WinProvision'");
        script.AppendLine("$ExePath = Join-Path $InstallDir 'WinProvision.Store.exe'");
        script.AppendLine("$MaxRetries = 5");
        script.AppendLine("$RetryDelaySeconds = 5");
        script.AppendLine("[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 -bor [Net.SecurityProtocolType]::Tls13");
        script.AppendLine("Write-Host 'WinProvision Store - Bootstrap FirstLogon' -ForegroundColor Cyan");
        script.AppendLine("New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null");
        script.AppendLine("$downloaded = $false");
        script.AppendLine("Remove-Item -LiteralPath $ExePath -Force -ErrorAction SilentlyContinue");
        script.AppendLine("for ($attempt = 1; $attempt -le $MaxRetries -and -not $downloaded; $attempt++) {");
        script.AppendLine("  try {");
        script.AppendLine("    Write-Host \"Baixando EXE (tentativa $attempt/$MaxRetries)...\" -ForegroundColor Yellow");
        script.AppendLine("    if (Get-Command curl.exe -ErrorAction SilentlyContinue) {");
        script.AppendLine("      & curl.exe --fail --location --silent --show-error --retry 2 --retry-delay 2 --connect-timeout 15 --max-time 180 --output $ExePath $ExeUrl");
        script.AppendLine("      if ($LASTEXITCODE -ne 0) { throw \"curl.exe retornou o código $LASTEXITCODE\" }");
        script.AppendLine("    } else {");
        script.AppendLine("      Invoke-WebRequest -Uri $ExeUrl -OutFile $ExePath -UseBasicParsing -TimeoutSec 180");
        script.AppendLine("    }");
        script.AppendLine("    $downloaded = (Test-Path $ExePath) -and ((Get-Item $ExePath).Length -gt 0)");
        script.AppendLine("  } catch {");
        script.AppendLine("    if ($attempt -lt $MaxRetries) { Start-Sleep -Seconds $RetryDelaySeconds } else { throw }");
        script.AppendLine("  }");
        script.AppendLine("}");
        script.AppendLine("Unblock-File -Path $ExePath -ErrorAction SilentlyContinue");
        script.AppendLine("$arguments = @('/auto', $ProfileUrl)");
        script.AppendLine("if ($WebhookUrl) { $arguments += @('/webhook', $WebhookUrl) }");
        script.AppendLine("$process = Start-Process -FilePath $ExePath -ArgumentList $arguments -Wait -PassThru -NoNewWindow");
        script.AppendLine("exit $process.ExitCode");
        return script.ToString();
    }

    private static string PowerShellLiteral(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    private async void PublishBootstrapButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string script = BuildBootstrapScript();
            string? url = await _backupService.PublishBootstrapAsync(script);
            if (url is null)
            {
                StatusText.Text = _backupService.IsConnected
                    ? "Não foi possível publicar o Bootstrap no Gist."
                    : "Vincule sua conta GitHub em Configurações → Conta antes de publicar no Gist.";
                return;
            }

            BootstrapScriptUrlTextBox.Text = url;
            StatusText.Text = "Bootstrap publicado no Gist secreto. A URL permanece estável nas próximas atualizações.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Não foi possível gerar o Bootstrap: {ex.Message}";
        }
    }

    private void SaveBootstrapButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "PowerShell (*.ps1)|*.ps1",
                FileName = "WinProvision_Store_Bootstrap.ps1",
                Title = "Salvar Bootstrap FirstLogon"
            };
            if (dialog.ShowDialog() != true) return;
            File.WriteAllText(dialog.FileName, BuildBootstrapScript(), new UTF8Encoding(false));
            StatusText.Text = "Bootstrap PowerShell salvo com sucesso.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Não foi possível salvar o Bootstrap: {ex.Message}";
        }
    }

    private void CopyBootstrapCommandButton_Click(object sender, RoutedEventArgs e)
    {
        UpdateBootstrapCommandPreview();
        Clipboard.SetText(BootstrapCommandPreviewTextBox.Text);
        StatusText.Text = "Comando CMD de FirstLogon copiado.";
    }

    /// <summary>Remonta o comando de terminal (Row "Comando") a partir do caminho/URL do perfil
    /// e, se marcadas, das flags /log e /webhook — mesmo formato lido por App.xaml.cs. Sempre
    /// "/auto": é o único modo de CLI do app (aplica apps/Office/provisionamento — o que
    /// estiver presente no JSON), então não há mais um seletor de modo pra ler aqui.</summary>
    private void UpdateCliCommandPreview()
    {
        if (CliCommandPreviewTextBox is null) return;

        string path = CliProfilePathTextBox.Text.Trim();
        if (path.Length == 0)
        {
            path = "<caminho-ou-URL-do-perfil.json>";
        }

        var command = new StringBuilder(".\\WinProvision.Store.exe /auto \"")
            .Append(path).Append('"');

        if (CliSilentRadioButton?.IsChecked == true)
        {
            command.Append(" /silent");
        }

        if (CliLogCheckBox.IsChecked == true)
        {
            string logPath = CliLogPathTextBox.Text.Trim();
            if (logPath.Length > 0)
            {
                command.Append(" /log \"").Append(logPath).Append('"');
            }
        }

        if (CliWebhookCheckBox.IsChecked == true)
        {
            string webhookUrl = CliWebhookUrlPasswordBox.Password.Trim();
            if (webhookUrl.Length > 0)
            {
                command.Append(" /webhook \"").Append(webhookUrl).Append('"');
            }
        }

        CliCommandPreviewTextBox.Text = command.ToString();
    }

    private void CopyCliCommandButton_Click(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText(CliCommandPreviewTextBox.Text);
        StatusText.Text = "Comando copiado para a área de transferência.";
    }

    /// <summary>
    /// Preenche "Caminho ou URL do perfil .json" com a URL "raw" do Gist de backup automático
    /// da conta conectada (Configurações → Conta) — a mesma sincronização quase em tempo real
    /// (guias de Pacotes + provisionamento) que já roda sozinha a cada instalação/remoção. Não
    /// digita nada por conta própria: só busca o link e deixa a pré-visualização do comando
    /// (<see cref="UpdateCliCommandPreview"/>, disparado pelo TextChanged) atualizar sozinha.
    /// </summary>
    private void SyncGistUrlButton_Click(object sender, RoutedEventArgs e)
    {
        string? url = _backupService.BackupRawUrl;

        if (url is null)
        {
            StatusText.Text = _backupService.IsConnected
                ? "Ainda não há um backup salvo no Gist desta conta — sincronize ao menos uma vez (Configurações → Backup → \"Sincronizar agora\", ou instale/remova algo) e tente de novo."
                : "Vincule sua conta GitHub em Configurações → Conta para usar o Gist de backup automático aqui.";
            return;
        }

        CliProfilePathTextBox.Text = url;
        StatusText.Text = "Preenchido com a URL do Gist de backup automático (guias de Pacotes + provisionamento sincronizados quase em tempo real).";
    }

    /// <summary>Preenche o caminho/URL do perfil e a URL do webhook a partir do que está salvo
    /// em Configurações → Conta — só nos campos que estiverem vazios agora, pra nunca
    /// sobrescrever o que o usuário já digitou nesta sessão.</summary>
    private void ApplyCliPresetsIfEmpty()
    {
        if (CliProfilePathTextBox.Text.Trim().Length == 0 && _cliPresetsService.ProfilePathOrUrl is { Length: > 0 } path)
        {
            CliProfilePathTextBox.Text = path;
        }

        if (CliWebhookUrlPasswordBox.Password.Trim().Length == 0 && _cliPresetsService.WebhookUrl is { Length: > 0 } webhook)
        {
            CliWebhookCheckBox.IsChecked = true;
            CliWebhookUrlPasswordBox.Password = webhook;
        }
    }

    /// <summary>Lê o caminho/URL do perfil e a URL do webhook preenchidos agora no gerador de
    /// CLI — usado por Configurações → Conta ("Usar valores da tela de Provisionamento") pra
    /// copiar sem precisar digitar de novo. Retorna null em cada posição vazia.</summary>
    internal (string? ProfilePathOrUrl, string? WebhookUrl) GetCurrentCliFieldValues()
    {
        string path = CliProfilePathTextBox.Text.Trim();
        string webhook = CliWebhookCheckBox.IsChecked == true ? CliWebhookUrlPasswordBox.Password.Trim() : string.Empty;

        return (path.Length > 0 ? path : null, webhook.Length > 0 ? webhook : null);
    }

    /// <summary>Salva o que está preenchido agora como padrão (ver <see cref="CliPresetsService"/>)
    /// — some volta a aparecer sozinho da próxima vez, tanto aqui quanto em Configurações.</summary>
    private void SaveCliDefaultsButton_Click(object sender, RoutedEventArgs e)
    {
        var (profilePathOrUrl, webhookUrl) = GetCurrentCliFieldValues();

        _cliPresetsService.SaveProfilePathOrUrl(profilePathOrUrl);

        if (webhookUrl is not null)
        {
            _cliPresetsService.SaveWebhookUrl(webhookUrl);
        }

        StatusText.Text = "Perfil/webhook salvos como padrão — vão preencher automaticamente da próxima vez.";
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
            && manifest.AutoCleanTempOnLogon is not true
            && manifest.DesktopIconLayout is null or { Count: 0 };

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
            changes.Add($"Nome do Perfil: {ProfileNameTextBox.Text.Trim()}");

        if (!string.IsNullOrWhiteSpace(ProfileCreatorTextBox.Text))
            changes.Add($"Criador: {ProfileCreatorTextBox.Text.Trim()}");

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

        if (_userAddedDesktopIcons.Count > 0)
            changes.Add($"Ícones da área de trabalho: {_userAddedDesktopIcons.Count} atalho(s) posicionado(s)");

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
        DesktopIconLayout = BuildDesktopIconLayout(),
    };

    /// <summary>
    /// Converte a posição atual (em pixel, no Canvas de simulação) de cada ícone arrastado
    /// pelo usuário pra Coluna/Linha — usando o mesmo espaçamento de grade (72×90, origem em
    /// 16,16) já usado pelo snap em <see cref="DesktopIcon_MouseLeftButtonUp"/> e
    /// <see cref="CreateDesktopIcon"/>, mesmo que "Alinhar à grade" esteja desligado (nesse
    /// caso o resultado é só um arredondamento pra célula mais próxima — ainda assim melhor
    /// que gravar pixels, que não fariam sentido nenhum na resolução da máquina-alvo).
    /// </summary>
    private List<DesktopIconPlacement>? BuildDesktopIconLayout()
    {
        if (_userAddedDesktopIcons.Count == 0) return null;

        var layout = new List<DesktopIconPlacement>();
        foreach (var icon in _userAddedDesktopIcons)
        {
            if (icon.Tag is not AppEntry app) continue;

            double left = Canvas.GetLeft(icon);
            double top = Canvas.GetTop(icon);
            if (double.IsNaN(left)) left = 16;
            if (double.IsNaN(top)) top = 16;

            int column = Math.Max(0, (int)Math.Round((left - 16) / 72.0));
            int row = Math.Max(0, (int)Math.Round((top - 16) / 90.0));

            layout.Add(new DesktopIconPlacement
            {
                AppId = app.Id,
                DisplayName = app.Name,
                Column = column,
                Row = row,
            });
        }

        return layout;
    }

    /// <summary>
    /// Reconstrói os ícones de usuário na simulação a partir de um perfil importado/carregado —
    /// resolve cada <see cref="DesktopIconPlacement"/> pra um <see cref="AppEntry"/> real (pelo
    /// Id do winget, com fallback pelo nome) em qualquer coleção/aba do usuário; caso não exista
    /// mais na coleção, cria um AppEntry temporário para assegurar a visualização exata de todos os ícones.
    /// </summary>
    private void ApplyDesktopIconLayoutToUi(List<DesktopIconPlacement>? layout)
    {
        if (DesktopIconsGroup is null) return;

        foreach (var icon in _userAddedDesktopIcons)
        {
            DesktopIconsGroup.Children.Remove(icon);
        }
        _userAddedDesktopIcons.Clear();

        if (layout is null || layout.Count == 0) return;

        var allApps = _packageCollectionService.Tabs.SelectMany(c => c.Items).ToList();

        foreach (var placement in layout)
        {
            var app = allApps.FirstOrDefault(a => a.Id.Equals(placement.AppId, StringComparison.OrdinalIgnoreCase))
                ?? allApps.FirstOrDefault(a => a.Name.Equals(placement.DisplayName, StringComparison.OrdinalIgnoreCase))
                ?? new AppEntry { Id = placement.AppId, Name = placement.DisplayName };

            double x = placement.Column * 72.0 + 16;
            double y = placement.Row * 90.0 + 16;

            var icon = CreateDesktopIcon(app, x, y);
            DesktopIconsGroup.Children.Add(icon);
            _userAddedDesktopIcons.Add(icon);
        }
    }

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
        ApplyDesktopIconLayoutToUi(manifest.DesktopIconLayout);

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
        catch (Exception ex)
        {
            StatusText.Text = $"Erro ao carregar imagem: {ex.Message}";
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
        
        // Also refresh the package shelf when in the personalization section
        if (ProfileOverviewPanel.Visibility == Visibility.Collapsed && PersonalizationSectionPanel.Visibility == Visibility.Visible)
        {
            RefreshPackagesShelfButton_Click(this, new RoutedEventArgs());
        }

        EnsureDefaultWallpapersLoaded();

        var selectedTheme = GetSelectedEnum<SystemThemeMode>(ThemeComboBox);
        var selectedAlign = GetSelectedEnum<TaskbarAlignmentMode>(TaskbarAlignmentComboBox);
        var selectedSearch = GetSelectedEnum<TaskbarSearchBoxMode>(TaskbarSearchBoxComboBox);
        bool autoHide = TaskbarAutoHideCheckBox.IsChecked is true;

        // 1. Wallpaper
        if (WallpaperPreviewImage?.Source is BitmapImage customBmp && !string.IsNullOrWhiteSpace(_wallpaperImageBase64))
        {
            DesktopPreviewWallpaper.Source = customBmp;
            if (DesktopPreviewThemeTag is not null) DesktopPreviewThemeTag.Text = "Wallpaper Personalizado";
            if (ClearWallpaperButton is not null) ClearWallpaperButton.Visibility = Visibility.Visible;
        }
        else
        {
            if (ClearWallpaperButton is not null) ClearWallpaperButton.Visibility = Visibility.Collapsed;
            if (selectedTheme == SystemThemeMode.Claro)
            {
                DesktopPreviewWallpaper.Source = _cachedLightWallpaper ?? _cachedDarkWallpaper;
                DesktopPreviewThemeTag.Text = "Tema Claro (Windows 11)";
            }
            else
            {
                // Escuro ou Não alterar (padrão escuro moderno do Windows 11)
                DesktopPreviewWallpaper.Source = _cachedDarkWallpaper ?? _cachedLightWallpaper;
                DesktopPreviewThemeTag.Text = selectedTheme == SystemThemeMode.Escuro
                    ? "Tema Escuro (Windows 11)"
                    : "Tema do Sistema (Padrão)";
            }
        }

        // 2. Cores da Barra de Tarefas (Tema Claro vs. Tema Escuro)
        bool isLight = selectedTheme == SystemThemeMode.Claro;
        if (isLight)
        {
            DesktopPreviewTaskbar.Background = new SolidColorBrush(Color.FromArgb(0xEA, 0xF3, 0xF4, 0xF6));
            DesktopPreviewTaskbar.BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 0x00, 0x00, 0x00));
            var darkTextBrush = new SolidColorBrush(Color.FromRgb(0x1E, 0x29, 0x3B));
            var darkSecBrush = new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B));

            DesktopPreviewClockText.Foreground = darkTextBrush;
            DesktopPreviewDateText.Foreground = darkSecBrush;
            DesktopPreviewSearchText.Foreground = darkSecBrush;
            DesktopPreviewSearchIcon.Foreground = darkSecBrush;
            DesktopPreviewSearchIconOnly.Foreground = darkTextBrush;
            DesktopPreviewTrayChevron.Foreground = darkSecBrush;
            DesktopPreviewTrayWifi.Foreground = darkSecBrush;
            DesktopPreviewTraySpeaker.Foreground = darkSecBrush;
            DesktopPreviewTrayLang1.Foreground = darkTextBrush;
            DesktopPreviewTrayLang2.Foreground = darkSecBrush;
            DesktopPreviewSearchBoxFull.Background = new SolidColorBrush(Color.FromArgb(0xF0, 0xFF, 0xFF, 0xFF));
            DesktopPreviewSearchBoxFull.BorderBrush = new SolidColorBrush(Color.FromArgb(0x25, 0x00, 0x00, 0x00));
        }
        else
        {
            DesktopPreviewTaskbar.Background = new SolidColorBrush(Color.FromArgb(0xEA, 0x1C, 0x1C, 0x1C));
            DesktopPreviewTaskbar.BorderBrush = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
            var lightTextBrush = new SolidColorBrush(Color.FromRgb(0xF8, 0xFA, 0xFC));
            var lightSecBrush = new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8));

            DesktopPreviewClockText.Foreground = lightTextBrush;
            DesktopPreviewDateText.Foreground = lightSecBrush;
            DesktopPreviewSearchText.Foreground = lightSecBrush;
            DesktopPreviewSearchIcon.Foreground = lightSecBrush;
            DesktopPreviewSearchIconOnly.Foreground = lightTextBrush;
            DesktopPreviewTrayChevron.Foreground = lightSecBrush;
            DesktopPreviewTrayWifi.Foreground = lightSecBrush;
            DesktopPreviewTraySpeaker.Foreground = lightSecBrush;
            DesktopPreviewTrayLang1.Foreground = lightTextBrush;
            DesktopPreviewTrayLang2.Foreground = lightSecBrush;
            DesktopPreviewSearchBoxFull.Background = new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));
            DesktopPreviewSearchBoxFull.BorderBrush = new SolidColorBrush(Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF));
        }

        // 3. Alinhamento da Barra de Tarefas
        if (selectedAlign == TaskbarAlignmentMode.Esquerda)
        {
            DesktopPreviewTaskbarIcons.HorizontalAlignment = HorizontalAlignment.Left;
        }
        else
        {
            // Centro ou Não alterar (padrão centralizado do Windows 11)
            DesktopPreviewTaskbarIcons.HorizontalAlignment = HorizontalAlignment.Center;
        }

        // 4. Caixa de Pesquisa
        switch (selectedSearch)
        {
            case TaskbarSearchBoxMode.Oculta:
                DesktopPreviewSearchBoxFull.Visibility = Visibility.Collapsed;
                DesktopPreviewSearchBoxIcon.Visibility = Visibility.Collapsed;
                break;
            case TaskbarSearchBoxMode.ApenasIcone:
                DesktopPreviewSearchBoxFull.Visibility = Visibility.Collapsed;
                DesktopPreviewSearchBoxIcon.Visibility = Visibility.Visible;
                break;
            case TaskbarSearchBoxMode.CaixaCompleta:
            default:
                DesktopPreviewSearchBoxFull.Visibility = Visibility.Visible;
                DesktopPreviewSearchBoxIcon.Visibility = Visibility.Collapsed;
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

        // 7. Menu de Contexto (Cores do Tema)
        if (DesktopContextMenuMain != null)
        {
            DesktopContextMenuMain.Background = new SolidColorBrush(isLight ? Color.FromArgb(0xF4, 0xF9, 0xF9, 0xF9) : Color.FromArgb(0xF4, 0x20, 0x20, 0x20));
            DesktopContextMenuMain.BorderBrush = new SolidColorBrush(isLight ? Color.FromArgb(0x25, 0x00, 0x00, 0x00) : Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
        }
        if (DesktopContextMenuSubExibir != null)
        {
            DesktopContextMenuSubExibir.Background = new SolidColorBrush(isLight ? Color.FromArgb(0xF4, 0xF9, 0xF9, 0xF9) : Color.FromArgb(0xF4, 0x20, 0x20, 0x20));
            DesktopContextMenuSubExibir.BorderBrush = new SolidColorBrush(isLight ? Color.FromArgb(0x25, 0x00, 0x00, 0x00) : Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
        }
    }

    #region Desktop Icon Dragging & Context Menu Interactivity

    private void SelectDesktopIcon(Border icon)
    {
        DeselectAllDesktopIcons();
        _selectedIcon = icon;
        icon.Background = new SolidColorBrush(Color.FromArgb(0x33, 0x00, 0x78, 0xD4));
        icon.BorderBrush = new SolidColorBrush(Color.FromArgb(0x80, 0x00, 0x78, 0xD4));
        icon.BorderThickness = new Thickness(1);
        UpdateContextMenuForSelection();
    }

    private void DeselectAllDesktopIcons()
    {
        _selectedIcon = null;
        if (DesktopIconRecycleBin != null)
        {
            DesktopIconRecycleBin.Background = Brushes.Transparent;
            DesktopIconRecycleBin.BorderBrush = Brushes.Transparent;
            DesktopIconRecycleBin.BorderThickness = new Thickness(0);
        }
        if (DesktopIconEdge != null)
        {
            DesktopIconEdge.Background = Brushes.Transparent;
            DesktopIconEdge.BorderBrush = Brushes.Transparent;
            DesktopIconEdge.BorderThickness = new Thickness(0);
        }
        
        // Deselect user-added icons
        foreach (var icon in _userAddedDesktopIcons)
        {
            icon.Background = Brushes.Transparent;
            icon.BorderBrush = Brushes.Transparent;
            icon.BorderThickness = new Thickness(0);
        }
        
        UpdateContextMenuForSelection();
    }

    private void DesktopIcon_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border border)
        {
            CloseContextMenu();
            SelectDesktopIcon(border);
            _draggedElement = border;
            _dragStartMouse = e.GetPosition(DesktopIconsCanvas);
            _dragStartLeft = Canvas.GetLeft(border);
            _dragStartTop = Canvas.GetTop(border);
            if (double.IsNaN(_dragStartLeft)) _dragStartLeft = 16;
            if (double.IsNaN(_dragStartTop)) _dragStartTop = 16;
            _isDragging = false;
            border.CaptureMouse();
            e.Handled = true;
        }
    }

    private void DesktopIcon_MouseMove(object sender, MouseEventArgs e)
    {
        if (_draggedElement != null)
        {
            Point curMouse = e.GetPosition(DesktopIconsCanvas);
            Vector delta = curMouse - _dragStartMouse;
            if (Math.Abs(delta.X) > 3 || Math.Abs(delta.Y) > 3)
            {
                _isDragging = true;
            }

            if (_isDragging)
            {
                double newLeft = _dragStartLeft + delta.X;
                double newTop = _dragStartTop + delta.Y;

                newLeft = Math.Clamp(newLeft, 8, 1280 - _draggedElement.ActualWidth - 8);
                newTop = Math.Clamp(newTop, 8, 672 - _draggedElement.ActualHeight - 8);

                Canvas.SetLeft(_draggedElement, newLeft);
                Canvas.SetTop(_draggedElement, newTop);
                e.Handled = true;
            }
        }
    }

    private void DesktopIcon_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_draggedElement != null)
        {
            _draggedElement.ReleaseMouseCapture();

            if (_autoArrange)
            {
                AutoArrangeIcons();
            }
            else if (_alignToGrid)
            {
                double curLeft = Canvas.GetLeft(_draggedElement);
                double curTop = Canvas.GetTop(_draggedElement);
                // Espaçamento de grade: 72px horizontal, 90px vertical
                double snappedLeft = Math.Round((curLeft - 16) / 72.0) * 72.0 + 16;
                double snappedTop = Math.Round((curTop - 16) / 90.0) * 90.0 + 16;

                snappedLeft = Math.Clamp(snappedLeft, 16, 1200);
                snappedTop = Math.Clamp(snappedTop, 16, 560);

                Canvas.SetLeft(_draggedElement, snappedLeft);
                Canvas.SetTop(_draggedElement, snappedTop);
            }

            var element = _draggedElement;
            _isDragging = false;
            _draggedElement = null;
            e.Handled = true;

            // Só um app de usuário (com AppEntry no Tag) entra no layout salvo — ícones de
            // sistema (Lixeira, Edge) são só decoração da simulação, não viajam no perfil.
            if (element is Border { Tag: AppEntry } && !_autoArrange)
            {
                PushCurrentToService();
            }
        }
    }

    private void DesktopIcon_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border border)
        {
            SelectDesktopIcon(border);
            Point pt = e.GetPosition(DesktopMenuOverlay);
            ShowContextMenu(pt);
            e.Handled = true;
        }
    }

    private void DesktopCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        DeselectAllDesktopIcons();
        CloseContextMenu();
    }

    private void DesktopCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (_draggedElement != null && _isDragging)
        {
            DesktopIcon_MouseMove(_draggedElement, e);
        }
    }

    private void DesktopCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_draggedElement != null)
        {
            DesktopIcon_MouseLeftButtonUp(_draggedElement, e);
        }
    }

    private void DesktopCanvas_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        DeselectAllDesktopIcons();
        Point pt = e.GetPosition(DesktopMenuOverlay);
        ShowContextMenu(pt);
        e.Handled = true;
    }

    private void ShowContextMenu(Point pt)
    {
        if (DesktopMenuOverlay == null || DesktopContextMenuMain == null) return;

        double left = pt.X;
        double top = pt.Y;

        if (left + 245 > 1280) left = 1280 - 250;
        if (top + 260 > 672) top = 672 - 265;
        if (left < 0) left = 0;
        if (top < 0) top = 0;

        Canvas.SetLeft(DesktopContextMenuMain, left);
        Canvas.SetTop(DesktopContextMenuMain, top);

        if (DesktopContextMenuSubExibir != null)
        {
            DesktopContextMenuSubExibir.Visibility = Visibility.Collapsed;
        }

        DesktopMenuOverlay.Visibility = Visibility.Visible;
    }

    private void CloseContextMenu()
    {
        if (DesktopMenuOverlay != null)
        {
            DesktopMenuOverlay.Visibility = Visibility.Collapsed;
        }
        if (DesktopContextMenuSubExibir != null)
        {
            DesktopContextMenuSubExibir.Visibility = Visibility.Collapsed;
        }
    }

    private void DesktopMenuOverlay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        CloseContextMenu();
    }

    private void DesktopMenuOverlay_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        CloseContextMenu();
    }

    private void MenuItemExibir_MouseEnter(object sender, MouseEventArgs e)
    {
        OpenSubmenuExibir();
    }

    private void MenuItemExibir_MouseLeave(object sender, MouseEventArgs e)
    {
    }

    private void MenuItemExibir_Click(object sender, MouseButtonEventArgs e)
    {
        OpenSubmenuExibir();
        e.Handled = true;
    }

    private void OpenSubmenuExibir()
    {
        if (DesktopContextMenuSubExibir == null || DesktopContextMenuMain == null) return;

        double mainLeft = Canvas.GetLeft(DesktopContextMenuMain);
        double mainTop = Canvas.GetTop(DesktopContextMenuMain);

        double subLeft = mainLeft + 238;
        double subTop = mainTop + 4;

        if (subLeft + 260 > 1280)
        {
            subLeft = mainLeft - 260;
        }
        if (subTop + 240 > 672)
        {
            subTop = Math.Max(0, 672 - 245);
        }

        Canvas.SetLeft(DesktopContextMenuSubExibir, subLeft);
        Canvas.SetTop(DesktopContextMenuSubExibir, subTop);
        DesktopContextMenuSubExibir.Visibility = Visibility.Visible;
    }

    private void MenuItemToggleDesktopIcons_Click(object sender, MouseButtonEventArgs e)
    {
        _showDesktopIcons = !_showDesktopIcons;
        if (MenuCheckShowDesktopIcons != null)
        {
            MenuCheckShowDesktopIcons.Visibility = _showDesktopIcons ? Visibility.Visible : Visibility.Collapsed;
        }
        if (DesktopIconsGroup != null)
        {
            DesktopIconsGroup.Visibility = _showDesktopIcons ? Visibility.Visible : Visibility.Collapsed;
        }
        CloseContextMenu();
        e.Handled = true;
    }

    private void MenuIconSize_Large_Click(object sender, MouseButtonEventArgs e)
    {
        SetIconSize(56, 92, 98);
        SetSizeChecks(true, false, false);
        CloseContextMenu();
        e.Handled = true;
    }

    private void MenuIconSize_Medium_Click(object sender, MouseButtonEventArgs e)
    {
        SetIconSize(44, 76, 82);
        SetSizeChecks(false, true, false);
        CloseContextMenu();
        e.Handled = true;
    }

    private void MenuIconSize_Small_Click(object sender, MouseButtonEventArgs e)
    {
        SetIconSize(32, 64, 70);
        SetSizeChecks(false, false, true);
        CloseContextMenu();
        e.Handled = true;
    }

    private void SetIconSize(double iconSize, double cellWidth, double cellHeight)
    {
        // Update default icons
        if (ImageRecycleBin != null) { ImageRecycleBin.Width = iconSize; ImageRecycleBin.Height = iconSize; }
        if (ImageEdge != null) { ImageEdge.Width = iconSize; ImageEdge.Height = iconSize; }
        if (DesktopIconRecycleBin != null) { DesktopIconRecycleBin.Width = cellWidth; DesktopIconRecycleBin.Height = cellHeight; }
        if (DesktopIconEdge != null) { DesktopIconEdge.Width = cellWidth; DesktopIconEdge.Height = cellHeight; }
        
        // Update user-added icons
        foreach (var icon in _userAddedDesktopIcons)
        {
            icon.Width = cellWidth;
            icon.Height = cellHeight;
            
            // Find the Image control within the icon and update its size
            if (icon.Child is StackPanel stackPanel && stackPanel.Children.Count > 0)
            {
                if (stackPanel.Children[0] is Image appIcon)
                {
                    appIcon.Width = iconSize;
                    appIcon.Height = iconSize;
                }
            }
        }
    }

    private void SetSizeChecks(bool large, bool medium, bool small)
    {
        if (MenuCheckLarge != null) MenuCheckLarge.Visibility = large ? Visibility.Visible : Visibility.Collapsed;
        if (MenuCheckMedium != null) MenuCheckMedium.Visibility = medium ? Visibility.Visible : Visibility.Collapsed;
        if (MenuCheckSmall != null) MenuCheckSmall.Visibility = small ? Visibility.Visible : Visibility.Collapsed;
    }

    private void MenuAutoArrange_Click(object sender, MouseButtonEventArgs e)
    {
        _autoArrange = !_autoArrange;
        if (MenuCheckAutoArrange != null)
        {
            MenuCheckAutoArrange.Visibility = _autoArrange ? Visibility.Visible : Visibility.Collapsed;
        }
        if (_autoArrange)
        {
            AutoArrangeIcons();
        }
        CloseContextMenu();
        e.Handled = true;
    }

    private void AutoArrangeIcons()
    {
        // Espaçamento de grade nativo: 72px horizontal, 90px vertical
        if (DesktopIconRecycleBin != null)
        {
            Canvas.SetLeft(DesktopIconRecycleBin, 16);
            Canvas.SetTop(DesktopIconRecycleBin, 16);
        }
        if (DesktopIconEdge != null)
        {
            Canvas.SetLeft(DesktopIconEdge, 16);
            Canvas.SetTop(DesktopIconEdge, 106); // 16 + 90 = 106
        }

        // Organiza TODOS os ícones adicionados pelo usuário na grade sequencial
        const int maxRowsPerColumn = 6; // 6 slots verticais por coluna no espaço útil acima da barra de tarefas
        for (int i = 0; i < _userAddedDesktopIcons.Count; i++)
        {
            var icon = _userAddedDesktopIcons[i];
            int slot = 2 + i; // slots 0 e 1 são ocupados pela Lixeira e pelo Edge
            int col = slot / maxRowsPerColumn;
            int row = slot % maxRowsPerColumn;

            double x = col * 72.0 + 16;
            double y = row * 90.0 + 16;

            Canvas.SetLeft(icon, x);
            Canvas.SetTop(icon, y);
        }

        PushCurrentToService();
    }

    private void MenuAlignToGrid_Click(object sender, MouseButtonEventArgs e)
    {
        _alignToGrid = !_alignToGrid;
        if (MenuCheckAlignToGrid != null)
        {
            MenuCheckAlignToGrid.Visibility = _alignToGrid ? Visibility.Visible : Visibility.Collapsed;
        }
        CloseContextMenu();
        e.Handled = true;
    }

    private void MenuItemAtualizar_Click(object sender, MouseButtonEventArgs e)
    {
        UpdateDesktopPreview();
        CloseContextMenu();
        e.Handled = true;
    }

    #endregion

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

    private async Task RefreshCleanTempTaskStatusAsync()
    {
        try
        {
            bool isEnabled = await _scheduledTempCleanerService.IsEnabledAsync();
            CleanTempTaskStatusText.Text = isEnabled
                ? "Status no sistema: Tarefa agendada (ativa a cada Logon)."
                : "Status no sistema: Tarefa não agendada.";
            ScheduleCleanTempNowButton.Content = isEnabled ? "Reagendar Tarefa" : "Agendar Tarefa Agora";
        }
        catch
        {
            CleanTempTaskStatusText.Text = "Status no sistema: Não foi possível verificar.";
        }
    }

    private async void ScheduleCleanTempNowButton_Click(object sender, RoutedEventArgs e)
    {
        ScheduleCleanTempNowButton.IsEnabled = false;
        StatusText.Text = "Agendando tarefa de limpeza de arquivos temporários no Logon…";

        try
        {
            var result = await _scheduledTempCleanerService.EnableAsync();
            if (result.Success)
            {
                StatusText.Text = "Tarefa agendada com sucesso! Os arquivos temporários serão limpos a cada logon.";
                AutoCleanTempOnLogonCheckBox.IsChecked = true;
            }
            else
            {
                StatusText.Text = $"Falha ao agendar tarefa: {result.Output}";
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Erro ao agendar tarefa: {ex.Message}";
        }
        finally
        {
            ScheduleCleanTempNowButton.IsEnabled = true;
            await RefreshCleanTempTaskStatusAsync();
        }
    }

    #region Package Shelf and Desktop Icon Integration

    /// <summary>
    /// Carrega os aplicativos da coleção de pacotes na bandeja de aplicativos para arrastar para o desktop.
    /// </summary>
    private void RefreshPackagesShelfButton_Click(object sender, RoutedEventArgs e)
    {
        if (AvailablePackagesShelf == null || AvailablePackagesCountText == null) return;

        AvailablePackagesShelf.Children.Clear();

        // Carrega TODOS os apps de TODAS as abas da coleção (não só a aba ativa),
        // e deduplica por Id (um app pode estar em várias abas, só precisa de 1 chip).
        var allApps = _packageCollectionService.Tabs
            .SelectMany(t => t.Items ?? Enumerable.Empty<AppEntry>())
            .GroupBy(a => a.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        if (allApps.Count > 0)
        {
            foreach (var app in allApps)
            {
                var chip = CreatePackageChip(app);
                AvailablePackagesShelf.Children.Add(chip);
            }

            AvailablePackagesCountText.Text = $"{allApps.Count} aplicativo(s)";
            StatusText.Text = $"Carregados {allApps.Count} aplicativos da coleção de pacotes " +
                              $"({_packageCollectionService.Tabs.Count} aba(s)).";
        }
        else
        {
            AvailablePackagesCountText.Text = "0 aplicativo(s)";
            StatusText.Text = "Nenhum aplicativo na coleção de pacotes. Adicione aplicativos na aba 'Pacotes' primeiro.";
        }
    }

    /// <summary>
    /// Cria um chip visual para um aplicativo que pode ser arrastado para o desktop.
    /// </summary>
    private Border CreatePackageChip(AppEntry app)
    {
        var chip = new Border
        {
            Width = 160,
            Height = 80,
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12),
            Margin = new Thickness(0, 0, 8, 0),
            Cursor = Cursors.Hand,
            Tag = app
        };

        var stackPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal
        };

        // Use real icon from IconService
        var iconUrl = _iconService.ResolveIconUrl(app);
        var iconImage = new Image
        {
            Width = 32,
            Height = 32,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0),
            Stretch = Stretch.Uniform
        };

        RenderOptions.SetBitmapScalingMode(iconImage, BitmapScalingMode.HighQuality);

        // PASSO 1: SÍNCRONO — põe o ícone padrão SEMPRE (nunca mostra caixa preta)
        var fallbackBitmap = new BitmapImage();
        fallbackBitmap.BeginInit();
        fallbackBitmap.CacheOption = BitmapCacheOption.OnLoad;
        fallbackBitmap.UriSource = new Uri(IconService.DefaultIconPackUri, UriKind.Absolute);
        fallbackBitmap.EndInit();
        fallbackBitmap.Freeze();
        iconImage.Source = fallbackBitmap;

        // PASSO 2: ASSÍNCRONO — tenta baixar/carregar o ícone real via AsyncImage
        // (cache em memória → disco → HTTP), e só troca se for bem-sucedido.
        // Se falhar (404, host fora do ar, formato ruim), fica com o fallback que já está aí.
        string localIconUrl = iconUrl;
        Image localIcon = iconImage;
        bool needsAsyncDownload = !localIconUrl.StartsWith("pack://", StringComparison.OrdinalIgnoreCase) &&
                                  !localIconUrl.Equals(IconService.DefaultIconPackUri, StringComparison.OrdinalIgnoreCase);
        if (needsAsyncDownload)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    BitmapImage? real = await AsyncImage.LoadBitmapAsync(localIconUrl);
                    if (real is not null)
                    {
                        await localIcon.Dispatcher.BeginInvoke(() =>
                        {
                            if (localIcon.Source == fallbackBitmap)
                                localIcon.Source = real;
                        });
                    }
                }
                catch
                {
                    // ignora — fallback já está em vigor
                }
            });
        }

        var textPanel = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center
        };

        var nameText = new TextBlock
        {
            Text = app.Name,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0xF8, 0xFA, 0xFC)),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 100
        };

        var idText = new TextBlock
        {
            Text = app.Id,
            FontSize = 10,
            Foreground = new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8)),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 100,
            Margin = new Thickness(0, 2, 0, 0)
        };

        textPanel.Children.Add(nameText);
        textPanel.Children.Add(idText);
        stackPanel.Children.Add(iconImage);
        stackPanel.Children.Add(textPanel);
        chip.Child = stackPanel;

        // Enable drag-and-drop from the chip
        chip.MouseLeftButtonDown += (s, args) =>
        {
            if (s is Border border && border.Tag is AppEntry appEntry)
            {
                var data = new DataObject(DataFormats.Text, appEntry.Id);
                DragDrop.DoDragDrop(border, data, DragDropEffects.Copy);
            }
        };

        return chip;
    }

    /// <summary>
    /// Permite arrastar aplicativos da bandeja para o canvas do desktop.
    /// </summary>
    private void DesktopIconsCanvas_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.Text) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>
    /// Cria um ícone no desktop quando um aplicativo é solto.
    /// Limitado a um ícone por aplicativo para evitar duplicação.
    /// </summary>
    private void DesktopIconsCanvas_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.Text) is string appId && DesktopIconsGroup != null)
        {
            // Find the app across all tabs
            var allApps = _packageCollectionService.Tabs.SelectMany(c => c.Items).ToList();
            var app = allApps.FirstOrDefault(a => a.Id.Equals(appId, StringComparison.OrdinalIgnoreCase));

            if (app != null)
            {
                // Check if an icon for this app already exists
                bool alreadyExists = _userAddedDesktopIcons.Any(icon => 
                    icon.Tag is AppEntry existingApp && existingApp.Id.Equals(app.Id, StringComparison.OrdinalIgnoreCase));

                if (alreadyExists)
                {
                    StatusText.Text = $"O atalho de '{app.Name}' já existe na área de trabalho. Apenas um ícone por aplicativo é permitido.";
                    e.Handled = true;
                    return;
                }

                // Calculate drop position
                Point dropPosition = e.GetPosition(DesktopIconsGroup);
                
                // Create desktop icon
                var icon = CreateDesktopIcon(app, dropPosition.X, dropPosition.Y);
                DesktopIconsGroup.Children.Add(icon);
                _userAddedDesktopIcons.Add(icon);

                if (_autoArrange)
                {
                    AutoArrangeIcons();
                }
                else
                {
                    PushCurrentToService();
                }

                StatusText.Text = $"Atalho de '{app.Name}' adicionado à área de trabalho.";
            }
        }

        e.Handled = true;
    }

    /// <summary>
    /// Cria um ícone de área de trabalho para um aplicativo usando ícones reais.
    /// Espaçamento mais compacto para melhor proporção (72px horizontal, 90px vertical).
    /// </summary>
    private Border CreateDesktopIcon(AppEntry app, double x, double y)
    {
        var icon = new Border
        {
            Width = 76,
            Height = 82,
            CornerRadius = new CornerRadius(4),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            Tag = app
        };

        // Snap to grid if enabled (spacing mais compacto: 72px horizontal, 90px vertical)
        if (_alignToGrid)
        {
            x = Math.Round((x - 16) / 72.0) * 72.0 + 16;
            y = Math.Round((y - 16) / 90.0) * 90.0 + 16;
        }

        // Clamp to canvas bounds
        x = Math.Clamp(x, 16, 1200);
        y = Math.Clamp(y, 16, 560);

        Canvas.SetLeft(icon, x);
        Canvas.SetTop(icon, y);

        var stackPanel = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        // Use real icon from IconService
        var iconUrl = _iconService.ResolveIconUrl(app);
        var iconImage = new Image
        {
            Name = "AppIcon",
            Width = 44,
            Height = 44,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 4),
            Stretch = Stretch.Uniform
        };

        RenderOptions.SetBitmapScalingMode(iconImage, BitmapScalingMode.HighQuality);

        // PASSO 1: SÍNCRONO — põe o ícone padrão SEMPRE (nunca mostra caixa preta)
        var fallbackBitmap = new BitmapImage();
        fallbackBitmap.BeginInit();
        fallbackBitmap.CacheOption = BitmapCacheOption.OnLoad;
        fallbackBitmap.UriSource = new Uri(IconService.DefaultIconPackUri, UriKind.Absolute);
        fallbackBitmap.EndInit();
        fallbackBitmap.Freeze();
        iconImage.Source = fallbackBitmap;

        // PASSO 2: ASSÍNCRONO — tenta baixar/carregar o ícone real via AsyncImage
        // (cache em memória → disco → HTTP), e só troca se for bem-sucedido.
        // Se falhar (404, host fora do ar, formato ruim), fica com o fallback que já está aí.
        string localIconUrl = iconUrl;
        Image localIcon = iconImage;
        bool needsAsyncDownload = !localIconUrl.StartsWith("pack://", StringComparison.OrdinalIgnoreCase) &&
                                  !localIconUrl.Equals(IconService.DefaultIconPackUri, StringComparison.OrdinalIgnoreCase);
        if (needsAsyncDownload)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    BitmapImage? real = await AsyncImage.LoadBitmapAsync(localIconUrl);
                    if (real is not null)
                    {
                        await localIcon.Dispatcher.BeginInvoke(() =>
                        {
                            if (localIcon.Source == fallbackBitmap)
                                localIcon.Source = real;
                        });
                    }
                }
                catch
                {
                    // ignora — fallback já está em vigor
                }
            });
        }

        var nameText = new TextBlock
        {
            Name = "AppName",
            Text = app.Name,
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF)),
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 70,
            Margin = new Thickness(0, 4, 0, 0),
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI")
        };

        // Add shadow effect
        var shadow = new DropShadowEffect
        {
            BlurRadius = 4,
            ShadowDepth = 1,
            Direction = 270,
            Color = Colors.Black,
            Opacity = 0.95
        };

        nameText.Effect = shadow;

        stackPanel.Children.Add(iconImage);
        stackPanel.Children.Add(nameText);
        icon.Child = stackPanel;

        // Attach event handlers for dragging and context menu
        icon.MouseLeftButtonDown += DesktopIcon_MouseLeftButtonDown;
        icon.MouseMove += DesktopIcon_MouseMove;
        icon.MouseLeftButtonUp += DesktopIcon_MouseLeftButtonUp;
        icon.MouseRightButtonUp += DesktopIcon_MouseRightButtonUp;

        return icon;
    }

    /// <summary>
    /// Fecha o menu de contexto quando o usuário clica no backdrop.
    /// </summary>
    private void DesktopMenuBackdrop_MouseDown(object sender, MouseButtonEventArgs e)
    {
        CloseContextMenu();
    }

    /// <summary>
    /// Remove o atalho selecionado da área de trabalho.
    /// </summary>
    private void MenuItemRemoveShortcut_Click(object sender, MouseButtonEventArgs e)
    {
        if (_selectedIcon != null && _userAddedDesktopIcons.Contains(_selectedIcon))
        {
            if (DesktopIconsGroup != null)
            {
                DesktopIconsGroup.Children.Remove(_selectedIcon);
            }
            _userAddedDesktopIcons.Remove(_selectedIcon);
            DeselectAllDesktopIcons();
            CloseContextMenu();
            StatusText.Text = "Atalho removido da área de trabalho.";
            PushCurrentToService();
        }
        e.Handled = true;
    }

    /// <summary>
    /// Atualiza o menu de contexto para mostrar a opção de remover quando um ícone do usuário está selecionado.
    /// </summary>
    private void UpdateContextMenuForSelection()
    {
        if (MenuItemRemoveShortcut != null && MenuSeparatorRemoveShortcut != null)
        {
            bool isUserIcon = _selectedIcon != null && _userAddedDesktopIcons.Contains(_selectedIcon);
            MenuItemRemoveShortcut.Visibility = isUserIcon ? Visibility.Visible : Visibility.Collapsed;
            MenuSeparatorRemoveShortcut.Visibility = isUserIcon ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    #endregion
}
