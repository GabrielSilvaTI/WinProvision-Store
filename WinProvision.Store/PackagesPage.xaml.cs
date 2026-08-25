using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using WinProvision.Core.Models;
using WinProvision.Core.Services;
using WinProvision.Core.Services.Profile;

namespace WinProvision.Store;

public partial class PackagesPage : Page
{
    private readonly PackageCollectionService _collectionService;
    private readonly ProfileService _profileService;
    private readonly StoreService _storeService;
    private readonly WingetExecutor _wingetExecutor;

    public PackagesPage()
    {
        InitializeComponent();

        _collectionService = App.Services.GetRequiredService<PackageCollectionService>();
        _profileService = App.Services.GetRequiredService<ProfileService>();
        _storeService = App.Services.GetRequiredService<StoreService>();
        _wingetExecutor = App.Services.GetRequiredService<WingetExecutor>();

        // Conecta a lista de abas ao TabControl
        ProfileTabControl.ItemsSource = _collectionService.Tabs;
        ProfileTabControl.SelectedItem = _collectionService.ActiveTab;

        UpdateStatus();
    }

    private void UpdateStatus()
    {
        var active = _collectionService.ActiveTab;
        if (active == null) return;

        StatusText.Text = active.Items.Count == 0
            ? $"[{active.Title}] Nenhuma aplicativo adicionado ainda."
            : $"[{active.Title}] {active.Items.Count} pacote(s) na coleção.";
    }

    private void NewTabButton_Click(object sender, RoutedEventArgs e)
    {
        var newTab = _collectionService.CreateNewTab();
        ProfileTabControl.SelectedItem = newTab;
        UpdateStatus();
    }

    private void CloseTabButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: PackageProfileTab tab })
        {
            _collectionService.CloseTab(tab);
            ProfileTabControl.SelectedItem = _collectionService.ActiveTab;
            UpdateStatus();
        }
    }

    private void ProfileTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProfileTabControl.SelectedItem is PackageProfileTab selectedTab)
        {
            _collectionService.ActiveTab = selectedTab;
            UpdateStatus();
        }
    }

    private void RemoveButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: AppEntry app } && _collectionService.ActiveTab != null)
        {
            _collectionService.ActiveTab.Items.Remove(app);
            UpdateStatus();
        }
    }

    private void ClearCollectionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_collectionService.ActiveTab == null || _collectionService.ActiveTab.Items.Count == 0) return;

        _collectionService.ActiveTab.Items.Clear();
        UpdateStatus();
    }

    private async void ImportProfileButton_Click(object sender, RoutedEventArgs e)
    {
        var openFileDialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "JSON Profile (*.json)|*.json",
            Title = "Selecione o perfil de aplicativos"
        };

        if (openFileDialog.ShowDialog() != true) return;

        StatusText.Text = "Lendo perfil de aplicativos...";

        try
        {
            var manifest = await _profileService.ImportAsync(openFileDialog.FileName);
            var installedIds = await _wingetExecutor.GetInstalledPackageIdsAsync();
            var (toInstall, alreadySatisfied) = _profileService.Reconcile(manifest, installedIds);
            var pendingIds = toInstall.Select(a => a.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

            var matched = _storeService.GetAll().Where(app => pendingIds.Contains(app.Id));

            // Adiciona em uma nova guia nomeada com o arquivo importado
            var importedTab = _collectionService.CreateNewTab(Path.GetFileNameWithoutExtension(openFileDialog.FileName));
            foreach (var app in matched)
            {
                importedTab.Items.Add(app);
            }

            ProfileTabControl.SelectedItem = importedTab;
            StatusText.Text = $"Perfil importado em nova guia: {importedTab.Items.Count} pacote(s).";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Erro ao importar: {ex.Message}";
        }
    }

    private async void ExportProfileButton_Click(object sender, RoutedEventArgs e)
    {
        var activeTab = _collectionService.ActiveTab;
        if (activeTab == null || activeTab.Items.Count == 0)
        {
            StatusText.Text = "A guia atual está vazia — nada para exportar.";
            return;
        }

        var saveFileDialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "JSON Profile (*.json)|*.json",
            FileName = $"{activeTab.Title.ToLower().Replace(" ", "-")}.json",
            Title = "Salvar Perfil de Aplicativos"
        };

        if (saveFileDialog.ShowDialog() != true) return;

        var manifest = _profileService.BuildFromSelection(activeTab.Items, activeTab.Title);
        await _profileService.ExportAsync(manifest, saveFileDialog.FileName);

        StatusText.Text = $"Perfil '{activeTab.Title}' exportado com sucesso.";
    }

    // -------------------------------------------------------------
    // EXPORTAÇÃO DINÂMICA -> SCRIPT POWERSHELL (.PS1)
    // -------------------------------------------------------------
    private async void ExportScriptButton_Click(object sender, RoutedEventArgs e)
    {
        var activeTab = _collectionService.ActiveTab;
        if (activeTab == null || activeTab.Items.Count == 0)
        {
            StatusText.Text = "A guia atual está vazia — nada para gerar script.";
            return;
        }

        var saveFileDialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "PowerShell Script (*.ps1)|*.ps1",
            FileName = $"{activeTab.Title.ToLower().Replace(" ", "-")}-install.ps1",
            Title = "Salvar Script de Instalação PowerShell"
        };

        if (saveFileDialog.ShowDialog() != true) return;

        var scriptBuilder = new StringBuilder();

        // Cabeçalho e configurações do PowerShell
        scriptBuilder.AppendLine("# ========================================================");
        scriptBuilder.AppendLine($"# WinProvision - Script Dinâmico de Instalação");
        scriptBuilder.AppendLine($"# Guia / Perfil: {activeTab.Title}");
        scriptBuilder.AppendLine($"# Gerado em: {DateTime.Now:dd/MM/yyyy HH:mm:ss}");
        scriptBuilder.AppendLine("# ========================================================");
        scriptBuilder.AppendLine();
        scriptBuilder.AppendLine("$ErrorActionPreference = 'Continue'");
        scriptBuilder.AppendLine("Write-Host 'Iniciando instalação dos aplicativos...' -ForegroundColor Green");
        scriptBuilder.AppendLine();

        // Itera sobre a lista de itens da guia ativa
        foreach (var app in activeTab.Items)
        {
            scriptBuilder.AppendLine($"Write-Host 'Instalando {app.Name} ({app.Id})...' -ForegroundColor Cyan");
            scriptBuilder.AppendLine($"winget install --id \"{app.Id}\" --exact --source winget --accept-source-agreements --disable-interactivity --silent --accept-package-agreements --force");
            scriptBuilder.AppendLine();
        }

        scriptBuilder.AppendLine("Write-Host 'Processo de instalação concluído!' -ForegroundColor Green");

        try
        {
            await File.WriteAllTextAsync(saveFileDialog.FileName, scriptBuilder.ToString(), Encoding.UTF8);
            StatusText.Text = $"Script .ps1 para '{activeTab.Title}' exportado com sucesso!";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Erro ao salvar o script: {ex.Message}";
        }
    }
}