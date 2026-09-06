namespace WinProvision.Core.Services;

/// <summary>
/// Cada bloco de trabalho que o modo CLI /auto pode reportar separadamente pra UI
/// (<c>AutoWindow</c>). Uma execução não passa necessariamente por todos — ver
/// <see cref="AutoInstallCliService.PlanStages"/>, que decide quais se aplicam de
/// acordo com o que o próprio <see cref="Models.ProfileManifest"/> contém.
/// </summary>
public enum AutoInstallStage
{
    PackagesAndApps,
    MicrosoftOffice,
    SystemPersonalization,
    PredefinedConfigurations,
}

public enum AutoInstallStageState
{
    Pending,
    InProgress,
    Completed,
    /// <summary>Etapa terminou, mas pelo menos um item dela falhou (nem todos, senão seria Failed) — ícone amarelo de aviso em vez de vermelho/verde.</summary>
    CompletedWithWarnings,
    Failed,
}

/// <summary>Metadados fixos (título/descrição) de uma etapa — usados pela UI pra montar a lista antes de a execução começar.</summary>
public record AutoInstallStageInfo(AutoInstallStage Stage, string Title, string Description);

/// <summary>Notificação de mudança de estado de uma etapa, reportada via <see cref="IProgress{T}"/> durante a execução.</summary>
public record AutoInstallStageEvent(
    AutoInstallStage Stage,
    AutoInstallStageState State,
    double Progress = 0,
    string? Detail = null);
