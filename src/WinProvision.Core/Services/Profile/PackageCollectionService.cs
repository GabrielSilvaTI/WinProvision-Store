using System.Collections.ObjectModel;
using WinProvision.Core.Models;

namespace WinProvision.Core.Services;

public class PackageProfileTab
{
    public string Id { get; } = Guid.NewGuid().ToString();
    public string Title { get; set; } = "Novo Perfil";
    public ObservableCollection<AppEntry> Items { get; } = new();
}

public class PackageCollectionService
{
    public ObservableCollection<PackageProfileTab> Tabs { get; } = new();
    public PackageProfileTab ActiveTab { get; set; }

    public PackageCollectionService()
    {
        // Cria a primeira guia padrão ao inicializar
        var defaultTab = CreateNewTab("Perfil Padrão");
        ActiveTab = defaultTab;
    }

    public PackageProfileTab CreateNewTab(string? name = null)
    {
        int count = Tabs.Count + 1;
        var tab = new PackageProfileTab
        {
            Title = name ?? $"Perfil {count}"
        };
        Tabs.Add(tab);
        ActiveTab = tab;
        return tab;
    }

    public void CloseTab(PackageProfileTab tab)
    {
        if (Tabs.Count <= 1) return; // Mantém ao menos uma guia aberta

        int index = Tabs.IndexOf(tab);
        Tabs.Remove(tab);

        if (ActiveTab == tab)
        {
            ActiveTab = Tabs[Math.Max(0, index - 1)];
        }
    }

    public int AddRangeToActive(IEnumerable<AppEntry> apps)
    {
        if (ActiveTab == null) return 0;

        var existingIds = ActiveTab.Items.Select(a => a.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        int addedCount = 0;

        foreach (var app in apps.Where(a => !existingIds.Contains(a.Id)))
        {
            ActiveTab.Items.Add(app);
            addedCount++;
        }

        return addedCount;
    }
}