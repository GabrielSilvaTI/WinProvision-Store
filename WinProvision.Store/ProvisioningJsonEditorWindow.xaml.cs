using System;
using System.IO;
using System.Windows;
using Wpf.Ui.Controls;

namespace WinProvision.Store;

/// <summary>
/// Editor de texto simples ("Bloco de Notas") para o JSON do perfil de provisionamento —
/// aberto a partir do botão "Abrir editor completo" na visão de Perfil. Não valida nem
/// reaplica o JSON editado ao <see cref="Core.Services.Provisioning.ProvisioningService"/>;
/// serve para inspeção/cópia/exportação manual do conteúdo completo.
/// </summary>
public partial class ProvisioningJsonEditorWindow : FluentWindow
{
    public ProvisioningJsonEditorWindow(string json)
    {
        InitializeComponent();
        JsonTextBox.Text = json;
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e) => Clipboard.SetText(JsonTextBox.Text);

    private void SaveAsButton_Click(object sender, RoutedEventArgs e)
    {
        var saveFileDialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "JSON Profile (*.json)|*.json",
            FileName = "provisionamento.json",
            Title = "Salvar JSON como"
        };

        if (saveFileDialog.ShowDialog() != true) return;

        try
        {
            File.WriteAllText(saveFileDialog.FileName, JsonTextBox.Text);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, $"Erro ao salvar: {ex.Message}", "Editor de Perfil", System.Windows.MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
