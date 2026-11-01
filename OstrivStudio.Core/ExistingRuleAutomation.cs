namespace OstrivStudio.Core;

public sealed record ExistingRuleTick(RuleMonitorReport BeforeEdit, int Applied, IReadOnlyList<int> AppliedRuleNumbers, IReadOnlyList<string> Skipped)
{
    public int CapacityApplied { get; init; }
    public IReadOnlyList<int> AppliedCapacityRuleNumbers { get; init; } = [];
}

// A fresh scan is required for every cycle. Candidate addresses are only used
// during this session and a changed candidate set stops the runner.
public static class ExistingRuleAutomation
{
    public static async Task RunAsync(Func<CancellationToken, Task<(DiscoveryResult Scan, ProfileDocument Profile)>> read,
        Func<ProfileDocument, DiscoveryResult, CancellationToken, Task<LiveRuleResult>> write, Action<ExistingRuleTick> publish,
        Func<TimeSpan, CancellationToken, Task> delay, CancellationToken cancellation,
        Func<CapacityRulePlan, DiscoveryResult, CancellationToken, Task<int>>? writeCapacities = null)
    {
        ArgumentNullException.ThrowIfNull(read); ArgumentNullException.ThrowIfNull(write);
        ArgumentNullException.ThrowIfNull(publish); ArgumentNullException.ThrowIfNull(delay);
        (int Id, DateTime Started, string Hash)? session = null;
        CandidateIdentity[]? identities = null;
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            var (scan, profile) = await read(cancellation).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            var current = (scan.ProcessId, scan.ProcessStartedUtc, scan.GameSha256);
            if (session is not null && session.Value != current) throw new InvalidOperationException("The game session changed; automation stopped.");
            session = current;
            if (!profile.AutomationProfiles.TryGetValue(profile.SelectedProfile, out var settings)) throw new InvalidOperationException("The selected profile is unavailable.");
            var report = RuleMonitor.Assess(scan, settings);
            var found = report.CandidateIdentities.ToArray();
            if (identities is not null && !identities.SequenceEqual(found))
                throw new InvalidOperationException("The matching building candidates or resource arrays changed; automation stopped.");
            identities = found;
            var plan = ExistingRuleExecutor.Prepare(profile, scan);
            var capacityPlan = CapacityRulePlanner.Create(scan, settings, validateWrites: writeCapacities is not null);
            int applied = 0, capacityApplied = 0;
            if (writeCapacities is not null && capacityPlan.Groups.Count > 0)
            {
                cancellation.ThrowIfCancellationRequested();
                capacityApplied = await writeCapacities(capacityPlan, scan, cancellation).ConfigureAwait(false);
                if (capacityApplied != capacityPlan.Groups.Sum(g => g.Buildings.Count))
                    throw new PartialGameWriteException("The capacity applied count was unexpected. Inspect the game and backup.", new InvalidOperationException("Unexpected capacity count."));
            }
            try
            {
              if (plan.Entries.Count > 0)
              {
                cancellation.ThrowIfCancellationRequested();
                var result = await write(profile, scan, cancellation).ConfigureAwait(false);
                if ((result.Snapshot.ProcessId, result.Snapshot.ProcessStartedUtc, result.Snapshot.GameSha256) != current ||
                    !RuleMonitor.CandidateIdentities(result.Snapshot, settings).SequenceEqual(found))
                    throw new InvalidOperationException("The game or building candidates changed during the edit.");
                scan = result.Snapshot;
                report = RuleMonitor.Assess(scan, settings);
                plan = result.Plan ?? ExistingRuleExecutor.Prepare(profile, scan);
                applied = result.Applied;
                if (applied != plan.Entries.Count) throw new InvalidOperationException("The applied count was unexpected. Stop and inspect the game and backup.");
              }
            }
            catch (PartialGameWriteException) { throw; }
            catch (Exception ex) when (capacityApplied > 0)
            { throw new PartialGameWriteException("Capacity changed before resource automation stopped. Inspect the game and reload the backup if uncertain. " + ex.Message, ex); }
            publish(new(report, applied, plan.Entries.Select(e => e.RuleNumber).ToArray(), plan.Skipped)
            { CapacityApplied = capacityApplied, AppliedCapacityRuleNumbers = capacityApplied > 0 ? capacityPlan.Groups.Select(g => g.RuleNumber).ToArray() : [] });
            await delay(TimeSpan.FromSeconds(settings.IntervalSeconds), cancellation).ConfigureAwait(false);
        }
    }
}
