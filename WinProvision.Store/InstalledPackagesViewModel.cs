using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Data;
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
    private readonly OfficeInstalledProductsDetector _officeDetector;
    private readonly Services.InstalledPackageClassifier _classifier;
    private bool _isBusy;
    private string _status = "Carregando pacotes instalados…";

    public ObservableCollection<InstalledPackageRow> Packages { get; } = [];
    public ICollectionView VisiblePackages { get; }
    private string _searchText = string.Empty;
    public string SearchText { get => _searchText; set { _searchText = value; VisiblePackages.Refresh(); OnPropertyChanged(); } }
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
    public bool CanRemove => !IsBusy && Packages.Any(x => x.IsSelected && x.CanRemove);
    public int SelectedCount => Packages.Count(x => x.IsSelected);
    public bool HasPackages => Packages.Count > 0;
    public bool IsEmpty => !IsBusy && !HasError && Packages.Count == 0;
    private bool _hasError;
    public bool HasError { get => _hasError; private set { _hasError = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsEmpty)); } }
    public string Status { get => _status; private set { _status = value; OnPropertyChanged(); } }

    public InstalledPackagesViewModel(Services.InstalledPackagesService service, WingetExecutor executor,
        OperationsQueueService queue, OfficeDeploymentToolService office, OfficeInstalledProductsDetector detector,
        Services.InstalledPackageClassifier classifier)
    {
        _service = service; _executor = executor; _queue = queue; _office = office; _officeDetector = detector; _classifier = classifier;
        VisiblePackages = CollectionViewSource.GetDefaultView(Packages);
        VisiblePackages.Filter = item => item is InstalledPackageRow row
            && !row.IsSystemComponent
            && (string.IsNullOrWhiteSpace(SearchText)
                || row.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
                || row.Id.Contains(SearchText, StringComparison.OrdinalIgnoreCase));
    }

    public async Task LoadAsync()
    {
        IsBusy = true;
        HasError = false;
        OnPropertyChanged(nameof(IsEmpty));
        try
        {
            var raw = await _service.ResolveIconsAsync(await _service.ListAsync());
            Packages.Clear();
            foreach (var item in raw)
            {
                Packages.Add(new InstalledPackageRow(item with { IsOffice = _classifier.IsOffice(item) }, this));
            }
            VisiblePackages.Refresh();
            Status = $"{Packages.Count} pacote(s) instalado(s).";
            OnPropertyChanged(nameof(HasPackages));
            OnPropertyChanged(nameof(IsEmpty));
        }
        catch (Exception ex) { HasError = true; Status = $"Não foi possível listar pacotes: {ex.Message}"; }
        finally { IsBusy = false; OnPropertyChanged(nameof(IsEmpty)); }
    }

    public async Task RemoveSelectedAsync()
    {
        var selected = Packages.Where(x => x.IsSelected && x.CanRemove).ToArray();
        if (selected.Length == 0) return;
        IsBusy = true;
        try
        {
            var office = selected.Where(x => x.IsOffice).ToArray();
            var nonOffice = selected.Where(x => !x.IsOffice).ToArray();
            int successCount = 0;
            var failures = new List<string>();
            if (office.Length > 0)
            {
                WinProvision.Store.Services.WinProvisionLog.Write(
                    $"INSTALLED UNINSTALL office-group count={office.Length} ids=\"{string.Join(",", office.Select(x => x.Id))}\"");
                var request = new OfficeRemoveRequest(true, DisplayLevel: OfficeDisplayLevel.Silent, CleanStoreEdition: true, UseRemoveMSI: true, UseAggressiveUninstall: true);
                bool officeSuccess = await OperationRunner.RunOfficeRemoveAsync(_queue, _office, request, "Remover todas as versões do Office");
                if (officeSuccess)
                    successCount++;
                else
                {
                    failures.Add("Office: falha ao remover todas as versões.");
                    WinProvision.Store.Services.WinProvisionLog.Write("INSTALLED UNINSTALL office-group result=failed");
                }
                if (officeSuccess)
                    WinProvision.Store.Services.WinProvisionLog.Write("INSTALLED UNINSTALL office-group result=success");
            }
            foreach (var item in nonOffice)
            {
                WinProvision.Store.Services.WinProvisionLog.Write($"INSTALLED UNINSTALL id=\"{item.Id}\" name=\"{item.Name}\"");
                var result = await OperationRunner.RunUninstallAsync(_queue, _executor, item.Id, item.Name, item.IconUrl);
                if (result.Success)
                {
                    successCount++;
                    WinProvision.Store.Services.WinProvisionLog.Write(
                        $"INSTALLED UNINSTALL result=success id=\"{item.Id}\" exitCode={result.ExitCode}");
                }
                else
                {
                    failures.Add($"{item.Name}: {result.FailureReason}");
                    WinProvision.Store.Services.WinProvisionLog.Write(
                        $"INSTALLED UNINSTALL result=failed id=\"{item.Id}\" exitCode={result.ExitCode} reason={result.FailureReason}");
                }
            }
            Status = $"Remoção concluída: {successCount} sucesso(s), {failures.Count} falha(s)."
                + (failures.Count == 0 ? string.Empty : $" {string.Join("; ", failures)}");
            foreach (var item in selected) item.IsSelected = false;
        }
        finally { IsBusy = false; }
    }

    internal void SelectionChanged()
    {
        OnPropertyChanged(nameof(CanRemove));
        OnPropertyChanged(nameof(SelectedCount));
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}

public sealed class InstalledPackageRow : INotifyPropertyChanged
{
    private bool _selected;
    private readonly InstalledPackagesViewModel _owner;
    public Services.InstalledPackage Package { get; }
    public string Name => Package.Name; public string Id => Package.Id; public string Version => Package.Version;
    public string Source => Package.Source; public string Scope => Package.Scope; public string IconUrl => Package.IconUrl;
    public bool IsOffice => Package.IsOffice;
    public bool IsSystemComponent => Package.IsSystemComponent;
    public bool CanRemove => IsOffice
        || (!string.IsNullOrWhiteSpace(Id) && !Id.Contains('…') && !Id.Contains("...", StringComparison.Ordinal));
    public bool IsSelected { get => _selected; set { if (_selected == value) return; _selected = value; OnPropertyChanged(); _owner.SelectionChanged(); } }
    public bool CanSelect => !_owner.IsBusy && CanRemove;
    internal void NotifySelectionStateChanged() => OnPropertyChanged(nameof(CanSelect));
    public InstalledPackageRow(Services.InstalledPackage package, InstalledPackagesViewModel owner) { Package = package; _owner = owner; }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}
