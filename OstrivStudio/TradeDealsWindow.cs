using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
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
    private readonly DataGrid _grid = new() { AutoGenerateColumns = false, IsReadOnly = true, SelectionMode = DataGridSelectionMode.Single, MinHeight = 180, Margin = new Thickness(0, 16, 0, 12) };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _refresh = new() { Content = "Refresh deals" }, _findStock = new() { Content = "Find stock buildings", IsEnabled = false };
    private readonly Button _prepare = new() { Content = "Use remaining as export target…", IsEnabled = false };
    private readonly ComboBox _post = new() { Width = 260, Margin = new Thickness(0, 0, 10, 0) };
    private sealed record PostChoice(BuildingRow Building) { public override string ToString() => Building.Name + "  ·  " + Building.Summary; }
    private (int Id, string Type)? _pending;
    public TradeDealsWindow(DiscoveryResult scan, IReadOnlyDictionary<int, string> names, IReadOnlyList<TradeDeal> deals, Action<int, string> find,
        IReadOnlyDictionary<ulong, BuildingRow> buildingRows, Action invalidate)
    {
        _scan = scan; _names = names; _find = find; _invalidate = invalidate;
        Title = "Trade deals"; Width = 1000; Height = 610; MinWidth = 820; MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(20, 40, 51)); Foreground = new SolidColorBrush(Color.FromRgb(240, 238, 230));
        void Column(string name, string property, double width) => _grid.Columns.Add(new DataGridTextColumn { Header = name, Binding = new Binding(property), Width = new DataGridLength(width, DataGridLengthUnitType.Star) });
        Column("City", nameof(Row.City), 1.4); Column("Resource", nameof(Row.Resource), 1.7); Column("Type", nameof(Row.Direction), 0.9);
        Column("Total", nameof(Row.Total), 0.8); Column("Completed", nameof(Row.Completed), 1); Column("Remaining", nameof(Row.Remaining), 1); Column("Trade stock", nameof(Row.Stock), 1.3);
        _grid.SelectionChanged += (_, _) => UpdateActions(); _post.SelectionChanged += (_, _) => UpdateActions();
        var root = new DockPanel { Margin = new Thickness(26) };
        var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        top.Children.Add(new TextBlock { Text = "Trade deals", FontFamily = new FontFamily("Georgia"), FontSize = 28 });
        top.Children.Add(new TextBlock { Text = "Open the world map in Ostriv and select a city. This view reads that city’s visible deals; trading-stock amounts are from the last building scan.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 14), Foreground = Brushes.LightGray });
        top.Children.Add(_status);
        var action = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        _post.ItemsSource = scan.Buildings.Where(b => b.ClassName == "building_trading_post" && buildingRows.ContainsKey(b.Address))
            .Select(b => new PostChoice(buildingRows[b.Address])).ToArray();
        _post.SelectedIndex = _post.Items.Count == 0 ? -1 : 0; action.Children.Add(_post); action.Children.Add(_prepare); top.Children.Add(action);
        var bottom = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        _refresh.Click += Refresh_Click; _findStock.Click += Find_Click; _prepare.Click += Prepare_Click;
        bottom.Children.Add(_refresh); bottom.Children.Add(_findStock); bottom.Children.Add(new Button { Content = "Close", IsCancel = true });
        DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom); root.Children.Add(_grid); Content = root;
        Closed += (_, _) => { if (_pending is { } p) _find(p.Id, p.Type); };
        ShowDeals(deals);
    }
    private static string City(int id) => id switch { 1 => "Merefa", 2 => "Balakliya", 3 => "Derkachi", 4 => "Lymanivka", 5 => "Barvinkove", _ => $"City {id}" };
    private static string Type(TradeDeal deal) => deal.CityId is 4 or 5 ? "building_trading_dock" : "building_trading_post";
    private static string Amount(double value) => value.ToString("N2", CultureInfo.CurrentCulture);
    private void UpdateActions()
    {
        _findStock.IsEnabled = _grid.SelectedItem is Row;
        _prepare.IsEnabled = _grid.SelectedItem is Row row && !row.Deal.IsImport && Type(row.Deal) == "building_trading_post" &&
            row.Deal.Remaining > 0 && row.Deal.Remaining <= 1_000_000 && _post.SelectedItem is PostChoice;
    }
    private void ShowDeals(IReadOnlyList<TradeDeal> deals)
    {
        _grid.ItemsSource = deals.Select(deal =>
        {
            var buildings = _scan.Buildings.Where(b => b.ClassName == Type(deal)).ToArray();
            double stock = buildings.SelectMany(b => b.Resources).Where(r => r.Id == deal.ResourceId && r.Status == 0).Sum(r => (double)r.Amount);
            return new Row(deal, City(deal.CityId), _names.GetValueOrDefault(deal.ResourceId, $"Resource {deal.ResourceId}"),
                deal.IsImport ? "Import" : "Export", Amount(deal.Total), Amount(deal.Completed), Amount(deal.Remaining),
                buildings.Length == 0 ? "No candidate" : Amount(stock));
        }).ToArray();
        _status.Text = deals.Count == 0 ? "No deals are visible for the selected city. Keep the world map open and choose a city with an active deal." :
            $"{deals.Count} visible deal(s). Select an export and a trading post to prepare its remaining quantity as an existing export target. Dock targets are not yet supported.";
        if (deals.Count > 0) _grid.SelectedIndex = 0;
    }
    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        _refresh.IsEnabled = false; _status.Text = "Reading the selected city's deals…";
        try { var deals = await Task.Run(() => TradeDealReader.CaptureFromGame(_scan)); if (IsLoaded) ShowDeals(deals); }
        catch (Exception ex) { if (IsLoaded) _status.Text = "Could not refresh deals: " + ex.Message; }
        finally { _refresh.IsEnabled = true; }
    }
    private void Find_Click(object sender, RoutedEventArgs e)
    {
        if (_grid.SelectedItem is not Row row) return;
        _pending = (row.Deal.ResourceId, Type(row.Deal)); Close();
    }
    private async void Prepare_Click(object sender, RoutedEventArgs e)
    {
        if (_grid.SelectedItem is not Row row || _post.SelectedItem is not PostChoice post) return;
        _prepare.IsEnabled = false; _refresh.IsEnabled = false; _status.Text = "Checking this deal and trading post…";
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
        finally { _refresh.IsEnabled = true; UpdateActions(); }
    }
}
