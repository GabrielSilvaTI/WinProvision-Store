using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace WinProvision.Core.Services;

/// <summary>
/// Caminho do winget.exe usado por TODO o app (CLI, elevação, listagens). Por padrão é o
/// alias "winget.exe" do PATH. Só muda quando o bootstrap (ver <see cref="WingetBootstrapper"/>)
/// confirma que o alias não existe mas o pacote App Installer está instalado em
/// "Program Files\WindowsApps": caso típico da conta SYSTEM, que não tem o alias por usuário
/// em %LocalAppData%\Microsoft\WindowsApps, e de sessões em que o alias ainda não foi criado.
/// </summary>
public static class WingetLocator
{
    public const string DefaultExecutable = "winget.exe";

    private const string PackageFolderPrefix = "Microsoft.DesktopAppInstaller_";
    private const string PublisherSuffix = "__8wekyb3d8bbwe";

    private static string _executablePath = DefaultExecutable;

    /// <summary>Executável a usar em qualquer chamada ao winget (alias ou caminho completo).</summary>
    public static string ExecutablePath => Volatile.Read(ref _executablePath);

    internal static void UsePackagedPath(string path) => Volatile.Write(ref _executablePath, path);

    /// <summary>
    /// Procura o winget.exe do App Installer já instalado em "Program Files\WindowsApps"
    /// (pasta "Microsoft.DesktopAppInstaller_{versão}_{arq}__8wekyb3d8bbwe"), escolhendo a
    /// maior versão. Devolve null se não achar ou se a pasta não for legível (contas sem
    /// permissão em WindowsApps; SYSTEM lê).
    /// </summary>
    public static string? FindPackagedExecutable()
    {
        try
        {
            string root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");
            if (!Directory.Exists(root))
                return null;

            string suffix = "_" + ArchitectureFolderName() + PublisherSuffix;

            return Directory.EnumerateDirectories(root, PackageFolderPrefix + "*")
                .Where(dir => Path.GetFileName(dir).EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(dir => ParseVersion(Path.GetFileName(dir)))
                .Select(dir => Path.Combine(dir, "winget.exe"))
                .FirstOrDefault(File.Exists);
        }
        catch
        {
            // Sem permissão / pasta inacessível: trata como "não encontrado".
            return null;
        }
    }

    private static Version ParseVersion(string folderName)
    {
        string[] parts = folderName.Split('_');
        return parts.Length > 1 && Version.TryParse(parts[1], out var version)
            ? version
            : new Version(0, 0);
    }

    private static string ArchitectureFolderName() => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => "x64",
        Architecture.X86 => "x86",
        Architecture.Arm64 => "arm64",
        Architecture.Arm => "arm",
        var other => other.ToString().ToLowerInvariant(),
    };
}
