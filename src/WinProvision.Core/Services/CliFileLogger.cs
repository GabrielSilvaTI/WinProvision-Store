using System;
using System.IO;

namespace WinProvision.Core.Services;

/// <summary>
/// Log do modo CLI (/auto): duplica cada linha no console (se houver um anexado) e num
/// arquivo .log em disco, com timestamp — pra dar pra investigar depois de uma instalação
/// silenciosa (ex.: rodada de dentro de uma task sequence, sem ninguém olhando o console
/// na hora). Nunca lança: se não conseguir abrir o arquivo, só avisa e segue sem logar
/// em disco (a instalação em si não pode falhar por causa do log).
/// </summary>
public sealed class CliFileLogger : IDisposable
{
    private readonly StreamWriter? _writer;
    private readonly object _gate = new();

    /// <summary>Caminho efetivo do arquivo de log, ou null se o log em arquivo está desabilitado.</summary>
    public string? FilePath { get; }

    public CliFileLogger(string? filePath)
    {
        FilePath = filePath;

        if (string.IsNullOrWhiteSpace(filePath))
        {
            return;
        }

        try
        {
            string? dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            _writer = new StreamWriter(filePath, append: true) { AutoFlush = true };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WinProvision] AVISO: não deu pra abrir o arquivo de log '{filePath}': {ex.Message}");
            _writer = null;
        }
    }

    /// <summary>Escreve a linha no console (se disponível) e no arquivo de log (se disponível).</summary>
    public void Log(string message)
    {
        string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}";

        Console.WriteLine(line);

        lock (_gate)
        {
            try
            {
                _writer?.WriteLine(line);
            }
            catch
            {
                // Best effort — um erro de disco no meio da instalação não pode derrubar o processo.
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _writer?.Dispose();
        }
    }
}
