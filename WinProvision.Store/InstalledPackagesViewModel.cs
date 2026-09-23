using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using WinProvision.Core.Models.Office;
using WinProvision.Core.Services;
using WinProvision.Core.Services.Office;

namespace WinProvision.Store;

public sealed class InstalledPackagesViewModel : INotifyPropertyChanged
{
    private readonly Services.InstalledPackagesService _service;
    private readonly WingetExecutor _executor;
    private readonly OperationsQueueService _queue;
    private readonly OfficeDeploymentToolService _office;
    private readonly Services.InstalledPackageClassifier _classifier;
    private readonly UninstallerEngineService _uninstallerEngine;
    private readonly InstalledAppsService _installedAppsService;

    private bool _isBusy;
    private bool _isInitialLoading;
    private string _status = "Carregando pacotes instalados…";
    private int _loadVersion;

    public ObservableCollection<InstalledPackageRow> Packages { get; } = [];

    private string _searchText = string.Empty;
    public string SearchText
    {
        get => _searchText;
        set
        {
            string next = value ?? string.Empty;
            if (string.Equals(_searchText, next, StringComparison.Ordinal))
                return;

            _searchText = next;
            ApplySearch();
            OnPropertyChanged();
            UpdateVisibleCount();
            SelectionChanged();
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            _isBusy = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanRemove));
            foreach (var package in Packages)
                package.NotifySelectionStateChanged();
        }
    }

    public bool IsInitialLoading
    {
        get => _isInitialLoading;
        private set
        {
            if (_isInitialLoading == value)
                return;
            _isInitialLoading = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsEmpty));
        }
    }

    public bool CanRemove => !IsBusy && Packages.Any(x => x.IsSearchMatch && x.IsSelected && x.CanRemove);
    public int SelectedCount => Packages.Count(x => x.IsSearchMatch && x.IsSelected);
    public int VisibleCount => Packages.Count(x => x.IsSearchMatch);
    public bool HasPackages => VisibleCount > 0;
    public bool IsEmpty => !IsInitialLoading && !HasError && VisibleCount == 0;

    private bool _hasError;
    public bool HasError
    {
        get => _hasError;
        private set { _hasError = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsEmpty)); }
    }

    public string Status { get => _status; private set { _status = value; OnPropertyChanged(); } }

    public InstalledPackagesViewModel(
        Services.InstalledPackagesService service,
        WingetExecutor executor,
        OperationsQueueService queue,
        OfficeDeploymentToolService office,
        OfficeInstalledProductsDetector detector,
        Services.InstalledPackageClassifier classifier,
        UninstallerEngineService uninstallerEngine,
        InstalledAppsService installedAppsService)
    {
        _service = service;
        _executor = executor;
        _queue = queue;
        _office = office;
        _ = detector;
        _classifier = classifier;
        _uninstallerEngine = uninstallerEngine;
        _installedAppsService = installedAppsService;
    }

    public async Task LoadAsync()
    {
        bool showInitialOverlay = Packages.Count == 0;
        int version = Interlocked.Increment(ref _loadVersion);
        if (showInitialOverlay)
        {
            IsInitialLoading = true;
            IsBusy = true;
            Status = "Carregando pacotes instalados…";
        }

        HasError = false;
        OnPropertyChanged(nameof(IsEmpty));
        try
        {
            var raw = await _service.ResolveIconsAsync(await _service.ListAsync());
            if (version != _loadVersion)
                return;

            var selectedIds = Packages
                .Where(x => x.IsSelected)
                .Select(x => x.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            Packages.Clear();
            foreach (var item in raw)
            {
                var classified = item with { IsOffice = _classifier.IsMicrosoftOffice(item.Id, item.Name) };
                var row = new InstalledPackageRow(classified, this);
                if (row.IsSystemComponent || !row.CanRemove)
                    continue;

                row.ApplySearch(_searchText);
                if (selectedIds.Contains(row.Id))
                    row.IsSelected = true;
                Packages.Add(row);
            }

            UpdateVisibleCount();
            Status = $"{VisibleCount} pacote(s) instalado(s).";
        }
        catch (Exception ex)
        {
            if (version != _loadVersion)
                return;
            HasError = true;
            Status = $"Não foi possível listar pacotes: {ex.Message}";
        }
        finally
        {
            if (version == _loadVersion && showInitialOverlay)
            {
                IsBusy = false;
                IsInitialLoading = false;
                OnPropertyChanged(nameof(IsEmpty));
            }
        }
    }

    public void ToggleSelectAll()
    {
        bool select = Packages.Any(x => x.IsSearchMatch && x.CanRemove && !x.IsSelected);
        foreach (var package in Packages.Where(x => x.IsSearchMatch && x.CanRemove))
            package.IsSelected = select;
    }

    public async Task RemoveSelectedAsync()
    {
        var selected = Packages.Where(x => x.IsSearchMatch && x.IsSelected && x.CanRemove).ToArray();
        if (selected.Length == 0)
            return;

        IsBusy = true;
        try
        {
            var office = selected.Where(x => x.IsOffice).ToArray();
            var nonOffice = selected.Where(x => !x.IsOffice).ToArray();
            int successCount = 0;
            var failures = new List<string>();

            if (office.Length > 0)
            {
                bool officeSuccess = await RemoveOfficeWithOfficeTabMethodAsync(office);
                if (officeSuccess)
                {
                    successCount++;
                    foreach (var item in office)
                        Packages.Remove(item);
                    UpdateVisibleCount();
                }
                else
                {
                    failures.Add("Office: falha ao remover todas as versões.");
                }
            }

            foreach (var item in nonOffice)
            {
                bool removed = await RemoveExternalPackageAsync(item);
                if (removed)
                {
                    successCount++;
                    Packages.Remove(item);
                    UpdateVisibleCount();
                }
                else
                {
                    failures.Add(item.Name);
                }
            }

            Status = $"Remoção concluída: {successCount} sucesso(s), {failures.Count} falha(s)."
                + (failures.Count == 0 ? string.Empty : $" {string.Join("; ", failures)}");

            foreach (var item in selected.Where(x => Packages.Contains(x)))
                item.IsSelected = false;

            if (successCount > 0)
            {
                await Task.Delay(800);
                await LoadAsync();
            }
            else
            {
                Status = $"{VisibleCount} pacote(s) instalado(s).";
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task<bool> RemoveByIdentityAsync(string id, string name, string iconUrl)
    {
        if (_classifier.IsMicrosoftOffice(id, name))
            return await RemoveOfficeWithOfficeTabMethodAsync(id, name);

        await _installedAppsService.EnsureLoadedAsync();
        var match = _installedAppsService.GetAllInstalledApps().FirstOrDefault(app =>
            app.Id.Equals(id, StringComparison.OrdinalIgnoreCase)
            || app.DisplayName.Equals(name, StringComparison.OrdinalIgnoreCase));

        var row = Packages.FirstOrDefault(x =>
            x.Id.Equals(id, StringComparison.OrdinalIgnoreCase)
            || x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

        return await OperationRunner.RunUninstallWithFallbackAsync(
            _queue,
            _executor,
            _uninstallerEngine,
            id,
            name,
            iconUrl,
            row?.Package.UninstallString ?? match?.UninstallString ?? string.Empty,
            row?.Package.QuietUninstallString ?? match?.QuietUninstallString ?? string.Empty,
            row?.Package.InstallLocation ?? match?.InstallLocation ?? string.Empty,
            _installedAppsService);
    }

    public async Task<bool> RemoveSinglePackageAsync(string id, string name, string iconUrl, string uninstallString, string quietUninstallString, string installLocation)
    {
        if (_classifier.IsMicrosoftOffice(id, name))
            return await RemoveOfficeWithOfficeTabMethodAsync(id, name);

        WinProvision.Store.Services.WinProvisionLog.Write($"SINGLE UNINSTALL id=\"{id}\" name=\"{name}\"");
        return await OperationRunner.RunUninstallWithFallbackAsync(
            _queue,
            _executor,
            _uninstallerEngine,
            id,
            name,
            iconUrl,
            uninstallString,
            quietUninstallString,
            installLocation,
            _installedAppsService);
    }

    public async Task AdvancedRemoveSelectedAsync() => await RemoveSelectedAsync();

    public async Task ForceRemoveSelectedAsync() => await RemoveSelectedAsync();

    private async Task<bool> RemoveOfficeWithOfficeTabMethodAsync(IReadOnlyList<InstalledPackageRow> officeRows)
    {
        string ids = string.Join(",", officeRows.Select(x => x.Id));
        WinProvision.Store.Services.WinProvisionLog.Write(
            $"INSTALLED UNINSTALL office-group count={officeRows.Count} ids=\"{ids}\"");
        return await RemoveOfficeWithOfficeTabMethodAsync(officeRows[0].Id, "Microsoft Office");
    }

    private async Task<bool> RemoveOfficeWithOfficeTabMethodAsync(string id, string name)
    {
        WinProvision.Store.Services.WinProvisionLog.Write($"OFFICE TAB UNINSTALL id=\"{id}\" name=\"{name}\"");
        var request = new OfficeRemoveRequest(
            true,
            DisplayLevel: OfficeDisplayLevel.Silent,
            CleanStoreEdition: true,
            UseRemoveMSI: true,
            UseAggressiveUninstall: true);
        bool success = await OperationRunner.RunOfficeRemoveAsync(
            _queue,
            _office,
            request,
            "Remoção completa do Office (RemoveAll)");
        WinProvision.Store.Services.WinProvisionLog.Write(
            $"OFFICE TAB UNINSTALL result={(success ? "success" : "failed")} id=\"{id}\"");
        return success;
    }

    private Task<bool> RemoveExternalPackageAsync(InstalledPackageRow item) =>
        RemoveSinglePackageAsync(
            item.Id,
            item.Name,
            item.IconUrl,
            item.Package.UninstallString,
            item.Package.QuietUninstallString,
            item.Package.InstallLocation);

    private void ApplySearch()
    {
        foreach (var package in Packages)
            package.ApplySearch(_searchText);
    }

    internal void SelectionChanged()
    {
        OnPropertyChanged(nameof(CanRemove));
        OnPropertyChanged(nameof(SelectedCount));
    }

    private void UpdateVisibleCount()
    {
        OnPropertyChanged(nameof(VisibleCount));
        OnPropertyChanged(nameof(HasPackages));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(CanRemove));
        OnPropertyChanged(nameof(SelectedCount));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}

public sealed class InstalledPackageRow : INotifyPropertyChanged
{
    private bool _selected;
    private bool _isSearchMatch = true;
    private readonly InstalledPackagesViewModel _owner;
    public Services.InstalledPackage Package { get; }

    public string Name => Package.Name;
    public string Id => Package.Id;
    public string Version => Package.Version;
    public string Source => Package.Source;
    public string Scope => Package.Scope;
    public string IconUrl => Package.IconUrl;

    public bool IsOffice => Package.IsOffice;
    public bool IsSystemComponent => Package.IsSystemComponent;
    public bool IsSearchMatch => _isSearchMatch;

    public bool CanRemove => IsOffice
        || (!IsSystemComponent && !string.IsNullOrWhiteSpace(Id) && !Id.Contains('…') && !Id.Contains("...", StringComparison.Ordinal));

    public bool IsSelected
    {
        get => _selected;
        set { if (_selected == value) return; _selected = value; OnPropertyChanged(); _owner.SelectionChanged(); }
    }

    public bool CanSelect => !_owner.IsBusy && CanRemove;

    internal void NotifySelectionStateChanged() => OnPropertyChanged(nameof(CanSelect));

    internal void ApplySearch(string searchText)
    {
        bool match = string.IsNullOrWhiteSpace(searchText)
            || Name.Contains(searchText, StringComparison.OrdinalIgnoreCase)
            || Id.Contains(searchText, StringComparison.OrdinalIgnoreCase);
        if (match == _isSearchMatch)
            return;
        _isSearchMatch = match;
        OnPropertyChanged(nameof(IsSearchMatch));
    }

    public InstalledPackageRow(Services.InstalledPackage package, InstalledPackagesViewModel owner)
    {
        Package = package;
        _owner = owner;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}
