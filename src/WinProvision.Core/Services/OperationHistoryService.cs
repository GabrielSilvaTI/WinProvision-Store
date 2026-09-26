using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using WinProvision.Core.Models;

namespace WinProvision.Core.Services;

public sealed class OperationHistoryService
{
    private const int MaximumEntries = 200;
    private readonly string _filePath;

    public ObservableCollection<OperationHistoryEntry> Entries { get; } = [];

    public OperationHistoryService()
    {
        string directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinProvisionStore");
        _filePath = Path.Combine(directory, "operation-history.json");
        Load();
    }

    public void Record(OperationItem item)
    {
        if (!item.IsFinished || Entries.Any(entry => entry.OperationId == item.Id))
        {
            return;
        }

        Entries.Insert(0, new OperationHistoryEntry(
            item.Id, item.AppName, item.Kind, item.State, DateTimeOffset.Now, item.Method));

        while (Entries.Count > MaximumEntries)
        {
            Entries.RemoveAt(Entries.Count - 1);
        }

        Save();
    }

    public void Clear()
    {
        Entries.Clear();
        Save();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return;
            }

            var entries = JsonSerializer.Deserialize<OperationHistoryEntry[]>(
                File.ReadAllText(_filePath), WinProvisionJsonOptions.Compact) ?? [];
            foreach (OperationHistoryEntry entry in entries.Take(MaximumEntries))
            {
                Entries.Add(entry);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Um histórico indisponível nunca deve impedir o início do aplicativo.
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            File.WriteAllText(_filePath, JsonSerializer.Serialize(Entries, WinProvisionJsonOptions.Compact));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A operação do pacote já terminou; falha ao persistir o histórico não a reverte.
        }
    }
}
