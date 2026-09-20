namespace WinProvision.Core.Models;

public enum InstallProgressPhase
{
    Downloading,
    Preparing,
    Installing
}

public sealed record InstallProgressUpdate(
    InstallProgressPhase Phase,
    int? Percent = null);
