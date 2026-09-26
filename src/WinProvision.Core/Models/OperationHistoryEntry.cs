using System;

namespace WinProvision.Core.Models;

public sealed record OperationHistoryEntry(
    Guid OperationId,
    string AppName,
    OperationKind Kind,
    OperationState State,
    DateTimeOffset FinishedAt,
    WingetMethod Method)
{
    public string KindLabel => Kind switch
    {
        OperationKind.Install => "Instalação",
        OperationKind.Update => "Atualização",
        OperationKind.Uninstall => "Desinstalação",
        _ => "Operação"
    };

    public string StateLabel => State switch
    {
        OperationState.Completed => "Concluída",
        OperationState.Failed => "Falhou",
        OperationState.Canceled => "Cancelada",
        _ => "Finalizada"
    };

    public string FinishedAtLocal => FinishedAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm");

    public string MethodLabel => Method switch
    {
        WingetMethod.ComApi => "API do WinGet",
        WingetMethod.OwnApi => "API da Store",
        WingetMethod.WingetExe => "WinGet CLI",
        _ => "Método não identificado"
    };
}
