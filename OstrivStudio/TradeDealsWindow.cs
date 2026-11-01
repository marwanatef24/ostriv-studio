using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using OstrivStudio.Core;

namespace OstrivStudio;

public sealed class TradeDealsWindow : Window
{
    private sealed record Row(TradeDeal Deal, string City, string Resource, string Direction, string Total, string Completed, string Remaining, string Stock);
    private readonly DiscoveryResult _scan;
    private readonly IReadOnlyDictionary<int, string> _names;
    private readonly Action<int, string> _find;
    private readonly Action _invalidate;
    private bool _invalidated;
    private bool _refreshing, _modalOpen, _everDisplayed;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(5) };
    private IReadOnlyList<TradeDeal>? _displayedDeals;
    private readonly DataGrid _grid = new() { AutoGenerateColumns = false, IsReadOnly = true, SelectionMode = DataGridSelectionMode.Extended, SelectionUnit = DataGridSelectionUnit.FullRow, MinHeight = 180, Margin = new Thickness(0, 16, 0, 12) };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _refresh = new() { Content = "Refresh deals" }, _findStock = new() { Content = "Find stock buildings", IsEnabled = false };
    private readonly Button _prepare = new() { Content = "Use remaining as export target…", IsEnabled = false };
    private readonly Button _editTargets = new() { Content = "Edit building targets…", IsEnabled = false, Margin = new Thickness(8, 0, 0, 0) };
    private readonly ComboBox _post = new() { Width = 260, Margin = new Thickness(0, 0, 10, 0) };
    private sealed record PostChoice(BuildingRow Building) { public override string ToString() => Building.Name + "  ·  " + Building.Summary; }
    private readonly PostChoice[] _choices;
    private readonly Button _allocate = new() { Content = "Allocate selected exports…", IsEnabled = false };
    private (int Id, string Type)? _pending;
    public TradeDealsWindow(DiscoveryResult scan, IReadOnlyDictionary<int, string> names, IReadOnlyList<TradeDeal> deals, Action<int, string> find,
        IReadOnlyDictionary<ulong, BuildingRow> buildingRows, Action invalidate)
    {
        _scan = scan; _names = names; _find = find; _invalidate = invalidate;
        Title = "Trade deals"; Width = 1000; Height = 610; MinWidth = 820; MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(242, 245, 249)); Foreground = new SolidColorBrush(Color.FromRgb(23, 37, 58));
        void Column(string name, string property, double width) => _grid.Columns.Add(new DataGridTextColumn { Header = name, Binding = new Binding(property), Width = new DataGridLength(width, DataGridLengthUnitType.Star) });
        Column("City", nameof(Row.City), 1.4); Column("Resource", nameof(Row.Resource), 1.7); Column("Type", nameof(Row.Direction), 0.9);
        Column("Total", nameof(Row.Total), 0.8); Column("Completed", nameof(Row.Completed), 1); Column("Remaining", nameof(Row.Remaining), 1); Column("Trade stock", nameof(Row.Stock), 1.3);
        _choices = scan.Buildings.Where(b => TradingStockLimits.SupportsClass(b.ClassName) && buildingRows.ContainsKey(b.Address))
            .Select(b => new PostChoice(buildingRows[b.Address])).ToArray();
        _grid.SelectionChanged += (_, _) => SelectBuildingChoices(); _post.SelectionChanged += (_, _) => UpdateActions();
        var root = new DockPanel { Margin = new Thickness(26) };
        var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        top.Children.Add(new TextBlock { Text = "Trade deals", FontWeight = FontWeights.SemiBold, FontSize = 28 });
        top.Children.Add(new TextBlock { Text = "Open the world map in Ostriv and select a city. Deals refresh every five seconds. Select deals with Ctrl or Shift to allocate exports. Building stock is from the last scan.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 14), Foreground = new SolidColorBrush(Color.FromRgb(86, 102, 125)) });
        top.Children.Add(_status);
        var action = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        action.Children.Add(_post); action.Children.Add(_prepare); action.Children.Add(_editTargets); top.Children.Add(action);
        var bottom = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        _refresh.Click += Refresh_Click; _findStock.Click += Find_Click; _prepare.Click += Prepare_Click; _editTargets.Click += EditTargets_Click;
        _allocate.Click += Allocate_Click;
        bottom.Children.Add(_refresh); bottom.Children.Add(_findStock); bottom.Children.Add(_allocate); bottom.Children.Add(new Button { Content = "Close", IsCancel = true });
        DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom); root.Children.Add(_grid); Content = root;
        _timer.Tick += async (_, _) => await RefreshDealsAsync();
        Loaded += (_, _) => _timer.Start();
        Closed += (_, _) => { _timer.Stop(); if (_pending is { } p) _find(p.Id, p.Type); };
        ShowDeals(deals);
    }
    private static string City(int id) => id switch { 1 => "Merefa", 2 => "Balakliya", 3 => "Derkachi", 4 => "Lymanivka", 5 => "Barvinkove", _ => $"City {id}" };
    private void SelectBuildingChoices()
    {
        string? type = (_grid.SelectedItem as Row)?.Deal.TradingBuildingClass;
        _post.ItemsSource = type is null ? [] : _choices.Where(c => c.Building.Candidate.ClassName == type).ToArray();
        _post.SelectedIndex = _post.Items.Count == 0 ? -1 : 0;
        UpdateActions();
    }
    private static string Amount(double value) => value.ToString("N2", CultureInfo.CurrentCulture);
    private void UpdateActions()
    {
        var selectedRows = _grid.SelectedItems.OfType<Row>().ToArray();
        _findStock.IsEnabled = selectedRows.Length == 1 && selectedRows[0].Deal.TradingBuildingClass is not null;
        _prepare.IsEnabled = selectedRows.Length == 1 && _grid.SelectedItem is Row row && !row.Deal.IsImport &&
            row.Deal.Remaining > 0 && row.Deal.Remaining <= 1_000_000 && _post.SelectedItem is PostChoice choice &&
            choice.Building.Candidate.ClassName == row.Deal.TradingBuildingClass;
        _editTargets.IsEnabled = !_refreshing && !_modalOpen && _post.SelectedItem is PostChoice;
        var selectedDeals = selectedRows.Select(r => r.Deal).ToArray();
        var route = selectedDeals.Select(d => d.TradingBuildingClass).Distinct().ToArray();
        _allocate.IsEnabled = selectedDeals.Length > 0 && selectedDeals.All(d => !d.IsImport && d.Remaining > 0 && d.Remaining <= 1_000_000)
            && route.Length == 1 && route[0] is not null && _choices.Any(c => c.Building.Candidate.ClassName == route[0]);
    }
    private void ShowDeals(IReadOnlyList<TradeDeal> deals)
    {
        if (_displayedDeals is not null && _displayedDeals.SequenceEqual(deals)) return;
        bool first = !_everDisplayed;
        var selected = _grid.SelectedItems.OfType<Row>().Select(r => (r.Deal.CityId, r.Deal.DealId, r.Deal.ResourceId, r.Deal.Flags)).ToHashSet();
        ulong? chosenPost = (_post.SelectedItem as PostChoice)?.Building.Candidate.Address;
        var rows = deals.Select(deal =>
        {
            var buildings = _scan.Buildings.Where(b => b.ClassName == deal.TradingBuildingClass).ToArray();
            double stock = buildings.SelectMany(b => b.Resources).Where(r => r.Id == deal.ResourceId && r.Status == 0).Sum(r => (double)r.Amount);
            return new Row(deal, City(deal.CityId), _names.GetValueOrDefault(deal.ResourceId, $"Resource {deal.ResourceId}"),
                deal.IsImport ? "Import" : "Export", Amount(deal.Total), Amount(deal.Completed), Amount(deal.Remaining),
                buildings.Length == 0 ? "No candidate" : Amount(stock));
        }).ToArray();
        _displayedDeals = deals.ToArray(); _everDisplayed = true;
        _grid.ItemsSource = rows;
        foreach (var row in rows.Where(r => selected.Contains((r.Deal.CityId, r.Deal.DealId, r.Deal.ResourceId, r.Deal.Flags))))
            if (rows.Count(r => (r.Deal.CityId, r.Deal.DealId, r.Deal.ResourceId, r.Deal.Flags) ==
                (row.Deal.CityId, row.Deal.DealId, row.Deal.ResourceId, row.Deal.Flags)) == 1) _grid.SelectedItems.Add(row);
        _status.Text = deals.Count == 0 ? "No deals are visible for the selected city. Keep the world map open and choose a city with an active deal." :
            $"{deals.Count} visible deal(s). Select an export or select several on the same route to allocate them together.";
        if (first && deals.Count > 0) _grid.SelectedIndex = 0;
        if (chosenPost is ulong address && _post.Items.OfType<PostChoice>().FirstOrDefault(p => p.Building.Candidate.Address == address) is PostChoice matching)
            _post.SelectedItem = matching;
        UpdateActions();
    }
    private void Allocate_Click(object sender, RoutedEventArgs e)
    {
        if (_refreshing || _modalOpen) return;
        var exports = _grid.SelectedItems.OfType<Row>().Select(r => r.Deal).ToArray();
        var types = exports.Select(d => d.TradingBuildingClass).Distinct().ToArray();
        if (exports.Length == 0 || types.Length != 1 || types[0] is null) { _status.Text = "Choose a known city with remaining export deals."; return; }
        var buildings = _choices.Where(c => c.Building.Candidate.ClassName == types[0]).Select(c => c.Building).ToArray();
        _modalOpen = true;
        try { new TradeAllocationWindow(_scan, exports, buildings, _names, () => { _invalidated = true; _invalidate(); }) { Owner = this }.ShowDialog(); }
        finally { _modalOpen = false; }
        if (_invalidated) Close();
    }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshDealsAsync();
    private async Task RefreshDealsAsync()
    {
        if (_refreshing || _modalOpen || !IsLoaded) return;
        _refreshing = true; _refresh.IsEnabled = false;
        try { var deals = await Task.Run(() => TradeDealReader.CaptureFromGame(_scan)); if (IsLoaded) ShowDeals(deals); }
        catch (Exception ex)
        {
            if (IsLoaded) { _displayedDeals = null; _grid.ItemsSource = null; UpdateActions(); _status.Text = "Deals unavailable: " + ex.Message; }
        }
        finally { _refreshing = false; if (IsLoaded) { _refresh.IsEnabled = true; UpdateActions(); } }
    }
    private void Find_Click(object sender, RoutedEventArgs e)
    {
        if (_grid.SelectedItem is not Row row || row.Deal.TradingBuildingClass is not string type) return;
        _pending = (row.Deal.ResourceId, type); Close();
    }
    private async void EditTargets_Click(object sender, RoutedEventArgs e)
    {
        if (_refreshing || _modalOpen || _post.SelectedItem is not PostChoice post) return;
        _modalOpen = true; _editTargets.IsEnabled = false; _prepare.IsEnabled = false; _refresh.IsEnabled = false;
        _status.Text = "Checking this trading building's export targets…";
        try
        {
            var limits = await Task.Run(() => TradingStockLimits.CaptureFromGame(_scan, [post.Building.Candidate]));
            if (!IsLoaded) return;
            new TradingLimitsWindow(limits, [post.Building], _names, () => { _invalidate(); _invalidated = true; }) { Owner = this }.ShowDialog();
            if (_invalidated) { Close(); return; }
            if (IsLoaded) _status.Text = "The target editor is closed. Stock amounts are from the last building scan.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception or OverflowException)
        { if (IsLoaded) _status.Text = ex.Message; }
        finally { _modalOpen = false; if (IsLoaded) { _refresh.IsEnabled = true; UpdateActions(); } }
    }
    private async void Prepare_Click(object sender, RoutedEventArgs e)
    {
        if (_refreshing || _modalOpen || _grid.SelectedItem is not Row row || _post.SelectedItem is not PostChoice post) return;
        _modalOpen = true;
        _prepare.IsEnabled = false; _refresh.IsEnabled = false; _status.Text = "Checking this deal and trading building…";
        try
        {
            var deal = row.Deal;
            var latest = await Task.Run(() => TradeDealReader.CaptureFromGame(_scan));
            if (latest.Count(d => d == deal) != 1) throw new InvalidOperationException("The selected deal changed. Refresh it and try again.");
            var limits = await Task.Run(() => TradingStockLimits.CaptureFromGame(_scan, [post.Building.Candidate]));
            if (!IsLoaded) return;
            new TradingLimitsWindow(limits, [post.Building], _names, () => { _invalidate(); _invalidated = true; }, deal) { Owner = this }.ShowDialog();
            if (_invalidated) { Close(); return; }
            if (IsLoaded) _status.Text = "The export target preview is closed; refresh deals if they change in Ostriv.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception or OverflowException)
        { if (IsLoaded) _status.Text = ex.Message; }
        finally { _modalOpen = false; _refresh.IsEnabled = true; UpdateActions(); }
    }
}
