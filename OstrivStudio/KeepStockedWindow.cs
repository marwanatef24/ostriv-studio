using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace OstrivStudio;

public sealed class KeepStockedWindow : Window
{
    private readonly TextBox _input = new() { MinWidth = 200, Margin = new Thickness(0, 9, 0, 18) };
    public decimal Target { get; private set; }
    public KeepStockedWindow(string resource, string buildingType, string profile, decimal? existing, decimal maximum = 1_000_000_000m)
    {
        Title = "Keep stock available"; Width = 560; Height = 390; MinWidth = 500; MinHeight = 350;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F2F5F9")); Foreground = new SolidColorBrush(Color.FromRgb(23, 37, 58));
        var root = new DockPanel { Margin = new Thickness(28) };
        var bottom = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Cancel", IsCancel = true }; bottom.Children.Add(cancel);
        var save = new Button { Content = "Save stock rule", IsDefault = true }; bottom.Children.Add(save); DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom);
        var content = new StackPanel(); root.Children.Add(content);
        content.Children.Add(new TextBlock { Text = "Keep stock available", FontSize = 28, FontWeight = FontWeights.SemiBold });
        content.Children.Add(new TextBlock { Text = $"Keep at least this much {resource} in each {buildingType}.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 12) });
        content.Children.Add(new TextBlock { Text = "Minimum quantity per building", Foreground = new SolidColorBrush(Color.FromRgb(86, 102, 125)) });
        if (existing.HasValue) _input.Text = existing.Value.ToString(CultureInfo.CurrentCulture);
        else if (maximum == 100) _input.Text = "100";
        content.Children.Add(_input);
        content.Children.Add(new TextBlock { Text = $"Saved in {profile}. This applies to all matching buildings of this type, not only the selected instance. Start the rule monitor to apply it while you play.",
            TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(86, 102, 125)), LineHeight = 19 });
        save.Click += (_, _) =>
        {
            if (!decimal.TryParse(_input.Text, NumberStyles.Number, CultureInfo.CurrentCulture, out var value) || value <= 0 || value > maximum || (decimal)(float)value != value)
            { MessageBox.Show(this, $"Enter a positive, exactly representable quantity up to {maximum:N0}.", Title, MessageBoxButton.OK, MessageBoxImage.Information); return; }
            Target = value; DialogResult = true;
        };
        Content = root; Loaded += (_, _) => _input.Focus();
    }
}
