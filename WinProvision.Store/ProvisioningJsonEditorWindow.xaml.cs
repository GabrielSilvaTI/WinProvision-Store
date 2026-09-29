using System;
using System.IO;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using WinProvision.Core.Services;
using Wpf.Ui.Controls;

namespace WinProvision.Store;

/// <summary>
/// Visualizador/editor do contrato JSON completo do perfil. O texto é validado a cada
/// alteração para que o usuário possa corrigir sintaxe e estrutura antes de exportar.
/// </summary>
public partial class ProvisioningJsonEditorWindow : FluentWindow
{
    private JsonErrorLineAdorner? _errorLineAdorner;

    public ProvisioningJsonEditorWindow(string json)
    {
        InitializeComponent();
        JsonTextBox.Text = json;
        JsonTextBox.TextChanged += JsonTextBox_TextChanged;
        JsonTextBox.Loaded += JsonTextBox_Loaded;
        ValidateJson();
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e) => Clipboard.SetText(JsonTextBox.Text);

    private void JsonTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) =>
        ValidateJson();

    private void JsonTextBox_Loaded(object sender, RoutedEventArgs e)
    {
        var layer = AdornerLayer.GetAdornerLayer(JsonTextBox);
        if (layer is null) return;

        _errorLineAdorner = new JsonErrorLineAdorner(JsonTextBox);
        layer.Add(_errorLineAdorner);
        UpdateErrorMarker(ProfileJsonValidator.Validate(JsonTextBox.Text));
    }

    private void ValidateJson()
    {
        var result = ProfileJsonValidator.Validate(JsonTextBox.Text);
        ValidationText.Text = result.Path is { Length: > 0 }
            ? $"{result.Message} Campo: {result.Path}"
            : result.Message;
        ValidationText.Foreground = (Brush)FindResource(
            result.IsValid ? "SystemFillColorSuccessBrush" : "SystemFillColorCriticalBrush");
        SaveButton.IsEnabled = result.IsValid;
        EditorBorder.BorderBrush = (Brush)FindResource(
            result.IsValid ? "AppBorderSubtleBrush" : "SystemFillColorCriticalBrush");
        UpdateErrorMarker(result);
    }

    private void UpdateErrorMarker(ProfileJsonValidationResult result)
    {
        _errorLineAdorner?.SetErrorLine(
            result.IsValid ? null : GetLineStartIndex(result.Line),
            result.IsValid ? null : GetLineLength(result.Line));
    }

    private int? GetLineStartIndex(long? line)
    {
        if (line is not { } lineNumber || lineNumber < 1) return null;
        int start = 0;
        for (long current = 1; current < lineNumber; current++)
        {
            int newline = JsonTextBox.Text.IndexOf('\n', start);
            if (newline < 0) return null;
            start = newline + 1;
        }
        return start <= JsonTextBox.Text.Length ? start : null;
    }

    private int? GetLineLength(long? line)
    {
        int? start = GetLineStartIndex(line);
        if (start is not { } index) return null;
        int end = JsonTextBox.Text.IndexOf('\n', index);
        return (end < 0 ? JsonTextBox.Text.Length : end) - index;
    }

    private async void SaveAsButton_Click(object sender, RoutedEventArgs e)
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
        catch
        {
            var errorDialog = new Wpf.Ui.Controls.MessageBox
            {
                Title = "Editor de Perfil",
                Content = "Não foi possível salvar o arquivo. Tente novamente.",
                CloseButtonText = "OK"
            };
            await errorDialog.ShowDialogAsync();
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private sealed class JsonErrorLineAdorner : Adorner
    {
        private int? _lineStart;
        private int? _lineLength;
        private readonly Pen _pen = new(new SolidColorBrush(Color.FromArgb(210, 239, 68, 68)), 1.5);

        public JsonErrorLineAdorner(UIElement adornedElement) : base(adornedElement)
        {
            IsHitTestVisible = false;
        }

        public void SetErrorLine(int? lineStart, int? lineLength)
        {
            _lineStart = lineStart;
            _lineLength = lineLength;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            base.OnRender(drawingContext);
            if (_lineStart is not { } start || _lineLength is not { } length) return;

            var textBox = (System.Windows.Controls.TextBox)AdornedElement;
            if (start > textBox.Text.Length) return;

            try
            {
                Rect rect = textBox.GetRectFromCharacterIndex(start);
                if (rect.IsEmpty) return;

                double left = Math.Max(0, rect.Left);
                double right = Math.Min(ActualWidth, Math.Max(left + 24, rect.Left + Math.Max(24, length * 7.2)));
                double y = Math.Min(ActualHeight - 2, rect.Bottom - 1);
                var geometry = new StreamGeometry();
                using (var context = geometry.Open())
                {
                    context.BeginFigure(new Point(left, y), false, false);
                    for (double x = left; x < right; x += 6)
                    {
                        context.LineTo(new Point(Math.Min(x + 3, right), y - 2), true, false);
                        context.LineTo(new Point(Math.Min(x + 6, right), y), true, false);
                    }
                }
                drawingContext.DrawGeometry(null, _pen, geometry);
            }
            catch (ArgumentOutOfRangeException)
            {
            }
        }
    }
}
