using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using OstrivStudio.Core;

namespace OstrivStudio;

public sealed class FarmRotationWindow : Window
{
    private sealed record CropRow(int Id, string Label) { public override string ToString() => Label; }
    private readonly FarmRotationSnapshot _farm;
    private readonly Action _invalidate;
    private readonly Dictionary<int, List<int>> _drafts;
    private readonly ComboBox _fields = new() { MinWidth = 180 };
    private readonly ComboBox _replacement = new() { MinWidth = 150, Margin = new Thickness(0, 0, 8, 0) };
    private readonly ListBox _crops = new() { MinHeight = 190, Margin = new Thickness(0, 12, 0, 12) };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
    private readonly Button _apply = new() { Content = "Apply to this field…", IsEnabled = false };
    private readonly Button _applyMatching = new() { Content = "Apply future crops to compatible fields…", IsEnabled = false };
    private bool _busy;
    private static string CropName(int id) => id switch
    {
        6 => "Wheat", 7 => "Buckwheat", 8 => "Potato", 9 => "Hemp", 10 => "Sunflower", 13 => "Fallow",
        83 => "Flax", 87 => "Barley", 179 => "Winter wheat", 181 => "Winter barley", _ => $"Unknown crop {id}"
    };
    public FarmRotationWindow(FarmRotationSnapshot farm, Action invalidate)
    {
        _farm = farm; _invalidate = invalidate;
        _drafts = farm.Fields.ToDictionary(f => f.Number, f => f.Crops.ToList());
        Title = "Farm rotations"; Width = 980; Height = 700; MinWidth = 760; MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(20, 40, 51)); Foreground = new SolidColorBrush(Color.FromRgb(240, 238, 230));
        _crops.Background = new SolidColorBrush(Color.FromRgb(28, 49, 59));
        _crops.Foreground = Foreground;
        _crops.BorderBrush = new SolidColorBrush(Color.FromRgb(67, 91, 97));
        _crops.FontSize = 14;
        var cropItemStyle = new Style(typeof(ListBoxItem));
        cropItemStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(14, 9, 14, 9)));
        cropItemStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        _crops.ItemContainerStyle = cropItemStyle;
        var root = new DockPanel { Margin = new Thickness(26) };
        var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        top.Children.Add(new TextBlock { Text = "Farm rotations", FontFamily = new FontFamily("Georgia"), FontSize = 28 });
        top.Children.Add(new TextBlock { Text = "Choose a field, then arrange its future crops. Apply the plan to this field or to all compatible fields; every field keeps its current crop.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 16), Foreground = Brushes.LightGray });
        _fields.ItemsSource = farm.Fields.Select(f => $"Field {f.Number}").ToArray();
        _fields.SelectionChanged += (_, _) => RefreshDraft(); top.Children.Add(_fields); top.Children.Add(_status);
        _replacement.ItemsSource = new[] { new CropRow(6, "Wheat"), new CropRow(7, "Buckwheat"), new CropRow(8, "Potato"), new CropRow(13, "Fallow") };
        _replacement.SelectedIndex = 0;
        var replaceControls = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        replaceControls.Children.Add(_replacement);
        var replace = new Button { Content = "Replace selected future crop" }; replace.Click += (_, _) => Replace();
        replaceControls.Children.Add(replace);
        var plough = new Button { Content = "Field ploughing…" }; plough.Click += Plough_Click; replaceControls.Children.Add(plough);
        top.Children.Add(replaceControls);
        var bottom = new WrapPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        var earlier = new Button { Content = "Move earlier" }; earlier.Click += (_, _) => Move(-1);
        var later = new Button { Content = "Move later" }; later.Click += (_, _) => Move(1);
        var reset = new Button { Content = "Reset field" }; reset.Click += (_, _) => Reset();
        _apply.Click += Apply_Click; _applyMatching.Click += Apply_Click; var close = new Button { Content = "Close", IsCancel = true };
        bottom.Children.Add(earlier); bottom.Children.Add(later); bottom.Children.Add(reset); bottom.Children.Add(_apply); bottom.Children.Add(_applyMatching); bottom.Children.Add(close);
        DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom); root.Children.Add(_crops); Content = root;
        Closing += (_, e) => { if (_busy) e.Cancel = true; };
        _fields.SelectedIndex = 0;
    }
    private FarmFieldSnapshot? Selected => _fields.SelectedIndex < 0 ? null : _farm.Fields[_fields.SelectedIndex];
    private void RefreshDraft(int selected = 0)
    {
        var field = Selected;
        if (field is null) { _crops.ItemsSource = null; _apply.IsEnabled = false; _applyMatching.IsEnabled = false; return; }
        var draft = _drafts[field.Number];
        _crops.ItemsSource = draft.Select((id, i) => new CropRow(id, i == 0 ? $"Current crop  ·  {CropName(id)}  (locked)" : $"Future {i}  ·  {CropName(id)}")).ToArray();
        _crops.SelectedIndex = Math.Clamp(selected, 0, draft.Count - 1);
        _apply.IsEnabled = !_busy && FarmCropEditor.Changed(field, draft);
        var matching = Matching(draft);
        _applyMatching.IsEnabled = !_busy && matching.Count > 1 && FarmCropEditor.CountChanges(_farm, matching) > 0;
        _status.Text = $"Field {field.Number} · {draft.Count} crop(s) · {matching.Count} of {_farm.Fields.Count} fields compatible. Each field keeps its current crop. Field numbers may change after a new scan.";
    }
    private Dictionary<int, IReadOnlyList<int>> Matching(IReadOnlyList<int> order)
    {
        return FarmCropEditor.CompatibleFutureRotations(_farm, order);
    }
    private void Replace()
    {
        if (_busy || Selected is not FarmFieldSnapshot field || _replacement.SelectedItem is not CropRow crop) return;
        var draft = _drafts[field.Number]; int selected = _crops.SelectedIndex;
        if (selected < 1 || selected >= draft.Count) return;
        draft[selected] = crop.Id; RefreshDraft(selected);
    }
    private void Move(int direction)
    {
        if (_busy || Selected is not FarmFieldSnapshot field) return;
        var draft = _drafts[field.Number]; int from = _crops.SelectedIndex, to = from + direction;
        if (from < 1 || to < 1 || to >= draft.Count) return;
        (draft[from], draft[to]) = (draft[to], draft[from]); RefreshDraft(to);
    }
    private void Reset()
    {
        if (_busy || Selected is not FarmFieldSnapshot field) return;
        _drafts[field.Number] = field.Crops.ToList(); RefreshDraft();
    }
    private async void Plough_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _busy = true; _status.Text = "Reading field settings…"; bool invalidated = false;
        try
        {
            var snapshot = await Task.Run(() => FarmPloughEditor.CaptureFromGame(_farm));
            if (!IsLoaded) return;
            new FarmPloughWindow(snapshot, () => invalidated = true) { Owner = this }.ShowDialog();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception or OverflowException)
        { if (IsLoaded) MessageBox.Show(this, ex.Message, "Field settings unavailable", MessageBoxButton.OK, MessageBoxImage.Information); }
        finally
        {
            _busy = false;
            if (invalidated) { _invalidate(); Close(); }
            else if (IsLoaded) RefreshDraft();
        }
    }
    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || Selected is not FarmFieldSnapshot field) return;
        int[] desired = _drafts[field.Number].ToArray();
        var changes = ReferenceEquals(sender, _applyMatching) ? Matching(desired) : new Dictionary<int, IReadOnlyList<int>> { [field.Number] = desired };
        if (FarmCropEditor.CountChanges(_farm, changes) == 0) return;
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
        { MessageBox.Show(this, "Backup failed; no crop rotation was changed.\n\n" + ex.Message, "Backup failed", MessageBoxButton.OK, MessageBoxImage.Error); return; }
        string order = string.Join(" → ", desired.Skip(1).Select(CropName));
        string fieldNames = string.Join(", ", changes.Keys.Order().Select(n => $"{n}"));
        string question = $"Backed up the selected save and {backup.Autosaves.Count} autosave(s):\n{backup.Directory}\n\nIs this the loaded save and do fields {fieldNames} match the intended fields? The game will briefly pause while applying these future crops:\n\n{order}\n\nEach field keeps its current crop. Other unsaved field drafts are not applied. Avoid changing game speed during the edit. If it fails, do not save; reload the backup and recover autosaves from the manifest if needed.";
        if (MessageBox.Show(this, question, "Apply crop rotation?", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _busy = true; _apply.IsEnabled = false; _applyMatching.IsEnabled = false; _fields.IsEnabled = false; _status.Text = "Applying the crop rotation…";
        try
        {
            int count = await Task.Run(() => FarmCropEditor.ApplyBatchToGame(_farm, changes, CancellationToken.None));
            MessageBox.Show(this, $"{count} crop rotation(s) passed memory readback. Check the fields in Ostriv; save there if they are correct.\n\nBackup: " + backup.Directory, "Rotation applied", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        { MessageBox.Show(this, "The rotation action stopped. If a write was attempted, do not save; reload the backup.\n\n" + ex.Message + "\n\nBackup: " + backup.Directory, "Rotation stopped", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { _busy = false; _invalidate(); Close(); }
    }
}
