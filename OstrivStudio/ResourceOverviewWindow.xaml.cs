using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.Win32;
using OstrivStudio.Core;

namespace OstrivStudio;

public sealed record LedgerResource(int Id, string Name, int Candidates, double Stored, double Reserved, double Awaited, double Other);
public sealed record LedgerLocation(string Name, double Stored, double Reserved, double Awaited, double Other);
public sealed record ResourceRefresh(DiscoveryResult Scan, DateTime ObservedUtc, IReadOnlyDictionary<ulong, string> Buildings);

public partial class ResourceOverviewWindow : Window
{
    private DiscoveryResult _scan;
    private readonly IReadOnlyDictionary<int, string> _names;
    private IReadOnlyDictionary<ulong, string> _buildings;
    private readonly ResourceSnapshotHistory _history;
    private readonly Action<int> _findBuildings;
    private readonly Func<CancellationToken, Task<ResourceRefresh>> _refresh;
    private readonly int _intervalSeconds;
    private readonly ObservableCollection<LedgerResource> _resources = [];
    private readonly ICollectionView _view;
    private ResourceInsightReport? _report;
    private bool _ready;
    private CancellationTokenSource? _operation;
    private bool _monitoring, _closeRequested;
    private DateTime _observedUtc;
    private int? _pendingFind;
    private ResourceScope Scope => ScopeBox.SelectedIndex == 1 ? ResourceScope.AllCandidates : ResourceScope.StorageBuildings;

    public ResourceOverviewWindow(DiscoveryResult scan, IReadOnlyDictionary<int, string> names,
        IReadOnlyDictionary<ulong, string> buildings, ResourceSnapshotHistory history, DateTime observedUtc,
        int intervalSeconds, Func<CancellationToken, Task<ResourceRefresh>> refresh, Action<int> findBuildings)
    {
        _scan = scan; _names = names; _buildings = buildings; _history = history; _findBuildings = findBuildings; _refresh = refresh;
        _intervalSeconds = Math.Clamp(intervalSeconds, 5, 86400); _observedUtc = observedUtc;
        InitializeComponent();
        _view = CollectionViewSource.GetDefaultView(_resources);
        _view.Filter = item => item is LedgerResource row && ResourceInsights.Matches(row.Id, row.Name, SearchBox.Text);
        ResourceGrid.ItemsSource = _view;
        SnapshotText.Text = $"Scan from {observedUtc.ToLocalTime():t} · Choose a resource to explore";
        ScopeBox.SelectedIndex = 0;
        _ready = true;
        Rebuild();
        MonitorButton.ToolTip = $"Read the running game every {_intervalSeconds} seconds after each completed scan. Each scan may take a while.";
        Closing += (_, e) =>
        {
            if (_operation is null) return;
            e.Cancel = true; _closeRequested = true; _operation.Cancel();
            SamplingStatus.Text = "Stopping the current scan before closing…";
        };
        Closed += (_, _) => { if (_pendingFind is int id) _findBuildings(id); };
    }

    private void Rebuild()
    {
        if (!_ready) return;
        int? selected = (ResourceGrid.SelectedItem as LedgerResource)?.Id;
        _report = ResourceInsights.Create(_scan, Scope);
        _resources.Clear();
        foreach (var item in _report.Resources.OrderBy(x => _names.GetValueOrDefault(x.ResourceId, $"Unknown resource {x.ResourceId}"), StringComparer.CurrentCultureIgnoreCase))
            _resources.Add(new(item.ResourceId, _names.GetValueOrDefault(item.ResourceId, $"Unknown resource {item.ResourceId}"), item.Candidates,
                item.Stored, item.Reserved, item.Awaited, item.Other));
        ScopeCount.Text = $"{_report.CandidateCount:N0} candidates";
        string partial = _scan.Complete && _scan.UnreadableChunks == 0 ? "" : "This scan is incomplete. ";
        string overlap = _report.SharedArrays > 0 ? $" {_report.SharedArrays} candidate(s) share arrays, so totals may overlap." : "";
        ScopeNote.Text = partial + (Scope == ResourceScope.StorageBuildings
            ? "Storage candidates include granaries, warehouses, camp, stone storage and hay barracks."
            : "All scanned candidates are included; nested or stale objects can duplicate amounts.") + overlap;
        _view.Refresh(); UpdateResourceCount();
        var visible = _view.Cast<LedgerResource>().ToArray();
        var row = visible.FirstOrDefault(x => x.Id == selected) ?? visible.FirstOrDefault();
        ResourceGrid.SelectedItem = row;
        if (row is null) ShowSelection(null);
    }

    private void UpdateResourceCount()
    {
        int count = _view.Cast<object>().Count();
        ResourceCount.Text = $"{count:N0} resources in view";
        ExportButton.IsEnabled = count > 0;
    }
    private void Scope_Changed(object sender, SelectionChangedEventArgs e) { if (_ready) Rebuild(); }
    private void Search_Changed(object sender, TextChangedEventArgs e)
    {
        if (SearchHint is not null) SearchHint.Visibility = string.IsNullOrWhiteSpace(SearchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        if (!_ready) return;
        _view.Refresh(); UpdateResourceCount();
        if (ResourceGrid.SelectedItem is not LedgerResource selected || !_view.Contains(selected))
            ResourceGrid.SelectedItem = _view.Cast<LedgerResource>().FirstOrDefault();
        if (ResourceGrid.SelectedItem is null) ShowSelection(null);
    }
    private void Resource_Changed(object sender, SelectionChangedEventArgs e) { if (_ready) ShowSelection(ResourceGrid.SelectedItem as LedgerResource); }
    private static string Amount(double value) => value.ToString("N2", CultureInfo.CurrentCulture);
    private void ShowSelection(LedgerResource? resource)
    {
        FindButton.IsEnabled = resource is not null;
        SelectedName.Text = resource?.Name ?? "Select a resource";
        SelectedDetail.Text = resource is null ? "Explore amounts and locations from this scan." : $"Resource {resource.Id} · present in {resource.Candidates:N0} candidate(s)";
        StoredText.Text = resource is null ? "—" : Amount(resource.Stored);
        ReservedText.Text = resource is null ? "—" : Amount(resource.Reserved);
        AwaitedText.Text = resource is null ? "—" : Amount(resource.Awaited);
        OtherText.Text = resource is null ? "—" : Amount(resource.Other);
        var locations = resource is null || _report is null ? [] : _report.Locations.Where(x => x.ResourceId == resource.Id)
            .OrderByDescending(x => x.Stored).ThenBy(x => _buildings.GetValueOrDefault(x.BuildingAddress, x.BuildingType))
            .Select(x => new LedgerLocation(_buildings.GetValueOrDefault(x.BuildingAddress, x.BuildingType), x.Stored, x.Reserved, x.Awaited, x.Other)).ToArray();
        LocationGrid.ItemsSource = locations;
        LocationDetail.Text = resource is null ? "Choose a resource to see its matching candidates." : $"{locations.Length:N0} matching candidate(s) in this scope.";
        DrawTrend(resource);
        DrawDistribution();
    }
    private void DrawTrend(LedgerResource? resource)
    {
        TrendCanvas.Children.Clear();
        if (resource is null) { HistoryText.Text = "Select a resource to see its scan history."; return; }
        var series = _history.MatchesSession(_scan) ? _history.Series(resource.Id, Scope) : [];
        if (series.Count < 2)
        { HistoryText.Text = "Scan again to start a history for this game session."; return; }
        double first = series[0].Stored, last = series[^1].Stored, delta = last - first;
        HistoryText.Text = $"{series.Count} scans · {series[0].ObservedUtc.ToLocalTime():t}–{series[^1].ObservedUtc.ToLocalTime():t} · {(delta >= 0 ? "+" : "")}{Amount(delta)}";
        double width = Math.Max(TrendCanvas.ActualWidth, 1), height = Math.Max(TrendCanvas.ActualHeight, 1);
        double min = series.Min(x => x.Stored), max = series.Max(x => x.Stored), range = max - min;
        var baseline = new Line { X1 = 0, Y1 = height - 2, X2 = width, Y2 = height - 2, Stroke = new SolidColorBrush(Color.FromRgb(84, 111, 114)), StrokeThickness = 1 };
        TrendCanvas.Children.Add(baseline);
        var path = new Polyline { Stroke = new SolidColorBrush(Color.FromRgb(36, 107, 253)), StrokeThickness = 2.5, StrokeLineJoin = PenLineJoin.Round };
        for (int i = 0; i < series.Count; i++)
        {
            double x = i * (width - 3) / (series.Count - 1);
            double y = range == 0 ? height / 2 : 3 + (height - 7) * (1 - (series[i].Stored - min) / range);
            path.Points.Add(new Point(x, y));
        }
        TrendCanvas.Children.Add(path);
    }
    private void Trend_SizeChanged(object sender, SizeChangedEventArgs e) { if (_ready) DrawTrend(ResourceGrid.SelectedItem as LedgerResource); }
    private void Distribution_SizeChanged(object sender, SizeChangedEventArgs e) { if (_ready) DrawDistribution(); }
    private void DrawDistribution()
    {
        DistributionCanvas.Children.Clear();
        var rows = (LocationGrid.ItemsSource as IEnumerable<LedgerLocation> ?? []).Where(x => x.Stored > 0).ToArray();
        double total = rows.Sum(x => x.Stored), width = DistributionCanvas.ActualWidth;
        if (rows.Length == 0 || total <= 0 || width <= 0)
        {
            DistributionText.Text = ResourceGrid.SelectedItem is null ? "Select a resource to see its distribution." : "No stored stock in this scope.";
            return;
        }
        Color[] palette = [Color.FromRgb(228,185,121), Color.FromRgb(111,177,166), Color.FromRgb(132,160,201), Color.FromRgb(202,137,124), Color.FromRgb(178,161,196), Color.FromRgb(116,142,145)];
        double x = 0;
        for (int i = 0; i < rows.Length; i++)
        {
            var row = rows[i]; double segment = width * row.Stored / total;
            var bar = new Rectangle { Width = Math.Max(0, segment), Height = 10, Fill = new SolidColorBrush(palette[Math.Min(i, palette.Length - 1)]),
                ToolTip = $"{row.Name}: {Amount(row.Stored)} ({row.Stored / total:P1})" };
            Canvas.SetLeft(bar, x); DistributionCanvas.Children.Add(bar); x += segment;
        }
        DistributionText.Text = string.Join("  ·  ", rows.Take(2).Select(row => $"{row.Name} {row.Stored / total:P0}")) + (rows.Length > 2 ? $"  ·  +{rows.Length - 2} more" : "");
    }
    private void FindBuildings_Click(object sender, RoutedEventArgs e)
    {
        if (ResourceGrid.SelectedItem is not LedgerResource selected) return;
        _pendingFind = selected.Id; Close();
    }
    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_report is null) return;
        var shown = _view.Cast<LedgerResource>().ToDictionary(r => r.Id);
        if (shown.Count == 0) return;
        string scope = Scope == ResourceScope.StorageBuildings ? "Storage buildings" : "All candidates";
        bool complete = _scan.Complete && _scan.UnreadableChunks == 0;
        var rows = _report.Locations.Where(l => shown.ContainsKey(l.ResourceId))
            .OrderBy(l => shown[l.ResourceId].Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(l => _buildings.GetValueOrDefault(l.BuildingAddress, l.BuildingType), StringComparer.CurrentCultureIgnoreCase)
            .Select(l => new ResourceLedgerExportRow(scope, _observedUtc, complete, _report.SharedArrays,
                shown[l.ResourceId].Name, l.ResourceId, _buildings.GetValueOrDefault(l.BuildingAddress, l.BuildingType),
                l.Stored, l.Reserved, l.Awaited, l.Other));
        var dialog = new SaveFileDialog { Title = "Export resource ledger", Filter = "CSV files (*.csv)|*.csv", DefaultExt = ".csv",
            AddExtension = true, FileName = "Ostriv resources.csv" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            File.WriteAllText(dialog.FileName, ResourceLedgerCsv.Create(rows), new UTF8Encoding(true));
            SamplingStatus.Text = $"Exported {shown.Count:N0} shown resource(s) and their candidate breakdown.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { MessageBox.Show(this, ex.Message, "Could not export", MessageBoxButton.OK, MessageBoxImage.Error); }
    }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RunRefreshAsync(false);
    private void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        _history.Clear(); DrawTrend(ResourceGrid.SelectedItem as LedgerResource);
        SamplingStatus.Text = _monitoring ? "History cleared. Monitoring will record the next completed scan." : "History cleared. Refresh or start monitoring to record a new scan.";
    }
    private async void Monitor_Click(object sender, RoutedEventArgs e)
    {
        if (_operation is not null) { if (_monitoring) _operation.Cancel(); return; }
        await RunRefreshAsync(true);
    }
    private async Task RunRefreshAsync(bool repeat)
    {
        if (_operation is not null) return;
        var cts = new CancellationTokenSource(); _operation = cts; _monitoring = repeat;
        RefreshButton.IsEnabled = false;
        MonitorButton.IsEnabled = repeat;
        MonitorButton.Content = repeat ? "Stop monitoring" : "Start monitoring";
        try
        {
            do
            {
                SamplingStatus.Text = "Reading the running game; large settlements can take a while…";
                var result = await _refresh(cts.Token);
                cts.Token.ThrowIfCancellationRequested();
                _scan = result.Scan; _buildings = result.Buildings; _observedUtc = result.ObservedUtc;
                SnapshotText.Text = $"Scan from {result.ObservedUtc.ToLocalTime():t} · Choose a resource to explore";
                Rebuild();
                bool complete = result.Scan.Complete && result.Scan.UnreadableChunks == 0;
                SamplingStatus.Text = !complete ? (repeat ? $"Partial scan; not added to history. The next scan starts in {_intervalSeconds} seconds." : "Partial scan; not added to history.")
                    : repeat ? $"Monitoring · next scan starts {_intervalSeconds} seconds after this one. Clear history when loading another save." : "Scan complete. Clear history when loading another save in the same game process.";
                if (repeat) await Task.Delay(TimeSpan.FromSeconds(_intervalSeconds), cts.Token);
            } while (repeat);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { SamplingStatus.Text = "Monitoring stopped. The last completed scan is still shown."; }
        catch (Exception ex) { SamplingStatus.Text = "Resource monitoring stopped: " + ex.Message; }
        finally
        {
            _operation = null; _monitoring = false; cts.Dispose();
            RefreshButton.IsEnabled = true; MonitorButton.IsEnabled = true; MonitorButton.Content = "Start monitoring";
            if (_closeRequested) { _closeRequested = false; Close(); }
        }
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
