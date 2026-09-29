using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace WinProvision.Core.Services.Backup;

/// <summary>
/// Guarda tokens e API keys criptografados em disco via DPAPI
/// (<see cref="ProtectedData"/>, escopo CurrentUser), vinculados à conta do Windows
/// que os salvou. O arquivo contém apenas o valor criptografado; outra conta do Windows
/// na mesma máquina não consegue descriptografá-lo.
///
/// Os metadados da conta não são sensíveis e podem ser lidos sem risco — só a
/// Tokens OAuth e API keys passam por aqui.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class SecureTokenStore
{
    public static void Save(string filePath, string token)
    {
        byte[] plain = Encoding.UTF8.GetBytes(token);
        try
        {
            byte[] protectedBytes = ProtectedData.Protect(plain, optionalEntropy: null, DataProtectionScope.CurrentUser);

            string? dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            string tempPath = filePath + ".tmp";
            try
            {
                File.WriteAllBytes(tempPath, protectedBytes);
                File.Move(tempPath, filePath, overwrite: true);
            }
            finally
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
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
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
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
