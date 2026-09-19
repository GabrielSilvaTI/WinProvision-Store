using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace WinProvision.Store.Services;

internal static class WinGetDiagnosticLog
{
    private static readonly object Gate = new();
    private static readonly string Path =
        System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "WinProvision-winget.log");

    public static string FilePath => Path;

    public static void Write(string message)
    {
        var line =
            $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] " +
            $"PID={Environment.ProcessId} TID={Environment.CurrentManagedThreadId} {message}";

        Trace.WriteLine(line);
        lock (Gate)
        {
            try
            {
                var directory = System.IO.Path.GetDirectoryName(Path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.AppendAllText(Path, line + Environment.NewLine);
            }
            catch
            {
                // Diagnostic logging must never change installation behavior.
            }
        }
    }

    public static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(
            WindowsBuiltInRole.Administrator);
    }

    public static void WriteComServerInfo()
    {
        var servers = Process.GetProcessesByName("WindowsPackageManagerServer");
        if (servers.Length == 0)
        {
            Write("COM SERVER name=WindowsPackageManagerServer pid=not-found");
            return;
        }

        foreach (var server in servers)
        {
            using (server)
            {
                var path = string.Empty;
                try { path = server.MainModule?.FileName ?? string.Empty; }
                catch (Exception ex) { path = $"unavailable:{ex.GetType().Name}"; }

                Write(
                    $"COM SERVER name={server.ProcessName}.exe pid={server.Id} " +
                    $"integrity={GetIntegrityLevel(server.Id)} path=\"{path}\"");
            }
        }

        Write(
            $"COM CLIENT pid={Environment.ProcessId} integrity={GetIntegrityLevel(Environment.ProcessId)} " +
            $"elevated={IsElevated()}");
    }

    private static string GetIntegrityLevel(int processId)
    {
        var process = OpenProcess(0x1000, false, processId);
        if (!OpenProcessToken(
                process,
                0x0008,
                out var token))
        {
            CloseHandle(process);
            return "unavailable";
        }

        try
        {
            const int TokenIntegrityLevel = 25;
            if (!GetTokenInformation(token, TokenIntegrityLevel, IntPtr.Zero, 0, out var length) &&
                length == 0)
            {
                return "unavailable";
            }

            var buffer = Marshal.AllocHGlobal((int)length);
            try
            {
                if (!GetTokenInformation(
                        token, TokenIntegrityLevel, buffer, length, out _))
                {
                    return "unavailable";
                }

                var label = Marshal.ReadIntPtr(buffer);
                var subAuthorityCount = Marshal.ReadByte(label, 1);
                var rid = Marshal.ReadInt32(
                    IntPtr.Add(label, 8 + ((subAuthorityCount - 1) * 4)));
                return rid switch
                {
                    >= 0x500 => "System",
                    >= 0x400 => "High",
                    >= 0x300 => "Medium",
                    >= 0x200 => "Low",
                    _ => $"0x{rid:X}"
                };
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            CloseHandle(token);
            CloseHandle(process);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inheritHandle, int processId);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(
        IntPtr processHandle,
        uint desiredAccess,
        out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(
        IntPtr tokenHandle,
        int tokenInformationClass,
        IntPtr tokenInformation,
        int tokenInformationLength,
        out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
