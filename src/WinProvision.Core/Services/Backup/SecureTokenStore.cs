using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace WinProvision.Core.Services.Backup;

/// <summary>
/// Guarda o Personal Access Token do GitHub criptografado em disco via DPAPI
/// (<see cref="ProtectedData"/>, escopo CurrentUser) — o mesmo mecanismo usado pelo
/// Credential Manager do Windows por baixo dos panos. Isso mantém o token fora de
/// texto puro no disco e amarrado ao usuário do Windows que o salvou: outra conta do
/// Windows na mesma máquina não consegue descriptografar o arquivo.
///
/// Deliberadamente separado de <see cref="BackupAccountInfo"/> (login/GistId/data),
/// que não é sensível e pode ser lido/logado sem risco — só o token passa por aqui.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class SecureTokenStore
{
    public static void Save(string filePath, string token)
    {
        byte[] plain = Encoding.UTF8.GetBytes(token);
        byte[] protectedBytes = ProtectedData.Protect(plain, optionalEntropy: null, DataProtectionScope.CurrentUser);

        string? dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        File.WriteAllBytes(filePath, protectedBytes);
    }

    /// <summary>Retorna null se o arquivo não existir ou não puder ser descriptografado
    /// (ex.: token salvo por outro usuário do Windows, ou arquivo corrompido) — nesses
    /// casos o chamador deve tratar como "não conectado", nunca lançar pro usuário.</summary>
    public static string? TryLoad(string filePath)
    {
        if (!File.Exists(filePath))
            return null;

        try
        {
            byte[] protectedBytes = File.ReadAllBytes(filePath);
            byte[] plain = ProtectedData.Unprotect(protectedBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    public static void Delete(string filePath)
    {
        if (File.Exists(filePath))
            File.Delete(filePath);
    }
}
