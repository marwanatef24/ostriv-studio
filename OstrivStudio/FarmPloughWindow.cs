using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Microsoft.Win32;
using OstrivStudio.Core;

namespace OstrivStudio;

public sealed class FarmPloughWindow : Window
{
    private sealed record Row(int Number, string Name, string Plough);
    private readonly FarmPloughSnapshot _snapshot;
    private readonly Action _invalidate;
    private readonly DataGrid _grid = new() { AutoGenerateColumns = false, IsReadOnly = true, SelectionMode = DataGridSelectionMode.Extended, SelectionUnit = DataGridSelectionUnit.FullRow, Margin = new Thickness(0, 14, 0, 14) };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(86, 102, 125)), Margin = new Thickness(0, 8, 0, 0) };
    private bool _busy;
    public FarmPloughWindow(FarmPloughSnapshot snapshot, Action invalidate)
    {
        _snapshot = snapshot; _invalidate = invalidate;
        Title = "Field ploughing"; Width = 680; Height = 500; MinWidth = 560; MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F2F5F9")); Foreground = new SolidColorBrush(Color.FromRgb(23, 37, 58));
        var root = new DockPanel { Margin = new Thickness(26) };
        var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        top.Children.Add(new TextBlock { Text = "Field ploughing", FontSize = 27, FontWeight = FontWeights.SemiBold });
        top.Children.Add(new TextBlock { Text = "Select one or more fields, then turn ploughing on or off. Use Ctrl or Shift to select several fields.", TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(86, 102, 125)), Margin = new Thickness(0, 8, 0, 0) });
        top.Children.Add(_status);
        _grid.Columns.Add(new DataGridTextColumn { Header = "Field", Binding = new Binding(nameof(Row.Name)), Width = new DataGridLength(2, DataGridLengthUnitType.Star) });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Ploughing", Binding = new Binding(nameof(Row.Plough)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        _grid.ItemsSource = snapshot.Fields.Select(f => new Row(f.Number, $"Field {f.Number}", f.Enabled ? "On" : "Off")).ToArray();
        var bottom = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var enable = new Button { Content = "Turn on…", Tag = true }; var disable = new Button { Content = "Turn off…", Tag = false };
        enable.Click += Apply_Click; disable.Click += Apply_Click;
        bottom.Children.Add(enable); bottom.Children.Add(disable); bottom.Children.Add(new Button { Content = "Close", IsCancel = true });
        DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom); root.Children.Add(_grid); Content = root;
        Closing += (_, e) => { if (_busy) e.Cancel = true; };
        _status.Text = "Changes are applied once. Field numbers are from the current scan.";
        if (snapshot.Fields.Count > 0) _grid.SelectedIndex = 0;
    }
    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || sender is not Button { Tag: bool enabled }) return;
        var selected = _grid.SelectedItems.OfType<Row>().Select(r => r.Number).ToArray();
        if (selected.Length == 0) { _status.Text = "Select one or more fields first."; return; }
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
        { MessageBox.Show(this, "Backup failed; no field was changed.\n\n" + ex.Message, "Backup failed", MessageBoxButton.OK, MessageBoxImage.Error); return; }
        string question = $"Backed up the selected save and {backup.Autosaves.Count} autosave(s):\n{backup.Directory}\n\nIs this the loaded save, and do fields {string.Join(", ", selected.Order())} match your intended fields? Turn ploughing {(enabled ? "on" : "off")}? The game pauses briefly; avoid changing its speed during the edit.\n\nIf the action fails, do not save; reload the backup and recover autosaves using its manifest.";
        if (MessageBox.Show(this, question, "Change ploughing?", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _busy = true; _status.Text = "Updating fields…";
        try
        {
            int count = await Task.Run(() => FarmPloughEditor.ApplyToGame(_snapshot, selected, enabled, CancellationToken.None));
            MessageBox.Show(this, $"{count} field setting(s) passed memory readback. Check them in Ostriv.\n\nBackup: {backup.Directory}", "Fields updated", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        { MessageBox.Show(this, "The action stopped. If a write was attempted, do not save; reload the backup.\n\n" + ex.Message + "\n\nBackup: " + backup.Directory, "Field update stopped", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { _busy = false; _invalidate(); Close(); }
    }
}
