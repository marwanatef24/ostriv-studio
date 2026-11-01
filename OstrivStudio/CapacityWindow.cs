using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using OstrivStudio.Core;

namespace OstrivStudio;

public sealed class CapacityWindow : Window
{
    private sealed class CapacityRow
    {
        public int Id { get; init; }
        public string Building { get; init; } = "";
        public string Amount { get; set; } = "";
        public bool Enabled { get; set; }
    }
    private sealed record BuildingOption(int Id, string Name)
    {
        public override string ToString() => Name.Replace("building_", "").Replace('_', ' ');
    }
    private readonly ObservableCollection<CapacityRow> _rows = [];
    private readonly DataGrid _grid = new() { AutoGenerateColumns = false, SelectionMode = DataGridSelectionMode.Extended };
    private readonly ComboBox _building = new() { Width = 220, Margin = new Thickness(0, 0, 8, 0) };
    public List<CapacityRule> Result { get; private set; } = [];
    public CapacityWindow(IReadOnlyList<CapacityRule> rules)
    {
        Title = "Capacity rules"; Width = 780; Height = 560; MinWidth = 650; MinHeight = 440;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#142833"));
        Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F0EEE6"));
        foreach (var r in rules) _rows.Add(new() { Id = r.LegacyBuildingId, Building = r.BuildingType, Amount = r.Capacity.ToString(CultureInfo.CurrentCulture), Enabled = r.Enabled });
        _building.ItemsSource = LegacyBuildingCatalog.All.Select(x => new BuildingOption(x.Key, x.Value)).ToArray();
        _building.SelectedIndex = 0;
        _grid.ItemsSource = _rows;
        _grid.Columns.Add(new DataGridCheckBoxColumn { Header = "Enabled", Binding = new Binding(nameof(CapacityRow.Enabled)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }, Width = 90 });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Building type", Binding = new Binding(nameof(CapacityRow.Building)), IsReadOnly = true, Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Capacity", Binding = new Binding(nameof(CapacityRow.Amount)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }, Width = 160 });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Original ID", Binding = new Binding(nameof(CapacityRow.Id)), IsReadOnly = true, Width = 90 });
        var root = new DockPanel { Margin = new Thickness(25) };
        var top = new StackPanel();
        top.Children.Add(new TextBlock { Text = "Capacity rules", FontFamily = new FontFamily("Georgia"), FontSize = 28 });
        top.Children.Add(new TextBlock { Text = "Saved with this profile. When you start the rule monitor, enabled rules can keep granary, warehouse and big hay barrack capacity at least at this value. Other types are shown as unsupported.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 15) });
        var tools = new WrapPanel { Margin = new Thickness(0, 0, 0, 15) };
        tools.Children.Add(_building);
        var add = new Button { Content = "Add building" }; add.Click += Add_Click; tools.Children.Add(add);
        var remove = new Button { Content = "Remove selected" }; remove.Click += Remove_Click; tools.Children.Add(remove);
        top.Children.Add(tools); DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 15, 0, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true }; actions.Children.Add(cancel);
        var save = new Button { Content = "Save rules", IsDefault = true, Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#DFBC7D")), Foreground = Brushes.Black };
        save.Click += Save_Click; actions.Children.Add(save); DockPanel.SetDock(actions, Dock.Bottom); root.Children.Add(actions);
        root.Children.Add(_grid); Content = root;
    }
    private void Add_Click(object sender, RoutedEventArgs e)
    {
        if (_building.SelectedItem is not BuildingOption option) return;
        if (_rows.Any(x => x.Id == option.Id)) { MessageBox.Show(this, "This building already has a capacity rule.", Title); return; }
        var row = new CapacityRow { Id = option.Id, Building = option.Name, Amount = "0" };
        _rows.Add(row); _grid.SelectedItem = row; _grid.ScrollIntoView(row);
    }
    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in _grid.SelectedItems.OfType<CapacityRow>().ToArray()) _rows.Remove(row);
    }
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _grid.CommitEdit(DataGridEditingUnit.Cell, true); _grid.CommitEdit(DataGridEditingUnit.Row, true);
        var result = new List<CapacityRule>();
        foreach (var row in _rows)
        {
            if (!decimal.TryParse(row.Amount, NumberStyles.Number, CultureInfo.CurrentCulture, out var amount) || amount is < 0 or > 1000000000m)
            { MessageBox.Show(this, $"Enter a valid capacity between 0 and 1,000,000,000 for {row.Building}.", Title, MessageBoxButton.OK, MessageBoxImage.Information); return; }
            result.Add(new() { LegacyBuildingId = row.Id, BuildingType = row.Building, Capacity = amount, Enabled = row.Enabled });
        }
        Result = result; DialogResult = true;
    }
}
