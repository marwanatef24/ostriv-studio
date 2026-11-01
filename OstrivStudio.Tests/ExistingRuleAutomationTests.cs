using OstrivStudio.Core;

internal static class ExistingRuleAutomationTests
{
    private static void Check(bool value, string text) { if (!value) throw new Exception(text); Console.WriteLine("PASS " + text); }
    public static async Task Run()
    {
        var started = DateTime.UtcNow; float amount = 4999;
        var profile = new ProfileDocument(); var settings = profile.AutomationProfiles[profile.SelectedProfile]; settings.IntervalSeconds = 5;
        settings.MaintenanceRules = [new() { Enabled=true, ResourceId=14, BuildingType="building_well", Target=5000 }];
        DiscoveryResult Snapshot(ulong pointer = 0x3000)
        {
            var well = new BuildingCandidate("building_well", 0x1000, 0x9000, pointer, 1, [new(0,14,amount,0,0,0)], "candidate");
            return new(7, started, "test", FishBulkEditor.SupportedHash, 408, 416, [well], 1, true, 0, "");
        }
        int scans=0,writes=0; var ticks=new List<ExistingRuleTick>();
        using (var cts = new CancellationTokenSource())
        {
            try
            {
                await ExistingRuleAutomation.RunAsync(_ => { scans++; return Task.FromResult((Snapshot(), profile)); },
                    (_, scan, _) => { writes++; amount=5000; return Task.FromResult(new LiveRuleResult(scan, 1)); }, ticks.Add,
                    (interval, _) => { Check(interval == TimeSpan.FromSeconds(5), "existing-rule runner uses the saved interval"); if (ticks.Count==2) cts.Cancel(); return Task.CompletedTask; }, cts.Token);
                throw new Exception("Cancellation ignored");
            }
            catch (OperationCanceledException) { }
        }
        Check(scans==2 && writes==1 && ticks.Select(t=>t.Applied).SequenceEqual([1,0]), "existing-rule runner applies once and skips the satisfied target next cycle");
        int calls=0; writes=0;
        try
        {
            await ExistingRuleAutomation.RunAsync(_ => Task.FromResult((Snapshot(++calls==1 ? 0x3000UL : 0x4000UL), profile)),
                (_, scan, _) => { writes++; return Task.FromResult(new LiveRuleResult(scan, 0)); }, _=>{}, (_,_)=>Task.CompletedTask, CancellationToken.None);
            throw new Exception("Changed pointer accepted");
        }
        catch (InvalidOperationException) { }
        Check(calls==2 && writes==0, "existing-rule runner stops on a changed candidate array");
        amount=4999; calls=0; writes=0;
        try
        {
            await ExistingRuleAutomation.RunAsync(_ => { calls++; return Task.FromResult((Snapshot(), profile)); },
                (_, _, _) => { writes++; throw new IOException("uncertain write"); }, _=>{}, (_,_)=>Task.CompletedTask, CancellationToken.None);
            throw new Exception("Write error ignored");
        }
        catch (IOException) { }
        Check(calls==1 && writes==1, "existing-rule runner does not retry an uncertain write");
    }
}
