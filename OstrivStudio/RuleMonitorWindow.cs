using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Microsoft.Win32;
using OstrivStudio.Core;

namespace OstrivStudio;

public sealed class RuleMonitorWindow : Window
{
    private sealed record DisplayRow(int Number, string Resource, string Building, string Target, string Candidates, string Stored, string State, string Detail);
    private readonly string _settingsPath, _profile, _gamePath;
    private readonly IReadOnlyDictionary<int, string> _resources;
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 14) };
    private readonly TextBlock _description = new() { TextWrapping = TextWrapping.Wrap };
    private readonly DataGrid _grid = new() { AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, CanUserDeleteRows = false };
    private readonly Button _start = new() { Content = "Check rules again", MinWidth = 140, Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button _stop = new() { Content = "Stop", MinWidth = 90, IsEnabled = false };
    private readonly Button _runRules = new() { Content = "Start automation…", MinWidth = 180, Margin = new Thickness(8, 0, 0, 0), IsEnabled = false, Background = new SolidColorBrush(Color.FromRgb(34, 109, 83)), Foreground = Brushes.White };
    private CancellationTokenSource? _cancellation;
    private TaskCompletionSource<bool>? _stopped;
    private bool _runningRules;
    private bool _startAfterFirstScan = true;
    private bool _closed, _closeRequested;
    private RuleMonitorReport? _reviewedReport;
    private byte[]? _reviewedSettingsHash;
    public bool MayHaveWritten { get; private set; }

    public RuleMonitorWindow(string settingsPath, string profile, string gamePath, IReadOnlyDictionary<int, string> resources)
    {
        _settingsPath = settingsPath; _profile = profile; _gamePath = gamePath; _resources = resources;
        Title = "Start saved rules"; Width = 1180; Height = 690; MinWidth = 850; MinHeight = 500;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F2F5F9"));
        Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#17253A"));
        var root = new DockPanel { Margin = new Thickness(25) };
        var header = new StackPanel();
        header.Children.Add(new TextBlock { Text = "Start saved rules", FontFamily = new FontFamily("Georgia"), FontSize = 29 });
        header.Children.Add(new TextBlock { Text = $"Profile: {_profile}", Margin = new Thickness(0, 5, 0, 5), Foreground = new SolidColorBrush(Color.FromRgb(139, 77, 18)) });
        _description.Text = "CHECKING ONLY · No quantities change yet. After the scan, choose your loaded save for backup and confirm to start automation. Leave this window open while you play.";
        header.Children.Add(_description);
        header.Children.Add(_status); DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        var footer = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
        footer.Children.Add(_start); footer.Children.Add(_stop); footer.Children.Add(_runRules); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        _grid.Columns.Add(Column("#", nameof(DisplayRow.Number), .3));
        _grid.Columns.Add(Column("Resource", nameof(DisplayRow.Resource), 1.2));
        _grid.Columns.Add(Column("Building", nameof(DisplayRow.Building), 1.5));
        _grid.Columns.Add(Column("Target", nameof(DisplayRow.Target), .7));
        _grid.Columns.Add(Column("Found", nameof(DisplayRow.Candidates), .55));
        _grid.Columns.Add(Column("Stored total", nameof(DisplayRow.Stored), .85));
        _grid.Columns.Add(Column("State", nameof(DisplayRow.State), 1.4));
        _grid.Columns.Add(Column("Details", nameof(DisplayRow.Detail), 2.8));
        root.Children.Add(_grid); Content = root;
        _start.Click += async (_, _) => await StartAsync();
        _stop.Click += (_, _) => _cancellation?.Cancel();
        _runRules.Click += async (_, _) => await StartRulesAsync();
        Closing += (_, e) =>
        {
            if (_cancellation is not null)
            {
                e.Cancel = true; _closeRequested = true; _cancellation.Cancel();
                _status.Text = "Stopping the current scan or edit before closing…";
            }
            else _closed = true;
        };
        Loaded += async (_, _) => await StartAsync();
    }

    private static DataGridTextColumn Column(string title, string property, double width) =>
        new() { Header = title, Binding = new Binding(property), Width = new DataGridLength(width, DataGridLengthUnitType.Star) };

    private static byte[] SettingsHash(string path)
    {
        if (!File.Exists(path)) return [];
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return SHA256.HashData(stream);
    }

    private async Task StartRulesAsync()
    {
        if (_cancellation is not null)
        {
            if (_runningRules) return;
            var stopped = _stopped;
            _cancellation.Cancel();
            if (stopped is not null) await stopped.Task;
            if (_closed || _closeRequested) return;
        }
        if (_reviewedReport is null || _reviewedSettingsHash is null)
        { MessageBox.Show(this, "Complete and review a read-only scan first.", "Automation", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        try
        {
            if (!_reviewedSettingsHash.AsSpan().SequenceEqual(SettingsHash(_settingsPath)))
                throw new InvalidOperationException("The profile changed after the scan. Monitor the saved rules again.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        { MessageBox.Show(this, ex.Message, "Automation", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        var select = new OpenFileDialog { Title = "Select the save currently loaded in Ostriv", Filter = "Ostriv saves (*.save)|*.save", CheckFileExists = true,
            InitialDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Saved Games", "Ostriv", "save") };
        _status.Text = "Choose the loaded save for backup, then confirm to start automation.";
        if (select.ShowDialog(this) != true) { _status.Text = "Automation was not started. Choose Start automation to try again."; return; }
        SaveBackup.SessionBackup backup;
        try
        {
            string root = Environment.GetEnvironmentVariable("OSTRIV_STUDIO_DATA_DIR") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OstrivStudio");
            backup = SaveBackup.CopySessionVerified(select.FileName, Path.Combine(root, "Backups"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException)
        { MessageBox.Show(this, "Backup failed; automation did not start.\n\n" + ex.Message, "Backup failed", MessageBoxButton.OK, MessageBoxImage.Error); return; }
        string pauseInstructions = "The editor will briefly pause the simulation during each edit, then restore its previous speed. Do not use the game's pause or speed controls during an edit.";
        string prompt = $"Backed up the selected save and {backup.Autosaves.Count} autosave(s):\n{backup.Directory}\n\nIs the selected save loaded, and do the reviewed candidates match the buildings in your settlement? {pauseInstructions}\n\nEnabled Each and Across rules can increase stored resources in matching camp storage, granary, well, stone mining camp, warehouse, trading post and fishing dock candidates, plus existing trading-dock broadcloth or clothes, ashery potash and brickworks brick. Across rules share a combined shortfall and require an existing whole-unit record in every matching candidate. Each rules can add selected nonperishable resources in verified spare slots; trading and industrial insertion are not supported. Enabled capacity rules can increase granary, warehouse and big hay barrack capacity while the monitor runs. Limited selections and other building classes may be skipped. Camp edits need an anchor: an existing stock of 1,000 or a nonempty item with a rule that tops it up first.\n\nThis is experimental: candidates are not guaranteed to be live buildings, and Ostriv can autosave even while paused. Stop and reload the backup without saving if a write fails.";
        if (MessageBox.Show(this, prompt, "Start experimental automation?", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        MayHaveWritten = true;
        await StartAsync(true, backup.Directory);
    }

    private async Task StartAsync(bool runRules = false, string? backupDirectory = null)
    {
        if (_cancellation is not null) return;
        var cts = new CancellationTokenSource(); _cancellation = cts; _runningRules = runRules;
        var stopped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); _stopped = stopped;
        _start.IsEnabled = false; _runRules.IsEnabled = false; _stop.IsEnabled = true;
        _description.Text = runRules
            ? "EXPERIMENTAL AUTOMATION · The game briefly pauses for each edit. Avoid changing speed while an edit is in progress; closing this window stops the runner."
            : "CHECKING ONLY · No quantities change yet. After the scan, choose your loaded save for backup and confirm to start automation. Leave this window open while you play.";
        _status.Text = "Scanning the running game. This can take a little while…";
        byte[]? profileHashAtScan = null;
        var progress = new Progress<RuleMonitorReport>(report =>
        {
            if (_closed || !ReferenceEquals(_cancellation, cts) || cts.IsCancellationRequested) return;
            _reviewedReport = report; _reviewedSettingsHash = profileHashAtScan?.ToArray();
            ShowRows(report, null);
            _runRules.IsEnabled = _reviewedSettingsHash is not null;
            _status.Text = $"Check complete: {report.Rows.Count} resource and {report.CapacityRows.Count} capacity rule(s). Select Start automation to apply supported rules; checking alone does not edit the game.";
            if (_startAfterFirstScan && _runRules.IsEnabled)
            {
                _startAfterFirstScan = false;
                _ = StartRulesAsync();
            }
        });
        var ruleProgress = new Progress<ExistingRuleTick>(tick =>
        {
            if (_closed || !ReferenceEquals(_cancellation, cts)) return;
            ShowRows(tick.BeforeEdit, tick);
            string skipped = tick.Skipped.Count == 0 ? "None skipped." : $"Skipped: {string.Join("; ", tick.Skipped.Take(3))}" + (tick.Skipped.Count > 3 ? $"; +{tick.Skipped.Count - 3} more" : "");
            _status.Text = $"Checked {tick.BeforeEdit.Rows.Count} resource and {tick.BeforeEdit.CapacityRows.Count} capacity rules at {tick.BeforeEdit.ObservedUtc.ToLocalTime():T}; applied {tick.Applied} resource and {tick.CapacityApplied} capacity change(s). {skipped} Values are from before edits. Backup: {backupDirectory}";
        });
        void ShowRows(RuleMonitorReport report, ExistingRuleTick? tick)
        {
            static string Amount(decimal? amount) => amount?.ToString("0.##", CultureInfo.CurrentCulture) ?? "—";
            var display = report.Rows.Select(row =>
            {
                bool applied = tick?.AppliedRuleNumbers.Contains(row.RuleNumber) == true;
                string? skipped = tick?.Skipped.FirstOrDefault(s => s.StartsWith($"Rule {row.RuleNumber} ", StringComparison.Ordinal));
                return new DisplayRow(row.RuleNumber, _resources.GetValueOrDefault(row.ResourceId, $"Unknown resource {row.ResourceId}"),
                    string.IsNullOrEmpty(row.BuildingType) ? "All building types" : row.BuildingType.Replace("building_", "").Replace('_', ' '),
                    Amount(row.Target), row.Candidates.ToString(CultureInfo.CurrentCulture), Amount(row.StoredTotal),
                    applied && skipped is not null ? "Partially applied" : applied ? "Applied" : skipped is not null ? "Skipped" : row.State,
                    applied && skipped is not null ? "Some targets written; " + skipped : applied ? "Target written; verify in Ostriv." : skipped ?? row.Detail);
            }).ToList();
            display.AddRange(report.CapacityRows.Select(row => new DisplayRow(row.RuleNumber, "Capacity", row.BuildingType.Replace("building_", "").Replace('_', ' '),
                Amount(row.Target), row.Candidates.ToString(CultureInfo.CurrentCulture), "—",
                tick?.AppliedCapacityRuleNumbers.Contains(row.RuleNumber) == true ? "Applied" : row.State,
                tick?.AppliedCapacityRuleNumbers.Contains(row.RuleNumber) == true ? "Capacity increased; check in Ostriv." : row.Detail)));
            _grid.ItemsSource = display;
        }
        try
        {
            async Task<(DiscoveryResult Scan, ProfileDocument Profile)> Read(CancellationToken token) => await Task.Run(() =>
            {
                byte[] before = SettingsHash(_settingsPath);
                var document = ProfileStore.Load(_settingsPath);
                string savedPath = string.IsNullOrWhiteSpace(document.GamePath) ? _gamePath : document.GamePath;
                if (!string.Equals(document.SelectedProfile, _profile, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(Path.GetFullPath(savedPath), Path.GetFullPath(_gamePath), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The selected profile or game location changed; start monitoring again.");
                var scan = LiveBuildingScanner.Scan(_gamePath, token);
                if (!before.AsSpan().SequenceEqual(SettingsHash(_settingsPath)))
                    throw new InvalidOperationException("Profile settings changed while scanning; restart with the saved rules.");
                if (runRules)
                {
                    if (_reviewedReport is null || _reviewedSettingsHash is null || !_reviewedSettingsHash.AsSpan().SequenceEqual(before))
                        throw new InvalidOperationException("The profile changed since the reviewed scan; monitor again.");
                    RuleMonitor.RequireReviewedSnapshot(_reviewedReport, scan, document.AutomationProfiles[_profile], false);
                }
                profileHashAtScan = before;
                return (scan, document);
            }, token);
            if (runRules)
                await ExistingRuleAutomation.RunAsync(Read,
                    (profile, scan, token) => Task.Run(() =>
                    {
                        if (profileHashAtScan is null || !profileHashAtScan.AsSpan().SequenceEqual(SettingsHash(_settingsPath)))
                            throw new InvalidOperationException("Profile settings changed before the edit; no write was attempted.");
                        Dispatcher.Invoke(() => _status.Text = "Applying rules · do not change the game speed or pause control…");
                        return LiveRuleExecutor.ApplyToGame(profile, scan, token);
                    }, token),
                    tick => ((IProgress<ExistingRuleTick>)ruleProgress).Report(tick), Task.Delay, cts.Token,
                    (capacityPlan, scan, token) => Task.Run(() =>
                    {
                        void CheckSettings()
                        {
                            if (profileHashAtScan is null || !profileHashAtScan.AsSpan().SequenceEqual(SettingsHash(_settingsPath)))
                                throw new InvalidOperationException("Profile settings changed before the capacity edit.");
                        }
                        CheckSettings();
                        Dispatcher.Invoke(() => _status.Text = "Applying capacity rules · do not change the game speed or pause control…");
                        return CapacityRulePlanner.ApplyToGame(capacityPlan, scan, token, CheckSettings);
                    }, token));
            else
                await RuleMonitor.RunAsync(async token => { var (scan, document) = await Read(token); return (scan, document.AutomationProfiles[_profile]); },
                    report => ((IProgress<RuleMonitorReport>)progress).Report(report), Task.Delay, cts.Token);
        }
        catch (OperationCanceledException) { if (!_closed) _status.Text = runRules ? "Automation stopped. Check Ostriv before saving." : "Monitoring stopped. No game changes were made."; }
        catch (Exception ex)
        {
            if (!runRules) { _reviewedReport = null; _reviewedSettingsHash = null; }
            if (!_closed) _status.Text = runRules ? "Automation stopped. A write may be partial; reload the backup without saving if uncertain. " + ex.Message + " Backup: " + backupDirectory : "Rule check stopped: " + ex.Message;
        }
        finally
        {
            cts.Dispose(); _cancellation = null; _runningRules = false; _stopped = null; stopped.TrySetResult(true);
            if (runRules) { _reviewedReport = null; _reviewedSettingsHash = null; }
            if (_closeRequested && !_closed) { _closeRequested = false; Close(); }
            if (!_closed) { _start.IsEnabled = true; _runRules.IsEnabled = _reviewedReport is not null && _reviewedSettingsHash is not null; _stop.IsEnabled = false; }
        }
    }
}
