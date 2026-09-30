using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Threading;
using Microsoft.Win32;
using WinProvision.Core.Models;
using WinProvision.Core.Models.Provisioning;
using WinProvision.Core.Services;

namespace WinProvision.Core.Services.Provisioning;

/// <summary>Resultado da aplicação de um único ajuste do perfil — usado para montar o log/relatório.</summary>
public record ProvisioningStepResult(string Setting, bool Success, string Message);

/// <summary>
/// Resultado consolidado de <see cref="ProvisioningService.ApplyAsync"/>. RestartRequired fica
/// true quando algum ajuste aplicado (hoje, só a renomeação da máquina) só surte efeito depois
/// que o Windows reiniciar — a tela/CLI decide se avisa o usuário ou dispara o reinício.
/// </summary>
public record ProvisioningApplyResult(List<ProvisioningStepResult> Steps, bool RestartRequired)
{
    public bool Success => Steps.Count > 0 && Steps.All(s => s.Success);
}

/// <summary>
/// Aplica, exporta e importa perfis de provisionamento do sistema (tema, barra de tarefas,
/// plano de energia, nome da máquina, wallpaper, ponto de
/// restauração). Diferente do
/// <see cref="WinProvision.Core.Services.Profile.ProfileService"/> (que fala com winget/ODT),
/// este serviço fala direto com o Registro do Windows, com o Win32
/// (SHAppBarMessage/SetComputerNameEx/SystemParametersInfo), com o powercfg.exe, com o WUAPI
/// e com o WMI SystemRestore
/// (<see cref="RestorePointService"/>) — por isso é inteiramente específico de Windows (ver
/// <see cref="SupportedOSPlatformAttribute"/> na classe).
///
/// Todo ajuste aqui é pensado pra rodar na máquina-alvo no momento do Apply — inclusive
/// atualizações e ponto de restauração, que não são ações "ao vivo" na máquina onde o perfil
/// foi montado, mas toggles do manifesto executados quando o perfil é de fato aplicado (botão
/// "Aplicar agora" ou CLI /Provision), tipicamente numa máquina recém-formatada e diferente.
///
/// Export/Import trabalham com <see cref="ProvisioningManifest"/> "puro", mas em disco o
/// arquivo é sempre um <see cref="ProfileManifest"/> (com essa seção preenchida em
/// <see cref="ProfileManifest.Provisioning"/>) — um único formato de perfil pra todo o
/// sistema, que tanto o modo CLI <c>/Provision</c> quanto o <c>/auto</c> conseguem ler.
///
/// Cada ajuste é aplicado de forma independente e melhor-esforço: uma falha num ajuste (ex.:
/// TaskbarAl não existe no Windows 10) não impede os demais de serem tentados — o chamador
/// recebe o relatório completo em <see cref="ProvisioningApplyResult.Steps"/>.
/// </summary>
[SupportedOSPlatform("windows")]
public class ProvisioningService(RestorePointService restorePointService, ScheduledTempCleanerService? tempCleanerService = null)
{
    /// <summary>
    /// Estado de provisionamento "atual" desta sessão do app — guardado em memória, usado
    /// por <see cref="Backup.BackupAutoSyncService"/> para incluir a seção de provisionamento
    /// no mesmo backup (local/Gist) que já cobre os pacotes. IMPORTANTE: isto NÃO significa
    /// "aplicado de fato no Windows desta máquina" — é atualizado tanto por um ApplyAsync
    /// real (tela Provisionamento "Aplicar agora", ou CLI /Provision e /auto) quanto por
    /// <see cref="SetCurrent"/>, chamado ao exportar/importar um perfil de provisionamento
    /// só pela UI, sem tocar no Registro. Motivo: o perfil de provisionamento é frequentemente
    /// montado numa máquina para ser distribuído a OUTRAS (via /auto URL) — exigir um
    /// ApplyAsync real antes de deixá-lo entrar no backup faria o botão "Sincronizar agora"
    /// (e a restauração de backup) nunca verem esses ajustes até alguém aplicá-los
    /// localmente, o que não faz sentido pra esse fluxo. Null até a primeira definição
    /// (aplicada ou apenas configurada) nesta execução do app.
    /// </summary>
    public ProvisioningManifest? Current { get; private set; }

    /// <summary>Disparado sempre que <see cref="Current"/> muda (ApplyAsync que aplicou algo, ou SetCurrent), para o backup automático reagir sem precisar dar poll.</summary>
    public event Action? Changed;

    /// <summary>
    /// Marca <paramref name="manifest"/> como o estado de provisionamento atual desta sessão,
    /// SEM aplicar nada no Windows (nenhuma chamada ao Registro/Win32) — usado pela tela
    /// Provisionamento ao exportar ou importar um perfil, para que ele entre no próximo
    /// backup automático mesmo sem o usuário clicar em "Aplicar agora". Para efetivamente
    /// mudar o sistema, use <see cref="ApplyAsync"/>.
    /// </summary>
    public void SetCurrent(ProvisioningManifest manifest)
    {
        Current = manifest;
        Changed?.Invoke();
    }

    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string ExplorerAdvancedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    private const string SearchKey = @"Software\Microsoft\Windows\CurrentVersion\Search";
    private const string DesktopKey = @"Control Panel\Desktop";

    /// <summary>Extensão usada quando o nome original não traz uma extensão de imagem reconhecida.</summary>
    private const string DefaultWallpaperExtension = ".png";

    // GUIDs oficiais dos planos de energia padrão do Windows (documentados pela Microsoft).
    // Usar o GUID em vez do alias (SCHEME_BALANCED
    // etc.) evita depender do locale do powercfg pra resolver o nome.
    private const string PowerSchemeBalanced = "381b4222-f694-41f0-9685-ff5bb260df2e";
    private const string PowerSchemeHighPerformance = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
    private const string PowerSchemePowerSaver = "a1841308-3541-4fab-bc81-f71556f20b4a";

    /// <summary>
    /// Serializa o perfil em disco, embrulhado num <see cref="ProfileManifest"/> (com
    /// <c>Apps</c> vazio) — mesmo formato usado pelo resto do sistema, então o arquivo
    /// resultante já funciona tanto com <c>/Provision</c> quanto com <c>/auto</c>, e entra
    /// de graça na sincronização por Gist (que faz backup do ProfileManifest inteiro).
    /// </summary>
    public async Task ExportAsync(ProvisioningManifest manifest, string filePath, CancellationToken ct = default)
    {
        var profile = new ProfileManifest
        {
            Name = manifest.Name,
            Provisioning = manifest,
        };

        string json = JsonSerializer.Serialize(profile, WinProvisionJsonOptions.Profile);
        await File.WriteAllTextAsync(filePath, json, ct);
    }

    /// <summary>
    /// Lê um perfil (caminho local ou URL http(s), ex.: link "raw" de Gist) e devolve só a
    /// seção de provisionamento — o mesmo arquivo pode ter apps/Office junto, que este
    /// método simplesmente ignora (quem cuida dos dois juntos é o modo CLI /auto). Aceita tanto
    /// um perfil único quanto um conjunto de backup completo (mesma URL do Gist de backup
    /// automático funciona aqui) — ver <see cref="ProfileManifestParser"/>.
    /// </summary>
    public async Task<ProvisioningManifest> ImportAsync(string filePath, CancellationToken ct = default)
    {
        string json = await ProfileSourceReader.ReadTextAsync(filePath, ct);
        var validation = ProfileJsonValidator.Validate(json);
        if (!validation.IsValid)
            throw new InvalidDataException(
                validation.Path is { Length: > 0 }
                    ? $"{validation.Message} Campo: {validation.Path}"
                    : validation.Message);
        var profile = ProfileManifestParser.Parse(json, Path.GetFileNameWithoutExtension(filePath));

        if (profile?.Provisioning is not { } manifest)
            throw new InvalidDataException($"O perfil em '{filePath}' não contém uma seção de provisionamento.");

        // Schema ausente/zero => trata como legado; hoje só existe v1 (mesma convenção do ProfileManifest).
        if (manifest.SchemaVersion <= 0)
            manifest.SchemaVersion = 1;

        // Perfis exportados pela própria tela Provisionamento têm o nome só no
        // ProfileManifest "pai" (ver ExportAsync acima) — traz de volta se a seção em si
        // não tiver o próprio Name.
        manifest.Name ??= profile.Name;

        return manifest;
    }

    /// <summary>
    /// Aplica todos os ajustes definidos no perfil (campos null são ignorados — "não mexer
    /// nesse ajuste"). Usado tanto pelo botão "Aplicar" da tela Provisionamento quanto pelo
    /// modo CLI (/Provision).
    /// </summary>
    public async Task<ProvisioningApplyResult> ApplyAsync(
        ProvisioningManifest manifest,
        Action<string>? log = null,
        CancellationToken ct = default,
        Action<ProvisioningStepResult>? stepProgress = null)
    {
        var steps = new List<ProvisioningStepResult>();
        bool restartRequired = false;

        void Report(string setting, bool success, string message)
        {
            var step = new ProvisioningStepResult(setting, success, message);
            steps.Add(step);
            stepProgress?.Invoke(step);
            log?.Invoke($"[Provisionamento] {setting}: {(success ? "OK" : "FALHOU")} — {message}");
        }

        if (manifest.Theme is { } theme && theme != SystemThemeMode.NaoDefinido)
        {
            TryApply("Tema do sistema", ApplyTheme, theme, Report);
        }

        if (manifest.TaskbarAlignment is { } alignment && alignment != TaskbarAlignmentMode.NaoDefinido)
        {
            TryApply("Alinhamento da barra de tarefas", ApplyTaskbarAlignment, alignment, Report);
        }

        if (manifest.TaskbarAutoHide is { } autoHide)
        {
            TryApply("Ocultar automaticamente a barra de tarefas", ApplyTaskbarAutoHide, autoHide, Report);
        }

        if (manifest.TaskbarSearchBox is { } searchBox && searchBox != TaskbarSearchBoxMode.NaoDefinido)
        {
            TryApply("Caixa de pesquisa da barra de tarefas", ApplyTaskbarSearchBox, searchBox, Report);
        }

        if (manifest.PowerPlan is { } powerPlan && powerPlan != PowerPlanMode.NaoDefinido)
        {
            var result = await ApplyPowerPlanAsync(powerPlan, ct);
            Report("Plano de energia", result.Success, result.Message);
        }

        if (manifest.DisplayTimeoutOnAc is not null || manifest.DisplayTimeoutOnDc is not null
            || manifest.StandbyTimeoutOnAc is not null || manifest.StandbyTimeoutOnDc is not null)
        {
            var timeoutsResult = await ApplyPowerTimeoutsAsync(new PowerTimeoutsInput(
                DisplayTimeoutOnAc: manifest.DisplayTimeoutOnAc,
                DisplayTimeoutOnDc: manifest.DisplayTimeoutOnDc,
                StandbyTimeoutOnAc: manifest.StandbyTimeoutOnAc,
                StandbyTimeoutOnDc: manifest.StandbyTimeoutOnDc), ct);
            Report("Tempo de tela e suspensão", timeoutsResult.Success, timeoutsResult.Message);
        }

        if (!string.IsNullOrWhiteSpace(manifest.MachineName))
        {
            var result = await ApplyMachineNameAsync(manifest.MachineName, ct);
            Report("Nome da máquina", result.Success, result.Message);
            if (result.Success) restartRequired = true;
        }

        if (manifest.WallpaperImageBase64 is { } wallpaperBase64 && !string.IsNullOrWhiteSpace(wallpaperBase64))
        {
            TryApply("Papel de parede", ApplyWallpaper, (wallpaperBase64, manifest.WallpaperFileName), Report);
        }

        if (!string.IsNullOrWhiteSpace(manifest.Region))
        {
            TryApply("Região", ApplyRegion, manifest.Region, Report);
        }

        if (!string.IsNullOrWhiteSpace(manifest.Creator) || !string.IsNullOrWhiteSpace(manifest.Name))
        {
            var oemResult = await ApplyOemInformationAsync((manifest.Creator?.Trim(), manifest.Name?.Trim()), ct);
            Report("Informações OEM (Autor do setup)", oemResult.Success, oemResult.Message);
        }

        if (manifest.AutoCreateRestorePoint == true)
        {
            try
            {
                var (success, message) = await restorePointService.CreateAsync(
                    manifest.Name is { } name ? $"WinProvision - {name}" : "WinProvision", ct);
                Report("Ponto de restauração", success, message);
            }
            catch (Exception ex)
            {
                Report("Ponto de restauração", false, $"Erro: {ex.Message}");
            }
        }


        if (manifest.AutoCleanTempOnLogon == true)
        {
            try
            {
                var cleaner = tempCleanerService ?? new ScheduledTempCleanerService();
                var result = await cleaner.EnableAsync(ct);
                Report("Limpeza de arquivos temporários ao Logon", result.Success, result.Success ? "Tarefa agendada para executar a cada Logon." : result.Output);
            }
            catch (Exception ex)
            {
                Report("Limpeza de arquivos temporários ao Logon", false, $"Erro: {ex.Message}");
            }
        }

        // Só atualiza Current (e dispara o backup automático) se algo de fato foi
        // tentado — um manifesto totalmente vazio (todos os campos null) não deve gerar
        // uma entrada de backup sem sentido.
        if (steps.Count > 0)
        {
            SetCurrent(manifest);
        }

        return new ProvisioningApplyResult(steps, restartRequired);
    }

    private static void TryApply<T>(
        string settingLabel,
        Func<T, (bool Success, string Message)> apply,
        T value,
        Action<string, bool, string> report)
    {
        try
        {
            var (success, message) = apply(value);
            report(settingLabel, success, message);
        }
        catch (Exception ex)
        {
            report(settingLabel, false, $"Erro: {ex.Message}");
        }
    }

    /// <summary>
    /// Grava AppsUseLightTheme + SystemUsesLightTheme (HKCU\...\Themes\Personalize) e avisa as
    /// janelas abertas via WM_SETTINGCHANGE — sem o broadcast, apps já abertos (inclusive o
    /// Explorer/barra de tarefas) só refletem a troca depois de reiniciados/relogados.
    /// </summary>
    private static (bool Success, string Message) ApplyTheme(SystemThemeMode theme)
    {
        int value = theme == SystemThemeMode.Claro ? 1 : 0;

        using var key = OpenOrCreateKey(PersonalizeKey);
        key.SetValue("AppsUseLightTheme", value, RegistryValueKind.DWord);
        key.SetValue("SystemUsesLightTheme", value, RegistryValueKind.DWord);

        NativeMethods.BroadcastSettingChange("ImmersiveColorSet");

        return (true, theme == SystemThemeMode.Claro ? "Tema claro aplicado." : "Tema escuro aplicado.");
    }

    /// <summary>
    /// TaskbarAl só existe/tem efeito no Windows 11 (a barra do Windows 10 é sempre à
    /// esquerda) — a chave é gravada mesmo assim, mas o Explorer precisa reiniciar pra refletir.
    /// </summary>
    private static (bool Success, string Message) ApplyTaskbarAlignment(TaskbarAlignmentMode alignment)
    {
        int value = alignment == TaskbarAlignmentMode.Centro ? 1 : 0;

        using var key = OpenOrCreateKey(ExplorerAdvancedKey);
        key.SetValue("TaskbarAl", value, RegistryValueKind.DWord);

        return (true, $"Definido como {(alignment == TaskbarAlignmentMode.Centro ? "centralizado" : "à esquerda")} " +
                       "(só tem efeito no Windows 11; reinicie o Explorer ou faça logoff para ver a mudança).");
    }

    /// <summary>
    /// Usa a API documentada SHAppBarMessage/ABM_SETSTATE (em vez de editar o blob binário
    /// não documentado de StuckRects3) — aplica na hora, sem precisar reiniciar o Explorer.
    /// </summary>
    private static (bool Success, string Message) ApplyTaskbarAutoHide(bool autoHide)
    {
        nint trayWnd = NativeMethods.FindWindow("Shell_TrayWnd", null);
        if (trayWnd == 0)
        {
            return (false, "Não foi possível localizar a janela da barra de tarefas (Shell_TrayWnd).");
        }

        var data = new NativeMethods.APPBARDATA
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.APPBARDATA>(),
            hWnd = trayWnd,
            lParam = autoHide ? NativeMethods.AbsAutoHide : NativeMethods.AbsAlwaysOnTop
        };

        NativeMethods.SHAppBarMessage(NativeMethods.AbmSetState, ref data);

        return (true, autoHide ? "Ocultação automática ativada." : "Ocultação automática desativada.");
    }

    private static (bool Success, string Message) ApplyTaskbarSearchBox(TaskbarSearchBoxMode mode)
    {
        int value = mode switch
        {
            TaskbarSearchBoxMode.Oculta => 0,
            TaskbarSearchBoxMode.ApenasIcone => 1,
            TaskbarSearchBoxMode.CaixaCompleta => 2,
            _ => 2
        };

        using var key = OpenOrCreateKey(SearchKey);
        key.SetValue("SearchboxTaskbarMode", value, RegistryValueKind.DWord);

        return (true, $"Modo da caixa de pesquisa definido como {mode}.");
    }

    /// <summary>
    /// CreateSubKey pode retornar null (ex.: falha de permissão) — centralizado aqui pra virar
    /// uma exceção com mensagem clara em vez de um NullReferenceException genérico no SetValue,
    /// e capturada pelo try/catch de <see cref="TryApply{T}"/>.
    /// </summary>
    private static RegistryKey OpenOrCreateKey(string subKeyPath) =>
        Registry.CurrentUser.CreateSubKey(subKeyPath)
            ?? throw new IOException($"Não foi possível abrir/criar a chave de registro 'HKCU\\{subKeyPath}'.");

    /// <summary>
    /// Troca o plano de energia ativo via powercfg.exe /setactive — não existe API .NET
    /// gerenciada para isso, e é o mesmo binário que a tela Configurações de Energia usa por
    /// baixo dos panos.
    /// </summary>
    private static async Task<(bool Success, string Message)> ApplyPowerPlanAsync(PowerPlanMode plan, CancellationToken ct)
    {
        string guid = plan switch
        {
            PowerPlanMode.Economia => PowerSchemePowerSaver,
            PowerPlanMode.Equilibrado => PowerSchemeBalanced,
            PowerPlanMode.AltoDesempenho => PowerSchemeHighPerformance,
            _ => PowerSchemeBalanced
        };

        var startInfo = new ProcessStartInfo
        {
            FileName = "powercfg.exe",
            Arguments = $"/setactive {guid}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        using var process = new Process { StartInfo = startInfo };
        var output = new StringBuilder();

        process.OutputDataReceived += (_, e) => { if (e.Data != null) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) output.AppendLine(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync(ct);

        return process.ExitCode == 0
            ? (true, $"Plano \"{plan}\" ativado.")
            : (false, $"powercfg retornou código {process.ExitCode}. {output}".Trim());
    }

    /// <summary>
    /// Value-tuple forte (via record) para carregar os 4 possíveis timeouts de energia
    /// tomada/bateria → tela/suspensão. Valor null = "não alterar esta configuração";
    /// 0 = "Nunca" (powercfg aceita 0 como desligado). Valor positivo = minutos.
    /// </summary>
    private readonly record struct PowerTimeoutsInput(
        int? DisplayTimeoutOnAc,
        int? DisplayTimeoutOnDc,
        int? StandbyTimeoutOnAc,
        int? StandbyTimeoutOnDc);

    /// <summary>
    /// Aplica os timeouts de tela / suspensão no PLANO ATIVO do Windows (o que acaba de
    /// ser definido por ApplyPowerPlanAsync ou o que já estava ativo). Usa powercfg /change
    /// sempre é escrito SEM ativo GUID, /setacvalueindex SCHEME_CURRENT SUB_VIDEO VIDEOIDLE e
    /// escritas similares — equivalente a clicar nos mesmos sliders da tela de Configurações →
    /// Sistema → Energia e bateria → Tela e suspensão.
    /// powercfg /change NOME MINUTOS é equivalente a configurar no plano ativo em AC e DC
    /// atual (por NOME em {monitor-timeout-ac|standby-timeout}-{ac|dc}. Se precisar escrever
    /// um plano customizado, use "/setacvalueindex ... + /setactive no GUID). Usa o comando
    /// `/change` por simplicidade — já é suficiente para 99% dos cenários de provisionamento.
    /// </summary>
    private static async Task<(bool Success, string Message)> ApplyPowerTimeoutsAsync(PowerTimeoutsInput input, CancellationToken ct)
    {
        static (string Name, int Minutes)[] BuildCommands(PowerTimeoutsInput i)
        {
            var cmds = new List<(string, int)>(4);
            if (i.DisplayTimeoutOnAc is { } displayAc && displayAc >= 0) cmds.Add(("monitor-timeout-ac", displayAc));
            if (i.DisplayTimeoutOnDc is { } displayDc && displayDc >= 0) cmds.Add(("monitor-timeout-dc", displayDc));
            if (i.StandbyTimeoutOnAc is { } standbyAc && standbyAc >= 0) cmds.Add(("standby-timeout-ac", standbyAc));
            if (i.StandbyTimeoutOnDc is { } standbyDc && standbyDc >= 0) cmds.Add(("standby-timeout-dc", standbyDc));
            return cmds.ToArray();
        }

        static string FriendlyMinutes(int m) => m == 0 ? "Nunca" : $"{m} min";

        var commands = BuildCommands(input);
        if (commands.Length == 0)
            return (true, "Nenhum timeout foi selecionado — nada a aplicar.");

        var summary = string.Join(", ", commands.Select(c => $"{c.Name}={FriendlyMinutes(c.Minutes)}"));

        foreach (var (settingName, minutes) in commands)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powercfg.exe",
                Arguments = $"/change {settingName} {minutes}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            using var proc = new Process { StartInfo = psi };
            var outB = new StringBuilder();
            proc.OutputDataReceived += (_, e) => { if (e.Data != null) outB.AppendLine(e.Data); };
            proc.ErrorDataReceived += (_, e) => { if (e.Data != null) outB.AppendLine(e.Data); };
            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            await proc.WaitForExitAsync(ct);

            if (proc.ExitCode != 0)
                return (false, $"Erro em {settingName}={minutes}: powercfg ExitCode={proc.ExitCode}. {outB}".Trim());
        }

        return (true, $"Aplicados no plano ativo: {summary}.");
    }

    /// <summary>
    /// Grava o novo nome pendente (NetBIOS + primeira label do nome DNS) via SetComputerNameEx.
    /// Renomear a máquina é a ÚNICA ação de Provisionamento que exige privilégio de
    /// administrador de verdade (todo o resto mexe só em HKEY_CURRENT_USER ou não precisa de
    /// elevação — ver app.manifest, que não roda mais elevado por padrão). Se
    /// SetComputerNameEx falhar por falta de privilégio (o caso comum agora, já que a Store
    /// roda sem elevação), cai pro fallback via "Rename-Computer" (PowerShell) relançado
    /// elevado só pra esse comando (ver ElevatedProcessRunner) — pede UAC uma vez, sem exigir
    /// que o app inteiro rode como Administrador.
    /// </summary>
    private static async Task<(bool Success, string Message)> ApplyMachineNameAsync(string machineName, CancellationToken ct)
    {
        bool ok = NativeMethods.SetComputerNameEx(NativeMethods.ComputerNamePhysicalDnsHostname, machineName);
        if (ok)
        {
            return (true, $"Nome pendente definido como \"{machineName}\" — só terá efeito após reiniciar o Windows.");
        }

        int error = Marshal.GetLastWin32Error();
        if (error != 5) // 5 = ERROR_ACCESS_DENIED — qualquer outro erro não é resolvido elevando.
        {
            return (false, $"SetComputerNameEx falhou (código de erro do Windows: {error}).");
        }

        string psArgs = $"-NoProfile -NonInteractive -Command \"Rename-Computer -NewName '{machineName}' -Force\"";
        var elevatedResult = await ElevatedProcessRunner.RunElevatedAsync("powershell.exe", psArgs, ct);

        return elevatedResult.FailureReason == WingetFailureReason.ElevationCanceled
            ? (false, "Renomear a máquina requer administrador — elevação (UAC) recusada.")
            : elevatedResult.Success
                ? (true, $"Nome pendente definido como \"{machineName}\" — só terá efeito após reiniciar o Windows.")
                : (false, "Falha ao renomear a máquina mesmo elevado. Veja o log para detalhes.");
    }

    /// <summary>
    /// Decodifica o Base64 do perfil, grava a imagem no perfil do usuário em
    /// %LocalAppData%\WinProvision\Wallpaper\wallpaper&lt;ext&gt; (sempre o mesmo nome, sobrescrevendo
    /// o wallpaper anterior — SystemParametersInfo precisa de um caminho real em disco, não
    /// aceita bytes em memória) e aplica via SystemParametersInfo/SPI_SETDESKWALLPAPER, com
    /// WallpaperStyle=10 (preencher) pra imagem não ficar centralizada/pequena em telas maiores.
    /// </summary>
    private static (bool Success, string Message) ApplyWallpaper((string Base64, string? FileName) input)
    {
        byte[] bytes = Convert.FromBase64String(input.Base64);

        string extension = Path.GetExtension(input.FileName)?.ToLowerInvariant() ?? string.Empty;
        if (extension is not (".jpg" or ".jpeg" or ".png"))
        {
            extension = DefaultWallpaperExtension;
        }

        string folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinProvision", "Wallpaper");
        Directory.CreateDirectory(folder);

        string imagePath = Path.Combine(folder, $"wallpaper{extension}");
        File.WriteAllBytes(imagePath, bytes);

        using (var key = OpenOrCreateKey(DesktopKey))
        {
            key.SetValue("WallpaperStyle", "10", RegistryValueKind.String); // 10 = preencher (Windows 7+)
            key.SetValue("TileWallpaper", "0", RegistryValueKind.String);
        }

        bool ok = NativeMethods.SystemParametersInfo(
            NativeMethods.SpiSetDeskWallpaper, 0, imagePath,
            NativeMethods.SpifUpdateIniFile | NativeMethods.SpifSendChange);

        if (!ok)
        {
            int error = Marshal.GetLastWin32Error();
            return (false, $"SystemParametersInfo falhou (código de erro do Windows: {error}). Imagem salva em '{imagePath}'.");
        }

        return (true, $"Wallpaper aplicado a partir de '{imagePath}'.");
    }

    /// <summary>
    /// SetUserGeoName grava a localização geográfica do usuário (chave GeoID no Registro) e
    /// já é a API recomendada pela própria Microsoft desde o Windows 10 1709 — a antecessora
    /// SetUserGeoID está descontinuada. Aceita o código ISO 3166-1 de duas letras direto,
    /// sem precisar resolver GeoID numérico antes.
    /// </summary>
    private static (bool Success, string Message) ApplyRegion(string regionCode)
    {
        bool ok = NativeMethods.SetUserGeoName(regionCode);

        if (!ok)
        {
            int error = Marshal.GetLastWin32Error();
            return (false, $"SetUserGeoName falhou (código de erro do Windows: {error}).");
        }

        return (true, $"Região definida como \"{regionCode}\".");
    }

    /// <summary>
    /// Grava Manufacturer + Model na chave HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\OEMInformation
    /// para que o Criador e o Nome do Perfil apareçam em "Configurações → Sistema → Sobre" como
    /// Autor do setup / Organização. Também grava RegisteredOwner / RegisteredOrganization em
    /// HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion, usados por algumas telas herdadas (WinVer etc.).
    /// Escrever em HKLM requer Administrador: primeiro tenta direto (caso o app já esteja elevado),
    /// se falhar com Acesso Negado usa <see cref="ElevatedProcessRunner"/> via PowerShell elevado
    /// (pede UAC uma vez só, sem precisar que o app inteiro rode como Admin).
    /// </summary>
    private static async Task<(bool Success, string Message)> ApplyOemInformationAsync(
        (string? Creator, string? ProfileName) input,
        CancellationToken ct)
    {
        string manufacturer = input.Creator ?? string.Empty;
        string model = input.ProfileName ?? string.Empty;
        string owner = string.IsNullOrWhiteSpace(input.Creator) ? "WinProvision" : input.Creator;
        string org = string.IsNullOrWhiteSpace(input.ProfileName)
            ? (string.IsNullOrWhiteSpace(input.Creator) ? "WinProvision" : input.Creator)
            : input.ProfileName;

        bool TryWriteDirect()
        {
            try
            {
                using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                using (var oem = hklm.CreateSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\OEMInformation", writable: true))
                {
                    if (oem is null) return false;
                    if (!string.IsNullOrEmpty(manufacturer))
                        oem.SetValue("Manufacturer", manufacturer, RegistryValueKind.String);
                    if (!string.IsNullOrEmpty(model))
                        oem.SetValue("Model", model, RegistryValueKind.String);
                }

                using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                using (var nt = hklm.CreateSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", writable: true))
                {
                    if (nt is null) return false;
                    nt.SetValue("RegisteredOwner", owner, RegistryValueKind.String);
                    nt.SetValue("RegisteredOrganization", org, RegistryValueKind.String);
                }

                return true;
            }
            catch (UnauthorizedAccessException)
            {
                throw;
            }
            catch (System.Security.SecurityException)
            {
                throw;
            }
            catch
            {
                return false;
            }
        }

        try
        {
            if (TryWriteDirect())
            {
                var parts = new List<string>();
                if (!string.IsNullOrEmpty(manufacturer)) parts.Add($"Manufacturer=\"{manufacturer}\"");
                if (!string.IsNullOrEmpty(model)) parts.Add($"Model=\"{model}\"");
                parts.Add($"RegisteredOwner=\"{owner}\"");
                parts.Add($"RegisteredOrganization=\"{org}\"");
                return (true, $"Gravado em HKLM: {string.Join(", ", parts)}.");
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (System.Security.SecurityException) { }

        string EscapePs(string s) => s.Replace("'", "''");

        string ps = $"-NoProfile -NonInteractive -Command \"$oemK = 'Registry::HKEY_LOCAL_MACHINE\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\OEMInformation'; " +
                    "$ntK  = 'Registry::HKEY_LOCAL_MACHINE\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion'; " +
                    "if (-not (Test-Path $oemK)) { New-Item -Path $oemK -Force | Out-Null }; ";

        if (!string.IsNullOrEmpty(manufacturer))
            ps += $"Set-ItemProperty -Path $oemK -Name 'Manufacturer' -Value '{EscapePs(manufacturer)}' -Type String -Force; ";
        if (!string.IsNullOrEmpty(model))
            ps += $"Set-ItemProperty -Path $oemK -Name 'Model' -Value '{EscapePs(model)}' -Type String -Force; ";

        ps += $"Set-ItemProperty -Path $ntK -Name 'RegisteredOwner' -Value '{EscapePs(owner)}' -Type String -Force; " +
              $"Set-ItemProperty -Path $ntK -Name 'RegisteredOrganization' -Value '{EscapePs(org)}' -Type String -Force;\"";

        var elevated = await ElevatedProcessRunner.RunElevatedAsync("powershell.exe", ps, ct);

        return elevated.FailureReason == WingetFailureReason.ElevationCanceled
            ? (false, "Gravar as informações OEM no HKLM requer Administrador — elevação (UAC) recusada pelo usuário.")
            : elevated.Success
                ? (true, $"Gravado via processo elevado: Manufacturer={manufacturer ?? "(não definido)"}, Model={model ?? "(não definido)"}.")
                : (false, $"Falha ao gravar OEMInformation mesmo elevado. {elevated.Output}".Trim());
    }

    /// <summary>
    /// Reposiciona, na Área de Trabalho real da máquina-alvo, os atalhos (.lnk) correspondentes
    /// aos ícones que o usuário posicionou na simulação da tela Provisionamento.
    /// Funciona de forma confiável inclusive em Windows Sandbox e máquinas recém-instaladas:
    /// 1. Configura as chaves de Registro do Desktop (sem GPO) desativando AutoArrange e ativando SnapToGrid.
    /// 2. Se o atalho (.lnk) não estiver na Área de Trabalho (comum em pacotes winget que só criam no Menu Iniciar),
    ///    localiza o atalho no Menu Iniciar (usuário ou público) ou o executável via App Paths e cria/copia para o Desktop.
    ///    **Se já existir apenas em CommonDesktop (Público), copia para UserDesktop — atalhos públicos não
    ///    permitem posicionamento individual por usuário normal.**
    /// 3. Conecta dinamicamente à interface IFolderView do Explorer ao vivo e posiciona cada item nas coordenadas exatas.
    /// 4. Persiste o layout após posicionamento (SaveViewState + Refresh múltiplo) para não "voltar ao padrão".
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static class NativeMethods
    {
        public const uint AbmSetState = 0x0000000A;
        public const int AbsAutoHide = 0x00000001;
        public const int AbsAlwaysOnTop = 0x00000002;

        public const int ComputerNamePhysicalDnsHostname = 5;

        public const uint SpiSetDeskWallpaper = 0x0014;
        public const uint SpifUpdateIniFile = 0x01;
        public const uint SpifSendChange = 0x02;

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct APPBARDATA
        {
            public uint cbSize;
            public nint hWnd;
            public uint uCallbackMessage;
            public uint uEdge;
            public RECT rc;
            public int lParam;
        }

        [DllImport("shell32.dll")]
        public static extern uint SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern nint FindWindow(string lpClassName, string? lpWindowName);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetComputerNameEx(int nameType, string lpBuffer);

        /// <summary>Ver "SetUserGeoName function (winnls.h)" — Kernel32.dll, Windows 10 1709+.</summary>
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetUserGeoName(string geoName);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SystemParametersInfo(uint uiAction, uint uiParam, string pvParam, uint fWinIni);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern nint SendMessageTimeout(
            nint hWnd, uint msg, nint wParam, string lParam, uint fuFlags, uint uTimeout, out nint lpdwResult);

        // nint só aceita 0 como valor constante de compilação — 0xffff (HWND_BROADCAST)
        // precisa ser "static readonly" em vez de "const".
        private static readonly nint HwndBroadcast = 0xffff;
        private const uint WmSettingChange = 0x001A;
        private const uint SmtoAbortIfHung = 0x0002;

        /// <summary>
        /// Avisa todas as janelas de topo que uma configuração do sistema mudou — sem isso,
        /// mudar o Registro não reflete em apps já abertos até reiniciarem/relogarem.
        /// </summary>
        public static void BroadcastSettingChange(string setting)
        {
            SendMessageTimeout(HwndBroadcast, WmSettingChange, 0, setting, SmtoAbortIfHung, 2000, out _);
        }
    }
}
