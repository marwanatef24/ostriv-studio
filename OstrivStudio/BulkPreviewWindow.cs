using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using OstrivStudio.Core;
using Microsoft.Win32;

namespace OstrivStudio;

public sealed class BulkPreviewWindow : Window
{
    private sealed record DisplayRow(string Building, string Stored, int Records, string Assessment);
    private readonly BuildingRow[] _buildings;
    private readonly LiveResourceRow _resource;
    private readonly DiscoveryResult? _snapshot;
    private readonly Action _invalidate;
    private readonly TextBox _amount = new() { MinWidth = 150, Padding = new Thickness(8), Margin = new Thickness(0, 7, 10, 7) };
    private readonly DataGrid _grid = new() { AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, CanUserDeleteRows = false, MinHeight = 220, Margin = new Thickness(0, 12, 0, 12) };
    private readonly TextBlock _summary = new() { TextWrapping = TextWrapping.Wrap };

    public BulkPreviewWindow(BuildingRow[] buildings, LiveResourceRow resource, DiscoveryResult? snapshot, Action invalidate, decimal? savedTarget = null)
    {
        _buildings = buildings; _resource = resource; _snapshot = snapshot; _invalidate = invalidate;
        Title = "Bulk target preview"; Width = 850; Height = 560; MinWidth = 700; MinHeight = 450;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(242, 245, 249));
        Foreground = new SolidColorBrush(Color.FromRgb(23, 37, 58));
        _grid.Columns.Add(new DataGridTextColumn { Header = "Building", Binding = new Binding(nameof(DisplayRow.Building)), Width = new DataGridLength(2, DataGridLengthUnitType.Star) });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Stored", Binding = new Binding(nameof(DisplayRow.Stored)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Records", Binding = new Binding(nameof(DisplayRow.Records)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Assessment", Binding = new Binding(nameof(DisplayRow.Assessment)), Width = new DataGridLength(5, DataGridLengthUnitType.Star) });
        var root = new DockPanel { Margin = new Thickness(24) };
        var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        top.Children.Add(new TextBlock { Text = $"Preview {_resource.Name} (ID {_resource.Id})", FontFamily = new FontFamily("Georgia"), FontSize = 25 });
        top.Children.Add(new TextBlock { Text = "Targets below are previews. A separate limited fish edit is available only on the tested game build.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 10) });
        var input = new StackPanel { Orientation = Orientation.Horizontal };
        input.Children.Add(_amount);
        var preview = new Button { Content = "Calculate preview", Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 7, 0, 7) };
        preview.Click += (_, _) => Calculate(); input.Children.Add(preview); top.Children.Add(input); top.Children.Add(_summary);
        var bottom = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        if (snapshot is { Complete: true, GameSha256: FishBulkEditor.SupportedHash } && resource.Id == 27 && buildings.Length is >= 1 and <= 2 && buildings.All(b => b.Candidate.ClassName == "building_fishery"))
        {
            var edit = new Button { Content = "Add 1 fish per dock…", Padding = new Thickness(16, 7, 16, 7), Margin = new Thickness(0, 0, 10, 0) };
            edit.Click += (_, _) => EditFish(edit); bottom.Children.Add(edit);
        }
        var close = new Button { Content = "Close", IsCancel = true, Padding = new Thickness(16, 7, 16, 7) };
        bottom.Children.Add(close); DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom);
        root.Children.Add(_grid); Content = root;
        _summary.Text = $"{buildings.Length} candidate buildings selected. Enter a target amount per building.";
        if (savedTarget.HasValue)
        {
            _amount.Text = savedTarget.Value.ToString(CultureInfo.CurrentCulture);
            _amount.IsReadOnly = true;
            Calculate();
            _summary.Text = $"Saved fish rule: target {savedTarget.Value.ToString(CultureInfo.CurrentCulture)} per dock. Review the results, then use the limited fish action.";
        }
        Loaded += (_, _) => _amount.Focus();
    }

    private void EditFish(Button edit)
    {
        if (_snapshot is null) return;
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
        { MessageBox.Show(this, "Backup failed; no edit was made.\n\n" + ex.Message, "Backup failed", MessageBoxButton.OK, MessageBoxImage.Error); return; }
        string question = $"Verified backups of the selected save and {backup.Autosaves.Count} autosave(s) are in:\n{backup.Directory}\n\nSave in Ostriv, pause the game, then scan before editing. Is the selected file your current save and is the game paused? The editor cannot verify either. This adds one fish to each of {_buildings.Length} selected candidates. Storage limits and candidate identities are not automatically verified. Ostriv can autosave even while paused; after a failure, reload the backed-up selected save without saving and use the manifest to recover autosaves if needed.";
        if (MessageBox.Show(this, question, "Confirm limited fish edit", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        edit.IsEnabled = false;
        try
        {
            GameWriteCoordinator.Run(() => { FishBulkEditor.ApplyToGame(_snapshot, _buildings.Select(b => b.Candidate).ToArray()); return true; });
            MessageBox.Show(this, "The memory readback succeeded. Check the docks in Ostriv, then save in the game to keep the changes.\n\nBackups: " + backup.Directory, "Fish edit applied", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or OverflowException)
        { MessageBox.Show(this, "Edit stopped. If any write was attempted, do not save; reload the selected save backup. The game can autosave partial changes even while paused.\n\n" + ex.Message + "\n\nBackups: " + backup.Directory, "Fish edit stopped", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { _invalidate(); Close(); }
    }

    private void Calculate()
    {
        if (!float.TryParse(_amount.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out float target) || !float.IsFinite(target) || target < 0 || target > 1_000_000_000)
        {
            _grid.ItemsSource = null;
            _summary.Text = "Enter a number between 0 and 1,000,000,000.";
            return;
        }
        var result = BulkPreview.Create(_buildings.Select(b => b.Candidate), _resource.Id, target);
        _grid.ItemsSource = result.Rows.Select((row, i) => new DisplayRow(_buildings[i].Name, row.Stored.ToString("0.##", CultureInfo.CurrentCulture), row.StoredRecords, row.Result)).ToArray();
        _summary.Text = $"Target per building: {target.ToString("0.##", CultureInfo.CurrentCulture)}. {result.Rows.Count} candidates examined; no changes made.";
    }
}
