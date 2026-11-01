using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using OstrivStudio.Core;

namespace OstrivStudio;

public sealed class RulePreviewWindow : Window
{
    private sealed record DisplayRow(string Resource, string Building, string Current, string Target, string Increase, string Assessment);
    public RulePreviewWindow(string profile, MaintenancePreviewResult preview, IReadOnlyDictionary<int, string> resources, IReadOnlyList<BuildingRow> buildings)
    {
        Title = "Preview saved rules"; Width = 1000; Height = 640; MinWidth = 760; MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F2F5F9"));
        Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#17253A"));
        var root = new DockPanel { Margin = new Thickness(25) };
        var heading = new StackPanel();
        heading.Children.Add(new TextBlock { Text = "Preview saved rules", FontFamily = new FontFamily("Georgia"), FontSize = 29 });
        heading.Children.Add(new TextBlock { Text = $"Profile: {profile} · {preview.Rows.Count} candidate matches", Margin = new Thickness(0, 6, 0, 6) });
        heading.Children.Add(new TextBlock { Text = "Read-only estimate. Candidate identity, allowed resources and capacity are not verified. Supported Across rules share the combined shortfall; no changes are made.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
        if (preview.Notices.Count > 0) heading.Children.Add(new TextBlock { Text = string.Join("  ", preview.Notices.Take(5)) + (preview.Notices.Count > 5 ? $"  +{preview.Notices.Count - 5} more" : ""), TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(139, 77, 18)), Margin = new Thickness(0, 0, 0, 12) });
        DockPanel.SetDock(heading, Dock.Top); root.Children.Add(heading);
        var close = new Button { Content = "Close", IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        DockPanel.SetDock(close, Dock.Bottom); root.Children.Add(close);
        var grid = new DataGrid { AutoGenerateColumns = false, IsReadOnly = true };
        grid.Columns.Add(new DataGridTextColumn { Header = "Resource", Binding = new Binding(nameof(DisplayRow.Resource)), Width = new DataGridLength(1.3, DataGridLengthUnitType.Star) });
        grid.Columns.Add(new DataGridTextColumn { Header = "Candidate", Binding = new Binding(nameof(DisplayRow.Building)), Width = new DataGridLength(2.0, DataGridLengthUnitType.Star) });
        grid.Columns.Add(new DataGridTextColumn { Header = "Stored", Binding = new Binding(nameof(DisplayRow.Current)), Width = new DataGridLength(.8, DataGridLengthUnitType.Star) });
        grid.Columns.Add(new DataGridTextColumn { Header = "Target", Binding = new Binding(nameof(DisplayRow.Target)), Width = new DataGridLength(.8, DataGridLengthUnitType.Star) });
        grid.Columns.Add(new DataGridTextColumn { Header = "Increase", Binding = new Binding(nameof(DisplayRow.Increase)), Width = new DataGridLength(.8, DataGridLengthUnitType.Star) });
        grid.Columns.Add(new DataGridTextColumn { Header = "Assessment", Binding = new Binding(nameof(DisplayRow.Assessment)), Width = new DataGridLength(2.4, DataGridLengthUnitType.Star) });
        var names = buildings.ToDictionary(b => b.Candidate.Address, b => b.Name);
        static string Amount(decimal d) => d.ToString("0.##", CultureInfo.CurrentCulture);
        grid.ItemsSource = preview.Rows.Select(r => new DisplayRow(resources.GetValueOrDefault(r.ResourceId, $"Unknown resource {r.ResourceId}"),
            names.GetValueOrDefault(r.Address, r.BuildingType), Amount(r.Current), Amount(r.Target), Amount(r.Increase), r.Assessment)).ToArray();
        root.Children.Add(grid); Content = root;
    }
}
