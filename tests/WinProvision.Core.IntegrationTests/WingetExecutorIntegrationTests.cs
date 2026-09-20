using WinProvision.Core.Services;
using Xunit;

namespace WinProvision.Core.IntegrationTests;

/// <summary>
/// Testes que rodam o winget.exe de verdade — nada aqui é mockado. Servem pra pegar
/// exatamente o tipo de regressão que já foi descoberta manualmente antes (ex.: "Falha na
/// pesquisa da origem: msstore", VCLibs/UI.Xaml faltando, bootstrap silencioso quebrado)
/// automaticamente, num runner windows-latest do CI, em vez de só quando um usuário reporta.
///
/// Efeitos colaterais reais: os testes de instalação instalam/desinstalam um pacote de
/// verdade no runner. Isso é seguro porque o runner é descartado no fim do job, mas por
/// isso NUNCA rodar esta suíte fora de CI num ambiente que você use de verdade sem revisar
/// primeiro — troque para um pacote descartável se for rodar localmente.
/// </summary>
[Trait("Category", "Integration")]
[Collection("WingetSequential")] // nunca em paralelo: mexe no estado real de pacotes instalados
public class WingetExecutorIntegrationTests
{
    // 7-Zip: instalador pequeno, rápido, totalmente silencioso, sem telas de EULA
    // interativas — a escolha mais previsível pra CI dentre os pacotes que já usamos
    // como referência nas sondagens anteriores.
    private const string KnownGoodPackageId = "7zip.7zip";
    private const string NonExistentPackageId = "WinProvision.Tests.PacoteQueNaoExiste.999";

    [Fact]
    public async Task InstallAppAsync_PacoteReal_InstalaComSucessoEApareceEmGetInstalledPackageIds()
    {
        var executor = new WingetExecutor();
        var log = new List<string>();

        var installResult = await executor.InstallAppAsync(
            KnownGoodPackageId,
            onLogReceived: line => log.Add(line));

        Assert.True(
            installResult.Success,
            $"Instalação falhou (ExitCode={installResult.ExitCode}, " +
            $"FailureReason={installResult.FailureReason}).\nOutput:\n{installResult.Output}\n\n" +
            $"Log:\n{string.Join('\n', log)}");

        var installedIds = await executor.GetInstalledPackageIdsAsync();
        Assert.Contains(
            installedIds,
            id => id.Equals(KnownGoodPackageId, StringComparison.OrdinalIgnoreCase));

        // Limpeza: desinstala pra não deixar lixo no runner (mesmo ele sendo descartado,
        // isso também exercita o caminho de desinstalação — ver teste abaixo com
        // asserção dedicada se quiser separar as duas responsabilidades no futuro).
        var uninstallResult = await executor.UninstallAppAsync(KnownGoodPackageId);
        Assert.True(
            uninstallResult.Success,
            $"Desinstalação de limpeza falhou (ExitCode={uninstallResult.ExitCode}, " +
            $"FailureReason={uninstallResult.FailureReason}).\nOutput:\n{uninstallResult.Output}");
    }

    [Fact]
    public async Task InstallAppAsync_PacoteInexistente_RetornaFalhaComMotivoClassificado()
    {
        var executor = new WingetExecutor();

        var result = await executor.InstallAppAsync(NonExistentPackageId);

        Assert.False(result.Success);
        // Asserção deliberadamente frouxa quanto ao valor exato do enum: o texto que o
        // winget.exe devolve pra "pacote não encontrado" já mudou de wording entre
        // versões antes. O que realmente importa pra esse teste é que o
        // WingetErrorTranslator conseguiu classificar a saída em vez de cair em
        // Unknown — é isso que garante que a UI mostra uma mensagem em pt-BR
        // específica em vez do texto cru do winget.
        Assert.NotEqual(WingetFailureReason.Unknown, result.FailureReason);
    }

    [Fact]
    public async Task GetInstalledPackageIdsAsync_SemInstalarNada_NaoLancaEDevolveListaNaoNula()
    {
        var executor = new WingetExecutor();

        var installedIds = await executor.GetInstalledPackageIdsAsync();

        Assert.NotNull(installedIds);
    }
}

/// <summary>
/// Cobre especificamente o caminho que já quebrou de verdade no Windows Sandbox
/// (VCLibs/UI.Xaml ausentes, "Não encontrei os .appx esperados"). No runner windows-latest
/// do GitHub o winget já vem funcional via App Installer, então este teste não reproduz
/// o cenário de dependência ausente — ele garante que a LÓGICA de detecção em si não
/// regride (não lança, não trava, reporta disponível quando de fato está).
/// </summary>
[Trait("Category", "Integration")]
[Collection("WingetSequential")]
public class WingetBootstrapperIntegrationTests
{
    [Fact]
    public async Task IsWingetAvailableAsync_EmRunnerComWingetPreinstalado_RetornaTrue()
    {
        var bootstrapper = new WingetBootstrapper();

        bool isAvailable = await bootstrapper.IsWingetAvailableAsync();

        Assert.True(isAvailable, "winget.exe deveria estar disponível no runner windows-latest.");
    }

    [Fact]
    public async Task EnsureWingetAsync_QuandoJaDisponivel_RetornaUsavelSemPrecisarBaixarNada()
    {
        var bootstrapper = new WingetBootstrapper();
        var log = new List<string>();

        var result = await bootstrapper.EnsureWingetAsync(line => log.Add(line));

        Assert.True(
            result.IsUsable,
            $"Bootstrap não deixou o winget usável: {result.ErrorMessage}\nLog:\n{string.Join('\n', log)}");
    }
}

[CollectionDefinition("WingetSequential", DisableParallelization = true)]
public class WingetSequentialCollection;
