using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.Win32;
using OstrivStudio.Core;

namespace OstrivStudio;

public sealed record BuildingRow(string Name, int ResourceCount, BuildingCandidate Candidate, string Summary = "")
{
    public string Capacity => Candidate.ObservedCapacity?.ToString("N0", CultureInfo.CurrentCulture) ?? "—";
    public string Category => Candidate.ClassName switch
    {
        var x when x.Contains("trading", StringComparison.Ordinal) || x.Contains("shop", StringComparison.Ordinal) => "Trade",
        var x when x.Contains("farm", StringComparison.Ordinal) || x.Contains("field", StringComparison.Ordinal) || x.Contains("hay", StringComparison.Ordinal) => "Farm",
        var x when x.Contains("warehouse", StringComparison.Ordinal) || x.Contains("granary", StringComparison.Ordinal) || x.Contains("storage", StringComparison.Ordinal) => "Storage",
        var x when x.Contains("well", StringComparison.Ordinal) => "Water",
        _ => "Other buildings"
    };
    public string Badge => Category switch { "Trade" => "T", "Farm" => "F", "Storage" => "S", "Water" => "W", _ => "B" };
}
public sealed record LiveResourceRow(string Name, int Id, string Stored, string Reserved, string Awaited, string Other);

public partial class BuildingWindow : Window
{
    private readonly string _settingsPath = Path.Combine(Environment.GetEnvironmentVariable("OSTRIV_STUDIO_DATA_DIR") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OstrivStudio"), "profiles.json");
    private readonly ObservableCollection<BuildingRow> _buildings = [];
    private readonly ObservableCollection<LiveResourceRow> _observations = [];
    private readonly Dictionary<int, string> _resourceNames = [];
    private readonly ICollectionView _buildingView;
    private ICollectionView? _resourceView;
    private ProfileDocument _state = new();
    private bool _settingsAvailable = true;
    private CancellationTokenSource? _scanCancellation;
    private DiscoveryResult? _snapshot;
    private DateTime _snapshotTimeUtc;
    private readonly ResourceSnapshotHistory _history = new();

    public BuildingWindow()
    {
        InitializeComponent();
        string version = typeof(App).Assembly.GetName().Version?.ToString(3) ?? "preview";
        ReleaseLabel.Text = $"Version {version} · Settlement workspace";
        Title = $"Ostriv Studio {version} · Settlement workspace";
        _buildingView = CollectionViewSource.GetDefaultView(_buildings);
        _buildingView.Filter = x => x is BuildingRow row && (BuildingCategory.SelectedIndex <= 0 || (BuildingCategory.SelectedItem as ComboBoxItem)?.Content?.ToString() == row.Category) && (string.IsNullOrWhiteSpace(BuildingSearch.Text) || row.Name.Contains(BuildingSearch.Text, StringComparison.OrdinalIgnoreCase) || row.Summary.Contains(BuildingSearch.Text, StringComparison.OrdinalIgnoreCase) || row.Category.Contains(BuildingSearch.Text, StringComparison.OrdinalIgnoreCase)) &&
            (FilterBuildingsByResource.IsChecked != true || string.IsNullOrWhiteSpace(ResourceSearch.Text) || row.Candidate.Resources.Any(r =>
                ResourceInsights.Matches(r.Id, _resourceNames.GetValueOrDefault(r.Id, $"Unknown resource {r.Id}"), ResourceSearch.Text)));
        BuildingGrid.ItemsSource = _buildingView;
        _resourceView = CollectionViewSource.GetDefaultView(_observations);
        _resourceView.Filter = x => x is LiveResourceRow row && ResourceInsights.Matches(row.Id, row.Name, ResourceSearch.Text);
        LiveResourceGrid.ItemsSource = _resourceView;
        try
        {
            var definitions = JsonSerializer.Deserialize<List<ResourceDefinition>>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "resources.json")), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new InvalidDataException("The resource catalog is empty.");
            foreach (var item in definitions)
                _resourceNames.Add(item.Id, item.Name.StartsWith("RES_", StringComparison.Ordinal) ? $"Unknown resource {item.Id}" : DisplayName(item.Name));
            _state = ProfileStore.Load(_settingsPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException)
        {
            _settingsAvailable = false;
            MessageBox.Show(this, "Saved profiles or the resource catalog could not be loaded. Existing files will not be overwritten.\n\n" + ex.Message, "Ostriv Studio", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        if (string.IsNullOrWhiteSpace(_state.GamePath))
        {
            _state.GamePath = SteamGameLocator.Find() ?? "";
        }
        ConnectionDetail.Text = string.IsNullOrWhiteSpace(_state.GamePath) ? "Choose your Ostriv executable to begin." : _state.GamePath;
        Closing += (_, _) => _scanCancellation?.Cancel();
        Loaded += async (_, _) => { await CheckConnectionAsync(); await UpdateService.CheckAsync(this, false); };
        UpdateActions();
    }

    private void SelectShown_Click(object sender, RoutedEventArgs e) => BuildingGrid.SelectAll();
    private void ClearSelection_Click(object sender, RoutedEventArgs e) => BuildingGrid.UnselectAll();
    private void FindMarkets_Click(object sender, RoutedEventArgs e)
    {
        BuildingCategory.SelectedIndex = 0;
        BuildingSearch.Text = "shop";
        ResourceSearch.Text = "";
        FilterBuildingsByResource.IsChecked = false;
    }
    private void ResourceSelection_Changed(object sender, SelectionChangedEventArgs e) => UpdateActions();
    private void ActionIntent_Changed(object sender, RoutedEventArgs e) => UpdateActions();
    private void ReviewAction_Click(object sender, RoutedEventArgs e)
    {
        if (HoldIntent.IsChecked == true) KeepStocked_Click(sender, e);
        else AddNow_Click(sender, e);
    }
    private void UpdateActions()
    {
        if (ReviewActionButton is null || BuildingGrid is null || LiveResourceGrid is null) return;
        var selected = BuildingGrid.SelectedItems.OfType<BuildingRow>().ToArray();
        var resource = LiveResourceGrid.SelectedItem as LiveResourceRow;
        bool hold = HoldIntent.IsChecked == true;
        ActionResource.Text = resource is null ? "Select a resource to manage" : $"{resource.Name} · {selected.Length} selected building(s)";
        ReviewActionButton.Content = hold ? "Review saved minimum…" : "Review one-time addition…";
        bool supported = resource is not null && selected.Length > 0 && selected.All(b => ExistingRuleExecutor.SupportsExisting(b.Candidate.ClassName, resource.Id));
        ReviewActionButton.IsEnabled = _snapshot is not null && supported && (!hold || selected.Select(b => b.Candidate.ClassName).Distinct().Count() == 1);
        ActionHint.Text = resource is null || selected.Length == 0 ? "Choose a building and a resource above." : !supported ?
            "This building and resource combination is available for inspection. Editing is not yet verified." : hold && selected.Select(b => b.Candidate.ClassName).Distinct().Count() != 1 ?
            "Select one building type to save a minimum. One rule applies to every matching building of that type." : hold ?
            "Save a minimum for every matching building of this type. Start the rule monitor to maintain it while you play." :
            "Add an amount once to the selected buildings. The next step previews every quantity before writing.";
        CapacityTool.IsEnabled = _snapshot is not null && selected.Length > 0 && selected.All(b => CapacityObservation.SupportsClass(b.Candidate.ClassName) && b.Candidate.ObservedCapacity is not null);
        FarmTool.IsEnabled = _snapshot is not null && selected.Length == 1 && selected[0].Candidate.ClassName == "building_grain_farm";
        ExportTool.IsEnabled = _snapshot is not null && selected.Length > 0 && selected.All(b => TradingStockLimits.SupportsClass(b.Candidate.ClassName));
        BuildingToolsSummary.Text = selected.Length == 0 ? "Select a building in the browser. Capacity controls support granaries, warehouses and big hay barracks." :
            $"{selected.Length} building(s) selected. " + (CapacityTool.IsEnabled ? "These buildings have a supported observed capacity." : "Capacity editing is unavailable for this selection. Choose supported storage buildings with an observed capacity.");
    }

    private static string DisplayName(string input) => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(input.Replace('_', ' ').ToLowerInvariant());
    private void BuildingCategory_Changed(object sender, SelectionChangedEventArgs e)
    {
        _buildingView?.Refresh();
        if (_buildingView is not null) UpdateCount();
    }
    private async void CheckUpdates_Click(object sender, RoutedEventArgs e) => await UpdateService.CheckAsync(this, true);
    private void ResourceSearch_Changed(object sender, TextChangedEventArgs e)
    {
        if (ResourceSearchHint is not null) ResourceSearchHint.Visibility = string.IsNullOrEmpty(ResourceSearch.Text) ? Visibility.Visible : Visibility.Collapsed;
        _resourceView?.Refresh();
        _buildingView?.Refresh();
        if (_buildingView is not null) UpdateCount();
    }
    private void BuildingResourceFilter_Changed(object sender, RoutedEventArgs e)
    {
        _buildingView?.Refresh();
        if (_buildingView is not null) UpdateCount();
    }
    private void ResourceLedger_Click(object sender, RoutedEventArgs e)
    {
        if (_snapshot is null) { MessageBox.Show(this, "Load a game and choose Find buildings to create a resource snapshot.", "Resource ledger", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        try
        {
            var names = _buildings.ToDictionary(row => row.Candidate.Address, row => row.Name);
            int interval = _state.AutomationProfiles[_state.SelectedProfile].IntervalSeconds;
            var window = new ResourceOverviewWindow(_snapshot, _resourceNames, names, _history, _snapshotTimeUtc, interval, async token =>
            {
                string path = _state.GamePath;
                var fresh = await Task.Run(() => LiveBuildingScanner.Scan(path, token), token);
                token.ThrowIfCancellationRequested();
                DateTime observed = DateTime.UtcNow;
                AdoptScan(fresh, observed);
                return new ResourceRefresh(fresh, observed, _buildings.ToDictionary(row => row.Candidate.Address, row => row.Name));
            }, id =>
            {
                BuildingSearch.Text = "";
                ResourceSearch.Text = id.ToString(CultureInfo.InvariantCulture);
                FilterBuildingsByResource.IsChecked = true;
                _buildingView.Refresh(); UpdateCount();
                if (_buildingView.Cast<object>().Any()) BuildingGrid.SelectedIndex = 0;
                BottomStatus.Text = $"Showing building candidates containing resource {id}.";
            }) { Owner = this };
            window.ShowDialog();
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or ArgumentException)
        { MessageBox.Show(this, ex.Message, "Resource ledger", MessageBoxButton.OK, MessageBoxImage.Information); }
    }
    private void Capabilities_Click(object sender, RoutedEventArgs e)
    {
        var selected = BuildingGrid.SelectedItem as BuildingRow;
        string type = selected?.Candidate.ClassName ?? "";
        bool supported = type is "building_camp_storage" or "building_granary" or "building_warehouse" or "building_well" or "building_stone_mining_camp" or "building_fishery" or "building_trading_post" or "building_trading_dock" or "building_ashery" or "building_brickworks" or "building_rowhouse_shop";
        string current = selected is null ? "Select a building to see whether it supports automated edits." :
            supported ? $"{selected.Name}: supported existing-resource automation on the tested game build." :
            $"{selected.Name}: resource inspection is available; automatic edits are not yet verified.";
        MessageBox.Show(this, current + "\n\nExplore scanned resources in the ledger, see where they appear, monitor recent scans and export the displayed building breakdown.\n\nEach and Across automation supports camp storage, granary, warehouse, well, stone mining camp, trading post and fishing dock, under different record restrictions. Trading-dock broadcloth and clothes, rowhouse-shop clothes, shoes and salt (up to 100), ashery potash and brickworks brick support existing-record rules only. Selected nonperishable resources can be inserted for Each rules. Granary, warehouse and big hay barrack capacities and existing post and dock export targets can be increased, and selected future farm crops can be changed. Supported saved capacity rules run while the monitor is active; capacity may reset on reload.\n\nOther building types, perishable insertion, other farm controls, other market stock, direct deal edits, vehicles, money and date are not yet available. A preview is not an edit.", "Supported features", MessageBoxButton.OK, MessageBoxImage.Information);
    }
    private void BuildingSearch_Changed(object sender, TextChangedEventArgs e)
    {
        if (BuildingSearchHint is not null) BuildingSearchHint.Visibility = string.IsNullOrEmpty(BuildingSearch.Text) ? Visibility.Visible : Visibility.Collapsed;
        if (_buildingView is not null) { _buildingView.Refresh(); UpdateCount(); }
    }
    private void UpdateCount() => BuildingCount.Text = _buildings.Count == 0 ? "No results yet" : $"{_buildingView.Cast<object>().Count()} shown from {_buildings.Count} candidates";
    private void Building_Changed(object sender, SelectionChangedEventArgs e)
    {
        _observations.Clear();
        if (BuildingGrid.SelectedItem is not BuildingRow row) { SelectedTitle.Text = "Choose a building"; SelectedSubtitle.Text = "Its stock and available controls will appear here."; UpdateActions(); return; }
        int selectedCount = BuildingGrid.SelectedItems.Count;
        SelectedTitle.Text = selectedCount > 1 ? $"{selectedCount} buildings selected" : row.Name;
        SelectedSubtitle.Text = (selectedCount > 1 ? $"The resource table shows {row.Name}. One-time actions use all {selectedCount} selected buildings. " : "") +
            $"{row.Candidate.Resources.Count} records in this snapshot. Instance numbers may change after a scan." +
            (row.Candidate.ObservedCapacity is float capacity ? $" Observed capacity: {capacity:N0} (check in game)." : "");
        static string FormatAmount(double value) => value.ToString("0.##", CultureInfo.CurrentCulture);
        foreach (var total in BuildingDiscovery.Summarize(row.Candidate.Resources).OrderBy(x => _resourceNames.GetValueOrDefault(x.Id, $"Unknown resource {x.Id}")))
            _observations.Add(new(_resourceNames.GetValueOrDefault(total.Id, $"Unknown resource {total.Id}"), total.Id,
                FormatAmount(total.Stored), FormatAmount(total.Reserved), FormatAmount(total.Awaited), FormatAmount(total.Other)));
        UpdateActions();
    }
    private void AdoptScan(DiscoveryResult result, DateTime observedUtc)
    {
        _snapshot = result; _snapshotTimeUtc = observedUtc;
        try { _history.Add(result, observedUtc); }
        catch (InvalidDataException) { /* The snapshot can still be inspected without recording history. */ }
        _buildings.Clear(); _observations.Clear();
        foreach (var group in result.Buildings.GroupBy(x => x.ClassName).OrderBy(x => x.Key))
        {
            int index = 0;
            foreach (var candidate in group)
            {
                var stock = BuildingDiscovery.Summarize(candidate.Resources).Where(r => r.Stored > 0).OrderByDescending(r => r.Stored).Take(3)
                    .Select(r => $"{_resourceNames.GetValueOrDefault(r.Id, $"Resource {r.Id}")} {r.Stored.ToString("0.##", CultureInfo.CurrentCulture)}");
                string summary = string.Join("  ·  ", stock);
                _buildings.Add(new($"{DisplayName(group.Key["building_".Length..])}  ·  {++index}", candidate.Resources.Count, candidate,
                    summary.Length == 0 ? "No stored resources detected" : summary));
            }
        }
        _buildingView.Refresh(); UpdateCount();
        ConnectionTitle.Text = result.Complete ? "Game connected" : "Partial scan";
        ConnectionDetail.Text = result.Complete ? $"Read {result.BytesScanned / (1024d * 1024 * 1024):0.0} GB. Results are candidate objects, not yet validated for editing." : $"Scan incomplete: {result.UnreadableChunks} unreadable areas or the scan limit was reached.";
        ConnectionBadge.Text = "LIMITED EDIT";
        ScanFooter.Text = "Results can include old or non-building objects. Re-scan after loading a game; instance numbers are temporary.";
        BottomStatus.Text = "Snapshot loaded. No game changes have been made by this scan.";
        if (_buildingView.Cast<object>().Any()) BuildingGrid.SelectedIndex = 0;
    }
    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        if (_scanCancellation is not null) return;
        if (!File.Exists(_state.GamePath)) { ConnectionTitle.Text = "Game not found"; ConnectionDetail.Text = "Choose the installed ostriv.exe first."; return; }
        _scanCancellation = new CancellationTokenSource();
        var token = _scanCancellation.Token;
        string path = _state.GamePath;
        _snapshot = null;
        _buildings.Clear(); _observations.Clear(); UpdateCount();
        ScanButton.IsEnabled = false; ChooseButton.IsEnabled = false; ProfilesButton.IsEnabled = false; CancelButton.Visibility = Visibility.Visible;
        ConnectionTitle.Text = "Searching the running game"; ConnectionDetail.Text = "Reading memory without writing to the game…";
        ScanFooter.Text = "Scanning in the background; this can take a little while.";
        try
        {
            var progress = new Progress<long>(bytes => { if (!token.IsCancellationRequested) ScanFooter.Text = $"Scanned {bytes / (1024d * 1024 * 1024):0.0} GB of game memory…"; });
            var result = await Task.Run(() => LiveBuildingScanner.Scan(path, token, progress), token);
            if (token.IsCancellationRequested) return;
            AdoptScan(result, DateTime.UtcNow);
        }
        catch (OperationCanceledException) { ConnectionTitle.Text = "Scan cancelled"; ConnectionDetail.Text = "Scan again to get a new snapshot."; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or BadImageFormatException or Win32Exception or InvalidOperationException or ArgumentException or OverflowException)
        { ConnectionTitle.Text = "Scan unavailable"; ConnectionDetail.Text = ex.Message; ScanFooter.Text = "No new results were loaded."; }
        finally { _scanCancellation.Dispose(); _scanCancellation = null; ScanButton.IsEnabled = true; ChooseButton.IsEnabled = true; ProfilesButton.IsEnabled = true; CancelButton.Visibility = Visibility.Collapsed; }
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => _scanCancellation?.Cancel();
    private void CapacityEdit_Click(object sender, RoutedEventArgs e)
    {
        var selected = BuildingGrid.SelectedItems.OfType<BuildingRow>().ToArray();
        if (_snapshot is null || selected.Length == 0)
        { MessageBox.Show(this, "Find buildings and select granaries, warehouses or big hay barracks first.", "Increase capacity", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        if (selected.Any(b => !CapacityObservation.SupportsClass(b.Candidate.ClassName) || b.Candidate.ObservedCapacity is null))
        { MessageBox.Show(this, "Select supported buildings with an observed capacity on the tested game build.", "Increase capacity", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        var window = new CapacityEditWindow(_snapshot, selected, () =>
        {
            _snapshot = null; _buildings.Clear(); _observations.Clear(); UpdateCount();
            ConnectionTitle.Text = "Scan again"; ConnectionDetail.Text = "A capacity edit was attempted; previous results are invalid.";
            BottomStatus.Text = "Check the game; reload the backup without saving if anything is unexpected.";
        }) { Owner = this };
        window.ShowDialog();
    }
    private async void FarmRotation_Click(object sender, RoutedEventArgs e)
    {
        var selected = BuildingGrid.SelectedItems.OfType<BuildingRow>().ToArray();
        if (_snapshot is null || selected.Length != 1 || selected[0].Candidate.ClassName != "building_grain_farm")
        { MessageBox.Show(this, "Find buildings and select one grain farm first.", "Farm rotations", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        var snapshot = _snapshot; var farm = selected[0].Candidate;
        try
        {
            BottomStatus.Text = "Reading the selected farm's fields…";
            var rotations = await Task.Run(() => FarmCropEditor.CaptureFromGame(snapshot, farm));
            if (!IsLoaded || !ReferenceEquals(snapshot, _snapshot)) return;
            var window = new FarmRotationWindow(rotations, () =>
            {
                _snapshot = null; _buildings.Clear(); _observations.Clear(); UpdateCount();
                ConnectionTitle.Text = "Scan again"; ConnectionDetail.Text = "A crop edit was attempted; previous results are invalid.";
                BottomStatus.Text = "Check the field in Ostriv; reload the backup without saving if anything is unexpected.";
            }) { Owner = this };
            window.ShowDialog();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or Win32Exception or OverflowException)
        { MessageBox.Show(this, ex.Message, "Farm rotations", MessageBoxButton.OK, MessageBoxImage.Information); }
    }
    private async void TradeDeals_Click(object sender, RoutedEventArgs e)
    {
        if (_snapshot is null)
        { MessageBox.Show(this, "Open the world map in Ostriv, select a city, then choose Find buildings before viewing deals.", "Trade deals", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        var snapshot = _snapshot;
        try
        {
            BottomStatus.Text = "Reading the selected city's deals…";
            var deals = await Task.Run(() => TradeDealReader.CaptureFromGame(snapshot));
            if (!IsLoaded || !ReferenceEquals(snapshot, _snapshot)) return;
            var window = new TradeDealsWindow(snapshot, _resourceNames, deals, (id, type) =>
            {
                BuildingSearch.Text = type == "building_trading_dock" ? "Trading Dock" : "Trading Post";
                ResourceSearch.Text = id.ToString(CultureInfo.InvariantCulture);
                FilterBuildingsByResource.IsChecked = true;
                _buildingView.Refresh(); UpdateCount();
                if (_buildingView.Cast<object>().Any()) BuildingGrid.SelectedIndex = 0;
                BottomStatus.Text = $"Showing trading candidates containing resource {id}.";
            }, _buildings.ToDictionary(b => b.Candidate.Address), () =>
            {
                _snapshot = null; _buildings.Clear(); _observations.Clear(); UpdateCount();
                ConnectionTitle.Text = "Scan again"; ConnectionDetail.Text = "An export target may have changed.";
                BottomStatus.Text = "Refresh the trading-post panel in Ostriv and verify the target.";
            }) { Owner = this };
            window.ShowDialog();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or Win32Exception or OverflowException)
        { MessageBox.Show(this, ex.Message, "Trade deals", MessageBoxButton.OK, MessageBoxImage.Information); }
    }
    private async void TradingLimits_Click(object sender, RoutedEventArgs e)
    {
        var selected = BuildingGrid.SelectedItems.OfType<BuildingRow>().ToArray();
        if (_snapshot is null || selected.Length == 0 || selected.Any(b => !TradingStockLimits.SupportsClass(b.Candidate.ClassName)))
        { MessageBox.Show(this, "Find buildings and select one or more trading posts or docks first.", "Export stock targets", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        var snapshot = _snapshot;
        try
        {
            BottomStatus.Text = "Reading selected export-stock targets…";
            var limits = await Task.Run(() => TradingStockLimits.CaptureFromGame(snapshot, selected.Select(b => b.Candidate).ToArray()));
            if (!IsLoaded || !ReferenceEquals(snapshot, _snapshot)) return;
            BottomStatus.Text = "Export-stock targets loaded.";
            var window = new TradingLimitsWindow(limits, selected, _resourceNames, () =>
            {
                _snapshot = null; _buildings.Clear(); _observations.Clear(); UpdateCount();
                ConnectionTitle.Text = "Scan again"; ConnectionDetail.Text = "An export-target edit was attempted; previous results are invalid.";
                BottomStatus.Text = "Refresh the trading-building panel in Ostriv and verify the target.";
            }) { Owner = this };
            window.ShowDialog();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or Win32Exception or OverflowException)
        { MessageBox.Show(this, ex.Message, "Export stock targets", MessageBoxButton.OK, MessageBoxImage.Information); }
    }
    private void PreviewBulk_Click(object sender, RoutedEventArgs e)
    {
        var selected = BuildingGrid.SelectedItems.Cast<BuildingRow>().ToArray();
        if (selected.Length == 0 || LiveResourceGrid.SelectedItem is not LiveResourceRow resource)
        {
            MessageBox.Show(this, "Select one or more buildings and a resource in the table first.", "Bulk preview", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var window = new BulkPreviewWindow(selected, resource, _snapshot, () => { _snapshot = null; _buildings.Clear(); _observations.Clear(); UpdateCount(); ConnectionTitle.Text = "Scan again"; ConnectionDetail.Text = "A write was attempted; previous results are invalid."; BottomStatus.Text = "For a successful edit, save in Ostriv. If the edit failed, reload the backup."; }) { Owner = this };
        window.ShowDialog();
    }
    private void AddNow_Click(object sender, RoutedEventArgs e)
    {
        var selected = BuildingGrid.SelectedItems.OfType<BuildingRow>().ToArray();
        if (_snapshot is null || selected.Length == 0 || LiveResourceGrid.SelectedItem is not LiveResourceRow resource)
        { MessageBox.Show(this, "Select buildings and one resource first.", "Add stock once", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        new AddStockWindow(_snapshot, selected, resource, () =>
        {
            _snapshot = null; _buildings.Clear(); _observations.Clear(); UpdateCount();
            ConnectionTitle.Text = "Scan again"; ConnectionDetail.Text = "The one-time stock action invalidated the previous snapshot.";
            BottomStatus.Text = "Check the game before saving; reload the backup if anything is unexpected.";
        }) { Owner = this }.ShowDialog();
    }
    private void KeepStocked_Click(object sender, RoutedEventArgs e)
    {
        var selected = BuildingGrid.SelectedItems.OfType<BuildingRow>().ToArray();
        if (!_settingsAvailable || selected.Length == 0 || LiveResourceGrid.SelectedItem is not LiveResourceRow resource)
        { MessageBox.Show(this, "Select a building and one resource first; profile settings must be available.", "Keep stock available", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        string type = selected[0].Candidate.ClassName;
        if (selected.Any(b => !string.Equals(b.Candidate.ClassName, type, StringComparison.OrdinalIgnoreCase)) || !ExistingRuleExecutor.SupportsExisting(type, resource.Id))
        { MessageBox.Show(this, "Select one supported building type for this resource. A saved rule covers all matching buildings of that type.", "Keep stock available", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        try
        {
            byte[] before = File.Exists(_settingsPath) ? SHA256.HashData(File.ReadAllBytes(_settingsPath)) : [];
            var fresh = ProfileStore.Load(_settingsPath);
            if (!string.Equals(fresh.SelectedProfile, _state.SelectedProfile, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The selected profile changed in another window. Reopen profiles and try again.");
            var settings = fresh.AutomationProfiles[fresh.SelectedProfile];
            var existing = settings.MaintenanceRules.SingleOrDefault(r => r.ResourceId == resource.Id && string.Equals(r.BuildingType.Trim(), type, StringComparison.OrdinalIgnoreCase));
            var dialog = new KeepStockedWindow(resource.Name, DisplayName(type["building_".Length..]), fresh.SelectedProfile, existing?.Target,
                type == "building_rowhouse_shop" ? 100 : 1_000_000_000m) { Owner = this };
            if (dialog.ShowDialog() != true) return;
            if (existing is not null && MessageBox.Show(this, "This replaces the existing rule for this resource and building type with an enabled per-building rule. Continue?", "Replace existing rule?", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            byte[] after = File.Exists(_settingsPath) ? SHA256.HashData(File.ReadAllBytes(_settingsPath)) : [];
            if (!before.AsSpan().SequenceEqual(after)) throw new InvalidOperationException("The profile changed while editing. No rule was saved; try again.");
            if (existing is null) { existing = new MaintenanceRule { ResourceId = resource.Id, BuildingType = type }; settings.MaintenanceRules.Add(existing); }
            existing.Target = dialog.Target; existing.Enabled = true; existing.Mode = MaintenanceMode.Each; existing.BuildingLimit = -1;
            ProfileStore.Save(_settingsPath, fresh); _state = fresh;
            BottomStatus.Text = $"Saved a {resource.Name} rule in {fresh.SelectedProfile}. Start the rule monitor to apply it.";
            MessageBox.Show(this, "Rule saved. Start the rule monitor to keep this quantity stocked while you play.", "Rule saved", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or ArgumentException or OverflowException or JsonException)
        { MessageBox.Show(this, ex.Message, "Rule not saved", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    private void ApplySavedFish_Click(object sender, RoutedEventArgs e)
    {
        var selected = BuildingGrid.SelectedItems.Cast<BuildingRow>().ToArray();
        try
        {
            if (!_settingsAvailable) throw new InvalidOperationException("Profile settings could not be loaded.");
            decimal target = SavedRulePlanner.CheckFishRule(_state, _snapshot, selected.Select(x => x.Candidate).ToArray());
            var fish = new LiveResourceRow("Fish", 27, "", "", "", "");
            var window = new BulkPreviewWindow(selected, fish, _snapshot, () => { _snapshot = null; _buildings.Clear(); _observations.Clear(); UpdateCount(); ConnectionTitle.Text = "Scan again"; ConnectionDetail.Text = "A write was attempted; previous results are invalid."; BottomStatus.Text = "If the edit failed, reload the backup without saving."; }, target) { Owner = this };
            window.ShowDialog();
        }
        catch (InvalidOperationException ex)
        { MessageBox.Show(this, ex.Message, "Saved fish rule", MessageBoxButton.OK, MessageBoxImage.Information); }
    }
    private void PreviewRules_Click(object sender, RoutedEventArgs e)
    {
        if (_snapshot is null) { MessageBox.Show(this, "Find buildings in the running game first.", "Preview saved rules", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        try
        {
            var settings = _state.AutomationProfiles[_state.SelectedProfile];
            var preview = MaintenancePreview.Create(_snapshot, settings);
            new RulePreviewWindow(_state.SelectedProfile, preview, _resourceNames, _buildings.ToArray()) { Owner = this }.ShowDialog();
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or ArgumentException or OverflowException)
        { MessageBox.Show(this, ex.Message, "Cannot preview rules", MessageBoxButton.OK, MessageBoxImage.Information); }
    }
    private void MonitorRules_Click(object sender, RoutedEventArgs e)
    {
        if (!_settingsAvailable) { MessageBox.Show(this, "Profile settings could not be loaded.", "Monitor saved rules", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        if (_scanCancellation is not null) return;
        var window = new RuleMonitorWindow(_settingsPath, _state.SelectedProfile, _state.GamePath, _resourceNames) { Owner = this };
        window.ShowDialog();
        if (window.MayHaveWritten)
        {
            _snapshot = null; _buildings.Clear(); _observations.Clear(); UpdateCount();
            ConnectionTitle.Text = "Scan again"; ConnectionDetail.Text = "Automation may have changed game memory.";
            BottomStatus.Text = "Check the game and scan again before editing.";
        }
    }
    private void ApplyCampRules_Click(object sender, RoutedEventArgs e)
    {
        var selected = BuildingGrid.SelectedItems.OfType<BuildingRow>().Select(x => x.Candidate).ToArray();
        CampRuleExecutor.BatchPlan plan;
        try
        {
            if (!_settingsAvailable) throw new InvalidOperationException("Profile settings could not be loaded.");
            plan = CampRuleExecutor.PrepareBatch(_state, _snapshot, selected);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or OverflowException)
        { MessageBox.Show(this, ex.Message, "Camp rules", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        string skipped = string.Join("\n", plan.Skipped);
        if (plan.Entries.Count == 0)
        { MessageBox.Show(this, "No supported one-unit changes are ready. No change was made.\n\n" + skipped + "\n\nOther rules are not applied by this action.", "Camp rules", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        var select = new OpenFileDialog { Title = "Select the save currently loaded in Ostriv", Filter = "Ostriv saves (*.save)|*.save", CheckFileExists = true,
            InitialDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Saved Games", "Ostriv", "save") };
        if (select.ShowDialog(this) != true) return;
        SaveBackup.SessionBackup backup;
        try
        {
            string root = Environment.GetEnvironmentVariable("OSTRIV_STUDIO_DATA_DIR") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OstrivStudio");
            backup = SaveBackup.CopySessionVerified(select.FileName, Path.Combine(root, "Backups"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException)
        { MessageBox.Show(this, "Backup failed; no edit was made.\n\n" + ex.Message, "Backup failed", MessageBoxButton.OK, MessageBoxImage.Error); return; }
        string changes = string.Join("\n", plan.Entries.Select(x => $"{x.ResourceName}: {x.Record.Amount:N0} → {x.Target:N0}"));
        string question = $"Verified backups of the selected save and {backup.Autosaves.Count} autosave(s) are in:\n{backup.Directory}\n\nIs this the loaded save, is the game paused, and is the selected camp-storage candidate the building you intend to edit? The app cannot verify these conditions or storage capacity. It checks that another stored item remains as an anchor, but cannot guarantee that the camp stays active.\n\nThese saved rules will run once:\n{changes}\n\nOther rules will not run. A failed batch may be partial. Ostriv can autosave even while paused; reload the backed-up selected save without saving and use the manifest to recover autosaves if needed.";
        if (MessageBox.Show(this, question, "Confirm camp rules", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            int applied = CampRuleExecutor.ApplyBatchToGame(_state, _snapshot!, selected);
            MessageBox.Show(this, $"{applied} resource change(s) passed memory readback. Check them in Ostriv.\n\nBackups: {backup.Directory}", "Camp rules", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or Win32Exception or OverflowException)
        { MessageBox.Show(this, "The action stopped. If a write was attempted, do not save; reload the selected save backup. The game can autosave partial changes even while paused.\n\n" + ex.Message + "\n\nBackups: " + backup.Directory, "Camp rules stopped", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally
        {
            _snapshot = null; _buildings.Clear(); _observations.Clear(); UpdateCount();
            ConnectionTitle.Text = "Scan again"; ConnectionDetail.Text = "The previous scan is no longer valid.";
            BottomStatus.Text = "Check the result in Ostriv; reload the backup without saving if anything is unexpected.";
        }
    }
    private void Profiles_Click(object sender, RoutedEventArgs e)
    {
        if (_scanCancellation is not null) return;
        var profiles = new MainWindow { Owner = this }; profiles.ShowDialog();
        if (profiles.MayHaveWritten)
        {
            _snapshot = null; _buildings.Clear(); _observations.Clear(); UpdateCount();
            ConnectionTitle.Text = "Scan again"; ConnectionDetail.Text = "Automation may have changed game memory.";
            BottomStatus.Text = "Check the game and scan again before editing.";
        }
        try
        {
            var reloaded = ProfileStore.Load(_settingsPath);
            if (string.IsNullOrWhiteSpace(reloaded.GamePath)) reloaded.GamePath = _state.GamePath;
            if (!string.Equals(reloaded.GamePath, _state.GamePath, StringComparison.OrdinalIgnoreCase))
            { _snapshot = null; _buildings.Clear(); _observations.Clear(); UpdateCount(); ConnectionTitle.Text = "Game location changed"; ConnectionDetail.Text = reloaded.GamePath; }
            _state = reloaded;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException)
        { _settingsAvailable = false; MessageBox.Show(this, "Profile settings could not be reloaded. Existing files were preserved.\n\n" + ex.Message, "Ostriv Studio", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    private void ChooseGame_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Choose Ostriv's executable", Filter = "Ostriv executable (ostriv.exe)|ostriv.exe", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        string previous = _state.GamePath; _state.GamePath = dialog.FileName;
        try { if (_settingsAvailable) ProfileStore.Save(_settingsPath, _state); else throw new InvalidDataException("Saved profiles must be repaired first."); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException)
        { _state.GamePath = previous; MessageBox.Show(this, ex.Message, "Could not save game location", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        _snapshot = null; _buildings.Clear(); _observations.Clear(); UpdateCount();
        ConnectionTitle.Text = "Game location saved"; ConnectionDetail.Text = _state.GamePath;
    }
    private async Task CheckConnectionAsync()
    {
        if (!File.Exists(_state.GamePath)) { ConnectionTitle.Text = "Choose your game"; return; }
        try
        {
            var report = await Task.Run(() => GameInspector.Inspect(_state.GamePath));
            if (_scanCancellation is not null) return;
            ConnectionTitle.Text = report.LiveRead ? "Game connected" : "Game not running";
            ConnectionDetail.Text = report.LiveRead ? $"Running game detected. {report.UniqueCount} of {report.Patterns.Count} legacy compatibility probes matched." : report.Detail;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or BadImageFormatException or Win32Exception) { ConnectionTitle.Text = "Game check unavailable"; ConnectionDetail.Text = ex.Message; }
    }
}
