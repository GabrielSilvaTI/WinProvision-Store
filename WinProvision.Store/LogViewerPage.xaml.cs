using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using WinProvision.Store.Services;

namespace WinProvision.Store;

public partial class LogViewerPage : Page
{
    private const int MaxDisplayedLines = 3000;

    public LogViewerPage()
    {
        InitializeComponent();
        LogFilePathText.ToolTip = WinProvisionLog.FilePath;
        Loaded += (_, _) => LoadLog();
    }

    private void RefreshLog_Click(object sender, RoutedEventArgs e) => LoadLog();

    private void LoadLog()
    {
        try
        {
            if (!File.Exists(WinProvisionLog.FilePath))
            {
                LogTextBox.Clear();
                LogStatusText.Text = "O log ainda não foi criado. Ele aparecerá aqui quando houver registros.";
                return;
            }

            var lines = new Queue<string>(MaxDisplayedLines);
            using (var stream = new FileStream(
                       WinProvisionLog.FilePath,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
            {
                string? line;
                while ((line = reader.ReadLine()) is not null)
                {
                    if (lines.Count == MaxDisplayedLines)
                        lines.Dequeue();
                    lines.Enqueue(line);
                }
            }

            LogTextBox.Text = string.Join(Environment.NewLine, lines);
            LogStatusText.Text = $"Exibindo as {lines.Count:N0} linhas mais recentes.";
            LogTextBox.CaretIndex = LogTextBox.Text.Length;
            LogTextBox.ScrollToEnd();
        }
        catch (Exception ex)
        {
            LogStatusText.Text = $"Não foi possível ler o log: {ex.Message}";
        }
    }
}
