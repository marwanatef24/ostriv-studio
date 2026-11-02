using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Microsoft.Win32;
using OstrivStudio.Core;

namespace OstrivStudio;

public sealed class CapacityEditWindow : Window
{
    private sealed record Row(string Building, string Current, string Result);
    private readonly DiscoveryResult _scan;
    private readonly BuildingRow[] _selected;
    private readonly Action _invalidate;
    private readonly TextBox _amount = new() { Width = 170, Margin = new Thickness(0, 0, 10, 0) };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 4) };
    private readonly DataGrid _grid = new() { AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, CanUserDeleteRows = false, Margin = new Thickness(0, 12, 0, 12), MinHeight = 180 };
    private readonly Button _apply = new() { Content = "Increase capacities…", IsEnabled = false, Padding = new Thickness(16, 9, 16, 9) };
    private decimal? _reviewedAmount;
    private bool _busy;

    public CapacityEditWindow(DiscoveryResult scan, BuildingRow[] selected, Action invalidate)
    {
        _scan = scan; _selected = selected; _invalidate = invalidate;
        Title = "Increase building capacity"; Width = 820; Height = 540; MinWidth = 680; MinHeight = 430;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(242, 245, 249)); Foreground = new SolidColorBrush(Color.FromRgb(23, 37, 58));
        _grid.Columns.Add(new DataGridTextColumn { Header = "Selected building", Binding = new Binding(nameof(Row.Building)), Width = new DataGridLength(2, DataGridLengthUnitType.Star) });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Observed capacity", Binding = new Binding(nameof(Row.Current)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Preview", Binding = new Binding(nameof(Row.Result)), Width = new DataGridLength(2, DataGridLengthUnitType.Star) });
        var root = new DockPanel { Margin = new Thickness(26) };
        var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        top.Children.Add(new TextBlock { Text = "Increase capacity", FontWeight = FontWeights.SemiBold, FontSize = 28 });
        top.Children.Add(new TextBlock { Text = "Set a higher capacity for selected granaries, warehouses and big hay barracks. Changes may reset when you reload: the tested hay barrack did. Saved capacity minimums reapply while the rule monitor runs.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 16), Foreground = new SolidColorBrush(Color.FromRgb(86, 102, 125)) });
        var controls = new StackPanel { Orientation = Orientation.Horizontal };
        controls.Children.Add(_amount);
        var preview = new Button { Content = "Preview changes", Padding = new Thickness(15, 9, 15, 9) };
        preview.Click += (_, _) => Preview(); controls.Children.Add(preview); top.Children.Add(controls); top.Children.Add(_status);
        var bottom = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var close = new Button { Content = "Close", IsCancel = true, Padding = new Thickness(16, 9, 16, 9) };
        _apply.Click += Apply_Click; bottom.Children.Add(_apply); bottom.Children.Add(close);
        DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom); root.Children.Add(_grid); Content = root;
        _amount.TextChanged += (_, _) => { _reviewedAmount = null; _apply.IsEnabled = false; };
        Closing += (_, e) => { if (_busy) e.Cancel = true; };
        ShowRows(null);
        _status.Text = $"{selected.Length} candidate(s) selected. Enter a target and preview it first.";
    }
    private void ShowRows(decimal? target)
    {
        _grid.ItemsSource = _selected.Select(b => new Row(b.Name,
            b.Candidate.ObservedCapacity?.ToString("N0", CultureInfo.CurrentCulture) ?? "Unavailable",
            target is null ? "Not calculated" : (decimal)b.Candidate.ObservedCapacity!.Value == target.Value ? "Already at target" : $"Increase to {target.Value:N0}")).ToArray();
    }
    private void Preview()
    {
        _reviewedAmount = null; _apply.IsEnabled = false;
        if (!decimal.TryParse(_amount.Text, NumberStyles.Number, CultureInfo.CurrentCulture, out decimal value))
        { _status.Text = "Enter a valid capacity."; return; }
        try
        {
            var plan = CapacityEditor.Prepare(_scan, _selected.Select(b => b.Candidate).ToArray(), value);
            ShowRows(value); _reviewedAmount = value; _apply.IsEnabled = plan.Entries.Count > 0;
            _status.Text = plan.Entries.Count == 0 ? "All selected capacities already match this target." :
                $"{plan.Entries.Count} candidate(s) will be increased. Review the selection and confirm in Ostriv before applying.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or OverflowException)
        { _status.Text = ex.Message; }
    }
    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _reviewedAmount is not decimal target) return;
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
        { MessageBox.Show(this, "Backup failed; no capacity was changed.\n\n" + ex.Message, "Backup failed", MessageBoxButton.OK, MessageBoxImage.Error); return; }
        string question = $"Backed up the selected save and {backup.Autosaves.Count} autosave(s):\n{backup.Directory}\n\nIs this the save loaded in Ostriv, and do these candidates match your selected buildings? The game will briefly pause while increasing their capacity to {target:N0}; avoid changing game speed during the edit. Capacity may reset when the game is reloaded. Saved minimums reapply while the rule monitor runs.\n\nIf an edit fails, it may be partial. Do not save; reload the backup and recover autosaves from its manifest if needed.";
        if (MessageBox.Show(this, question, "Apply capacity increase?", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _busy = true; _apply.IsEnabled = false; _amount.IsEnabled = false; _status.Text = "Applying capacity changes…";
        try
        {
            int count = await Task.Run(() => GameWriteCoordinator.Run(() => CapacityEditor.ApplyToGame(_scan, _selected.Select(b => b.Candidate).ToArray(), target, CancellationToken.None)));
            MessageBox.Show(this, $"{count} capacity increase(s) passed memory readback. Check them in Ostriv. Capacity may reset when the game is reloaded; the tested hay barrack did, so restart the rule monitor to reapply saved capacity minimums.\n\nBackup: {backup.Directory}", "Capacity increased", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        { MessageBox.Show(this, "The capacity action stopped. If a write was attempted, do not save; reload the backup.\n\n" + ex.Message + "\n\nBackup: " + backup.Directory, "Capacity action stopped", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { _busy = false; _invalidate(); Close(); }
    }
}
