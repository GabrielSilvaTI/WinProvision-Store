using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using WinProvision.Core.Models;

namespace WinProvision.Core.Services;

public class WingetExecutionResult
{
    public bool Success { get; set; }
    public int ExitCode { get; set; }
    public string Output { get; set; } = string.Empty;
}

public class WingetExecutor
{
    /// <summary>
    /// Instala um pacote do Winget de forma silenciosa e reporta o progresso em tempo real.
    /// </summary>
    public async Task<WingetExecutionResult> InstallAppAsync(string appId, Action<string>? onLogReceived = null, CancellationToken cancellationToken = default)
    {
        // Argumentos otimizados para instalação não interativa e aceitação automática de termos
        string args = $"install --id \"{appId}\" --exact --silent --accept-source-agreements --accept-package-agreements";
        return await ExecuteWingetCommandAsync(args, onLogReceived, cancellationToken);
    }

    /// <summary>
    /// Desinstala um pacote silenciosamente.
    /// </summary>
    public async Task<WingetExecutionResult> UninstallAppAsync(string appId, Action<string>? onLogReceived = null, CancellationToken cancellationToken = default)
    {
        // Faltava --accept-source-agreements aqui (presente no Install e no export):
        // sem ela, winget uninstall precisa consultar a source para resolver o --exact
        // e, se o acordo da source ainda não tiver sido aceito no perfil elevado em que
        // a Store roda (requireAdministrator), fica esperando uma confirmação
        // interativa que nunca chega (stdin não é redirecionado) e a operação sempre
        // termina em falha/timeout.
        string args = $"uninstall --id \"{appId}\" --exact --silent --accept-source-agreements";
        return await ExecuteWingetCommandAsync(args, onLogReceived, cancellationToken);
    }

    /// <summary>
    /// Atualiza um pacote específico via "winget update" (alias de "winget upgrade"),
    /// silenciosamente e sem interação — mesmos argumentos usados no InstallAppAsync,
    /// mais --include-unknown (necessário pro winget atualizar apps cuja versão instalada
    /// ele não consegue detectar com certeza) e --force (ignora hash mismatch/instalador
    /// já baixado em cache desatualizado).
    /// </summary>
    public async Task<WingetExecutionResult> UpdateAppAsync(string appId, Action<string>? onLogReceived = null, CancellationToken cancellationToken = default)
    {
        string args = $"update --id \"{appId}\" --exact --source winget --accept-source-agreements --disable-interactivity --silent --include-unknown --accept-package-agreements --force";
        return await ExecuteWingetCommandAsync(args, onLogReceived, cancellationToken);
    }

    /// <summary>
    /// Lista os pacotes com atualização pendente ("winget upgrade"). Diferente de
    /// GetInstalledPackageIdsAsync (que usa "winget export", com saída JSON), esse comando
    /// não tem opção de saída estruturada — a resposta é a tabela de texto padrão do
    /// console, parseada por WingetUpgradeListParser. --include-unknown inclui pacotes cuja
    /// versão instalada o winget não consegue confirmar (comuns em apps instalados fora do
    /// winget), pra não esconder atualizações reais só por causa disso.
    /// </summary>
    public async Task<List<UpgradablePackage>> GetUpgradablePackagesAsync(CancellationToken cancellationToken = default)
    {
        string args = "upgrade --include-unknown --accept-source-agreements --disable-interactivity";
        var result = await ExecuteWingetCommandAsync(args, onLogReceived: null, cancellationToken);

        return WingetUpgradeListParser.Parse(result.Output);
    }

    /// <summary>
    /// Lista os Package Ids atualmente instalados via "winget export" (JSON estruturado
    /// gerado pelo próprio winget), em vez de parsear a saída de texto do console —
    /// evita quebra por locale/versão do winget. Usado pela reconciliação de perfis
    /// e pela contagem de updates.
    /// </summary>
    public async Task<List<string>> GetInstalledPackageIdsAsync(CancellationToken cancellationToken = default)
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"winprovision-export-{Guid.NewGuid():N}.json");

        try
        {
            string args = $"export -o \"{tempFile}\" --accept-source-agreements --include-versions";
            var result = await ExecuteWingetCommandAsync(args, onLogReceived: null, cancellationToken);

            if (!result.Success || !File.Exists(tempFile))
                return [];

            string json = await File.ReadAllTextAsync(tempFile, cancellationToken);
            return ParseInstalledIdsFromExportJson(json);
        }
        finally
        {
            TryDeleteFile(tempFile);
        }
    }

    private static List<string> ParseInstalledIdsFromExportJson(string json)
    {
        var ids = new List<string>();

        try
        {
            using var document = JsonDocument.Parse(json);

            if (!document.RootElement.TryGetProperty("Sources", out var sources))
                return ids;

            foreach (var source in sources.EnumerateArray())
            {
                if (!source.TryGetProperty("Packages", out var packages))
                    continue;

                foreach (var package in packages.EnumerateArray())
                {
                    if (package.TryGetProperty("PackageIdentifier", out var idProp) &&
                        idProp.GetString() is { Length: > 0 } id)
                    {
                        ids.Add(id);
                    }
                }
            }
        }
        catch (JsonException)
        {
            // Export corrompido/incompleto: melhor devolver lista vazia do que derrubar
            // a reconciliação — quem chama trata "nada instalado detectado" como seguro.
        }

        return ids;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Arquivo temporário órfão não é crítico; melhor esforço de limpeza.
        }
    }

    private static async Task<WingetExecutionResult> ExecuteWingetCommandAsync(string arguments, Action<string>? onLogReceived, CancellationToken cancellationToken)
    {
        var outputBuilder = new StringBuilder();

        var startInfo = new ProcessStartInfo
        {
            FileName = "winget.exe",
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        using var process = new Process { StartInfo = startInfo };

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            outputBuilder.AppendLine(e.Data);
            onLogReceived?.Invoke(e.Data);
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            outputBuilder.AppendLine(e.Data);
            onLogReceived?.Invoke(e.Data);
        };

        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await process.WaitForExitAsync(cancellationToken);

            return new WingetExecutionResult
            {
                Success = process.ExitCode == 0,
                ExitCode = process.ExitCode,
                Output = outputBuilder.ToString()
            };
        }
        catch (OperationCanceledException)
        {
            // Cancelamento vindo do orquestrador (WPF/PowerShell): mata o processo
            // em vez de deixar o winget.exe orfão rodando em segundo plano.
            TryKill(process);
            return new WingetExecutionResult
            {
                Success = false,
                ExitCode = -1,
                Output = outputBuilder.Append("Operação cancelada pelo usuário.").ToString()
            };
        }
        catch (Exception ex)
        {
            TryKill(process);
            return new WingetExecutionResult
            {
                Success = false,
                ExitCode = -1,
                Output = $"Falha ao executar o Winget: {ex.Message}"
            };
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Processo já pode ter saído entre a checagem e o Kill; sem ação necessária.
        }
    }
}
