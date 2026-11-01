using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using OstrivStudio.Core;

namespace OstrivStudio;

public sealed class RetailAssignmentsWindow : Window
{
    private sealed record Row(string Shop, string Item, string Stored, string Reserved, string OtherRecords);

    public RetailAssignmentsWindow(RetailAssignmentSnapshot snapshot, IReadOnlyDictionary<ulong, BuildingRow> buildings,
        IReadOnlyDictionary<int, string> names)
    {
        Title = "Shop assignments"; Width = 840; Height = 590; MinWidth = 650; MinHeight = 430;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(242, 245, 249));
        Foreground = new SolidColorBrush(Color.FromRgb(23, 37, 58));
        var root = new DockPanel { Margin = new Thickness(26) };
        var heading = new StackPanel(); DockPanel.SetDock(heading, Dock.Top); root.Children.Add(heading);
        heading.Children.Add(new TextBlock { Text = "What each shop sells", FontSize = 28, FontWeight = FontWeights.SemiBold });
        heading.Children.Add(new TextBlock { Text = "These are the goods assigned in Ostriv. Quantities come from your last building scan. Other records can include goods on the way; check Ostriv for details.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) });
        int empty = snapshot.Buildings.Sum(b => b.SlotIds.Count(id => id == 0));
        heading.Children.Add(new TextBlock { Text = $"{snapshot.Buildings.Count} shop(s) selected · {empty} empty assignment slot(s).", Foreground = new SolidColorBrush(Color.FromRgb(86, 102, 125)), Margin = new Thickness(0, 0, 0, 12) });
        var footer = new StackPanel { Margin = new Thickness(0, 12, 0, 0) }; DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        footer.Children.Add(new TextBlock { Text = "Inspection only. Existing buckwheat, clothes, shoes and warm clothes can be edited from Resources; creating a missing stock record is not yet supported.", TextWrapping = TextWrapping.Wrap });
        footer.Children.Add(new Button { Content = "Close", IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) });
        var grid = new DataGrid { AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, SelectionMode = DataGridSelectionMode.Single };
        void Column(string headingText, string property, double width) => grid.Columns.Add(new DataGridTextColumn { Header = headingText, Binding = new Binding(property), Width = new DataGridLength(width, DataGridLengthUnitType.Star) });
        Column("Shop", nameof(Row.Shop), 1.4); Column("Assigned item", nameof(Row.Item), 1.7); Column("Stored", nameof(Row.Stored), 1); Column("Reserved", nameof(Row.Reserved), 1); Column("Other records", nameof(Row.OtherRecords), 1.2);
        string Amount(double value) => value.ToString("N2", CultureInfo.CurrentCulture);
        grid.ItemsSource = snapshot.Buildings.SelectMany(shop => shop.AssignedResourceIds.Select(id =>
        {
            var records = shop.Building.Resources.Where(r => r.Id == id).ToArray();
            return new Row(buildings.GetValueOrDefault(shop.Building.Address)?.Name ?? "Shop",
                names.GetValueOrDefault(id, $"Unknown resource {id}"),
                Amount(records.Where(r => r.Status == 0).Sum(r => (double)r.Amount)),
                Amount(records.Where(r => r.Status == 1).Sum(r => (double)r.Amount)),
                records.Any(r => r.Status is not (0 or 1)) ? "Present" : "None");
        })).ToArray();
        root.Children.Add(grid); Content = root;
    }
}
