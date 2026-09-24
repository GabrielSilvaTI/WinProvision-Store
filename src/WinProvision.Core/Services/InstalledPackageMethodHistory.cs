using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using WinProvision.Core.Models;

namespace WinProvision.Core.Services;

/// <summary>Persists the last successful install/update method for each WinGet package.</summary>
public static class InstalledPackageMethodHistory
{
    private static readonly object Gate = new();
    private static Dictionary<string, WingetMethod>? _methods;
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WinProvisionStore", "installed-package-methods.json");

    public static void Record(string packageId, WingetMethod method)
    {
        if (string.IsNullOrWhiteSpace(packageId) || method == WingetMethod.Unknown)
            return;

        lock (Gate)
        {
            var methods = Load();
            methods[packageId.Trim()] = method;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                string temporaryPath = FilePath + ".tmp";
                File.WriteAllText(temporaryPath, JsonSerializer.Serialize(methods));
                File.Move(temporaryPath, FilePath, overwrite: true);
            }
            catch
            {
                // Tracking the method must never cause a successful package operation to fail.
            }
        }
    }

    public static WingetMethod Get(string packageId)
    {
        if (string.IsNullOrWhiteSpace(packageId))
            return WingetMethod.Unknown;

        lock (Gate)
            return Load().TryGetValue(packageId.Trim(), out var method)
                ? method
                : WingetMethod.Unknown;
    }

    public static void Forget(string packageId)
    {
        if (string.IsNullOrWhiteSpace(packageId))
            return;

        lock (Gate)
        {
            var methods = Load();
            if (!methods.Remove(packageId.Trim()))
                return;
            try
            {
                string temporaryPath = FilePath + ".tmp";
                File.WriteAllText(temporaryPath, JsonSerializer.Serialize(methods));
                File.Move(temporaryPath, FilePath, overwrite: true);
            }
            catch
            {
                // A stale history entry is harmless; the installed package list is authoritative.
            }
        }
    }

    private static Dictionary<string, WingetMethod> Load()
    {
        if (_methods is not null)
            return _methods;

        try
        {
            if (File.Exists(FilePath))
            {
                var stored = JsonSerializer.Deserialize<Dictionary<string, WingetMethod>>(File.ReadAllText(FilePath));
                if (stored is not null)
                    return _methods = new Dictionary<string, WingetMethod>(stored, StringComparer.OrdinalIgnoreCase);
            }
        }
        catch
        {
            // A malformed history file should not block normal package discovery.
        }

        return _methods = new Dictionary<string, WingetMethod>(StringComparer.OrdinalIgnoreCase);
    }
}
