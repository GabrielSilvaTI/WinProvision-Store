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

    // GUIDs oficiais dos planos de energia padrão do Windows (documentados pela Microsoft —
    // ver "powercfg -list" ou learn.microsoft.com/windows-hardware/customize/desktop/unattend/
    // microsoft-windows-powercpl-preferredplan). Usar o GUID em vez do alias (SCHEME_BALANCED
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

        string json = JsonSerializer.Serialize(profile, WinProvisionJsonOptions.Default);
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

        if (manifest.DesktopIconLayout is { Count: > 0 } iconLayout)
        {
            TryApply("Layout de ícones da área de trabalho", ApplyDesktopIconLayout, iconLayout, Report);
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
    /// Decodifica o Base64 do perfil, grava a imagem em
    /// %ProgramData%\WinProvision\Wallpaper\wallpaper&lt;ext&gt; (sempre o mesmo nome, sobrescrevendo
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
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
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
    private static (bool Success, string Message) ApplyDesktopIconLayout(List<DesktopIconPlacement> layout)
    {
        if (layout.Count == 0)
        {
            return (true, "Nenhum atalho para posicionar.");
        }

        // 1. Configuração direta via Registro (sem GPO):
        // Desativa AutoArrange, ativa SnapToGrid e assegura exibição de ícones na Área de Trabalho
        ConfigureDesktopRegistry();

        // 2. Localização e auto-criação de atalhos para a Área de Trabalho — com ESPERA em
        //    rodadas progressivas. Em máquinas recém-instaladas (Sandbox/VDI) o instalador
        //    cria os .lnk de forma ASSÍNCRONA: o winget retorna sucesso e o atalho aparece
        //    segundos depois. Por isso cada rodada espera um pouco mais antes de reverificar;
        //    esgotadas as rodadas, o EnsureDesktopShortcut ainda tenta CRIAR o atalho a
        //    partir do executável instalado (App Paths/MSIX/pastas comuns).
        var resolved = new List<(DesktopIconPlacement Placement, string LnkPath)>();
        var notFound = new List<string>();
        var autoCreated = new List<string>();

        // Atraso (ms) ANTES de cada varredura. Rodada 0 = verificação imediata (atalho já
        // existente), depois esperas progressivas para instaladores que demoram a criar
        // os atalhos. Janela total máxima: 0 + 2 + 3 + 5 + 10 = ~20s.
        int[] waitBeforeProbeMs = [0, 2_000, 3_000, 5_000, 10_000];
        var pending = new List<DesktopIconPlacement>(layout);

        for (int round = 0; round < waitBeforeProbeMs.Length && pending.Count > 0; round++)
        {
            if (waitBeforeProbeMs[round] > 0)
                Thread.Sleep(waitBeforeProbeMs[round]);

            var stillMissing = new List<DesktopIconPlacement>();
            foreach (var placement in pending)
            {
                var (lnkPath, wasCreatedOrCopied) = EnsureDesktopShortcut(placement);
                if (!string.IsNullOrEmpty(lnkPath))
                {
                    resolved.Add((placement, lnkPath));
                    if (wasCreatedOrCopied)
                        autoCreated.Add(placement.DisplayName);
                }
                else
                {
                    stillMissing.Add(placement);
                }
            }
            pending = stillMissing;
        }

        notFound.AddRange(pending.Select(p => p.DisplayName));

        if (resolved.Count == 0)
        {
            return (false, "Nenhum dos aplicativos especificados foi encontrado no sistema, no Menu Iniciar " +
                $"ou como executável instalado (procurados: {string.Join(", ", layout.Select(p => p.DisplayName))}). " +
                "Certifique-se de que os pacotes foram instalados antes desta etapa.");
        }

        // 3. Conexão ao vivo com o Explorer para posicionar os ícones na grade
        if (!DesktopIconInterop.TryGetLiveDesktopFolderView(out var folderView, out var refreshView, out var saveViewState, out string? error) || folderView is null)
        {
            // Se o Explorer live view não estiver acessível de imediato (ex.: execução em contexto não-interativo ou pré-logon),
            // os atalhos já foram criados/posicionados na pasta e o registro foi gravado com sucesso!
            string msg = $"{resolved.Count} atalho(s) configurado(s) na Área de Trabalho via Registro.";
            if (autoCreated.Count > 0)
                msg += $" Criados ou copiados do Público: {string.Join(", ", autoCreated)}.";
            if (notFound.Count > 0)
                msg += $" Não encontrados: {string.Join(", ", notFound)}.";
            msg += $" (Explorer live view indisponível: {error}).";

            return (true, msg);
        }

        try
        {
            // Desativa o AutoArrange na view ao vivo ANTES de posicionar. Sem isso o Explorer
            // reordena os ícones em coluna (abaixo da Lixeira) ignorando as posições pedidas.
            DesktopIconInterop.DisableAutoArrange(folderView);

            // Força múltiplos Refresh para que o Explorer veja os atalhos copiados do Público e
            // descarte qualquer posição em cache do instalador.
            for (int i = 0; i < 2; i++)
            {
                refreshView?.Invoke();
                Thread.Sleep(250);
            }

            // RE-ASSEGURA o AutoArrange desligado DEPOIS do último Refresh: a view pode ter
            // recarregado as flags da sessão/Registro e re-ligado a organização automática — o
            // que faria o Explorer jogar os atalhos na coluna abaixo da Lixeira.
            DesktopIconInterop.DisableAutoArrange(folderView);

            // ANCORAGEM REAL (antes: +16 adivinhado → causava ícones aparecerem "abaixo" ao invés de "ao lado").
            // 1) Pega espaçamento de fallback via API IFolderView::GetSpacing (em caso de Desktop vazio).
            var fallbackSpacing = DesktopIconInterop.GetSpacing(folderView);
            // 2) Mede a posição REAL da Lixeira e o espaçamento REAL entre células a partir dos
            //    itens atualmente visíveis (mesma linha / mesma coluna). Isso garante que
            //    Col=0,Row=0 da simulação = exatamente a Lixeira na tela real, e Col=1,Row=0
            //    = CÉLULA VIZINHA AO LADO, não abaixo.
            var (anchorX, anchorY, cellX, cellY) = DesktopIconInterop.GetDesktopAnchorAndSpacing(folderView, fallbackSpacing);

            var itemsToPosition = new List<(string LnkPath, string DisplayName, int X, int Y)>();
            foreach (var (placement, lnkPath) in resolved)
            {
                // Usa a âncora REAL (posição da Lixeira) como origem, mais o espaçamento REAL medido.
                int targetX = anchorX + placement.Column * cellX;
                int targetY = anchorY + placement.Row * cellY;
                itemsToPosition.Add((lnkPath, placement.DisplayName, targetX, targetY));
            }

            // Tenta o posicionamento até 3 vezes (o Explorer às vezes ignora a primeira tentativa
            // quando há AutoArrange desativado recentemente ou atalhos recém-copiados). Entre as
            // tentativas re-assegura o AutoArrange desligado: um Refresh da view pode recarregar
            // as flags a partir do Registro e re-ligar a organização automática.
            int applied = 0;
            List<string> failures = new();
            for (int attempt = 0; attempt < 3; attempt++)
            {
                DesktopIconInterop.DisableAutoArrange(folderView);
                (applied, failures) = DesktopIconInterop.TrySetItemsPositionsBatch(folderView, itemsToPosition);
                if (applied >= resolved.Count) break;
                Thread.Sleep(250);
                refreshView?.Invoke();
            }

            // ÚLTIMO RECURSO: se NENHUM ícone foi posicionado ao vivo (applied == 0) mas os
            // atalhos existem, reinicia o Explorer e tenta de novo. Comum logo após instalar
            // vários apps, quando a view do Desktop ainda está "presa" num estado ruim — o
            // reinício entrega uma view limpa. Aceitável em provisionamento (máquina recém-
            // configurada), pois a breve piscada do desktop não afeta trabalho do usuário.
            if (applied == 0 && resolved.Count > 0)
            {
                RestartExplorer();

                if (DesktopIconInterop.TryGetLiveDesktopFolderView(out var freshView, out var freshRefresh, out var freshSave, out _) && freshView is not null)
                {
                    try
                    {
                        DesktopIconInterop.DisableAutoArrange(freshView);
                        (applied, failures) = DesktopIconInterop.TrySetItemsPositionsBatch(freshView, itemsToPosition);
                        if (applied <= 0)
                        {
                            Thread.Sleep(500);
                            freshRefresh?.Invoke();
                            (applied, failures) = DesktopIconInterop.TrySetItemsPositionsBatch(freshView, itemsToPosition);
                        }
                        freshSave?.Invoke();
                        freshRefresh?.Invoke();
                    }
                    finally
                    {
                        DesktopIconInterop.Release(freshView);
                    }
                }

                // Se mesmo assim nada foi posicionado, o Explorer pode simplesmente não ter
                // conseguido; segue com a mensagem informando.
            }

            // Persiste o layout de ícones no estado salvo do Explorer (evita que "reiniciar o Explorer"
            // ou fazer logoff/logon jogue tudo de volta no canto superior esquerdo).
            saveViewState?.Invoke();
            DesktopIconInterop.NotifyShellDesktopUpdated();

            // Último refresh para garantir a renderização
            refreshView?.Invoke();

            var messageParts = new List<string>
            {
                $"{applied}/{resolved.Count} ícone(s) posicionado(s) na Área de Trabalho.",
                $"Âncora Lixeira=({anchorX},{anchorY}) Grade=({cellX}×{cellY}px)."
            };
            if (autoCreated.Count > 0) messageParts.Add($"Atalhos criados ou copiados do Público: {string.Join(", ", autoCreated)}.");
            if (notFound.Count > 0) messageParts.Add($"Não encontrados: {string.Join(", ", notFound)}.");
            if (failures.Count > 0) messageParts.Add($"Falha ao posicionar: {string.Join(", ", failures)}.");

            return (applied > 0 || resolved.Count > 0, string.Join(" ", messageParts));
        }
        finally
        {
            DesktopIconInterop.Release(folderView);
        }
    }

    /// <summary>
    /// Normaliza um nome de atalho/aplicativo removendo ruído comum (parênteses,
    /// versões, arquitetura, etc.) para permitir correspondência flexível.
    /// </summary>
    private static string NormalizeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        string s = name.Trim().ToLowerInvariant();
        // Remove ruído entre parênteses: "(x64)", "(2)", "(Português)", etc.
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\s*\([^)]*\)", "");
        // Remove separadores extras e espaços duplos
        s = System.Text.RegularExpressions.Regex.Replace(s, @"[\s_\-\.]+", " ");
        return s.Trim();
    }

    /// <summary>Retorna todos os tokens (palavras) significativos de um nome normalizado.</summary>
    private static HashSet<string> NameTokens(string normalized)
    {
        var tokens = new HashSet<string>(
            normalized.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries),
            StringComparer.OrdinalIgnoreCase);
        // Remove stop words / ruído muito comuns em nomes de atalhos
        tokens.RemoveWhere(t => t is "e" or "de" or "do" or "da" or "the" or "for" or "x86" or "x64" or "32" or "64" or "bit");
        return tokens;
    }

    /// <summary>
    /// Reinicia o Explorer (taskkill + start) — usado como ÚLTIMO recurso para obter uma view
    /// nova do Desktop quando o posicionamento ao vivo falhou com o Explorer em estado ruim.
    /// O desktop "pisca" por alguns segundos; em provisionamento (máquina recém-configurada)
    /// isso é aceitável e não afeta trabalho do usuário.
    /// </summary>
    private static void RestartExplorer()
    {
        try
        {
            using var kill = Process.Start(new ProcessStartInfo
            {
                FileName = "taskkill.exe",
                Arguments = "/f /im explorer.exe",
                CreateNoWindow = true,
                UseShellExecute = false,
            });
            kill?.WaitForExit(3000);
        }
        catch { }

        Thread.Sleep(1500);

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                UseShellExecute = true,
            });
        }
        catch { }

        Thread.Sleep(3000); // dá tempo do Explorer subir e criar a view do Desktop
    }

    /// <summary>Score mínimo (soma de heurísticas) para considerar um nome de arquivo corretamente correspondido.</summary>
    private const int DesktopShortcutScoreThreshold = 300;

    /// <summary>
    /// Pontua similaridade entre o nome de um arquivo (.lnk ou .exe) e o nome desejado do app.
    /// Valores altos = match forte (exato 10.000, id do winget 8.000, prefixo 5.000...). Usado
    /// tanto para localizar atalhos já criados quanto para escolher o executável instalado
    /// quando é preciso CRIAR o atalho na hora.
    /// </summary>
    private static int ScoreShortcutMatch(string fileNameNoExt, string normalizedWant, HashSet<string> wantTokens, string normalizedId)
    {
        string n = NormalizeName(fileNameNoExt);
        if (string.IsNullOrEmpty(n)) return 0;

        // Match exato (normalizado)
        if (n == normalizedWant) return 10_000;

        // Match exato com o Id (último componente do winget id)
        if (!string.IsNullOrEmpty(normalizedId) && n == normalizedId) return 8_000;

        // StartsWith / EndsWith do nome desejado
        int score = 0;
        if (!string.IsNullOrEmpty(normalizedWant))
        {
            if (n.StartsWith(normalizedWant, StringComparison.Ordinal)) score += 5000;
            else if (normalizedWant.StartsWith(n, StringComparison.Ordinal)) score += 4000;
            if (n.Contains(normalizedWant, StringComparison.Ordinal)) score += 1000;
        }

        // Correspondência de tokens (palavras em comum)
        var fileTokens = NameTokens(n);
        int common = fileTokens.Intersect(wantTokens, StringComparer.OrdinalIgnoreCase).Count();
        score += common * 500;

        // Id do winget parcial
        if (!string.IsNullOrEmpty(normalizedId))
        {
            if (n.StartsWith(normalizedId, StringComparison.Ordinal)) score += 2500;
            if (n.Contains(normalizedId, StringComparison.Ordinal)) score += 600;
        }

        // Penaliza por diferença de comprimento muito grande
        if (!string.IsNullOrEmpty(normalizedWant))
        {
            int diff = Math.Abs(n.Length - normalizedWant.Length);
            score -= Math.Max(0, diff - 4) * 20;
        }

        return score;
    }

    /// <summary>
    /// Localiza o executável instalado do app, usado como ÚLTIMO recurso para CRIAR o atalho
    /// .lnk no Desktop quando o instalador ainda não o criou sozinho — situação comum em
    /// máquinas recém-provisionadas/Sandbox, onde o winget termina antes do instalador
    /// finalizar a criação dos atalhos (criação assíncrona). Procura em:
    ///   1) App Paths (registrados por instaladores clássicos);
    ///   2) aliases de pacotes MSIX/Store em WindowsApps (AppExecutionAlias);
    ///   3) pastas de instalação comuns (Programs do usuário, Program Files, Program Files (x86)).
    /// </summary>
    private static string? FindInstalledExecutable(string displayName, string appId)
    {
        // 1) App Paths
        string? fromAppPaths = FindExecutableInAppPaths(displayName, appId);
        if (fromAppPaths is not null && File.Exists(fromAppPaths)) return fromAppPaths;

        string normalizedWant = NormalizeName(string.IsNullOrWhiteSpace(displayName) ? appId : displayName);
        var wantTokens = NameTokens(normalizedWant);
        string normalizedId = NormalizeName(string.IsNullOrWhiteSpace(appId) ? string.Empty : appId.Split('.').Last());

        string? best = null;
        int bestScore = 0;

        // 2) Aliases de pacotes MSIX/Store (AppExecutionAlias .exe)
        string windowsApps = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WindowsApps");
        if (Directory.Exists(windowsApps))
        {
            foreach (var alias in SafeEnumerateFiles(windowsApps, "*.exe"))
            {
                string fn = Path.GetFileNameWithoutExtension(alias);
                int sc = ScoreShortcutMatch(fn, normalizedWant, wantTokens, normalizedId);
                if (sc > bestScore) { bestScore = sc; best = alias; }
            }
        }

        // 3) Pastas de instalação comuns (raio de 2 níveis para não varrer o disco inteiro)
        string[] roots =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        ];

        foreach (string root in roots)
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;

            foreach (string dir in SafeEnumerateDirectories(root, depth: 2))
            {
                foreach (var exe in SafeEnumerateFiles(dir, "*.exe"))
                {
                    string fn = Path.GetFileNameWithoutExtension(exe);
                    int sc = ScoreShortcutMatch(fn, normalizedWant, wantTokens, normalizedId);
                    if (sc > bestScore) { bestScore = sc; best = exe; }
                }
            }
        }

        return bestScore >= DesktopShortcutScoreThreshold ? best : null;
    }

    /// <summary>Enumera os arquivos de um diretório com try/catch (pastas sem permissão são ignoradas).</summary>
    private static IEnumerable<string> SafeEnumerateFiles(string dir, string pattern)
    {
        try
        {
            return Directory.EnumerateFiles(dir, pattern, SearchOption.TopDirectoryOnly);
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// Enumera um diretório e seus subdiretórios até a profundidade informada, ignorando
    /// silenciosamente pastas sem permissão. Ex.: depth 0 = só a raiz; depth 1 = raiz + subpastas.
    /// </summary>
    private static IEnumerable<string> SafeEnumerateDirectories(string root, int depth)
    {
        var queue = new Queue<(string Dir, int Level)>();
        queue.Enqueue((root, 0));

        while (queue.Count > 0)
        {
            var (dir, level) = queue.Dequeue();
            yield return dir;
            if (level >= depth) continue;

            try
            {
                foreach (var sub in Directory.EnumerateDirectories(dir))
                    queue.Enqueue((sub, level + 1));
            }
            catch { }
        }
    }

    /// <summary>
    /// Garante que o atalho .lnk existe na Área de Trabalho da máquina-alvo.
    /// Regras importantes:
    /// 1. Se o atalho existir em CommonDesktopDirectory (Público) e não em UserDesktop,
    ///    COPIA para UserDesktop — porque atalhos públicos não podem ter sua posição
    ///    individual alterada por usuários não-administradores no Explorer (IFolderView
    ///    simplesmente ignora a mudança ou falha).
    /// 2. Usa correspondência flexível de nomes (parcial, tokens, sem ruído de versão/arq.)
    ///    para não depender de match exato — instaladores frequentemente adicionam
    ///    "(x64)", versão, idioma, etc. ao nome do atalho.
    /// 3. Se não existir, busca no Menu Iniciar.
    /// 4. Por fim, localiza o executável instalado (App Paths, aliases MSIX/Store ou pastas
    ///    de instalação comuns) e CRIA o atalho no Desktop — cobre o caso de máquinas
    ///    recém-provisionadas onde o instalador ainda não finalizou a criação dos atalhos.
    /// </summary>
    private static (string? Path, bool WasCreatedOrCopied) EnsureDesktopShortcut(DesktopIconPlacement placement)
    {
        string userDesktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        string commonDesktop = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);

        string wantName = placement.DisplayName ?? placement.AppId;
        string normalizedWant = NormalizeName(wantName);
        var wantTokens = NameTokens(normalizedWant);
        string idName = string.IsNullOrWhiteSpace(placement.AppId) ? string.Empty : placement.AppId.Split('.').Last();
        string normalizedId = NormalizeName(idName);

        // A pontuação de similaridade de nomes ficou no método estático da classe
        // ScoreShortcutMatch — reutilizado também pela busca de executáveis instalados.

        // A) Varre Áreas de Trabalho (Usuário + Público) com scoring flexível
        string? bestUserMatch = null;
        int bestUserScore = 0;
        string? bestCommonMatch = null;
        int bestCommonScore = 0;

        foreach (var (dir, isUser) in new[] { (Dir: userDesktop, IsUser: true), (Dir: commonDesktop, IsUser: false) })
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;

            try
            {
                foreach (var f in Directory.EnumerateFiles(dir, "*.lnk"))
                {
                    string fn = Path.GetFileNameWithoutExtension(f);
                    int sc = ScoreShortcutMatch(fn, normalizedWant, wantTokens, normalizedId);
                    if (sc <= 0) continue;

                    if (isUser)
                    {
                        if (sc > bestUserScore) { bestUserScore = sc; bestUserMatch = f; }
                    }
                    else
                    {
                        if (sc > bestCommonScore) { bestCommonScore = sc; bestCommonMatch = f; }
                    }
                }
            }
            catch { }
        }

        // Regra: se existe apenas no Público (comum) e com score bom,
        // COPIA para o Desktop do Usuário — posição de atalhos públicos é
        // global e não pode ser trocada por usuário normal via IFolderView.
        const int threshold = DesktopShortcutScoreThreshold;
        if (bestUserScore >= threshold)
        {
            return (bestUserMatch, false);
        }
        if (bestCommonScore >= threshold)
        {
            try
            {
                if (!string.IsNullOrEmpty(userDesktop) && Directory.Exists(userDesktop) && bestCommonMatch is not null)
                {
                    string destLnk = Path.Combine(userDesktop, Path.GetFileName(bestCommonMatch));
                    // Prefere o DisplayName como nome do arquivo de destino — facilita ID futuro
                    if (!string.IsNullOrWhiteSpace(placement.DisplayName))
                        destLnk = Path.Combine(userDesktop, placement.DisplayName + ".lnk");

                    File.Copy(bestCommonMatch, destLnk, overwrite: true);
                    // Tenta apagar do público caso tenhamos permissão (opcional, ignora erro)
                    try { File.Delete(bestCommonMatch); } catch { }
                    return (destLnk, true);
                }
                return (bestCommonMatch, false);
            }
            catch
            {
                return (bestCommonMatch, false);
            }
        }

        // B) Procurar no Menu Iniciar (do usuário e público) — também com scoring flexível
        string userPrograms = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        string commonPrograms = Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms);

        string? bestStartMatch = null;
        int bestStartScore = 0;

        foreach (var dir in new[] { userPrograms, commonPrograms })
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;

            try
            {
                foreach (var f in Directory.EnumerateFiles(dir, "*.lnk", SearchOption.AllDirectories))
                {
                    string fn = Path.GetFileNameWithoutExtension(f);
                    int sc = ScoreShortcutMatch(fn, normalizedWant, wantTokens, normalizedId);
                    if (sc <= 0) continue;
                    if (sc > bestStartScore) { bestStartScore = sc; bestStartMatch = f; }
                }
            }
            catch { }
        }

        if (bestStartScore >= threshold && bestStartMatch is not null && File.Exists(bestStartMatch))
        {
            try
            {
                string targetDir = Directory.Exists(userDesktop) ? userDesktop : commonDesktop;
                string destLnk = Path.Combine(targetDir,
                    string.IsNullOrWhiteSpace(placement.DisplayName)
                        ? Path.GetFileName(bestStartMatch)
                        : placement.DisplayName + ".lnk");
                File.Copy(bestStartMatch, destLnk, overwrite: true);
                return (destLnk, true);
            }
            catch { }
        }

        // C) Último recurso: localizar o executável instalado e CRIAR o atalho no Desktop.
        //    (App Paths → aliases MSIX/Store → pastas de instalação comuns.) Essencial em
        //    máquinas recém-provisionadas onde o instalador ainda não criou o atalho.
        string? exePath = FindInstalledExecutable(placement.DisplayName ?? "", placement.AppId ?? "");
        if (exePath is not null && File.Exists(exePath))
        {
            try
            {
                string targetDir = Directory.Exists(userDesktop) ? userDesktop : commonDesktop;
                string destLnk = Path.Combine(targetDir, (placement.DisplayName ?? placement.AppId) + ".lnk");
                if (TryCreateShortcut(destLnk, exePath))
                {
                    return (destLnk, true);
                }
            }
            catch { }
        }

        return (null, false);
    }

    private static string? FindExecutableInAppPaths(string displayName, string appId)
    {
        string simpleName = displayName.Replace(" ", "");
        string idName = appId.Split('.').Last();

        string[] names = [
            displayName + ".exe",
            simpleName + ".exe",
            idName + ".exe",
            displayName,
            simpleName
        ];

        string[] roots = [
            @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths",
            @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths"
        ];

        foreach (var root in roots)
        {
            foreach (var name in names)
            {
                try
                {
                    var val = Registry.GetValue($@"{root}\{name}", "", null) as string;
                    if (!string.IsNullOrEmpty(val))
                    {
                        val = val.Trim('"');
                        if (File.Exists(val)) return val;
                    }
                }
                catch { }
            }
        }

        // Busca em Program Files / LocalAppData / WindowsApps
        string[] searchDirs = [
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs")
        ];

        foreach (var sDir in searchDirs)
        {
            if (string.IsNullOrEmpty(sDir) || !Directory.Exists(sDir)) continue;
            try
            {
                string[] candidates = [
                    Path.Combine(sDir, displayName, displayName + ".exe"),
                    Path.Combine(sDir, displayName, simpleName + ".exe"),
                    Path.Combine(sDir, idName, idName + ".exe")
                ];

                foreach (var c in candidates)
                {
                    if (File.Exists(c)) return c;
                }
            }
            catch { }
        }

        return null;
    }

    private static bool TryCreateShortcut(string lnkPath, string targetPath)
    {
        try
        {
            Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null) return false;
            object? shell = Activator.CreateInstance(shellType);
            if (shell is null) return false;

            object? shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, [lnkPath]);
            if (shortcut is null) return false;

            Type shortcutType = shortcut.GetType();
            shortcutType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, [targetPath]);
            shortcutType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut, [Path.GetDirectoryName(targetPath) ?? ""]);
            shortcutType.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);

            return File.Exists(lnkPath);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Configurações da Área de Trabalho gravadas diretamente no Registro (sem GPO):
    /// - HKCU\Software\Microsoft\Windows\Shell\Bags\1\Desktop\FFlags:
    ///   Desativa AutoArrange (bit 0x01 = 0), ativa SnapToGrid (bit 0x02 = 1) e garante ícones visíveis (bit 0x400 = 1).
    /// - HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced:
    ///   HideIcons = 0 (Garante visibilidade dos ícones).
    /// </summary>
    private static void ConfigureDesktopRegistry()
    {
        try
        {
            const string bagsDesktopKey = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\Shell\Bags\1\Desktop";
            object? fflagsObj = Registry.GetValue(bagsDesktopKey, "FFlags", null);
            int fflags = fflagsObj is int i ? i : 0x40200224;
            fflags &= ~0x00000001; // Desativa AutoArrange
            fflags |= 0x00000002;  // Ativa SnapToGrid
            fflags |= 0x00000400;  // Garante exibição dos ícones

            Registry.SetValue(bagsDesktopKey, "FFlags", fflags, RegistryValueKind.DWord);

            const string advancedKey = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
            Registry.SetValue(advancedKey, "HideIcons", 0, RegistryValueKind.DWord);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ProvisioningService] Aviso ao configurar registro do Desktop: {ex.Message}");
        }
    }

    /// <summary>
    /// Interop COM com o Explorer do Windows para manipulação e posicionamento de ícones na Área de Trabalho.
    /// Utiliza Shell.Application -> IShellWindows -> IServiceProvider -> IShellBrowser -> IFolderView.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static class DesktopIconInterop
    {
        private const uint SvsiPositionItem = 0x00000080;

        private static readonly Guid IidIFolderView = new("cde725b0-ccc9-4519-917e-325d72fab4ce");
        private static readonly Guid IidIShellBrowser = new("000214E2-0000-0000-C000-000000000046");
        private static readonly Guid SidSTopLevelBrowser = new("4C96BE40-915C-11CF-99D3-00AA004AE837");

        [StructLayout(LayoutKind.Sequential)]
        public struct Point
        {
            public int X;
            public int Y;
        }

        [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IServiceProviderCom
        {
            [PreserveSig]
            int QueryService(in Guid guidService, in Guid riid, out IntPtr ppvObject);
        }

        [ComImport, Guid("000214E3-0000-0000-C000-000000000046")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellViewCom
        {
            // IShellView herda IOleWindow (GetWindow, ContextSensitiveHelp). Ordem da vtable:
            // 3 GetWindow | 4 ContextSensitiveHelp | 5 TranslateAccelerator | 6 EnableModeless |
            // 7 UIActivate | 8 Refresh | 9 CreateViewWindow | 10 DestroyViewWindow |
            // 11 GetCurrentInfo | 12 AddPropertySheetPages | 13 SaveViewState | ...
            void GetWindow_Unused();
            void ContextSensitiveHelp_Unused();
            void TranslateAccelerator_Unused();
            void EnableModeless_Unused();
            void UIActivate_Unused();

            [PreserveSig]
            int Refresh();

            void CreateViewWindow_Unused();
            void DestroyViewWindow_Unused();
            void GetCurrentInfo_Unused();
            void AddPropertySheetPages_Unused();

            [PreserveSig]
            int SaveViewState();
        }

        [ComImport, Guid("000214E2-0000-0000-C000-000000000046")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellBrowserCom
        {
            void GetWindow_Unused();
            void ContextSensitiveHelp_Unused();
            void InsertMenusSB_Unused();
            void SetMenuSB_Unused();
            void RemoveMenusSB_Unused();
            void SetStatusTextSB_Unused();
            void EnableModelessSB_Unused();
            void TranslateAcceleratorSB_Unused();
            void BrowseObject_Unused();
            void GetViewStateStream_Unused();
            void GetControlWindow_Unused();
            void SendControlMsg_Unused();

            [PreserveSig]
            int QueryActiveShellView(out IntPtr ppshv);
        }

        [ComImport, Guid("cde725b0-ccc9-4519-917e-325d72fab4ce")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IFolderView
        {
            void GetCurrentViewMode(out uint pViewMode);
            void SetCurrentViewMode(uint viewMode);
            void GetFolder(in Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
            void Item(int iItemIndex, out IntPtr ppidl);
            void ItemCount(uint uFlags, out int pcItems);
            void Items(uint uFlags, in Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
            void GetSelectionMarkedItem(out int piItem);
            void GetFocusedItem(out int piItem);
            void GetItemPosition(IntPtr pidl, out Point ppt);
            void GetSpacing(out Point ppt);
            void GetDefaultSpacing(out Point ppt);

            [PreserveSig]
            int GetAutoArrange();

            void SelectItem(int iItem, uint dwFlags);

            [PreserveSig]
            int SelectAndPositionItems(uint cidl, IntPtr apidl, IntPtr apt, uint dwFlags);
        }

        // IFolderView2 permite alterar as flags da view em tempo de execução via
        // SetCurrentFolderFlags — a forma documentada e confiável de desativar o AutoArrange
        // (FWF_AUTOARRANGE = 0x01) e ativar o SnapToGrid (FWF_SNAPTOGRID = 0x02) na view ao vivo.
        // Definida como interface INDEPENDENTE (não herdando IFolderView) para garantir a ordem
        // exata da vtable: os 14 métodos de IFolderView primeiro, depois SetGroupBy/GetGroupBy/
        // SetViewProperty/GetViewProperty/SetTileViewProperties/SetExtendedTileViewProperties/
        // SetText e então SetCurrentFolderFlags/GetCurrentFolderFlags.
        //
        // IMPORTANTE: o IID oficial desta interface é 1af3a467-214f-4298-908e-06b03e0b39f9
        // (SDK 10.0.16299.0+). Este código usava antes o IID 1e18bd10-4ed4-11d1-a6da-0060974790aa
        // (layout de outra era) que o Explorer moderno NÃO implementa — o QI falhava e o
        // AutoArrange nunca era realmente desativado, causando exatamente o sintoma
        // "ícones embaixo em vez de ao lado". Os métodos intermediários que não usamos são
        // stubs só para manter o deslocamento de vtable correto.
        [ComImport, Guid("1af3a467-214f-4298-908e-06b03e0b39f9")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IFolderView2
        {
            void GetCurrentViewMode(out uint pViewMode);
            void SetCurrentViewMode(uint viewMode);
            void GetFolder(in Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
            void Item(int iItemIndex, out IntPtr ppidl);
            void ItemCount(uint uFlags, out int pcItems);
            void Items(uint uFlags, in Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
            void GetSelectionMarkedItem(out int piItem);
            void GetFocusedItem(out int piItem);
            void GetItemPosition(IntPtr pidl, out Point ppt);
            void GetSpacing(out Point ppt);
            void GetDefaultSpacing(out Point ppt);

            [PreserveSig]
            int GetAutoArrange();

            void SelectItem(int iItem, uint dwFlags);

            [PreserveSig]
            int SelectAndPositionItems(uint cidl, IntPtr apidl, IntPtr apt, uint dwFlags);

            void SetGroupBy_Unused();
            void GetGroupBy_Unused();
            void SetViewProperty_Unused();
            void GetViewProperty_Unused();
            void SetTileViewProperties_Unused();
            void SetExtendedTileViewProperties_Unused();
            void SetText_Unused();

            [PreserveSig]
            int SetCurrentFolderFlags(uint dwMask, uint dwFlags);

            [PreserveSig]
            int GetCurrentFolderFlags(out uint dwFlags);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHParseDisplayName(string pszName, IntPtr pbc, out IntPtr ppidl, uint sfgaoIn, out uint psfgaoOut);

        [DllImport("shell32.dll")]
        private static extern IntPtr ILFindLastID(IntPtr pidl);

        [DllImport("shell32.dll")]
        private static extern void SHChangeNotify(uint wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
        private static extern int StrRetToBufW(ref STRRET pstr, IntPtr pidl, StringBuilder pszBuf, uint cchBuf);

        [StructLayout(LayoutKind.Explicit, Size = 264)]
        private struct STRRET
        {
            [FieldOffset(0)] public uint uType;
            [FieldOffset(4)] public IntPtr pOleStr;
            [FieldOffset(4)] public uint uOffset;
            [FieldOffset(4)] public IntPtr cStr;
        }

        private const uint ShgdnNormal = 0x00000000;
        private const uint ShgdnInFolder = 0x00000001;
        private const uint SvgAllitems = 0x2; // SVGIO_ALLVIEW (ver ShObjIdl_core.h; 0x40000 não é flag de SVGIO válida)

        [ComImport, Guid("000214E6-0000-0000-C000-000000000046")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellFolderCom
        {
            void ParseDisplayName_Unused();
            void EnumObjects_Unused();

            [PreserveSig]
            int BindToObject(IntPtr pidl, IntPtr pbc, in Guid riid, out IntPtr ppv);

            void BindToStorage_Unused();

            [PreserveSig]
            int CompareIDs_Unused();

            void CreateViewObject_Unused();
            void GetAttributesOf_Unused();

            [PreserveSig]
            int GetUIObjectOf_Unused();

            [PreserveSig]
            int GetDisplayNameOf(IntPtr pidl, uint uFlags, out STRRET pName);

            void SetNameOf_Unused();
        }

        public static void NotifyShellDesktopUpdated()
        {
            try
            {
                SHChangeNotify(0x08000000 /*SHCNE_ASSOCCHANGED*/, 0x0000 /*SHCNF_IDLIST*/, IntPtr.Zero, IntPtr.Zero);
            }
            catch { }
        }

        /// <summary>
        /// Obtém a IFolderView ativa da Área de Trabalho do Explorer através do Shell.Application.
        /// Inclui tentativas com intervalo para aguardar a inicialização do Explorer (essencial no Windows Sandbox)
        /// e fornece uma ação de atualização (refreshView) caso novos atalhos tenham acabado de ser criados.
        /// </summary>
        public static bool TryGetLiveDesktopFolderView(out IFolderView? folderView, out Action? refreshView, out Action? saveViewState, out string? error)
        {
            folderView = null;
            refreshView = null;
            saveViewState = null;
            error = null;

            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    Type? shellType = Type.GetTypeFromProgID("Shell.Application");
                    if (shellType is null)
                    {
                        error = "Objeto COM Shell.Application não disponível.";
                        return false;
                    }

                    object? shell = Activator.CreateInstance(shellType);
                    if (shell is null)
                    {
                        error = "Instância de Shell.Application é nula.";
                        return false;
                    }

                    object? windows = shellType.InvokeMember("Windows", BindingFlags.InvokeMethod, null, shell, null);
                    if (windows is null)
                    {
                        error = "Shell.Windows() retornou nulo.";
                        return false;
                    }

                    object[] args = [0, null!, 8 /*SWC_DESKTOP*/, 0, 1 /*SWFO_NEEDDISPATCH*/];
                    ParameterModifier[] mods = [new ParameterModifier(5)];
                    mods[0][3] = true;

                    object? desktopWin = windows.GetType().InvokeMember("FindWindowSW",
                        BindingFlags.InvokeMethod, null, windows, args, mods, null, null);

                    if (desktopWin is null)
                    {
                        if (attempt < 4)
                        {
                            Thread.Sleep(600);
                            continue;
                        }
                        error = "O Shell não retornou a janela da Área de Trabalho (SWC_DESKTOP).";
                        return false;
                    }

                    // Desativa AutoArrange em memória via FolderFlags do Explorer
                    try
                    {
                        object? doc = desktopWin.GetType().InvokeMember("Document", BindingFlags.GetProperty, null, desktopWin, null);
                        if (doc is not null)
                        {
                            uint folderFlags = (uint)doc.GetType().InvokeMember("FolderFlags", BindingFlags.GetProperty, null, doc, null)!;
                            if ((folderFlags & 0x01) != 0)
                            {
                                uint newFlags = folderFlags & ~0x01u;
                                doc.GetType().InvokeMember("FolderFlags", BindingFlags.SetProperty, null, doc, [newFlags]);
                            }
                        }
                    }
                    catch { }

                    var serviceProvider = (IServiceProviderCom)desktopWin;
                    var sidTopLevelBrowser = SidSTopLevelBrowser;
                    var iidShellBrowser = IidIShellBrowser;

                    int hr = serviceProvider.QueryService(in sidTopLevelBrowser, in iidShellBrowser, out IntPtr pShellBrowser);
                    if (hr < 0 || pShellBrowser == IntPtr.Zero)
                    {
                        error = $"QueryService(SID_STopLevelBrowser) falhou (0x{hr:X8}).";
                        return false;
                    }

                    var shellBrowser = (IShellBrowserCom)Marshal.GetObjectForIUnknown(pShellBrowser);
                    hr = shellBrowser.QueryActiveShellView(out IntPtr pShellView);
                    if (hr < 0 || pShellView == IntPtr.Zero)
                    {
                        error = $"QueryActiveShellView falhou (0x{hr:X8}).";
                        return false;
                    }

                    var shellView = (IShellViewCom)Marshal.GetObjectForIUnknown(pShellView);
                    refreshView = () =>
                    {
                        try
                        {
                            shellView.Refresh();
                            Thread.Sleep(350);
                        }
                        catch { }
                    };
                    // IShellView::SaveViewState — faz o Explorer gravar o estado atual da view
                    // (Bags\1\Desktop, incluindo IconLayouts) no Registro. Sem isso as posições
                    // definidas via COM não sobrevivem a logoff/reinício do Explorer.
                    saveViewState = () =>
                    {
                        try
                        {
                            shellView.SaveViewState();
                            Thread.Sleep(50);
                        }
                        catch { }
                    };

                    var iidFolderView = IidIFolderView;
                    hr = Marshal.QueryInterface(pShellView, in iidFolderView, out IntPtr pFolderView);
                    if (hr < 0 || pFolderView == IntPtr.Zero)
                    {
                        error = $"IFolderView não suportado na view ativa (0x{hr:X8}).";
                        return false;
                    }

                    folderView = (IFolderView)Marshal.GetObjectForIUnknown(pFolderView);
                    return true;
                }
                catch (Exception ex)
                {
                    if (attempt < 4)
                    {
                        Thread.Sleep(600);
                        continue;
                    }
                    error = ex.Message;
                    return false;
                }
            }

            return false;
        }

        public static Point GetSpacing(IFolderView folderView)
        {
            try
            {
                folderView.GetSpacing(out var pt);
                if (pt.X > 0 && pt.Y > 0) return pt;
            }
            catch { }

            return new Point { X = 76, Y = 100 };
        }

        /// <summary>
        /// Desativa o AutoArrange e ativa o SnapToGrid na view ao vivo do Explorer.
        /// Sem isso, o Explorer reordena todos os ícones em uma única coluna (abaixo da Lixeira)
        /// ignorando as posições pedidas via SelectAndPositionItems — exatamente o sintoma
        /// "ícones embaixo em vez de ao lado" relatado.
        ///
        /// Caminho A (confiável): IFolderView2::SetCurrentFolderFlags (IID oficial
        /// 1af3a467-214f-4298-908e-06b03e0b39f9). Depois de setar, VERIFICA com
        /// GetCurrentFolderFlags que o bit FWF_AUTOARRANGE realmente saiu — um único Set pode
        /// ser "engolido" pela view ocupada (ex.: logo após Refresh de atalhos recém-criados),
        /// então repete até 3 vezes. Antes este código usava um IID errado/layout antigo de
        /// IFolderView2, o QI falhava silenciosamente e o AutoArrange ficava ligado — os ícones
        /// caíam na coluna automática abaixo da Lixeira.
        ///
        /// Caminho B (fallback): reflection de FolderFlags no objeto Document do Desktop.
        /// </summary>
        public static void DisableAutoArrange(IFolderView folderView)
        {
            if (folderView is null || !Marshal.IsComObject(folderView)) return;

            const uint fwfAutoArrange = 0x00000001;
            const uint fwfSnapToGrid = 0x00000002;

            // Caminho A (confiável): IFolderView2::SetCurrentFolderFlags + verificação.
            if (folderView is IFolderView2 fv2)
            {
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    try
                    {
                        fv2.SetCurrentFolderFlags(fwfAutoArrange, 0);           // desativa AutoArrange
                        fv2.SetCurrentFolderFlags(fwfSnapToGrid, fwfSnapToGrid); // ativa SnapToGrid

                        if (GetAutoArrangeState(fv2) == false)
                        {
                            return; // confirmado: AutoArrange desligado na view
                        }
                    }
                    catch { }

                    Thread.Sleep(50);
                }
                // Se chegou aqui o AutoArrange segue ligado apesar do IFolderView2 — cai pro Caminho B.
            }

            // Caminho B (fallback): reflection de FolderFlags no objeto Document
            try
            {
                var shellApp = Type.GetTypeFromProgID("Shell.Application");
                if (shellApp is null) return;
                object? shell = Activator.CreateInstance(shellApp);
                object? windows = shell?.GetType().InvokeMember("Windows", BindingFlags.InvokeMethod, null, shell, null);
                if (windows is null) return;

                object[] args = [0, null!, 8 /*SWC_DESKTOP*/, 0, 1 /*SWFO_NEEDDISPATCH*/];
                ParameterModifier[] mods = [new ParameterModifier(5)];
                mods[0][3] = true;
                object? desktopWin = windows.GetType().InvokeMember("FindWindowSW",
                    BindingFlags.InvokeMethod, null, windows, args, mods, null, null);
                object? doc = desktopWin?.GetType().InvokeMember("Document", BindingFlags.GetProperty, null, desktopWin, null);
                if (doc is not null)
                {
                    uint folderFlags = (uint)doc.GetType().InvokeMember("FolderFlags", BindingFlags.GetProperty, null, doc, null)!;
                    uint newFlags = (folderFlags & ~fwfAutoArrange) | fwfSnapToGrid;
                    if (newFlags != folderFlags)
                    {
                        doc.GetType().InvokeMember("FolderFlags", BindingFlags.SetProperty, null, doc, [newFlags]);
                    }
                }
            }
            catch { }
        }

        /// <summary>
        /// Lê as flags correntes da view via IFolderView2::GetCurrentFolderFlags e devolve
        /// true se FWF_AUTOARRANGE estiver ligado; null se a chamada falhar.
        /// </summary>
        private static bool? GetAutoArrangeState(IFolderView2 fv2)
        {
            try
            {
                fv2.GetCurrentFolderFlags(out uint flags);
                return (flags & 0x00000001) != 0;
            }
            catch
            {
                return null;
            }
        }

        private static string StrRetToString(ref STRRET strret, IntPtr pidl)
        {
            var sb = new StringBuilder(512);
            int hr = StrRetToBufW(ref strret, pidl, sb, (uint)sb.Capacity);
            return hr >= 0 ? sb.ToString() : string.Empty;
        }

        /// <summary>
        /// Enumera TODOS os itens atualmente visíveis na Área de Trabalho real do Explorer
        /// (incluindo ícones de sistema como Lixeira, Este Computador, Arquivos, Rede, etc. — além
        /// dos atalhos .lnk normais), retornando (DisplayName, X, Y) para cada um.
        /// Essencial para "ancorar" o nosso layout virtual em itens que o usuário já tem na área
        /// de trabalho (ex.: Lixeira), e para medir o espaçamento REAL das células em vez de
        /// adivinhar offset.
        /// </summary>
        public static List<(string DisplayName, int X, int Y)> EnumerateDesktopItems(IFolderView folderView)
        {
            var result = new List<(string, int, int)>();
            try
            {
                folderView.ItemCount(SvgAllitems, out int count);
                if (count <= 0) return result;

                Guid iidShellFolder = typeof(IShellFolderCom).GUID;
                folderView.GetFolder(in iidShellFolder, out object? shellFolderObj);
                if (shellFolderObj is not IShellFolderCom shellFolder) return result;

                for (int i = 0; i < count; i++)
                {
                    try
                    {
                        folderView.Item(i, out IntPtr pidlChild);
                        if (pidlChild == IntPtr.Zero) continue;

                        string? name = null;
                        try
                        {
                            int hrName = shellFolder.GetDisplayNameOf(pidlChild, ShgdnNormal, out STRRET strret);
                            if (hrName >= 0)
                                name = StrRetToString(ref strret, pidlChild);
                        }
                        catch { }

                        folderView.GetItemPosition(pidlChild, out Point pos);

                        result.Add((name ?? $"<item-{i}>", pos.X, pos.Y));
                    }
                    catch { }
                }
            }
            catch { }

            return result;
        }

        /// <summary>
        /// Detecta a referência de origem (âncora) para (Coluna 0, Linha 0) a partir do item
        /// "Lixeira" (em português) ou "Recycle Bin" (em inglês), que SEMPRE está na
        /// Área de Trabalho e ocupa a posição de slot (0,0) na nossa simulação.
        /// Também tenta calcular o espaçamento REAL entre células a partir de itens vizinhos.
        /// Retorna (AnchorX, AnchorY, SpacingX, SpacingY).
        /// </summary>
        public static (int AnchorX, int AnchorY, int SpacingX, int SpacingY) GetDesktopAnchorAndSpacing(
            IFolderView folderView,
            Point fallbackSpacing)
        {
            // 1) Enumera todos os itens reais
            var items = EnumerateDesktopItems(folderView);
            if (items.Count == 0)
                return (16, 16, fallbackSpacing.X, fallbackSpacing.Y);

            // 2) Procura a Lixeira (o item que a nossa simulação fixa em Col=0, Row=0)
            var recycleNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "lixeira", "recycle bin", "recyclebin", "přepravka na odpadky",
                "papierkorb", "cestino", "papelera", "corbeille", "prullenbak"
            };
            (string DisplayName, int X, int Y)? recycle = null;
            foreach (var it in items)
            {
                string norm = (it.DisplayName ?? string.Empty).Trim().ToLowerInvariant();
                if (recycleNames.Contains(norm) || norm.Contains("lixeira") || norm.Contains("recycle"))
                {
                    recycle = it;
                    break;
                }
            }

            int anchorX, anchorY;
            if (recycle.HasValue)
            {
                anchorX = recycle.Value.X;
                anchorY = recycle.Value.Y;
            }
            else
            {
                // Fallback: usa o item com menor X + Y (canto superior esquerdo) como âncora
                var topLeft = items.OrderBy(i => i.X + i.Y).First();
                anchorX = topLeft.X;
                anchorY = topLeft.Y;
            }

            // 3) Tenta calcular o espaçamento REAL a partir de itens vizinhos.
            //    Pega 2 itens que estejam "na mesma linha" (Y próximo) com X diferente:
            //    SpacingX = delta X. E 2 itens "na mesma coluna": SpacingY = delta Y.
            int spacingX = fallbackSpacing.X;
            int spacingY = fallbackSpacing.Y;
            bool foundX = false, foundY = false;

            // Mesma linha (Y estável) — medir delta X
            var byY = items.GroupBy(i => (int)Math.Round(i.Y / 20.0) * 20)
                           .Where(g => g.Count() >= 2)
                           .OrderBy(g => g.Key)
                           .FirstOrDefault();
            if (byY is not null)
            {
                var sortedX = byY.OrderBy(i => i.X).ToList();
                for (int k = 1; k < sortedX.Count && !foundX; k++)
                {
                    int dx = sortedX[k].X - sortedX[k - 1].X;
                    if (dx is > 40 and < 300)
                    {
                        spacingX = dx;
                        foundX = true;
                    }
                }
            }

            // Mesma coluna (X estável) — medir delta Y
            var byX = items.GroupBy(i => (int)Math.Round(i.X / 20.0) * 20)
                           .Where(g => g.Count() >= 2)
                           .OrderBy(g => g.Key)
                           .FirstOrDefault();
            if (byX is not null)
            {
                var sortedY = byX.OrderBy(i => i.Y).ToList();
                for (int k = 1; k < sortedY.Count && !foundY; k++)
                {
                    int dy = sortedY[k].Y - sortedY[k - 1].Y;
                    if (dy is > 50 and < 400)
                    {
                        spacingY = dy;
                        foundY = true;
                    }
                }
            }

            return (anchorX, anchorY, spacingX, spacingY);
        }

        /// <summary>
        /// Posiciona uma lista de atalhos na Área de Trabalho do Windows de forma simultânea (batch)
        /// através de SelectAndPositionItems(cidl, apidl, apt, SVSI_POSITIONITEM).
        /// Se a chamada em lote falhar, realiza fallback posicionando cada atalho individualmente.
        /// </summary>
        public static (int Succeeded, List<string> Failures) TrySetItemsPositionsBatch(
            IFolderView folderView,
            IList<(string LnkPath, string DisplayName, int X, int Y)> items)
        {
            var failures = new List<string>();
            if (items.Count == 0) return (0, failures);

            var validPointers = new List<(IntPtr FullPidl, IntPtr ChildPidl, string DisplayName, int X, int Y)>();

            foreach (var item in items)
            {
                int hr = SHParseDisplayName(item.LnkPath, IntPtr.Zero, out IntPtr pidlFull, 0, out _);
                if (hr >= 0 && pidlFull != IntPtr.Zero)
                {
                    IntPtr child = ILFindLastID(pidlFull);
                    if (child != IntPtr.Zero)
                    {
                        validPointers.Add((pidlFull, child, item.DisplayName, item.X, item.Y));
                        continue;
                    }
                    Marshal.FreeCoTaskMem(pidlFull);
                }
                failures.Add(item.DisplayName);
            }

            if (validPointers.Count == 0)
            {
                return (0, failures);
            }

            // Tenta o posicionamento em lote (batch) de todos os itens de uma só vez
            IntPtr pApidl = IntPtr.Zero;
            IntPtr pApt = IntPtr.Zero;
            bool batchSuccess = false;

            try
            {
                pApidl = Marshal.AllocHGlobal(IntPtr.Size * validPointers.Count);
                for (int i = 0; i < validPointers.Count; i++)
                {
                    Marshal.WriteIntPtr(pApidl, i * IntPtr.Size, validPointers[i].ChildPidl);
                }

                pApt = Marshal.AllocHGlobal(Marshal.SizeOf<Point>() * validPointers.Count);
                for (int i = 0; i < validPointers.Count; i++)
                {
                    var pt = new Point { X = validPointers[i].X, Y = validPointers[i].Y };
                    Marshal.StructureToPtr(pt, pApt + i * Marshal.SizeOf<Point>(), false);
                }

                int hrBatch = folderView.SelectAndPositionItems((uint)validPointers.Count, pApidl, pApt, SvsiPositionItem);
                batchSuccess = hrBatch >= 0;
            }
            catch
            {
                batchSuccess = false;
            }
            finally
            {
                if (pApidl != IntPtr.Zero) Marshal.FreeHGlobal(pApidl);
                if (pApt != IntPtr.Zero) Marshal.FreeHGlobal(pApt);
            }

            int succeeded = 0;
            if (batchSuccess)
            {
                succeeded = validPointers.Count;
            }
            else
            {
                // Fallback: se a chamada em lote falhar, posiciona item a item individualmente
                foreach (var vp in validPointers)
                {
                    IntPtr singleApidl = Marshal.AllocHGlobal(IntPtr.Size);
                    IntPtr singleApt = Marshal.AllocHGlobal(Marshal.SizeOf<Point>());
                    try
                    {
                        Marshal.WriteIntPtr(singleApidl, vp.ChildPidl);
                        Marshal.StructureToPtr(new Point { X = vp.X, Y = vp.Y }, singleApt, false);
                        int hr = folderView.SelectAndPositionItems(1, singleApidl, singleApt, SvsiPositionItem);
                        if (hr >= 0)
                        {
                            succeeded++;
                        }
                        else
                        {
                            failures.Add(vp.DisplayName);
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(singleApidl);
                        Marshal.FreeHGlobal(singleApt);
                    }
                }
            }

            foreach (var vp in validPointers)
            {
                Marshal.FreeCoTaskMem(vp.FullPidl);
            }

            return (succeeded, failures);
        }

        public static void Release(IFolderView? folderView)
        {
            if (folderView is not null && Marshal.IsComObject(folderView))
            {
                Marshal.ReleaseComObject(folderView);
            }
        }
    }

    /// <summary>P/Invoke usado só por este serviço — mantido privado pra não vazar detalhes de Win32 pro resto do Core.</summary>
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
