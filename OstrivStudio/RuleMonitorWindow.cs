using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
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
    private bool _runningRules, _startingRules;
    private bool _startAfterFirstScan = true;
    private bool _closed, _closeRequested, _stopRequested, _allowClose;
    private readonly bool _background;
    private TaskCompletionSource<bool>? _startupFinished;
    private readonly CancellationTokenSource _shutdown = new();
    private RuleMonitorReport? _reviewedReport;
    private byte[]? _reviewedSettingsHash;
    public bool MayHaveWritten { get; private set; }
    public bool IsRunning => _runningRules;
    public string Status => _status.Text;
    public event Action? StatusChanged;
    public event Action? StopRequested;
    public event Action? WriteStarting;
    public event Action<DiscoveryResult>? InitialScanReady;

    public RuleMonitorWindow(string settingsPath, string profile, string gamePath, IReadOnlyDictionary<int, string> resources, bool background = false)
    {
        _settingsPath = settingsPath; _profile = profile; _gamePath = gamePath; _resources = resources; _background = background;
        Title = "Start saved rules"; Width = 1180; Height = 690; MinWidth = 850; MinHeight = 500;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F2F5F9"));
        Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#17253A"));
        var root = new DockPanel { Margin = new Thickness(25) };
        var header = new StackPanel();
        header.Children.Add(new TextBlock { Text = "Start saved rules", FontFamily = new FontFamily("Georgia"), FontSize = 29 });
        header.Children.Add(new TextBlock { Text = $"Profile: {_profile}", Margin = new Thickness(0, 5, 0, 5), Foreground = new SolidColorBrush(Color.FromRgb(139, 77, 18)) });
        _description.Text = "Starting your saved rules. Your existing saves are backed up automatically before changes begin. Leave this window open while you play.";
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
        _start.Click += async (_, _) => { _startAfterFirstScan = true; await StartAsync(); };
        _stop.Click += async (_, _) => { if (StopRequested is not null) StopRequested(); else await ShutdownAsync(); };
        _runRules.Click += async (_, _) => await StartRulesAsync();
        Closing += (_, e) =>
        {
            if (_background && !_allowClose) { e.Cancel = true; Hide(); return; }
            if (_cancellation is not null)
            {
                e.Cancel = true; _closeRequested = true; _cancellation.Cancel();
                _status.Text = "Stopping the current scan or edit before closing…";
            }
            else _closed = true;
        };
        Loaded += async (_, _) => { if (_background) Hide(); await StartAsync(); };
    }

    private void SetStatus(string value) { _status.Text = value; StatusChanged?.Invoke(); }

    public async Task ShutdownAsync()
    {
        _stopRequested = true; _startAfterFirstScan = false; _shutdown.Cancel();
        _cancellation?.Cancel();
        var startup = _startupFinished;
        var stopped = _stopped;
        if (startup is not null) await startup.Task;
        else if (stopped is not null) await stopped.Task;
        if (!_closed) { _allowClose = true; Close(); }
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
        if (_startingRules || _stopRequested) return;
        _startingRules = true;
        var startup = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _startupFinished = startup;
        try
        {
        if (_cancellation is not null)
        {
            if (_runningRules) return;
            var stopped = _stopped;
            _cancellation.Cancel();
            if (stopped is not null) await stopped.Task;
            if (_closed || _closeRequested || _stopRequested) return;
        }
        if (_reviewedReport is null || _reviewedSettingsHash is null)
        { SetStatus("Automation stopped: a complete game scan is required."); return; }
        try
        {
            if (!_reviewedSettingsHash.AsSpan().SequenceEqual(SettingsHash(_settingsPath)))
                throw new InvalidOperationException("The profile changed after the scan. Monitor the saved rules again.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        { SetStatus("Automation stopped: " + ex.Message); return; }
        _start.IsEnabled = false;
        _runRules.IsEnabled = false;
        SetStatus("Backing up your existing saves automatically…");
        SaveBackup.SaveDirectoryBackup? backup = null;
        try
        {
            string root = Environment.GetEnvironmentVariable("OSTRIV_STUDIO_DATA_DIR") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OstrivStudio");
            string saves = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Saved Games", "Ostriv", "save");
            backup = await BackupRetry.RunAsync(
                () => Task.Run(() => SaveBackup.CopyAllSavesVerified(saves, Path.Combine(root, "Backups"))),
                _ => Dispatcher.Invoke(() => SetStatus("A save changed during backup. Retrying automatically…")),
                Task.Delay, _shutdown.Token);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException)
        {
            if (!_closed) SetStatus("Automation did not start because the saves could not be backed up: " + ex.Message);
            return;
        }
        if (_closed || _closeRequested || _stopRequested || backup is null) return;
        try
        {
            if (!_reviewedSettingsHash.AsSpan().SequenceEqual(SettingsHash(_settingsPath)))
            { SetStatus("Automation stopped: your profile changed during backup. Check the rules again to start."); return; }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { SetStatus("Automation did not start because the profile could not be checked: " + ex.Message); return; }
        MayHaveWritten = true;
        await StartAsync(true, backup.Directory);
        }
        finally
        {
            _startingRules = false;
            _startupFinished = null; startup.TrySetResult(true);
            if (!_closed && _cancellation is null)
            { _start.IsEnabled = true; _runRules.IsEnabled = _reviewedReport is not null && _reviewedSettingsHash is not null; }
        }
    }

    private async Task StartAsync(bool runRules = false, string? backupDirectory = null)
    {
        if (_cancellation is not null || _stopRequested) return;
        var cts = new CancellationTokenSource(); _cancellation = cts; _runningRules = runRules;
        var stopped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); _stopped = stopped;
        _start.IsEnabled = false; _runRules.IsEnabled = false; _stop.IsEnabled = true;
        _description.Text = runRules
            ? (_background ? "AUTOMATION ON · The game briefly pauses during an edit. Close this window to return to the editor; use Stop to pause automation." : "EXPERIMENTAL AUTOMATION · The game briefly pauses for each edit. Avoid changing speed while an edit is in progress; closing this window stops the runner.")
            : "Checking your saved rules. Your existing saves are backed up automatically before automation starts.";
        SetStatus("Scanning the running game. This can take a little while…");
        byte[]? profileHashAtScan = null;
        bool settlementDetected = false;
        bool initialScanSent = false;
        var initialScanProgress = new Progress<DiscoveryResult>(scan =>
        {
            if (!_closed && ReferenceEquals(_cancellation, cts) && !cts.IsCancellationRequested)
                InitialScanReady?.Invoke(scan);
        });
        var progress = new Progress<RuleMonitorReport>(report =>
        {
            if (_closed || !ReferenceEquals(_cancellation, cts) || cts.IsCancellationRequested) return;
            _reviewedReport = report; _reviewedSettingsHash = profileHashAtScan?.ToArray();
            ShowRows(report, null);
            if (!settlementDetected)
            { _runRules.IsEnabled = false; SetStatus("Waiting for a settlement to load in Ostriv…"); return; }
            _runRules.IsEnabled = _reviewedSettingsHash is not null;
            SetStatus($"Found {report.Rows.Count} resource and {report.CapacityRows.Count} capacity rule(s). Preparing automation…");
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
            SetStatus($"Checked {tick.BeforeEdit.Rows.Count} resource and {tick.BeforeEdit.CapacityRows.Count} capacity rules at {tick.BeforeEdit.ObservedUtc.ToLocalTime():T}; applied {tick.Applied} resource and {tick.CapacityApplied} capacity change(s). {skipped} Values are from before edits. Backup: {backupDirectory}");
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
                settlementDetected = scan.Buildings.Count > 0;
                if (!before.AsSpan().SequenceEqual(SettingsHash(_settingsPath)))
                    throw new InvalidOperationException("Profile settings changed while scanning; restart with the saved rules.");
                if (!runRules && !initialScanSent && scan.Complete && scan.UnreadableChunks == 0 && scan.Buildings.Count > 0)
                {
                    initialScanSent = true;
                    ((IProgress<DiscoveryResult>)initialScanProgress).Report(scan);
                }
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
                        Dispatcher.Invoke(() => { SetStatus("Applying rules · do not change the game speed or pause control…"); WriteStarting?.Invoke(); });
                        return GameWriteCoordinator.Run(() =>
                        {
                            void CheckSettings()
                            {
                                if (profileHashAtScan is null || !profileHashAtScan.AsSpan().SequenceEqual(SettingsHash(_settingsPath)))
                                    throw new InvalidOperationException("Profile settings changed before the edit; no write was attempted.");
                            }
                            CheckSettings();
                            return LiveRuleExecutor.ApplyToGame(profile, scan, token, _ => CheckSettings());
                        }, token);
                    }, token),
                    tick => ((IProgress<ExistingRuleTick>)ruleProgress).Report(tick), Task.Delay, cts.Token,
                    (capacityPlan, scan, token) => Task.Run(() =>
                    {
                        void CheckSettings()
                        {
                            if (profileHashAtScan is null || !profileHashAtScan.AsSpan().SequenceEqual(SettingsHash(_settingsPath)))
                                throw new InvalidOperationException("Profile settings changed before the capacity edit.");
                        }
                        Dispatcher.Invoke(() => { SetStatus("Applying capacity rules · do not change the game speed or pause control…"); WriteStarting?.Invoke(); });
                        return GameWriteCoordinator.Run(() => { CheckSettings(); return CapacityRulePlanner.ApplyToGame(capacityPlan, scan, token, CheckSettings); }, token);
                    }, token));
            else
                await RuleMonitor.RunAsync(async token => { var (scan, document) = await Read(token); return (scan, document.AutomationProfiles[_profile]); },
                    report => ((IProgress<RuleMonitorReport>)progress).Report(report), Task.Delay, cts.Token);
        }
        catch (OperationCanceledException) { if (!_closed) SetStatus(runRules ? "Automation stopped. Check Ostriv before saving." : "Monitoring stopped. No game changes were made."); }
        catch (Exception ex)
        {
            if (!runRules) { _reviewedReport = null; _reviewedSettingsHash = null; }
            if (!_closed) SetStatus(runRules ? "Automation stopped. A write may be partial; reload the backup without saving if uncertain. " + ex.Message + " Backup: " + backupDirectory : "Rule check stopped: " + ex.Message);
        }
        finally
        {
            cts.Dispose(); _cancellation = null; _runningRules = false; _stopped = null; stopped.TrySetResult(true);
            StatusChanged?.Invoke();
            if (runRules) { _reviewedReport = null; _reviewedSettingsHash = null; }
            if (_closeRequested && !_closed) { _closeRequested = false; Close(); }
            if (!_closed) { _start.IsEnabled = true; _runRules.IsEnabled = _reviewedReport is not null && _reviewedSettingsHash is not null; _stop.IsEnabled = false; }
        }
    }
}
