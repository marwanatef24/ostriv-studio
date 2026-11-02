using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OstrivStudio.Core;

namespace OstrivStudio;

public sealed class FarmRotationWindow : Window
{
    private sealed record CropRow(int Id, string Label) { public override string ToString() => Label; }
    private sealed record PresetChoice(string Name, CropPreset? Saved = null, CropRotationTemplate? BuiltIn = null)
    { public override string ToString() => Name; }
    private readonly FarmRotationSnapshot _farm;
    private readonly Action _invalidate;
    private readonly Dictionary<int, List<int>> _drafts;
    private readonly ComboBox _fields = new() { MinWidth = 180 };
    private readonly ComboBox _replacement = new() { MinWidth = 150, Margin = new Thickness(0, 0, 8, 0) };
    private readonly ListBox _crops = new() { MinHeight = 190, Margin = new Thickness(0, 12, 0, 12) };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
    private readonly TextBlock _pattern = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), Foreground = new SolidColorBrush(Color.FromRgb(86, 102, 125)) };
    private readonly Button _apply = new() { Content = "Apply to this field…", IsEnabled = false };
    private readonly Button _applyMatching = new() { Content = "Apply future crops to compatible fields…", IsEnabled = false };
    private bool _busy;
    private readonly string _presetPath = Path.Combine(Environment.GetEnvironmentVariable("OSTRIV_STUDIO_DATA_DIR") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OstrivStudio"), "crop-presets.json");
    private readonly ComboBox _presets = new() { MinWidth = 220, Margin = new Thickness(0, 0, 8, 0) };
    private CropPresetStore.Snapshot? _savedPresets;
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
        Background = new SolidColorBrush(Color.FromRgb(242, 245, 249)); Foreground = new SolidColorBrush(Color.FromRgb(23, 37, 58));
        _crops.Background = new SolidColorBrush(Color.FromRgb(255, 255, 255));
        _crops.Foreground = Foreground;
        _crops.BorderBrush = new SolidColorBrush(Color.FromRgb(67, 91, 97));
        _crops.FontSize = 14;
        var cropItemStyle = new Style(typeof(ListBoxItem));
        cropItemStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(14, 9, 14, 9)));
        cropItemStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        _crops.ItemContainerStyle = cropItemStyle;
        var root = new DockPanel { Margin = new Thickness(26) };
        var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        top.Children.Add(new TextBlock { Text = "Farm rotations", FontWeight = FontWeights.SemiBold, FontSize = 28 });
        top.Children.Add(new TextBlock { Text = "Choose a field, then arrange its future crops. Apply the plan to this field or to all compatible fields; every field keeps its current crop.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 16), Foreground = new SolidColorBrush(Color.FromRgb(86, 102, 125)) });
        _fields.ItemsSource = farm.Fields.Select(f => $"Field {f.Number}").ToArray();
        _fields.SelectionChanged += (_, _) => RefreshDraft(); top.Children.Add(_fields); top.Children.Add(_status);
        _replacement.ItemsSource = new[] { new CropRow(6, "Wheat"), new CropRow(7, "Buckwheat"), new CropRow(8, "Potato"), new CropRow(13, "Fallow") };
        _replacement.SelectedIndex = 0;
        var replaceControls = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        replaceControls.Children.Add(_replacement);
        var replace = new Button { Content = "Replace selected future crop" }; replace.Click += (_, _) => Replace();
        replaceControls.Children.Add(replace);
        var plough = new Button { Content = "Field ploughing…" }; plough.Click += Plough_Click; replaceControls.Children.Add(plough);
        var fertility = new Button { Content = "Restore fertility…" }; fertility.Click += Fertility_Click; replaceControls.Children.Add(fertility);
        top.Children.Add(replaceControls);
        var presetControls = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
        presetControls.Children.Add(_presets);
        var loadPreset = new Button { Content = "Load preset" }; loadPreset.Click += (_, _) => LoadPreset(); presetControls.Children.Add(loadPreset);
        var generate = new Button { Content = "Generate plan…" }; generate.Click += (_, _) => GeneratePlan(); presetControls.Children.Add(generate);
        var savePreset = new Button { Content = "Save this plan…" }; savePreset.Click += (_, _) => SavePreset(); presetControls.Children.Add(savePreset);
        var deletePreset = new Button { Content = "Delete preset" }; deletePreset.Click += (_, _) => DeletePreset(); presetControls.Children.Add(deletePreset);
        top.Children.Add(presetControls);
        top.Children.Add(_pattern);
        var bottom = new WrapPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        var earlier = new Button { Content = "Move earlier" }; earlier.Click += (_, _) => Move(-1);
        var later = new Button { Content = "Move later" }; later.Click += (_, _) => Move(1);
        var reset = new Button { Content = "Reset field" }; reset.Click += (_, _) => Reset();
        _apply.Click += Apply_Click; _applyMatching.Click += Apply_Click; var close = new Button { Content = "Close", IsCancel = true };
        bottom.Children.Add(earlier); bottom.Children.Add(later); bottom.Children.Add(reset); bottom.Children.Add(_apply); bottom.Children.Add(_applyMatching); bottom.Children.Add(close);
        DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom); root.Children.Add(_crops); Content = root;
        Closing += (_, e) => { if (_busy) e.Cancel = true; };
        _fields.SelectedIndex = 0;
        try { _savedPresets = CropPresetStore.Load(_presetPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { _status.Text = "Saved crop presets could not be loaded: " + ex.Message; }
        RefreshPresets();
        savePreset.IsEnabled = _savedPresets is not null;
        deletePreset.IsEnabled = _savedPresets is not null;
    }
    private void RefreshPresets(string? name = null)
    {
        _presets.ItemsSource = (_savedPresets?.Presets.OrderBy(p => p.Name).Select(p => new PresetChoice(p.Name, p)) ?? [])
            .Concat(CropPresetCatalog.Defaults.Select(p => new PresetChoice("Built-in · " + p.Name, BuiltIn: p))).ToArray();
        _presets.SelectedItem = _presets.Items.Cast<PresetChoice>().FirstOrDefault(p => p.Name == name);
        if (_presets.SelectedItem is null && _presets.Items.Count > 0) _presets.SelectedIndex = 0;
    }
    private void GeneratePlan()
    {
        if (_busy || Selected is not FarmFieldSnapshot field) return;
        var dialog = new CropPlanDialog(field) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Plan is not int[] plan) return;
        _ = FarmCropEditor.Changed(field, plan);
        _drafts[field.Number] = plan.ToList(); RefreshDraft();
        _status.Text += " Generated plan loaded into the draft; review before applying.";
    }
    private void LoadPreset()
    {
        if (_busy || Selected is not FarmFieldSnapshot field || _presets.SelectedItem is not PresetChoice preset) return;
        try
        {
            int[] future = preset.Saved?.FutureCrops ?? preset.BuiltIn!.FutureFor(field.Crops[0]);
            int[] desired = [field.Crops[0], .. future];
            _ = FarmCropEditor.Changed(field, desired);
            _drafts[field.Number] = desired.ToList(); RefreshDraft();
            _status.Text += " Preset loaded into the draft; review before applying.";
        }
        catch (InvalidOperationException ex) { _status.Text = "This preset is incompatible with the selected field. " + ex.Message; }
    }
    private void SavePreset()
    {
        if (_busy || _savedPresets is null || Selected is not FarmFieldSnapshot field) return;
        var dialog = new NameDialog("Save crop preset", "Name this future-crop plan", "Save preset") { Owner = this };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var preset = new CropPreset(dialog.ProfileName.Trim(), _drafts[field.Number].Skip(1).ToArray());
            _savedPresets = CropPresetStore.Save(_presetPath, _savedPresets, [.. _savedPresets.Presets, preset]);
            RefreshPresets(preset.Name); _status.Text = "Crop preset saved. It will be available when you reopen the planner.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Text.Json.JsonException)
        { _status.Text = "Preset not saved: " + ex.Message; }
    }
    private void DeletePreset()
    {
        if (_busy || _savedPresets is null || _presets.SelectedItem is not PresetChoice choice) return;
        if (choice.Saved is not CropPreset preset) { _status.Text = "Built-in suggestions stay available. Only your saved presets can be deleted."; return; }
        if (MessageBox.Show(this, $"Delete the saved preset '{preset.Name}'? Field drafts and game crops will not change.", "Delete crop preset", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        try
        {
            _savedPresets = CropPresetStore.Save(_presetPath, _savedPresets, _savedPresets.Presets.Where(p => p.Name != preset.Name).ToArray());
            RefreshPresets(); _status.Text = "Crop preset deleted.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Text.Json.JsonException)
        { _status.Text = "Preset not deleted: " + ex.Message; }
    }
    private FarmFieldSnapshot? Selected => _fields.SelectedIndex < 0 ? null : _farm.Fields[_fields.SelectedIndex];
    private void RefreshDraft(int selected = 0)
    {
        var field = Selected;
        if (field is null) { _crops.ItemsSource = null; _pattern.Text = ""; _apply.IsEnabled = false; _applyMatching.IsEnabled = false; return; }
        var draft = _drafts[field.Number];
        _crops.ItemsSource = draft.Select((id, i) => new CropRow(id, i == 0 ? $"Current crop  ·  {CropName(id)}  (locked)" : $"Future {i}  ·  {CropName(id)}")).ToArray();
        _crops.SelectedIndex = Math.Clamp(selected, 0, draft.Count - 1);
        _apply.IsEnabled = !_busy && FarmCropEditor.Changed(field, draft);
        var matching = Matching(draft);
        _applyMatching.IsEnabled = !_busy && matching.Count > 1 && FarmCropEditor.CountChanges(_farm, matching) > 0;
        _status.Text = $"Field {field.Number} · {draft.Count} crop(s) · {matching.Count} of {_farm.Fields.Count} fields compatible. Each field keeps its current crop. Field numbers may change after a new scan.";
        var patterns = CropPresetCatalog.MatchPatterns(draft);
        _pattern.Text = patterns.Count == 0 ? "No built-in rotation matches this draft." :
            $"Built-in pattern: {string.Join(", ", patterns.Select(p => p.Name))}. Ploughing and fertility are not checked.";
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
    private async void Fertility_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _busy = true; _status.Text = "Reading field nutrients…"; bool invalidated = false;
        try
        {
            var snapshot = await Task.Run(() => FarmPloughEditor.CaptureFertilityFromGame(_farm));
            if (!IsLoaded) return;
            new FarmFertilityWindow(snapshot, () => invalidated = true) { Owner = this }.ShowDialog();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception or OverflowException)
        { if (IsLoaded) MessageBox.Show(this, ex.Message, "Field nutrients unavailable", MessageBoxButton.OK, MessageBoxImage.Information); }
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
        _busy = true; IsEnabled = false; _status.Text = "Backing up your saves automatically…";
        SaveBackup.SaveDirectoryBackup backup;
        try
        {
            backup = await AutomaticSaveBackup.CreateAsync(new Progress<int>(_ => _status.Text = "A save changed during backup. Retrying automatically…"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException)
        { _busy = false; IsEnabled = true; _status.Text = "Backup failed; no crop rotation was changed. " + ex.Message; return; }
        _apply.IsEnabled = false; _applyMatching.IsEnabled = false; _fields.IsEnabled = false; _status.Text = "Applying the crop rotation…";
        try
        {
            int count = await Task.Run(() => GameWriteCoordinator.Run(() => FarmCropEditor.ApplyBatchToGame(_farm, changes, CancellationToken.None)));
            IsEnabled = true;
            MessageBox.Show(this, $"{count} crop rotation(s) passed memory readback. Check the fields in Ostriv; save there if they are correct.\n\nBackup: " + backup.Directory, "Rotation applied", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        { IsEnabled = true; MessageBox.Show(this, "The rotation action stopped. If a write was attempted, do not save; reload the backup.\n\n" + ex.Message + "\n\nBackup: " + backup.Directory, "Rotation stopped", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { _busy = false; IsEnabled = true; _invalidate(); Close(); }
    }
}
