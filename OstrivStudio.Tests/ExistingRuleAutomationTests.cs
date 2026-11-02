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
        int calls=0; writes=0; amount=4999;
        var candidateTicks = new List<ExistingRuleTick>();
        using (var changed = new CancellationTokenSource())
        {
            try
            {
                await ExistingRuleAutomation.RunAsync(_ => { calls++; return Task.FromResult((Snapshot(0x4000), profile)); },
                    (_, scan, _) => { writes++; amount=5000; return Task.FromResult(new LiveRuleResult(scan, 1)); },
                    tick => { candidateTicks.Add(tick); if (candidateTicks.Count == 2) changed.Cancel(); },
                    (_,_)=>Task.CompletedTask, changed.Token,
                    initialCandidates: RuleMonitor.CandidateIdentities(Snapshot(0x3000), settings));
                throw new Exception("Cancellation ignored");
            }
            catch (OperationCanceledException) { }
        }
        Check(calls==2 && writes==1 && candidateTicks[0].WaitingForStableCandidates && !candidateTicks[1].WaitingForStableCandidates,
            "changed candidates are checked twice before automation writes again");
        calls=0; writes=0; amount=4999;
        using (var unstable = new CancellationTokenSource())
        {
            try
            {
                await ExistingRuleAutomation.RunAsync(_ => { calls++; return Task.FromResult((Snapshot(calls % 2 == 0 ? 0x3000UL : 0x4000UL), profile)); },
                    (_, _, _) => { writes++; throw new Exception("Unstable candidates were written"); },
                    _ => { if (calls == 3) unstable.Cancel(); }, (_,_)=>Task.CompletedTask, unstable.Token,
                    initialCandidates: RuleMonitor.CandidateIdentities(Snapshot(0x3000), settings));
                throw new Exception("Cancellation ignored");
            }
            catch (OperationCanceledException) { }
        }
        Check(calls==3 && writes==0, "repeatedly changing candidates never trigger a write");
        calls=0; writes=0;
        try
        {
            await ExistingRuleAutomation.RunAsync(_ => { calls++; var scan=Snapshot(); return Task.FromResult((calls == 1 ? scan : scan with { ProcessStartedUtc = started.AddSeconds(1) }, profile)); },
                (_, scan, _) => { writes++; return Task.FromResult(new LiveRuleResult(scan, 1)); }, _ => { }, (_,_) => Task.CompletedTask, CancellationToken.None);
            throw new Exception("Changed game session accepted");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("session changed")) { }
        Check(calls==2 && writes==1, "a changed game session stops the runner before another write");
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
