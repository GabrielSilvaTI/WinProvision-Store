using WinProvision.Core.Models;

namespace WinProvision.Core.Services.Backup;

/// <summary>Resultado de <see cref="GitHubBackupService.ConnectAsync"/>.</summary>
public record GitHubConnectResult(bool Success, string? ErrorMessage = null, string? Login = null)
{
    public static GitHubConnectResult Ok(string login) => new(true, null, login);
    public static GitHubConnectResult Fail(string message) => new(false, message);
}

/// <summary>Resultado da publicação do bootstrap e do perfil em um Gist.</summary>
public record GitHubBootstrapPublishResult(bool Success, string? RawUrl = null, string? ErrorMessage = null)
{
    public static GitHubBootstrapPublishResult Ok(string rawUrl) => new(true, rawUrl);
    public static GitHubBootstrapPublishResult Fail(string message) => new(false, null, message);
}

public enum BootstrapDisplayMode
{
    UserInterface,
    Terminal
}

public enum BootstrapLogMode
{
    Local,
    Cloud
}

public record GitHubBackupUploadResult(bool Success, string? ErrorMessage = null)
{
    public static GitHubBackupUploadResult Ok() => new(true);
    public static GitHubBackupUploadResult Fail(string message) => new(false, message);
}

public record GitHubBackupDownloadResult(ProfileBackupSet? BackupSet, string? ErrorMessage = null)
{
    public bool Success => BackupSet is not null;
    public static GitHubBackupDownloadResult Ok(ProfileBackupSet backupSet) => new(backupSet);
    public static GitHubBackupDownloadResult NotFound() => new(null!);
    public static GitHubBackupDownloadResult Fail(string message) => new(null!, message);
}

/// <summary>
/// Metadados persistidos em disco (texto puro, não sensível) sobre a conexão com o
/// GitHub: login, id do Gist secreto usado como backup e a última sincronização
/// bem-sucedida. O Personal Access Token em si NUNCA fica aqui — ele vive
/// separadamente, protegido via <see cref="SecureTokenStore"/> (DPAPI).
/// </summary>
internal class BackupAccountInfo
{
    public string? Login { get; set; }
    public string? AvatarUrl { get; set; }
    public string? GistId { get; set; }
    public string? BootstrapGistId { get; set; }
    public DateTime? LastSyncUtc { get; set; }
}
