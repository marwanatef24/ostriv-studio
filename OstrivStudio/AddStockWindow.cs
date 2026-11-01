using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Microsoft.Win32;
using OstrivStudio.Core;

namespace OstrivStudio;

public sealed class AddStockWindow : Window
{
    private sealed record Row(string Building, string Current, string Added, string Result);
    private readonly DiscoveryResult _scan;
    private readonly BuildingRow[] _buildings;
    private readonly LiveResourceRow _resource;
    private readonly Action _invalidate;
    private readonly TextBox _amount = new() { Width = 180, Margin = new Thickness(0, 0, 10, 0) };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 8), Foreground = new SolidColorBrush(Color.FromRgb(86, 102, 125)) };
    private readonly DataGrid _grid = new() { AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, Margin = new Thickness(0, 14, 0, 14) };
    private readonly Button _apply = new() { Content = "Add to selected buildings…", IsEnabled = false, Padding = new Thickness(18, 11, 18, 11) };
    private decimal? _reviewedAmount;
    private bool _busy;

    public AddStockWindow(DiscoveryResult scan, BuildingRow[] buildings, LiveResourceRow resource, Action invalidate)
    {
        _scan = scan; _buildings = buildings; _resource = resource; _invalidate = invalidate;
        Title = "Add stock once"; Width = 850; Height = 590; MinWidth = 700; MinHeight = 470;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F2F5F9")); Foreground = new SolidColorBrush(Color.FromRgb(23, 37, 58));
        var root = new DockPanel { Margin = new Thickness(28) };
        var header = new StackPanel(); DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        header.Children.Add(new TextBlock { Text = "Add stock once", FontSize = 28, FontWeight = FontWeights.SemiBold });
        header.Children.Add(new TextBlock { Text = $"Add the same amount of {resource.Name} to each selected building. This does not create a saved rule.",
            TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(86, 102, 125)), Margin = new Thickness(0, 8, 0, 18) });
        var controls = new StackPanel { Orientation = Orientation.Horizontal };
        controls.Children.Add(_amount);
        var preview = new Button { Content = "Preview addition", Padding = new Thickness(16, 9, 16, 9) };
        preview.Click += (_, _) => Preview(); controls.Children.Add(preview); header.Children.Add(controls); header.Children.Add(_status);
        _grid.Columns.Add(Column("Selected building", nameof(Row.Building), 2));
        _grid.Columns.Add(Column("Stored now", nameof(Row.Current), 1));
        _grid.Columns.Add(Column("Add", nameof(Row.Added), 1));
        _grid.Columns.Add(Column("After", nameof(Row.Result), 1));
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var close = new Button { Content = "Cancel", IsCancel = true }; footer.Children.Add(close);
        _apply.Click += Apply_Click; footer.Children.Add(_apply); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer); root.Children.Add(_grid); Content = root;
        _amount.TextChanged += (_, _) => { _reviewedAmount = null; _apply.IsEnabled = false; };
        Closing += (_, e) => { if (_busy) e.Cancel = true; };
        _status.Text = $"{buildings.Length} building(s) selected. Enter a whole-unit amount to add.";
        Loaded += (_, _) => _amount.Focus();
    }
    private static DataGridTextColumn Column(string title, string property, double width) =>
        new() { Header = title, Binding = new Binding(property), Width = new DataGridLength(width, DataGridLengthUnitType.Star) };
    private void Preview()
    {
        _reviewedAmount = null; _apply.IsEnabled = false;
        if (!decimal.TryParse(_amount.Text, NumberStyles.Number, CultureInfo.CurrentCulture, out var value)) { _status.Text = "Enter a whole-unit amount to add."; return; }
        try
        {
            var plan = ManualStockEditor.Prepare(_scan, _buildings.Select(b => b.Candidate).ToArray(), _resource.Id, value);
            _grid.ItemsSource = plan.Entries.Select((entry, i) =>
            {
                decimal current = entry.Building.Resources.Where(r => r.Id == _resource.Id && r.Status == 0).Sum(r => (decimal)r.Amount);
                return new Row(_buildings[i].Name, current.ToString("0.##", CultureInfo.CurrentCulture), value.ToString("N0"), (current + value).ToString("0.##", CultureInfo.CurrentCulture));
            }).ToArray();
            _reviewedAmount = value; _apply.IsEnabled = plan.Entries.Count > 0;
            _status.Text = "Review the selected buildings and quantities. Existing stored records only; the game will pause briefly for the edit.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or OverflowException)
        { _grid.ItemsSource = null; _status.Text = ex.Message; }
    }
    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _reviewedAmount is not decimal amount) return;
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
        { MessageBox.Show(this, "Backup failed; no stock was changed.\n\n" + ex.Message, "Backup failed", MessageBoxButton.OK, MessageBoxImage.Error); return; }
        string prompt = $"Backed up the selected save and {backup.Autosaves.Count} autosave(s):\n{backup.Directory}\n\nIs this the loaded save, and do the selected candidates match your buildings? Add {amount:N0} {_resource.Name} to each of {_buildings.Length} selected building(s)? The game pauses briefly; avoid changing its speed during the edit.\n\nA failure may be partial. Do not save if uncertain; reload the backup and recover autosaves from its manifest.";
        if (MessageBox.Show(this, prompt, "Add stock once?", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _busy = true; _apply.IsEnabled = false; _amount.IsEnabled = false; _status.Text = "Adding stock…";
        try
        {
            int count = await Task.Run(() => ManualStockEditor.ApplyToGame(_scan, _buildings.Select(b => b.Candidate).ToArray(), _resource.Id, amount, CancellationToken.None));
            MessageBox.Show(this, $"Added stock to {count} building(s); memory readback passed. Check the result in Ostriv.\n\nBackup: {backup.Directory}", "Stock added", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        { MessageBox.Show(this, "The action stopped. If a write was attempted, do not save; reload the backup.\n\n" + ex.Message + "\n\nBackup: " + backup.Directory, "Addition stopped", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { _busy = false; _invalidate(); Close(); }
    }
}
