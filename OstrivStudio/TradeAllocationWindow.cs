using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Microsoft.Win32;
using OstrivStudio.Core;

namespace OstrivStudio;

public sealed class TradeAllocationWindow : Window
{
    private sealed record PreviewRow(string Building, string Resource, string Current, string Minimum, string Result);
    private readonly DiscoveryResult _scan;
    private readonly TradeDeal[] _deals;
    private readonly BuildingRow[] _buildings;
    private readonly IReadOnlyDictionary<int, string> _names;
    private readonly Action _invalidate;
    private readonly ListBox _selection = new() { SelectionMode = SelectionMode.Extended, Height = 135, Background = Brushes.White, Foreground = Brushes.Black, Margin = new Thickness(0, 10, 0, 10) };
    private readonly ComboBox _mode = new() { MinWidth = 250, Margin = new Thickness(0, 0, 10, 0) };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 12) };
    private readonly DataGrid _preview = new() { AutoGenerateColumns = false, IsReadOnly = true };
    private readonly Button _apply = new() { Content = "Apply reviewed export targets…", IsEnabled = false };
    private TradingLimitsSnapshot? _reviewed;
    private TradeAllocationMode _reviewedMode;
    private bool _busy;
    public TradeAllocationWindow(DiscoveryResult scan, IReadOnlyList<TradeDeal> deals, BuildingRow[] buildings, IReadOnlyDictionary<int, string> names, Action invalidate)
    {
        _scan = scan; _deals = deals.Where(d => !d.IsImport && d.Remaining > 0).ToArray(); _buildings = buildings; _names = names; _invalidate = invalidate;
        Title = "Allocate export deals"; Width = 1060; Height = 780; MinWidth = 860; MinHeight = 650; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(242, 245, 249)); Foreground = new SolidColorBrush(Color.FromRgb(23, 37, 58));
        var root = new DockPanel { Margin = new Thickness(28) }; var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        top.Children.Add(new TextBlock { Text = "Allocate export requirements", FontSize = 28, FontWeight = FontWeights.SemiBold });
        top.Children.Add(new TextBlock { Text = "Split combined remaining exports evenly, or assign each resource to a building in turn. This raises existing export targets; higher targets stay unchanged. Keep this city's deal list open in Ostriv.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 10) });
        top.Children.Add(new TextBlock { Text = "Select matching buildings. Ctrl or Shift selects several.", FontWeight = FontWeights.SemiBold });
        _selection.ItemsSource = buildings; _selection.DisplayMemberPath = nameof(BuildingRow.Name); _selection.SelectionChanged += (_, _) => Invalidate(); top.Children.Add(_selection);
        _mode.ItemsSource = new[] { "Split exports across selected buildings", "Assign resources to buildings in turn" }; _mode.SelectedIndex = 0; _mode.SelectionChanged += (_, _) => Invalidate();
        var actions = new WrapPanel(); actions.Children.Add(_mode); var preview = new Button { Content = "Preview allocation" }; preview.Click += Preview_Click; actions.Children.Add(preview); top.Children.Add(actions); top.Children.Add(_status);
        foreach (var item in new[] { ("Building", "Building", 2d), ("Resource", "Resource", 1.5d), ("Current target", "Current", 1d), ("Minimum", "Minimum", 1d), ("Change", "Result", 1.7d) })
            _preview.Columns.Add(new DataGridTextColumn { Header = item.Item1, Binding = new Binding(item.Item2), Width = new DataGridLength(item.Item3, DataGridLengthUnitType.Star) });
        var bottom = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) }; bottom.Children.Add(new Button { Content = "Close", IsCancel = true }); bottom.Children.Add(_apply); _apply.Click += Apply_Click; DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom); root.Children.Add(_preview); Content = root;
        Closing += (_, e) => { if (_busy) e.Cancel = true; };
        _status.Text = "Choose the buildings that should supply these exports. No stock is moved by this action.";
    }
    private void Invalidate() { _reviewed = null; _apply.IsEnabled = false; _preview.ItemsSource = null; }
    private async void Preview_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var selected = _selection.SelectedItems.Cast<BuildingRow>().ToArray();
        _busy = true; _selection.IsEnabled = false; _mode.IsEnabled = false; Invalidate(); _status.Text = "Checking export entries…";
        try
        {
            var mode = (TradeAllocationMode)_mode.SelectedIndex;
            var plan = TradeAllocationPlanner.Create(_deals, selected.Select(b => b.Candidate).ToArray(), mode);
            var snapshot = await Task.Run(() => TradingStockLimits.CaptureFromGame(_scan, selected.Select(b => b.Candidate).ToArray()));
            int changes = TradingStockLimits.CountAllocationChanges(snapshot, plan);
            if (!IsLoaded) return;
            _preview.ItemsSource = plan.Select(p => { var b = snapshot.Posts.Single(b => b.Building.Address == p.BuildingAddress); float current = b.Limits.Single(l => l.ResourceId == p.ResourceId).Target;
                return new PreviewRow(selected.Single(s => s.Candidate.Address == p.BuildingAddress).Name, _names.GetValueOrDefault(p.ResourceId, $"Resource {p.ResourceId}"), current.ToString("N0"), p.Minimum.ToString("N0"), (decimal)current < p.Minimum ? "Increase" : "Already sufficient"); }).ToArray();
            _reviewed = snapshot; _reviewedMode = mode; _apply.IsEnabled = changes > 0; _status.Text = $"{changes} target(s) will increase. Existing higher targets are preserved. Deal quantities and stored stock do not change.";
        }
        catch (Exception ex) { _status.Text = "Allocation unavailable: " + ex.Message; }
        finally { _busy = false; _selection.IsEnabled = true; _mode.IsEnabled = true; }
    }
    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _reviewed is not TradingLimitsSnapshot snapshot) return;
        var select = new OpenFileDialog { Title = "Select the save loaded in Ostriv", Filter = "Ostriv save (*.save)|*.save", CheckFileExists = true, InitialDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Saved Games", "Ostriv", "save") };
        if (select.ShowDialog(this) != true) return;
        SaveBackup.SessionBackup backup;
        try { string root = Environment.GetEnvironmentVariable("OSTRIV_STUDIO_DATA_DIR") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OstrivStudio"); backup = SaveBackup.CopySessionVerified(select.FileName, Path.Combine(root, "Backups")); }
        catch (Exception ex) { _status.Text = "Backup failed; no target was changed. " + ex.Message; return; }
        if (MessageBox.Show(this, $"The selected save and {backup.Autosaves.Count} autosaves were backed up to:\n{backup.Directory}\n\nIs this the loaded save, and do the reviewed buildings match the settlement? Apply these export-target increases? The game pauses briefly. If an edit fails, reload the backup without saving.", "Apply export allocation?", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _busy = true; _apply.IsEnabled = false; _selection.IsEnabled = false; _mode.IsEnabled = false;
        try { int count = await Task.Run(() => GameWriteCoordinator.Run(() => TradingStockLimits.ApplyAllocationsToGame(snapshot, _deals, _reviewedMode, CancellationToken.None))); MessageBox.Show(this, $"{count} export target(s) passed memory readback. Reopen the trading panels in Ostriv and check the result.\n\nBackup: {backup.Directory}", "Export allocation applied"); }
        catch (Exception ex) { MessageBox.Show(this, "The allocation stopped. If any write was attempted, reload the backup without saving.\n\n" + ex.Message + "\n\nBackup: " + backup.Directory, "Export allocation stopped"); }
        finally { _busy = false; _invalidate(); Close(); }
    }
}
