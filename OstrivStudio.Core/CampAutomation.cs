namespace OstrivStudio.Core;

public sealed record CampAutomationTick(RuleMonitorReport BeforeEdit, int Applied, IReadOnlyList<string> Skipped);

// This runner intentionally reuses the already-tested narrow camp writer. It
// does not insert records or claim to execute the rest of a profile.
public static class CampAutomation
{
    public static async Task RunAsync(Func<CancellationToken, Task<(DiscoveryResult Scan, ProfileDocument Profile)>> read,
        Func<ProfileDocument, DiscoveryResult, IReadOnlyList<BuildingCandidate>, CancellationToken, Task<int>> write,
        Action<CampAutomationTick> publish, Func<TimeSpan, CancellationToken, Task> delay, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(read); ArgumentNullException.ThrowIfNull(write);
        ArgumentNullException.ThrowIfNull(publish); ArgumentNullException.ThrowIfNull(delay);
        (int Id, DateTime Started, string Hash)? session = null;
        (ulong Address, ulong Vtable, ulong ResourcePointer)? campIdentity = null;
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            var (scan, profile) = await read(cancellation).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            var current = (scan.ProcessId, scan.ProcessStartedUtc, scan.GameSha256);
            if (session is not null && session.Value != current)
                throw new InvalidOperationException("The game session changed; automation stopped.");
            session = current;
            if (!profile.AutomationProfiles.TryGetValue(profile.SelectedProfile, out var settings))
                throw new InvalidOperationException("The selected profile is unavailable.");
            var report = RuleMonitor.Assess(scan, settings);
            var selected = scan.Buildings.Where(b => b.ClassName == CampRuleExecutor.BuildingType).ToArray();
            if (selected.Length != 1) throw new InvalidOperationException("Exactly one camp-storage candidate is required; automation stopped.");
            var identity = (selected[0].Address, selected[0].Vtable, selected[0].ResourcePointer);
            if (campIdentity is not null && campIdentity.Value != identity)
                throw new InvalidOperationException("The camp-storage address or resource array changed; automation stopped.");
            campIdentity = identity;
            var plan = CampRuleExecutor.PrepareBatch(profile, scan, selected);
            int applied = 0;
            if (plan.Entries.Count > 0)
            {
                cancellation.ThrowIfCancellationRequested();
                applied = await write(profile, scan, selected, cancellation).ConfigureAwait(false);
                if (applied != plan.Entries.Count) throw new InvalidOperationException("The applied count was unexpected. Stop and inspect the game and backup.");
            }
            publish(new(report, applied, plan.Skipped));
            await delay(TimeSpan.FromSeconds(settings.IntervalSeconds), cancellation).ConfigureAwait(false);
        }
    }
}
