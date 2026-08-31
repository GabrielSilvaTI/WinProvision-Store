using System.Runtime.Versioning;
using WinProvision.Core.Models.Provisioning;

namespace WinProvision.Core.Services.Provisioning;

/// <summary>Resultado da instalação de um único item — usado para montar o log/relatório.</summary>
public record WindowsUpdateStepResult(string Title, bool Success, string Message);

/// <summary>Resultado consolidado de <see cref="WindowsUpdateService.InstallAsync"/>.</summary>
public record WindowsUpdateApplyResult(List<WindowsUpdateStepResult> Steps, bool RestartRequired)
{
    public bool Success => Steps.Count > 0 && Steps.All(s => s.Success);
}

/// <summary>
/// Busca e instala atualizações do Windows (qualidade/segurança e driver) via Windows Update
/// Agent (WUAPI) — a mesma engine por trás de "Windows Update" nas Configurações. Diferente do
/// <see cref="WingetExecutor"/> (que fala com o winget para apps), este serviço fala direto com
/// a engine COM do sistema operacional (ProgID "Microsoft.Update.Session").
///
/// A API do WUAPI só existe como COM (wuapi.dll); em vez de referenciar um assembly de
/// interop gerado (que quebraria a compilação cross-target deste projeto em plataformas sem
/// esse COM registrado), os objetos são resolvidos em tempo de execução via
/// <see cref="Type.GetTypeFromProgID"/> + <c>dynamic</c> — o mesmo padrão usado nos scripts de
/// exemplo oficiais da Microsoft (VBScript/PowerShell), só que em C#.
/// </summary>
[SupportedOSPlatform("windows")]
public class WindowsUpdateService
{
    /// <summary>
    /// Objetos COM (IUpdate) da última busca, na MESMA ORDEM dos <see cref="WindowsUpdateItem"/>
    /// devolvidos por <see cref="SearchAsync"/> — Download/Install exigem o objeto COM original,
    /// não aceitam o DTO, por isso ficam guardados aqui em vez de descartados após montar o DTO.
    /// Repetir SearchAsync substitui esta lista inteira (índices de uma busca anterior deixam de
    /// ser válidos).
    /// </summary>
    private readonly List<dynamic> _lastSearchUpdates = [];

    private static dynamic CreateSession()
    {
        var sessionType = Type.GetTypeFromProgID("Microsoft.Update.Session")
            ?? throw new InvalidOperationException("Windows Update Agent (WUAPI) não está disponível neste sistema.");

        dynamic session = Activator.CreateInstance(sessionType)!;
        session.ClientApplicationID = "WinProvision.Store";
        return session;
    }

    private static dynamic CreateUpdateCollection()
    {
        var collectionType = Type.GetTypeFromProgID("Microsoft.Update.UpdateColl")
            ?? throw new InvalidOperationException("Windows Update Agent (WUAPI) não está disponível neste sistema.");

        return Activator.CreateInstance(collectionType)!;
    }

    /// <summary>
    /// Busca atualizações pendentes (instaladas ou ocultas ficam de fora) — o mesmo critério
    /// ("IsInstalled=0 and IsHidden=0") usado pelo botão "Verificar atualizações" das
    /// Configurações do Windows. Bloqueante por natureza (API COM síncrona), por isso roda em
    /// Task.Run.
    /// </summary>
    public Task<IReadOnlyList<WindowsUpdateItem>> SearchAsync(CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            dynamic session = CreateSession();
            dynamic searcher = session.CreateUpdateSearcher();
            dynamic result = searcher.Search("IsInstalled=0 and IsHidden=0");

            _lastSearchUpdates.Clear();
            var items = new List<WindowsUpdateItem>();

            int count = result.Updates.Count;
            for (int i = 0; i < count; i++)
            {
                ct.ThrowIfCancellationRequested();

                dynamic update = result.Updates.Item(i);

                var kbIds = new List<string>();
                int kbCount = update.KBArticleIDs.Count;
                for (int k = 0; k < kbCount; k++)
                {
                    kbIds.Add((string)update.KBArticleIDs.Item(k));
                }

                // UpdateType (wuapi.h): utSoftware = 1, utDriver = 2.
                bool isDriver = (int)update.Type == 2;
                long maxSizeBytes = (long)(double)update.MaxDownloadSize;

                items.Add(new WindowsUpdateItem((string)update.Title, kbIds, isDriver, maxSizeBytes));
                _lastSearchUpdates.Add(update);
            }

            return (IReadOnlyList<WindowsUpdateItem>)items;
        }, ct);
    }

    /// <summary>
    /// Busca e instala TODAS as atualizações/drivers pendentes, sem seleção manual — usado
    /// pelo Apply automático do perfil de provisionamento (<see cref="ProvisioningManifest.AutoInstallWindowsUpdates"/>),
    /// pensado pra rodar na máquina-alvo no momento em que o perfil é aplicado, não na máquina
    /// onde o perfil foi montado.
    /// </summary>
    public async Task<WindowsUpdateApplyResult> CheckAndInstallAllAsync(
        Action<string>? log = null,
        CancellationToken ct = default)
    {
        var updates = await SearchAsync(ct);

        if (updates.Count == 0)
        {
            return new WindowsUpdateApplyResult([], false);
        }

        var allIndices = Enumerable.Range(0, updates.Count).ToList();
        return await InstallAsync(allIndices, log, ct);
    }

    /// <summary>
    /// Baixa e instala os itens da última <see cref="SearchAsync"/> cujo índice (posição na
    /// lista devolvida por ela) está em <paramref name="selectedIndices"/>. Cada etapa
    /// (download, depois instalação) é melhor-esforço: uma falha num item não impede os demais.
    /// </summary>
    public Task<WindowsUpdateApplyResult> InstallAsync(
        IReadOnlyList<int> selectedIndices,
        Action<string>? log = null,
        CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            var steps = new List<WindowsUpdateStepResult>();

            void Report(string title, bool success, string message)
            {
                steps.Add(new WindowsUpdateStepResult(title, success, message));
                log?.Invoke($"[Windows Update] {title}: {(success ? "OK" : "FALHOU")} — {message}");
            }

            dynamic updatesToDownload = CreateUpdateCollection();

            foreach (int index in selectedIndices)
            {
                if (index < 0 || index >= _lastSearchUpdates.Count) continue;

                dynamic update = _lastSearchUpdates[index];

                // EulaAccepted precisa ser aceito programaticamente aqui porque não há UI do
                // Windows Update nesta tela para o usuário aceitar manualmente — sem isso,
                // Download() falha silenciosamente para o item.
                if (!(bool)update.EulaAccepted)
                {
                    update.AcceptEula();
                }

                updatesToDownload.Add(update);
            }

            ct.ThrowIfCancellationRequested();

            if (updatesToDownload.Count == 0)
            {
                return new WindowsUpdateApplyResult(steps, false);
            }

            dynamic session = CreateSession();
            dynamic downloader = session.CreateUpdateDownloader();
            downloader.Updates = updatesToDownload;
            dynamic downloadResult = downloader.Download();

            // OperationResultCode (wuapi.h): orcSucceeded = 2, orcSucceededWithErrors = 3.
            int downloadCode = (int)downloadResult.ResultCode;
            if (downloadCode is not (2 or 3))
            {
                int total = updatesToDownload.Count;
                for (int i = 0; i < total; i++)
                {
                    dynamic failed = updatesToDownload.Item(i);
                    Report((string)failed.Title, false, $"Falha ao baixar (código de resultado {downloadCode}).");
                }
                return new WindowsUpdateApplyResult(steps, false);
            }

            dynamic updatesToInstall = CreateUpdateCollection();
            int downloadedCount = updatesToDownload.Count;
            for (int i = 0; i < downloadedCount; i++)
            {
                dynamic update = updatesToDownload.Item(i);
                if ((bool)update.IsDownloaded)
                {
                    updatesToInstall.Add(update);
                }
                else
                {
                    Report((string)update.Title, false, "Não foi possível baixar este item.");
                }
            }

            if (updatesToInstall.Count == 0)
            {
                return new WindowsUpdateApplyResult(steps, false);
            }

            ct.ThrowIfCancellationRequested();

            dynamic installer = session.CreateUpdateInstaller();
            installer.Updates = updatesToInstall;
            dynamic installResult = installer.Install();

            bool restartRequired = (bool)installResult.RebootRequired;

            int installCount = updatesToInstall.Count;
            for (int i = 0; i < installCount; i++)
            {
                dynamic update = updatesToInstall.Item(i);
                dynamic itemResult = installResult.GetUpdateResult(i);
                int code = (int)itemResult.ResultCode;
                Report((string)update.Title, code is 2 or 3, $"Código de resultado {code}.");
            }

            return new WindowsUpdateApplyResult(steps, restartRequired);
        }, ct);
    }
}
