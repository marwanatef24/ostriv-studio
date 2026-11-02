using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using OstrivStudio.Core;

namespace OstrivStudio;

public sealed class FarmFertilityWindow : Window
{
    private sealed record Row(int Number, string Field, string Nutrients);
    private readonly FarmFertilitySnapshot _snapshot;
    private readonly Action _invalidate;
    private readonly DataGrid _grid = new() { AutoGenerateColumns = false, IsReadOnly = true, SelectionMode = DataGridSelectionMode.Extended, SelectionUnit = DataGridSelectionUnit.FullRow, Margin = new Thickness(0, 16, 0, 16) };
    private bool _busy;
    public FarmFertilityWindow(FarmFertilitySnapshot snapshot, Action invalidate)
    {
        _snapshot = snapshot; _invalidate = invalidate;
        Title = "Field fertility"; Width = 700; Height = 510; MinWidth = 560; MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F2F5F9"));
        var root = new DockPanel { Margin = new Thickness(26) };
        var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        top.Children.Add(new TextBlock { Text = "Restore field fertility", FontSize = 27, FontWeight = FontWeights.SemiBold });
        top.Children.Add(new TextBlock { Text = "Select fields to restore all three nutrients to 100%. Hold Ctrl or Shift to select several fields. Check field numbers against the game before applying.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Field", Binding = new Binding(nameof(Row.Field)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Nutrients: red / green / blue", Binding = new Binding(nameof(Row.Nutrients)), Width = new DataGridLength(2, DataGridLengthUnitType.Star) });
        _grid.ItemsSource = snapshot.Fields.Select(f => new Row(f.Number, $"Field {f.Number}", $"{(int)(f.Red * 100)}% / {(int)(f.Green * 100)}% / {(int)(f.Blue * 100)}%")).ToArray();
        var bottom = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var all = new Button { Content = "Select all fields" }; all.Click += (_, _) => _grid.SelectAll();
        var restore = new Button { Content = "Restore selected…" }; restore.Click += Apply_Click;
        bottom.Children.Add(all); bottom.Children.Add(restore); bottom.Children.Add(new Button { Content = "Close", IsCancel = true });
        DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom); root.Children.Add(_grid); Content = root;
        Closing += (_, e) => { if (_busy) e.Cancel = true; };
        if (snapshot.Fields.Count > 0) _grid.SelectedIndex = 0;
    }
    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        int[] selected = _grid.SelectedItems.OfType<Row>().Select(r => r.Number).ToArray();
        if (selected.Length == 0) { MessageBox.Show(this, "Select one or more fields first."); return; }
        _busy = true; IsEnabled = false;
        SaveBackup.SaveDirectoryBackup backup;
        try
        {
            backup = await AutomaticSaveBackup.CreateAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException)
        { _busy = false; IsEnabled = true; MessageBox.Show(this, "Backup failed; no field was changed.\n\n" + ex.Message, "Backup failed", MessageBoxButton.OK, MessageBoxImage.Error); return; }
        try
        {
            int count = await Task.Run(() => GameWriteCoordinator.Run(() => FarmPloughEditor.RestoreFertilityInGame(_snapshot, selected, CancellationToken.None)));
            IsEnabled = true;
            MessageBox.Show(this, $"{count} field(s) passed memory readback. Check their nutrients in Ostriv.\n\nBackup: {backup.Directory}", "Fertility restored", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        { IsEnabled = true; MessageBox.Show(this, "The action stopped. If a write was attempted, do not save; reload the backup.\n\n" + ex.Message + "\n\nBackup: " + backup.Directory, "Fertility update stopped", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { _busy = false; IsEnabled = true; _invalidate(); Close(); }
    }
}
