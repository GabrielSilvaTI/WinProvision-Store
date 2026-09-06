namespace WinProvision.Core.Services.Backup;

/// <summary>Resultado de <see cref="GitHubBackupService.ConnectAsync"/>.</summary>
public record GitHubConnectResult(bool Success, string? ErrorMessage = null, string? Login = null)
{
    public static GitHubConnectResult Ok(string login) => new(true, null, login);
    public static GitHubConnectResult Fail(string message) => new(false, message);
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
    public string? GistId { get; set; }
    public DateTime? LastSyncUtc { get; set; }
}
