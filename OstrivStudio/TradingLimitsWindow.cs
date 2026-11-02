using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using OstrivStudio.Core;

namespace OstrivStudio;

public sealed class TradingLimitsWindow : Window
{
    private sealed record ResourceChoice(int Id, string Name) { public override string ToString() => Name; }
    private sealed record Row(string Building, string Stock, string Current, string Result);
    private readonly TradingLimitsSnapshot _snapshot;
    private readonly BuildingRow[] _selected;
    private readonly Action _invalidate;
    private readonly TradeDeal? _requiredDeal;
    private readonly ComboBox _resource = new() { Width = 220, Margin = new Thickness(0, 0, 10, 0) };
    private readonly TextBox _amount = new() { Width = 150, Margin = new Thickness(0, 0, 10, 0) };
    private readonly ComboBox _mode = new() { Width = 165, Margin = new Thickness(0, 0, 10, 0) };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 4) };
    private readonly DataGrid _grid = new() { AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, CanUserDeleteRows = false, MinHeight = 180, Margin = new Thickness(0, 12, 0, 12) };
    private readonly Button _apply = new() { Content = "Apply targets…", IsEnabled = false };
    private (int Resource, decimal Target, bool Add)? _reviewed;
    private bool _busy;

    public TradingLimitsWindow(TradingLimitsSnapshot snapshot, BuildingRow[] selected, IReadOnlyDictionary<int, string> names, Action invalidate,
        TradeDeal? requiredDeal = null)
    {
        _snapshot = snapshot; _selected = selected; _invalidate = invalidate; _requiredDeal = requiredDeal;
        Title = "Export stock targets"; Width = 850; Height = 560; MinWidth = 700; MinHeight = 440;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(242, 245, 249)); Foreground = new SolidColorBrush(Color.FromRgb(23, 37, 58));
        var common = snapshot.Posts[0].Limits.Select(x => x.ResourceId).Where(id => snapshot.Posts.All(p => p.Limits.Any(x => x.ResourceId == id)))
            .OrderBy(id => names.GetValueOrDefault(id, $"Resource {id}"))
            .Select(id => new ResourceChoice(id, names.GetValueOrDefault(id, $"Resource {id}"))).ToArray();
        _resource.ItemsSource = common;
        _grid.Columns.Add(new DataGridTextColumn { Header = "Trading building", Binding = new Binding(nameof(Row.Building)), Width = new DataGridLength(2, DataGridLengthUnitType.Star) });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Stock at scan", Binding = new Binding(nameof(Row.Stock)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Current target", Binding = new Binding(nameof(Row.Current)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Preview", Binding = new Binding(nameof(Row.Result)), Width = new DataGridLength(2, DataGridLengthUnitType.Star) });
        var root = new DockPanel { Margin = new Thickness(26) };
        var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        top.Children.Add(new TextBlock { Text = "Export stock targets", FontWeight = FontWeights.SemiBold, FontSize = 28 });
        top.Children.Add(new TextBlock { Text = "Increase existing export targets across selected trading posts and docks. This does not change deals or stock amounts.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 16), Foreground = new SolidColorBrush(Color.FromRgb(86, 102, 125)) });
        _mode.Items.Add("Raise to minimum");
        if (requiredDeal is null) _mode.Items.Add("Add to each target");
        _mode.SelectedIndex = 0;
        var controls = new StackPanel { Orientation = Orientation.Horizontal };
        controls.Children.Add(_resource); controls.Children.Add(_mode); controls.Children.Add(_amount);
        var preview = new Button { Content = "Preview changes" }; preview.Click += (_, _) => Preview(); controls.Children.Add(preview);
        top.Children.Add(controls); top.Children.Add(_status);
        var bottom = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        _apply.Click += Apply_Click; bottom.Children.Add(_apply); bottom.Children.Add(new Button { Content = "Close", IsCancel = true });
        DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom); root.Children.Add(_grid); Content = root;
        _resource.SelectionChanged += (_, _) => ResetReview(); _amount.TextChanged += (_, _) => ResetReview(); _mode.SelectionChanged += (_, _) => ResetReview();
        Closing += (_, e) => { if (_busy) e.Cancel = true; };
        _resource.SelectedIndex = requiredDeal is null ? (common.Length == 0 ? -1 : 0) : Array.FindIndex(common, c => c.Id == requiredDeal.ResourceId);
        if (requiredDeal is not null) _resource.IsEnabled = false;
        if (requiredDeal is not null) _amount.Text = Math.Ceiling(requiredDeal.Remaining).ToString(CultureInfo.CurrentCulture);
        _status.Text = common.Length == 0 || (requiredDeal is not null && _resource.SelectedIndex < 0)
            ? "The selected building does not already have this export-stock entry; adding new entries is not supported."
            : requiredDeal is null ? "Choose a resource and a minimum target, then preview the changes."
            : "The remaining deal quantity is prefilled. Keep the city’s deal list open; it will be checked again before writing.";
    }
    private Row[] RowsFor(int resourceId, decimal? target, IReadOnlyDictionary<ulong, decimal>? increments = null) => _snapshot.Posts.Select((p, i) =>
    {
        float current = p.Limits.Single(x => x.ResourceId == resourceId).Target;
        double stock = BuildingDiscovery.Summarize(p.Building.Resources).Where(x => x.Id == resourceId).Sum(x => x.Stored);
        string result = target is null ? "Enter a quantity above" : increments is not null ? $"Increase to {increments[p.Building.Address]:N0}" : (decimal)current >= target.Value ? "Already at or above target" : $"Increase to {target.Value:N0}";
        return new Row(_selected[i].Name, stock.ToString("N2", CultureInfo.CurrentCulture), current.ToString("N0", CultureInfo.CurrentCulture), result);
    }).ToArray();
    private void ResetReview()
    {
        _reviewed = null; _apply.IsEnabled = false;
        _grid.ItemsSource = _resource.SelectedItem is ResourceChoice resource ? RowsFor(resource.Id, null) : null;
    }
    private void Preview()
    {
        ResetReview();
        if (_resource.SelectedItem is not ResourceChoice resource || !decimal.TryParse(_amount.Text, NumberStyles.Number, CultureInfo.CurrentCulture, out decimal target))
        { _status.Text = "Choose a resource and enter a whole-unit target."; return; }
        try
        {
            bool add = _mode.SelectedIndex == 1 && _requiredDeal is null;
            var increments = add ? TradingStockLimits.CreateIncrementPlan(_snapshot, resource.Id, target).ToDictionary(x => x.BuildingAddress, x => x.Minimum) : null;
            int count = add ? increments!.Count : TradingStockLimits.CountChanges(_snapshot, resource.Id, target);
            _grid.ItemsSource = RowsFor(resource.Id, target, increments);
            _reviewed = (resource.Id, target, add); _apply.IsEnabled = count > 0;
            _status.Text = count == 0 ? "All selected buildings are already at or above this target." : $"{count} building(s) will be increased. Verify each candidate in Ostriv before applying.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or OverflowException) { _status.Text = ex.Message; }
    }
    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _reviewed is not { } reviewed) return;
        _busy = true; IsEnabled = false; _status.Text = "Backing up your saves automatically…";
        SaveBackup.SaveDirectoryBackup backup;
        try
        {
            backup = await AutomaticSaveBackup.CreateAsync(new Progress<int>(_ => _status.Text = "A save changed during backup. Retrying automatically…"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException)
        { _busy = false; IsEnabled = true; _status.Text = "Backup failed; no export target was changed. " + ex.Message; return; }
        _apply.IsEnabled = false; _resource.IsEnabled = false; _amount.IsEnabled = false; _mode.IsEnabled = false; _status.Text = "Applying export targets…";
        try
        {
            int count = await Task.Run(() => GameWriteCoordinator.Run(() => reviewed.Add
                ? TradingStockLimits.ApplyIncrementToGame(_snapshot, reviewed.Resource, reviewed.Target, CancellationToken.None)
                : TradingStockLimits.ApplyToGame(_snapshot, reviewed.Resource, reviewed.Target, CancellationToken.None, _requiredDeal)));
            IsEnabled = true;
            MessageBox.Show(this, $"{count} export target(s) passed memory readback. Close and reopen the trading-building panel in Ostriv to refresh it, then save there if correct.\n\nBackup: {backup.Directory}", "Export targets increased", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        { IsEnabled = true; MessageBox.Show(this, "The export-target action stopped. If a write was attempted, do not save; reload the backup.\n\n" + ex.Message + "\n\nBackup: " + backup.Directory, "Export targets stopped", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { _busy = false; IsEnabled = true; _invalidate(); Close(); }
    }
}
