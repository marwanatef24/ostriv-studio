using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using Microsoft.Win32;
using OstrivStudio.Core;

namespace OstrivStudio;

public sealed class ResourceRow : INotifyPropertyChanged
{
    private string _target = "";
    private string _buildingType = "";
    private string _mode = "Each", _limit = "All";
    private bool _enabled;
    public static IReadOnlyList<string> Modes { get; } = ["Each", "Across"];
    public static IReadOnlyList<string> BuildingTypes { get; } = LegacyBuildingCatalog.All.Select(x => x.Value).Append("building_fishery").Prepend("").Distinct().OrderBy(x => x).ToArray();
    public int Id { get; init; }
    public string Name { get; init; } = "";
    public string Category { get; init; } = "";
    public int? LegacyBuildingId { get; init; }
    public string Target { get => _target; set { if (_target == value) return; _target = value; PropertyChanged?.Invoke(this, new(nameof(Target))); } }
    public bool Enabled { get => _enabled; set { if (_enabled == value) return; _enabled = value; PropertyChanged?.Invoke(this, new(nameof(Enabled))); } }
    public string BuildingType { get => _buildingType; set { if (_buildingType == value) return; _buildingType = value; PropertyChanged?.Invoke(this, new(nameof(BuildingType))); PropertyChanged?.Invoke(this, new(nameof(BuildingDisplay))); } }
    public string BuildingDisplay => string.IsNullOrWhiteSpace(BuildingType) ? "All building types" : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(BuildingType.Replace("building_", "", StringComparison.OrdinalIgnoreCase).Replace('_', ' ').ToLowerInvariant());
    public string Mode { get => _mode; set { if (_mode == value) return; _mode = value; PropertyChanged?.Invoke(this, new(nameof(Mode))); PropertyChanged?.Invoke(this, new(nameof(ModeDisplay))); } }
    public string ModeDisplay => Mode == "Across" ? "Across buildings" : "Each building";
    public string Limit { get => _limit; set { if (_limit == value) return; _limit = value; PropertyChanged?.Invoke(this, new(nameof(Limit))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public partial class MainWindow : Window
{
    private readonly string _settingsPath = Path.Combine(Environment.GetEnvironmentVariable("OSTRIV_STUDIO_DATA_DIR") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OstrivStudio"), "profiles.json");
    private readonly ObservableCollection<ResourceRow> _resources = [];
    private readonly Dictionary<int, ResourceDefinition> _catalog = [];
    private readonly ICollectionView _view;
    private ProfileDocument _state = new();
    private bool _loading = true, _dirty, _settingsAvailable = true;
    private readonly DispatcherTimer _autoSave = new() { Interval = TimeSpan.FromSeconds(1) };
    public bool MayHaveWritten { get; private set; }

    public MainWindow()
    {
        InitializeComponent();
        _autoSave.Tick += (_, _) => { _autoSave.Stop(); if (_dirty) SaveCurrentProfile(false); };
        _view = CollectionViewSource.GetDefaultView(_resources);
        _view.Filter = FilterResource;
        ResourceGrid.ItemsSource = _view;
        try
        {
            string json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "resources.json"));
            var definitions = JsonSerializer.Deserialize<List<ResourceDefinition>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new InvalidDataException("Resource catalog is empty.");
            foreach (var item in definitions) _catalog.Add(item.Id, item);
            _state = ProfileStore.Load(_settingsPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException)
        {
            _settingsAvailable = false;
            MessageBox.Show(this, "The catalog or saved profiles could not be loaded. Existing files will not be overwritten.\n\n" + ex.Message, "Ostriv Studio", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        if (string.IsNullOrWhiteSpace(_state.GamePath))
        {
            _state.GamePath = SteamGameLocator.Find() ?? "";
        }
        GamePathText.Text = _state.GamePath;
        RefreshProfiles();
        LoadProfile();
        UpdateCount();
        Closing += Window_Closing;
        if (File.Exists(_state.GamePath)) Loaded += async (_, _) => await InspectAsync();
    }

    private static string DisplayName(string name) => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(name.Replace('_', ' ').ToLowerInvariant());
    private static bool IsConfigured(ResourceRow row) => row.Enabled || !string.IsNullOrWhiteSpace(row.Target) || !string.IsNullOrWhiteSpace(row.BuildingType) ||
        row.Mode == "Across" || !string.Equals(row.Limit?.Trim(), "All", StringComparison.OrdinalIgnoreCase);
    private bool FilterResource(object item) => item is ResourceRow row &&
        (ConfiguredOnly?.IsChecked != true || IsConfigured(row)) &&
        (string.IsNullOrWhiteSpace(SearchBox.Text) || row.Name.Contains(SearchBox.Text, StringComparison.OrdinalIgnoreCase) ||
         row.Category.Contains(SearchBox.Text, StringComparison.OrdinalIgnoreCase) || row.Id.ToString().Contains(SearchBox.Text) ||
         row.BuildingType?.Contains(SearchBox.Text, StringComparison.OrdinalIgnoreCase) == true);
    private void UpdateCount() => ResourceCount.Text = $"{_view.Cast<object>().Count()} rows";
    private void Search_Changed(object sender, TextChangedEventArgs e)
    {
        if (_view is null) return;
        SearchHint.Visibility = string.IsNullOrEmpty(SearchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        _view.Refresh(); UpdateCount();
    }
    private void ConfiguredOnly_Changed(object sender, RoutedEventArgs e)
    {
        if (_view is null) return;
        _view.Refresh(); UpdateCount();
    }
    private void RefreshProfiles()
    {
        _loading = true;
        ProfileBox.ItemsSource = _state.Profiles.Keys.OrderBy(x => x).ToArray();
        ProfileBox.SelectedItem = _state.SelectedProfile;
        _loading = false;
    }
    private void LoadProfile()
    {
        _loading = true;
        var settings = _state.AutomationProfiles[_state.SelectedProfile];
        IntervalBox.Text = settings.IntervalSeconds.ToString(CultureInfo.CurrentCulture);
        _resources.Clear();
        foreach (var id in _catalog.Keys.Union(settings.MaintenanceRules.Select(r => r.ResourceId)).OrderBy(id => _catalog.TryGetValue(id, out var d) ? d.Name : $"Unknown {id}"))
        {
            var rules = settings.MaintenanceRules.Where(r => r.ResourceId == id).ToArray();
            if (rules.Length == 0) AddRow(id);
            else foreach (var rule in rules) AddRow(id, rule);
        }
        _loading = false; _dirty = false;
        ConfiguredOnly.IsChecked = settings.MaintenanceRules.Count > 0;
        _view.Refresh(); UpdateCount();
        StatusText.Text = $"Settings saved locally · {settings.CapacityRules.Count} capacity rules · run supported resource rules from the monitor.";
    }
    private void AddRow(int id, MaintenanceRule? rule = null)
    {
        _catalog.TryGetValue(id, out var definition);
        var row = new ResourceRow {
            Id = id, Name = definition is null ? $"Unknown resource {id}" : DisplayName(definition.Name),
            Category = definition is null ? "Unknown" : DisplayName(definition.Category),
            Target = rule?.Target?.ToString(CultureInfo.CurrentCulture) ?? "", Enabled = rule?.Enabled ?? false,
            BuildingType = rule?.BuildingType ?? "", Mode = rule?.Mode == MaintenanceMode.Across ? "Across" : "Each",
            Limit = rule is null || rule.BuildingLimit == -1 ? "All" : rule.BuildingLimit.ToString(CultureInfo.CurrentCulture),
            LegacyBuildingId = rule?.LegacyBuildingId
        };
        row.PropertyChanged += (_, _) => MarkDirty();
        _resources.Add(row);
    }
    private void MarkDirty()
    {
        if (_loading) return;
        _dirty = true; StatusText.Text = "Saving changes…";
        _autoSave.Stop(); _autoSave.Start();
    }
    private void Interval_Changed(object sender, TextChangedEventArgs e) => MarkDirty();
    private bool SavePending()
    {
        ResourceGrid.CommitEdit(DataGridEditingUnit.Cell, true); ResourceGrid.CommitEdit(DataGridEditingUnit.Row, true);
        return !_dirty || SaveCurrentProfile(true);
    }
    private void MonitorRules_Click(object sender, RoutedEventArgs e)
    {
        if (!_settingsAvailable || !SavePending()) return;
        if (!File.Exists(_state.GamePath))
        { MessageBox.Show(this, "Choose the installed Ostriv executable first.", "Monitor saved rules", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        var names = _catalog.ToDictionary(item => item.Key, item => item.Value.Name.StartsWith("RES_", StringComparison.Ordinal)
            ? $"Unknown resource {item.Key}" : DisplayName(item.Value.Name));
        var monitor = new RuleMonitorWindow(_settingsPath, _state.SelectedProfile, _state.GamePath, names) { Owner = this };
        monitor.ShowDialog();
        MayHaveWritten |= monitor.MayHaveWritten;
        if (monitor.MayHaveWritten) StatusText.Text = "Automation stopped · check the game before saving.";
    }
    private void Profile_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ProfileBox.SelectedItem is not string name || name == _state.SelectedProfile) return;
        if (!SavePending()) { RefreshProfiles(); return; }
        var previous = _state.SelectedProfile;
        _state.SelectedProfile = name;
        if (!SaveSettings()) { _state.SelectedProfile = previous; RefreshProfiles(); return; }
        LoadProfile();
    }
    private void NewProfile_Click(object sender, RoutedEventArgs e)
    {
        if (!_settingsAvailable || !SavePending()) return;
        var dialog = new NameDialog { Owner = this };
        if (dialog.ShowDialog() != true) return;
        string name = dialog.ProfileName.Trim();
        if (name.Length is < 1 or > 50 || _state.Profiles.ContainsKey(name))
        { MessageBox.Show(this, "Use a unique name between 1 and 50 characters.", "Profile name", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        var previous = _state.SelectedProfile;
        _state.Profiles.Add(name, []); _state.AutomationProfiles.Add(name, new()); _state.SelectedProfile = name;
        if (!SaveSettings()) { _state.Profiles.Remove(name); _state.AutomationProfiles.Remove(name); _state.SelectedProfile = previous; return; }
        RefreshProfiles(); LoadProfile();
        StatusText.Text = "Profile created. Changes are saved automatically.";
    }
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        ResourceGrid.CommitEdit(DataGridEditingUnit.Cell, true); ResourceGrid.CommitEdit(DataGridEditingUnit.Row, true);
        SaveCurrentProfile(true);
    }
    private bool SaveCurrentProfile(bool showErrors)
    {
        if (!_settingsAvailable) return InvalidDraft("Saved settings could not be loaded; existing files were preserved.", showErrors);
        if (!int.TryParse(IntervalBox.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out int interval) || interval is < 5 or > 86400)
        { return InvalidDraft("Enter an interval between 5 and 86,400 seconds.", showErrors); }
        var rules = new List<MaintenanceRule>();
        var keys = new HashSet<(int, string)>();
        foreach (var row in _resources)
        {
            decimal? target = null;
            if (!string.IsNullOrWhiteSpace(row.Target))
            {
                if (!decimal.TryParse(row.Target, NumberStyles.Number, CultureInfo.CurrentCulture, out var value) || value < 0 || value > 1000000000m)
                    return InvalidDraft($"Enter a valid amount between 0 and 1,000,000,000 for {row.Name}.", showErrors);
                target = value;
            }
            string scope = (row.BuildingType ?? "").Trim();
            if (scope.Length > 100 || scope.Any(char.IsControl)) return InvalidDraft($"Building type for {row.Name} is invalid.", showErrors);
            int limit = -1;
            if (!string.Equals(row.Limit?.Trim(), "All", StringComparison.OrdinalIgnoreCase) &&
                (!int.TryParse(row.Limit, NumberStyles.Integer, CultureInfo.CurrentCulture, out limit) || limit is < 0 or > 100000))
                return InvalidDraft($"Use All or a building count between 0 and 100,000 for {row.Name}.", showErrors);
            if (!Enum.TryParse<MaintenanceMode>(row.Mode, out var mode) || !Enum.IsDefined(mode))
                return InvalidDraft($"Choose Each or Across for {row.Name}.", showErrors);
            if (target is null && !row.Enabled && scope.Length == 0 && limit == -1 && mode == MaintenanceMode.Each) continue;
            if (!keys.Add((row.Id, scope.ToUpperInvariant())))
                return InvalidDraft($"There are duplicate {row.Name} rules for the same building type.", showErrors);
            rules.Add(new() { ResourceId = row.Id, Target = target, Enabled = row.Enabled, BuildingType = scope,
                Mode = mode, BuildingLimit = limit,
                LegacyBuildingId = row.LegacyBuildingId is int oldId && LegacyBuildingCatalog.Resolve(oldId).Equals(scope, StringComparison.OrdinalIgnoreCase) ? oldId : null });
        }
        var oldAutomation = _state.AutomationProfiles[_state.SelectedProfile];
        _state.AutomationProfiles[_state.SelectedProfile] = new() { IntervalSeconds = interval, Rules = oldAutomation.Rules,
            MaintenanceRules = rules, CapacityRules = oldAutomation.CapacityRules };
        if (SaveSettings(showErrors))
        {
            _dirty = false; _autoSave.Stop(); StatusText.Text = $"Saved {_state.SelectedProfile} · {rules.Count(r => r.Enabled)} rules configured · automation starts separately"; return true;
        }
        _state.AutomationProfiles[_state.SelectedProfile] = oldAutomation;
        StatusText.Text = "Could not save changes; previous settings are intact."; return false;
    }
    private bool InvalidDraft(string message, bool showErrors)
    {
        StatusText.Text = $"Not saved: {message}";
        if (showErrors) MessageBox.Show(this, message, "Settings not saved", MessageBoxButton.OK, MessageBoxImage.Warning);
        return false;
    }
    private void BulkTargets_Click(object sender, RoutedEventArgs e)
    {
        var selected = ResourceGrid.SelectedItems.OfType<ResourceRow>().ToArray();
        if (selected.Length == 0) { MessageBox.Show(this, "Select one or more resource rows first.", "Set profile targets", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        var dialog = new AmountDialog(selected.Length) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        foreach (var row in selected) row.Target = dialog.Amount.ToString(CultureInfo.CurrentCulture);
        StatusText.Text = $"Updated {selected.Length} targets. Saving…";
    }
    private void AddRule_Click(object sender, RoutedEventArgs e)
    {
        if (ResourceGrid.SelectedItem is not ResourceRow selected)
        { MessageBox.Show(this, "Select a resource row first.", "Add another rule", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        // A new blank row must remain visible even when the user was viewing
        // only configured rules.
        ConfiguredOnly.IsChecked = false;
        AddRow(selected.Id);
        _view.Refresh(); UpdateCount();
        ResourceGrid.SelectedItem = _resources.Last();
        ResourceGrid.ScrollIntoView(_resources.Last());
        StatusText.Text = "Set a different building type on the new row.";
    }
    private void RemoveRule_Click(object sender, RoutedEventArgs e)
    {
        var selected = ResourceGrid.SelectedItems.OfType<ResourceRow>().ToArray();
        if (selected.Length == 0) { MessageBox.Show(this, "Select one or more rule rows first.", "Remove rules", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        foreach (var row in selected) _resources.Remove(row);
        foreach (int id in selected.Select(r => r.Id).Distinct())
            if (_catalog.ContainsKey(id) && !_resources.Any(r => r.Id == id)) AddRow(id);
        _view.Refresh(); UpdateCount(); MarkDirty();
        StatusText.Text = $"Removed {selected.Length} rules. Saving…";
    }
    private void ToggleSelected_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string value) return;
        var rows = ResourceGrid.SelectedItems.OfType<ResourceRow>().ToArray();
        if (rows.Length == 0) { MessageBox.Show(this, "Select one or more resource rows first.", "Select rules", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        foreach (var row in rows) row.Enabled = value == "on";
        StatusText.Text = $"Updated {rows.Length} rules. Saving…";
    }
    private void ImportOriginal_Click(object sender, RoutedEventArgs e)
    {
        if (!_settingsAvailable || !SavePending()) return;
        var dialog = new OpenFileDialog { Title = "Choose the original editor's preferences", Filter = "Original preferences (*.userpref)|*.userpref|All files (*.*)|*.*", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var imported = LegacyRuleImport.Read(dialog.FileName);
            string explanation = $"Found {imported.Rules.Count} resource rules and {imported.Capacities.Count} capacity rules.\n\nA new profile will preserve their building types, amounts, modes, limits and enabled settings. The original file will not be changed. Other original-editor settings are not imported. Imported rules do not start by themselves; supported rules can be run from the experimental monitor.\n\nThe original editor may be open. If rules change during import, close it and import again.";
            if (MessageBox.Show(this, explanation, "Import original rules", MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK) return;
            string name = "Original editor";
            for (int i = 2; _state.Profiles.ContainsKey(name); i++) name = $"Original editor ({i})";
            var previous = _state.SelectedProfile;
            _state.Profiles.Add(name, []);
            _state.AutomationProfiles.Add(name, new AutomationProfileSettings {
                MaintenanceRules = imported.Rules.ToList(), CapacityRules = imported.Capacities.ToList()
            });
            _state.SelectedProfile = name;
            if (!SaveSettings())
            {
                _state.Profiles.Remove(name); _state.AutomationProfiles.Remove(name); _state.SelectedProfile = previous;
                return;
            }
            RefreshProfiles(); LoadProfile();
            StatusText.Text = $"Imported {imported.Rules.Count} resource rules and {imported.Capacities.Count} capacity rules into {name}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or DllNotFoundException or EntryPointNotFoundException or OverflowException)
        { MessageBox.Show(this, ex.Message, "Could not import original rules", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    private void CapacityRules_Click(object sender, RoutedEventArgs e)
    {
        if (!_settingsAvailable || !SavePending()) return;
        var automation = _state.AutomationProfiles[_state.SelectedProfile];
        var dialog = new CapacityWindow(automation.CapacityRules) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        var previous = automation.CapacityRules;
        automation.CapacityRules = dialog.Result;
        if (!SaveSettings()) { automation.CapacityRules = previous; return; }
        StatusText.Text = $"Saved {automation.CapacityRules.Count} capacity rules. Supported enabled rules run from the monitor.";
    }
    private bool SaveSettings(bool showErrors = true)
    {
        if (!_settingsAvailable) return false;
        try { ProfileStore.Save(_settingsPath, _state); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or ArgumentException)
        { if (showErrors) MessageBox.Show(this, "Settings could not be saved. The previous file was preserved.\n\n" + ex.Message, "Save failed", MessageBoxButton.OK, MessageBoxImage.Error); return false; }
    }
    private void ChooseGame_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Choose Ostriv's executable", Filter = "Ostriv executable (ostriv.exe)|ostriv.exe", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        var previous = _state.GamePath; _state.GamePath = dialog.FileName;
        if (!SaveSettings()) { _state.GamePath = previous; return; }
        GamePathText.Text = _state.GamePath;
        StatusText.Text = _dirty ? "Game path saved · profile changes are unsaved." : "Game path saved.";
        GameHeading.Text = "GAME NOT CHECKED"; GameDetail.Text = "Check this installation before relying on compatibility results.";
    }
    private async void Inspect_Click(object sender, RoutedEventArgs e) => await InspectAsync();
    private async Task InspectAsync()
    {
        if (!File.Exists(_state.GamePath)) { GameHeading.Text = "GAME NOT FOUND"; GameDetail.Text = "Choose the installed ostriv.exe to check its compatibility."; return; }
        InspectButton.IsEnabled = false; GameHeading.Text = "CHECKING GAME…";
        try
        {
            var path = _state.GamePath;
            var report = await Task.Run(() => GameInspector.Inspect(path));
            if (path != _state.GamePath) return;
            GameHeading.Text = report.LiveRead ? "GAME CONNECTED · READ ONLY" : "INSTALLATION CHECKED · READ ONLY";
            GameDetail.Text = $"{report.UniqueCount} of {report.Patterns.Count} legacy patterns matched uniquely. Experimental automation has limited compatibility. {report.Detail}";
            GamePathText.Text = $"{report.Path}  ·  FILE SHA-256 {report.Sha256[..12]}…";
            GameBadge.Text = report.LiveRead ? "LIVE · READ ONLY" : "FILE · READ ONLY";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or BadImageFormatException or System.ComponentModel.Win32Exception)
        { GameHeading.Text = "CHECK FAILED"; GameDetail.Text = ex.Message; GameBadge.Text = "READ ONLY"; }
        finally { InspectButton.IsEnabled = true; }
    }
    private void Window_Closing(object? sender, CancelEventArgs e) { _autoSave.Stop(); if (!SavePending()) e.Cancel = true; }
}
