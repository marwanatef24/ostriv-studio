using OstrivStudio.Core;

internal static class RuleMonitorTests
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        Console.WriteLine("PASS " + message);
    }

    public static async Task Run()
    {
        var inputs = new (int Id, string Type, decimal Target)[] {
            (33,"camp_storage",1000), (18,"camp_storage",5000), (87,"granary",2000),
            (85,"camp_storage",2000), (69,"camp_storage",2000), (133,"camp_storage",2000),
            (10,"camp_storage",20000), (14,"well",5000), (41,"stone_mining_camp",5000),
            (65,"stone_mining_camp",5000), (109,"camp_storage",1000), (6,"camp_storage",2000),
            (48,"warehouse",2000), (123,"camp_storage",2000), (9,"camp_storage",2000),
            (92,"camp_storage",2000), (80,"camp_storage",2000)
        };
        var settings = new AutomationProfileSettings { IntervalSeconds = 5,
            MaintenanceRules = inputs.Select(x => new MaintenanceRule { ResourceId = x.Id, BuildingType = "building_" + x.Type, Target = x.Target, Enabled = true }).ToList() };
        var buildings = inputs.GroupBy(x => x.Type).Select((group, i) =>
        {
            var resources = group.Where(x => x.Id != 87).Select((x, n) =>
                new ResourceObservation(n, x.Id, (float)(x.Id == 14 ? x.Target - 1 : x.Target), 0, 0, 0)).ToArray();
            return new BuildingCandidate("building_" + group.Key, (ulong)(0x1000 + i * 0x1000), 1, 2, resources.Length, resources, "candidate");
        }).ToArray();
        var scan = new DiscoveryResult(42, DateTime.UtcNow, "test", FishBulkEditor.SupportedHash, 408, 416, buildings, 1, true, 0, "");
        var report = RuleMonitor.Assess(scan, settings);
        Check(report.Rows.Count == 17 && report.Rows.All(r => r.Candidates == 1), "all 17 saved rules receive an independent assessment");
        Check(report.Rows.Single(r => r.ResourceId == 87).State == "Missing stored record" &&
            report.Rows.Single(r => r.ResourceId == 14).State == "Below target" &&
            report.Rows.Single(r => r.ResourceId == 48).State == "At or above target", "monitor distinguishes missing, below-target and satisfied records");
        RuleMonitor.RequireReviewedSnapshot(report, scan, settings, true);
        var alteredWell = scan.Buildings.Single(b => b.ClassName == "building_well");
        var changedRecords = alteredWell.Resources.Select(r => r with { Amount = r.Amount + 1 }).ToArray();
        var changedScan = scan with { Buildings = scan.Buildings.Select(b => b == alteredWell ? b with { Resources = changedRecords } : b).ToArray() };
        try { RuleMonitor.RequireReviewedSnapshot(report, changedScan, settings, true); throw new Exception("Changed amounts accepted"); }
        catch (InvalidOperationException) { Console.WriteLine("PASS reviewed snapshot rejects changed amounts before the first edit"); }
        RuleMonitor.RequireReviewedSnapshot(report, changedScan, settings, false);
        var changedPointer = scan with { Buildings = scan.Buildings.Select(b => b == alteredWell ? b with { ResourcePointer = b.ResourcePointer + 8 } : b).ToArray() };
        try { RuleMonitor.RequireReviewedSnapshot(report, changedPointer, settings, false); throw new Exception("Changed candidate accepted"); }
        catch (InvalidOperationException) { Console.WriteLine("PASS reviewed snapshot rejects changed candidate identity on later cycles"); }

        settings.MaintenanceRules.Add(new() { ResourceId = 14, Enabled = true, Target = 100 });
        report = RuleMonitor.Assess(scan, settings);
        Check(report.Rows.Where(r => r.ResourceId == 14).All(r => r.State == "Conflicting rules"), "overlapping wildcard rules are flagged");
        settings.MaintenanceRules.RemoveAt(settings.MaintenanceRules.Count - 1);
        var unsupported = new AutomationProfileSettings { MaintenanceRules = [
            new() { ResourceId = 8, BuildingType = "building_rowhouse_shop", Enabled = true, Target = 50 },
            new() { ResourceId = 7, BuildingType = "building_rowhouse_shop", Enabled = true, Target = 101 } ] };
        var unsupportedReport = RuleMonitor.Assess(scan, unsupported);
        Check(unsupportedReport.Rows.All(r => r.State == "Not supported"), "read-only rule checks explain unsupported shop goods and excessive targets before automation starts");
        var acrossSettings = new AutomationProfileSettings { MaintenanceRules = [
            new() { ResourceId = 14, BuildingType = "building_well", Enabled = true, Target = 5000, Mode = MaintenanceMode.Across } ] };
        var fractional = scan with { Buildings = scan.Buildings.Select(b => b == alteredWell
            ? b with { Resources = b.Resources.Select(r => r with { Amount = 4999.5f }).ToArray() } : b).ToArray() };
        var fractionalRow = RuleMonitor.Assess(fractional, acrossSettings).Rows.Single();
        Check(fractionalRow.State == "Not supported" && fractionalRow.Detail.Contains("whole-unit"),
            "across monitor explains fractional stock that the automation planner cannot edit");
        var missingWell = alteredWell with { Resources = [], ResourceCount = 0 };
        var missingScan = scan with { Buildings = scan.Buildings.Select(b => b == alteredWell ? missingWell : b).ToArray() };
        var missingRow = RuleMonitor.Assess(missingScan, acrossSettings).Rows.Single();
        Check(missingRow.State == "Not supported" && !string.IsNullOrWhiteSpace(missingRow.Detail),
            "across monitor reports a missing resource layout instead of promising an edit");
        Check(RuleMonitor.Assess(scan, acrossSettings).Rows.Single().State == "Below target",
            "across monitor still reports a supported shortfall");
        try { RuleMonitor.Assess(scan with { Complete = false }, settings); throw new Exception("Incomplete scan accepted"); }
        catch (InvalidOperationException) { Console.WriteLine("PASS rule monitor rejects incomplete scans"); }

        int scans = 0, active = 0, maxActive = 0, reports = 0, waits = 0;
        using var cts = new CancellationTokenSource();
        try
        {
            await RuleMonitor.RunAsync(async token =>
            {
                scans++; active++; maxActive = Math.Max(maxActive, active);
                await Task.Yield(); token.ThrowIfCancellationRequested(); active--;
                return (scan, settings);
            }, _ => reports++, (interval, token) =>
            {
                Check(interval == TimeSpan.FromSeconds(5), "monitor uses the saved interval");
                if (++waits == 2) cts.Cancel();
                return Task.CompletedTask;
            }, cts.Token);
            throw new Exception("Cancelled monitor continued");
        }
        catch (OperationCanceledException) { }
        Check(scans == 2 && reports == 2 && maxActive == 1, "monitor repeats without overlapping scans and stops on cancellation");

        scans = 0; reports = 0;
        try
        {
            await RuleMonitor.RunAsync(_ => Task.FromResult((++scans == 1 ? scan : scan with { ProcessId = 43 }, settings)),
                _ => reports++, (_, _) => Task.CompletedTask, CancellationToken.None);
            throw new Exception("Changed process accepted");
        }
        catch (InvalidOperationException) { }
        Check(scans == 2 && reports == 1, "monitor stops before publishing a different game session");

        byte[] capacity = BitConverter.GetBytes(20000f);
        Check(CapacityObservation.Decode(FishBulkEditor.SupportedHash, "building_granary", capacity, capacity) == 20000f &&
            CapacityObservation.Decode("unknown", "building_granary", capacity, capacity) is null &&
            CapacityObservation.Decode(FishBulkEditor.SupportedHash, "building_camp_storage", capacity, capacity) is null &&
            CapacityObservation.Decode(FishBulkEditor.SupportedHash, "building_warehouse", capacity, BitConverter.GetBytes(10000f)) is null &&
            CapacityObservation.Decode(FishBulkEditor.SupportedHash, "building_warehouse", BitConverter.GetBytes(float.NaN), BitConverter.GetBytes(float.NaN)) is null,
            "capacity observations require a tested build and class, stable finite values");
    }
}
