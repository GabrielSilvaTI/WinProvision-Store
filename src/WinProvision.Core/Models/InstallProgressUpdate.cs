namespace WinProvision.Core.Models;

public enum InstallProgressPhase
{
    Downloading,
    Preparing,
    Installing
}

public sealed record InstallProgressUpdate(
    InstallProgressPhase Phase,
    double? Percent = null,
    WingetMethod Method = WingetMethod.Unknown);
