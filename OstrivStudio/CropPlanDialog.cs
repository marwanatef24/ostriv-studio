using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OstrivStudio.Core;

namespace OstrivStudio;

public sealed class CropPlanDialog : Window
{
    private readonly FarmFieldSnapshot _field;
    private readonly Dictionary<int, CheckBox> _crops = [];
    private readonly CheckBox _plough = new() { Content = "Assume ploughing is enabled", Margin = new Thickness(0, 14, 0, 0) };
    private readonly TextBlock _result = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) };
    private readonly Button _use = new() { Content = "Use this draft", IsEnabled = false, MinWidth = 130 };
    public int[]? Plan { get; private set; }
    private static string CropName(int id) => id switch { 6 => "Wheat", 7 => "Buckwheat", 8 => "Potato", 9 => "Hemp", 10 => "Sunflower", 13 => "Fallow", 83 => "Flax", 87 => "Barley", 179 => "Winter wheat", 181 => "Winter barley", _ => $"Crop {id}" };

    public CropPlanDialog(FarmFieldSnapshot field)
    {
        _field = field; Title = "Generate a crop plan"; Width = 680; Height = 600; MinWidth = 560; MinHeight = 540;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; Background = new SolidColorBrush(Color.FromRgb(242, 245, 249)); Foreground = new SolidColorBrush(Color.FromRgb(23, 37, 58));
        var root = new DockPanel { Margin = new Thickness(26) };
        var bottom = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true }; bottom.Children.Add(cancel); bottom.Children.Add(_use);
        DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom);
        var body = new StackPanel(); root.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        body.Children.Add(new TextBlock { Text = "Generate a crop plan", FontSize = 26, FontWeight = FontWeights.SemiBold });
        body.Children.Add(new TextBlock { Text = $"Field {field.Number} · {field.Crops.Count} seasons · current crop: {CropName(field.Crops[0])}", Margin = new Thickness(0, 8, 0, 18), Foreground = new SolidColorBrush(Color.FromRgb(86, 102, 125)) });
        body.Children.Add(new TextBlock { Text = "Include these crops", FontWeight = FontWeights.SemiBold, FontSize = 16 });
        var options = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        foreach (int id in new[] { 6, 7, 8, 9, 10, 83, 87, 179, 181 })
        {
            bool available = id is 6 or 7 or 8 || field.Crops.Contains(id);
            var check = new CheckBox { Content = CropName(id), IsChecked = id == 6, IsEnabled = available, Margin = new Thickness(0, 0, 20, 10),
                ToolTip = available ? null : "This crop can only be used when it is already in this field's rotation." };
            _crops.Add(id, check); options.Children.Add(check);
        }
        body.Children.Add(options);
        body.Children.Add(new TextBlock { Text = "Other crops are available when already present in this field's rotation.", TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(86, 102, 125)) });
        body.Children.Add(_plough);
        body.Children.Add(new TextBlock { Text = "The model starts with full fertility and uses assumptions from the original editor. It does not read your field's actual fertility or guarantee a yield. The current crop and rotation length stay unchanged.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 16, 0, 0), Foreground = new SolidColorBrush(Color.FromRgb(86, 102, 125)) });
        var generate = new Button { Content = "Find a plan", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 18, 0, 0) }; generate.Click += (_, _) => Generate(); body.Children.Add(generate); body.Children.Add(_result);
        foreach (var option in _crops.Values.Append(_plough)) { option.Checked += (_, _) => Clear(); option.Unchecked += (_, _) => Clear(); }
        _use.Click += (_, _) => { if (Plan is not null) DialogResult = true; };
        Content = root;
    }
    private void Clear() { Plan = null; _use.IsEnabled = false; _result.Text = ""; }
    private void Generate()
    {
        Clear();
        int[] crops = _crops.Where(x => x.Value.IsChecked == true).Select(x => x.Key).ToArray();
        if (crops.Length == 0) { _result.Text = "Choose at least one crop."; return; }
        Plan = CropPlanGenerator.Suggest(_field, crops, _plough.IsChecked == true);
        _result.Text = Plan is null ? "No matching rotation was found for this field length. Try fewer crops or a different ploughing assumption." :
            "Suggested cycle: " + string.Join(" → ", Plan.Select(CropName)) + ". This is a draft; review it before applying.";
        _use.IsEnabled = Plan is not null;
    }
}
