using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace OstrivStudio;
public sealed class AmountDialog : Window
{
    private readonly TextBox _input = new() { Margin = new Thickness(0, 10, 0, 16), MinWidth = 270 };
    public decimal Amount { get; private set; }
    public AmountDialog(int count)
    {
        Title = "Set profile targets"; SizeToContent = SizeToContent.WidthAndHeight; WindowStartupLocation = WindowStartupLocation.CenterOwner; ResizeMode = ResizeMode.NoResize;
        Background = new SolidColorBrush(Color.FromRgb(25, 39, 31)); Foreground = new SolidColorBrush(Color.FromRgb(232, 237, 229));
        var stack = new StackPanel { Margin = new Thickness(24) };
        stack.Children.Add(new TextBlock { Text = $"Target amount for {count} selected resources", FontSize = 16, FontWeight = FontWeights.SemiBold });
        stack.Children.Add(new TextBlock { Text = "This changes the profile only.", Margin = new Thickness(0, 6, 0, 0) }); stack.Children.Add(_input);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var apply = new Button { Content = "Set targets", IsDefault = true };
        apply.Click += (_, _) =>
        {
            if (!decimal.TryParse(_input.Text, NumberStyles.Number, CultureInfo.CurrentCulture, out var value) || value < 0 || value > 1000000000m)
            { MessageBox.Show(this, "Enter a valid amount between 0 and 1,000,000,000.", "Invalid amount", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            Amount = value; DialogResult = true;
        };
        buttons.Children.Add(cancel); buttons.Children.Add(apply); stack.Children.Add(buttons); Content = stack; Loaded += (_, _) => _input.Focus();
    }
}
