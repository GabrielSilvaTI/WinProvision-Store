using WinProvision.Core.Services;
using System.Net;
using System.Text;
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

        // O runner windows-latest do GitHub pode já vir com o 7-Zip pré-instalado de
        // fábrica (aconteceu — winget tenta "upgrade" em vez de instalar do zero, não
        // acha versão nova, e reporta falha). Garante estado limpo aqui em vez de supor
        // o que a imagem do runner tem hoje: resultado ignorado de propósito, porque
        // "já não estava instalado" também é um resultado válido (NoPackageFound).
        await executor.UninstallAppAsync(KnownGoodPackageId);

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

        // Classificação por exit code (0x8A150014, NO_APPLICATIONS_FOUND) + texto: não depende
        // do idioma do winget nem do wording exato da mensagem. A mensagem do Assert mostra a
        // saída crua pra diagnosticar de cara caso o winget mude de novo.
        Assert.True(
            result.FailureReason == WingetFailureReason.PackageNotInCatalog,
            $"Esperado PackageNotInCatalog, veio {result.FailureReason} " +
            $"(ExitCode={result.ExitCode}).\nOutput:\n{result.Output}");
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

/// <summary>
/// Testes puros (sem winget.exe, sem rede) do classificador — rápidos e determinísticos.
/// Cobrem os wordings conhecidos (en/pt-BR) e o fallback por exit code.
/// </summary>
[Trait("Category", "Integration")]
public class WingetErrorTranslatorTests
{
    [Theory]
    [InlineData("No package found matching input criteria.", WingetFailureReason.PackageNotInCatalog)]
    [InlineData("Nenhum pacote encontrado correspondendo aos critérios de entrada.", WingetFailureReason.PackageNotInCatalog)]
    [InlineData("No installed package found matching input criteria.", WingetFailureReason.NoPackageFound)]
    [InlineData("Access is denied", WingetFailureReason.ElevationRequired)]
    [InlineData("", WingetFailureReason.Unknown)]
    [InlineData("qualquer coisa que não reconhecemos", WingetFailureReason.Unknown)]
    public void Classify_PorTexto_ReconheceWordingsConhecidos(string output, WingetFailureReason expected)
    {
        Assert.Equal(expected, WingetErrorTranslator.Classify(output));
    }

    [Theory]
    [InlineData(unchecked((int)0x8A150014), WingetFailureReason.PackageNotInCatalog)]
    [InlineData(unchecked((int)0x8A150010), WingetFailureReason.NoApplicableInstallers)]
    [InlineData(unchecked((int)0x8A150008), WingetFailureReason.DownloadError)]
    [InlineData(unchecked((int)0x8A150011), WingetFailureReason.InstallerHashMismatch)]
    [InlineData(unchecked((int)0x8A150041), WingetFailureReason.PackageAgreementsNotAccepted)]
    [InlineData(unchecked((int)0x8A150056), WingetFailureReason.ElevationProhibited)]
    [InlineData(1, WingetFailureReason.Unknown)]
    public void Classify_PorExitCode_FuncionaMesmoSemTextoReconhecivel(int exitCode, WingetFailureReason expected)
    {
        // Texto em idioma/wording desconhecido — só o exit code identifica a causa.
        Assert.Equal(expected, WingetErrorTranslator.Classify(exitCode, "Mensagem em outro idioma"));
    }

    [Fact]
    public void Classify_TextoTemPrioridadeSobreExitCode()
    {
        // "não está instalado" também sai com 0x8A150014 — o texto é que distingue.
        Assert.Equal(
            WingetFailureReason.NoPackageFound,
            WingetErrorTranslator.Classify(
                unchecked((int)0x8A150014),
                "No installed package found matching input criteria."));
    }
}

[Trait("Category", "Unit")]
public class WinProvisionInstallerSelectionTests
{
    [Fact]
    public void PickInstaller_PrefereArquiteturaEScopoDetectadosPelaCom()
    {
        var manifest = new PackageManifest
        {
            Installers =
            [
                new PackageInstaller { Architecture = "x64", Scope = "user", Url = "user.exe" },
                new PackageInstaller { Architecture = "x64", Scope = "machine", Url = "machine.exe" },
                new PackageInstaller { Architecture = "x86", Scope = "machine", Url = "x86.exe" }
            ]
        };

        var installer = WinProvisionApiService.PickInstaller(
            manifest, System.Runtime.InteropServices.Architecture.X64, "machine");

        Assert.Equal("machine.exe", installer?.Url);
    }

    [Fact]
    public void PickInstaller_UsaArquiteturaCompativelComoFallback()
    {
        var manifest = new PackageManifest
        {
            Installers =
            [
                new PackageInstaller { Architecture = "x86", Scope = "user", Url = "x86.exe" }
            ]
        };

        var installer = WinProvisionApiService.PickInstaller(
            manifest, System.Runtime.InteropServices.Architecture.X64);

        Assert.Equal("x86.exe", installer?.Url);
    }
}

[Trait("Category", "Unit")]
public class WinProvisionApiReliabilityTests
{
    [Fact]
    public async Task GetIndexAsync_RetryEmErroTemporarioEUsaRespostaValida()
    {
        int attempts = 0;
        using var http = new HttpClient(new StubHttpMessageHandler(_ =>
        {
            attempts++;
            return attempts == 1
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : Json("""{"schema":1,"generatedAt":"2026-09-23T00:00:00Z","count":1,"packages":[{"id":"Vendor.App","version":"1.0","architectures":["x64"]}]}""");
        }));
        string cache = Path.Combine(Path.GetTempPath(), $"WinProvisionApiTests-{Guid.NewGuid():N}");
        try
        {
            var api = new WinProvisionApiService(http, cache);
            var index = await api.GetIndexAsync();

            Assert.Equal(2, attempts);
            Assert.Equal("Vendor.App", Assert.Single(index.Packages).Id);
            Assert.True(File.Exists(Path.Combine(cache, "index.json")));
        }
        finally
        {
            if (Directory.Exists(cache)) Directory.Delete(cache, recursive: true);
        }
    }

    [Fact]
    public async Task GetPackageAsync_Distingue404DeFalhaTemporaria()
    {
        using var http = new HttpClient(new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.NotFound)));
        string cache = Path.Combine(Path.GetTempPath(), $"WinProvisionApiTests-{Guid.NewGuid():N}");
        try
        {
            var api = new WinProvisionApiService(http, cache);
            Assert.Null(await api.GetPackageAsync("Vendor.Missing"));
        }
        finally
        {
            if (Directory.Exists(cache)) Directory.Delete(cache, recursive: true);
        }
    }

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }
}

[Trait("Category", "Unit")]
public class WinProvisionInstallerCommandBuilderTests
{
    [Fact]
    public void PackageInstaller_DetectaPortableZipMesmoSemSilentSupport()
    {
        var installer = new PackageInstaller
        {
            Type = "zip",
            NestedType = "portable",
            NestedInstallerFile = "bin\\app.exe",
            SilentSupported = false
        };

        Assert.True(installer.IsPortable);
    }

    [Theory]
    [InlineData("msi", "C:\\Temp\\AppSetup.msi")]
    [InlineData("exe", "C:\\Temp\\AppSetup.msi")]
    public void Build_RouteMSIParaWindowsInstaller(string type, string path)
    {
        var command = WinProvisionInstallerCommandBuilder.Build(path, type, "/quiet /norestart");

        Assert.Equal("msiexec.exe", Path.GetFileName(command.FileName));
        Assert.Equal($"/i \"{path}\" /quiet /norestart", command.Arguments);
    }

    [Fact]
    public void Build_MantemEXEComoExecutavelDireto()
    {
        const string path = "C:\\Temp\\AppSetup.exe";
        var command = WinProvisionInstallerCommandBuilder.Build(path, "inno", "/VERYSILENT");

        Assert.Equal(path, command.FileName);
        Assert.Equal("/VERYSILENT", command.Arguments);
    }
}

[CollectionDefinition("WingetSequential", DisableParallelization = true)]
public class WingetSequentialCollection;
