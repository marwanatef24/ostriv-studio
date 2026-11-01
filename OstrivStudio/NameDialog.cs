using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace OstrivStudio;
public sealed class NameDialog : Window
{
    private readonly TextBox _input = new() { Margin = new Thickness(0, 8, 0, 16), MinWidth = 270 };
    public string ProfileName => _input.Text;
    public NameDialog(string title = "New profile", string prompt = "Give your profile a name", string action = "Create")
    {
        Title = title; SizeToContent = SizeToContent.WidthAndHeight; WindowStartupLocation = WindowStartupLocation.CenterOwner; ResizeMode = ResizeMode.NoResize;
        Background = new SolidColorBrush(Color.FromRgb(242, 245, 249));
        Foreground = new SolidColorBrush(Color.FromRgb(23, 37, 58));
        var stack = new StackPanel { Margin = new Thickness(24) };
        stack.Children.Add(new TextBlock { Text = prompt, FontSize = 17, FontWeight = FontWeights.SemiBold }); stack.Children.Add(_input);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var create = new Button { Content = action, IsDefault = true }; create.Click += (_, _) => { DialogResult = true; };
        buttons.Children.Add(cancel); buttons.Children.Add(create); stack.Children.Add(buttons); Content = stack;
        Loaded += (_, _) => _input.Focus();
    }
}
