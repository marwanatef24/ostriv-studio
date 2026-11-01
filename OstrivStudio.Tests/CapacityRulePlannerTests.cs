using OstrivStudio.Core;

internal static class CapacityRulePlannerTests
{
    private static void Check(bool condition, string message)
    { if (!condition) throw new Exception(message); Console.WriteLine("PASS " + message); }

    public static async Task Run()
    {
        float capacity = 7000;
        var started = DateTime.UtcNow;
        var profile = new ProfileDocument();
        var settings = profile.AutomationProfiles[profile.SelectedProfile];
        settings.IntervalSeconds = 5;
        settings.CapacityRules = [new() { LegacyBuildingId = 106, BuildingType = "building_hay_barrack_big", Capacity = 7001, Enabled = true }];
        DiscoveryResult Snapshot(ulong address = 0x10000)
        {
            var hay = new BuildingCandidate("building_hay_barrack_big", address, 0x9000, 0, 0, [], "candidate", capacity);
            return new(7, started, "test", FishBulkEditor.SupportedHash, 408, 416, [hay], 1, true, 0, "");
        }
        var plan = CapacityRulePlanner.Create(Snapshot(), settings, validateWrites: true);
        Check(plan.Rows.Single().State == "Below target" && plan.Groups.Single().Buildings.Count == 1 &&
            RuleMonitor.CandidateIdentities(Snapshot(), settings).Length == 1, "capacity rules plan a supported increase and include candidates in review");
        capacity = 8000;
        Check(CapacityRulePlanner.Create(Snapshot(), settings).Groups.Count == 0, "capacity rules never reduce a larger capacity");
        settings.CapacityRules.Add(new() { LegacyBuildingId = 101, BuildingType = "building_well", Capacity = 9000, Enabled = true });
        Check(CapacityRulePlanner.Create(Snapshot(), settings).Rows[1].State == "Unsupported", "unverified capacity classes are reported as unsupported");
        settings.CapacityRules.Add(new() { LegacyBuildingId = 999, BuildingType = "building_hay_barrack_big", Capacity = 9000, Enabled = true });
        Check(CapacityRulePlanner.Create(Snapshot(), settings).Rows[0].State == "Conflicting rules" &&
            CapacityRulePlanner.Create(Snapshot(), settings).Rows[2].State == "Conflicting rules", "conflicting capacity rules are not applied");
        settings.CapacityRules.RemoveAt(2); settings.CapacityRules.RemoveAt(1); capacity = 7000;
        var ticks = new List<ExistingRuleTick>(); int writes = 0, scans = 0;
        using var cancellation = new CancellationTokenSource();
        try
        {
            await ExistingRuleAutomation.RunAsync(_ => { scans++; return Task.FromResult((Snapshot(), profile)); },
                (_, _, _) => throw new Exception("No resource writes expected"), ticks.Add,
                (_, _) => { if (ticks.Count == 2) capacity = 7000; if (ticks.Count == 3) cancellation.Cancel(); return Task.CompletedTask; },
                cancellation.Token, (p, _, _) => { writes++; capacity = (float)p.Groups.Single().Target; return Task.FromResult(1); });
            throw new Exception("Cancellation ignored");
        }
        catch (OperationCanceledException) { }
        Check(scans == 3 && writes == 2 && ticks.Select(t => t.CapacityApplied).SequenceEqual([1, 0, 1]),
            "capacity automation reapplies the saved minimum after a reset and does not repeat a satisfied write");
        capacity = 7000; scans = 0; writes = 0;
        try
        {
            await ExistingRuleAutomation.RunAsync(_ => Task.FromResult((Snapshot(++scans == 1 ? 0x10000UL : 0x20000UL), profile)),
                (_, _, _) => throw new Exception("No resource writes expected"), _ => { }, (_, _) => Task.CompletedTask,
                CancellationToken.None, (_, _, _) => { writes++; capacity = 7001; return Task.FromResult(1); });
            throw new Exception("Changed candidate accepted");
        }
        catch (InvalidOperationException) { }
        Check(scans == 2 && writes == 1, "capacity automation stops when the reviewed candidate changes");
    }
}
