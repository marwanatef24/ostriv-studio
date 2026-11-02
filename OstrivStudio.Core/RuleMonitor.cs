namespace OstrivStudio.Core;

public sealed record RuleMonitorRow(int RuleNumber, int ResourceId, string BuildingType, decimal? Target,
    int Candidates, decimal? StoredTotal, string State, string Detail);

public sealed record CandidateIdentity(string Type, ulong Address, ulong Vtable, ulong ResourcePointer);
public sealed record RuleMonitorReport(DateTime ObservedUtc, int ProcessId, DateTime ProcessStartedUtc,
    string GameSha256, IReadOnlyList<RuleMonitorRow> Rows, IReadOnlyList<CandidateIdentity> CandidateIdentities)
{
    public IReadOnlyList<CapacityRuleRow> CapacityRows { get; init; } = [];
}

// This is intentionally read-only. A memory scan identifies candidates, not
// necessarily live buildings; automatic game writes require stronger evidence.
public static class RuleMonitor
{
    public static void RequireReviewedSnapshot(RuleMonitorReport reviewed, DiscoveryResult scan, AutomationProfileSettings settings, bool compareAmounts)
    {
        ArgumentNullException.ThrowIfNull(reviewed);
        var current = Assess(scan, settings);
        if (reviewed.ProcessId != current.ProcessId || reviewed.ProcessStartedUtc != current.ProcessStartedUtc ||
            reviewed.GameSha256 != current.GameSha256 || !reviewed.CandidateIdentities.SequenceEqual(current.CandidateIdentities))
            throw new InvalidOperationException("The game or building candidates changed since the reviewed scan; monitor again.");
        if (compareAmounts && (!reviewed.Rows.SequenceEqual(current.Rows) || !reviewed.CapacityRows.SequenceEqual(current.CapacityRows)))
            throw new InvalidOperationException("The resource amounts changed since the reviewed scan; monitor again.");
    }
    public static CandidateIdentity[] CandidateIdentities(DiscoveryResult scan, AutomationProfileSettings settings)
    {
        var types = settings.MaintenanceRules.Where(r => r.Enabled && !string.IsNullOrWhiteSpace(r.BuildingType))
            .Select(r => r.BuildingType.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in settings.CapacityRules.Where(r => r.Enabled && !string.IsNullOrWhiteSpace(r.BuildingType))) types.Add(rule.BuildingType.Trim());
        return scan.Buildings.Where(b => types.Contains(b.ClassName))
            .OrderBy(b => b.ClassName, StringComparer.OrdinalIgnoreCase).ThenBy(b => b.Address)
            .Select(b => new CandidateIdentity(b.ClassName.ToUpperInvariant(), b.Address, b.Vtable, b.ResourcePointer)).ToArray();
    }
    public static RuleMonitorReport Assess(DiscoveryResult scan, AutomationProfileSettings settings)
    {
        ArgumentNullException.ThrowIfNull(scan);
        ArgumentNullException.ThrowIfNull(settings);
        if (!scan.Complete || scan.UnreadableChunks != 0 || scan.Buildings.Count > 10000 ||
            scan.Buildings.Select(b => b.Address).Distinct().Count() != scan.Buildings.Count)
            throw new InvalidOperationException("The building scan is incomplete or ambiguous; monitoring stopped.");
        if (settings.IntervalSeconds is < 5 or > 86400 || settings.MaintenanceRules is null || settings.MaintenanceRules.Count > 10000)
            throw new InvalidDataException("The saved automation settings are invalid.");

        var rules = settings.MaintenanceRules;
        bool Overlaps(MaintenanceRule left, MaintenanceRule right) => left.ResourceId == right.ResourceId &&
            (string.IsNullOrWhiteSpace(left.BuildingType) || string.IsNullOrWhiteSpace(right.BuildingType) ||
             string.Equals(left.BuildingType.Trim(), right.BuildingType.Trim(), StringComparison.OrdinalIgnoreCase));
        var rows = new List<RuleMonitorRow>(rules.Count);
        for (int index = 0; index < rules.Count; index++)
        {
            var rule = rules[index] ?? throw new InvalidDataException("A saved rule is missing.");
            if (rule.ResourceId is <= 0 or > 65535 || rule.BuildingType is null || rule.BuildingType.Length > 100 ||
                rule.Target is < 0 or > 1_000_000_000m || rule.BuildingLimit is < -1 or > 100000 || !Enum.IsDefined(rule.Mode))
                throw new InvalidDataException("A saved rule is invalid.");
            string type = rule.BuildingType.Trim();
            RuleMonitorRow Row(int count, decimal? total, string state, string detail) =>
                new(index + 1, rule.ResourceId, type, rule.Target, count, total, state, detail);
            if (!rule.Enabled) { rows.Add(Row(0, null, "Disabled", "This rule is switched off.")); continue; }
            if (rule.Target is null) { rows.Add(Row(0, null, "No target", "Set a target in the profile.")); continue; }
            if (rule.BuildingLimit == 0) { rows.Add(Row(0, null, "No buildings selected", "The rule's building limit is zero.")); continue; }
            if (rules.Where((_, i) => i != index).Any(other => other is not null && other.Enabled && Overlaps(rule, other)))
            { rows.Add(Row(0, null, "Conflicting rules", "Another enabled rule can target the same resource and building.")); continue; }
            if (!ExistingRuleExecutor.SupportsExisting(type, rule.ResourceId))
            { rows.Add(Row(0, null, "Not supported", "Automatic edits for this building and resource are not supported.")); continue; }
            if (type.Equals("building_rowhouse_shop", StringComparison.OrdinalIgnoreCase) && rule.Target > 100)
            { rows.Add(Row(0, null, "Not supported", "Shop minimums are limited to 100.")); continue; }

            var matches = scan.Buildings.Where(b => type.Length == 0 || string.Equals(type, b.ClassName, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length == 0) { rows.Add(Row(0, null, "No candidates", "No matching building candidate was found.")); continue; }
            if (rule.BuildingLimit > 0 && matches.Length > rule.BuildingLimit)
            { rows.Add(Row(matches.Length, null, "Selection unverified", "The original editor's limited-building selection order is not yet verified.")); continue; }

            int missing = 0, below = 0;
            decimal total = 0;
            foreach (var building in matches)
            {
                var stored = building.Resources.Where(r => r.Id == rule.ResourceId && r.Status == 0).ToArray();
                if (stored.Any(r => !float.IsFinite(r.Amount) || r.Amount < 0 || r.Amount > 1_000_000_000f))
                    throw new InvalidDataException("A candidate contains an invalid resource amount; monitoring stopped.");
                decimal amount = stored.Sum(r => (decimal)r.Amount);
                total += amount;
                if (stored.Length == 0 && rule.Target > 0) missing++;
                if (amount < rule.Target) below++;
            }
            if (rule.Mode == MaintenanceMode.Across)
            {
                // Use the same preflight as the writer: a combined total alone
                // cannot show whether every building has an editable record.
                var across = AcrossRulePlanner.Create(rule, matches);
                if (across.Unsupported is not null)
                {
                    rows.Add(Row(matches.Length, total, "Not supported", across.Unsupported));
                    continue;
                }
                bool needsIncrease = total < rule.Target;
                rows.Add(Row(matches.Length, total, needsIncrease ? "Below target" : "At or above target",
                    "Across shares a combined shortfall between existing whole-unit records on supported buildings; other cases are skipped."));
            }
            else if (missing > 0)
                rows.Add(Row(matches.Length, total, "Missing stored record", ExistingRuleExecutor.SupportsInsertion(type, rule.ResourceId)
                    ? "Selected nonperishable resource: insertion requires an allocated spare slot and no conflicting record."
                    : $"{missing} candidate(s) have no stored record; insertion is not supported."));
            else if (below > 0)
                rows.Add(Row(matches.Length, total, "Below target", $"{below} candidate(s) are below target."));
            else
                rows.Add(Row(matches.Length, total, "At or above target", "Based on candidate snapshots; no edits were made."));
        }
        return new(DateTime.UtcNow, scan.ProcessId, scan.ProcessStartedUtc, scan.GameSha256, rows, CandidateIdentities(scan, settings))
        { CapacityRows = CapacityRulePlanner.Create(scan, settings).Rows };
    }

    // Await each scan and delay before starting the next: scans cannot overlap.
    // A new game process or executable ends the session instead of silently
    // applying a saved profile to another session.
    public static async Task RunAsync(Func<CancellationToken, Task<(DiscoveryResult Scan, AutomationProfileSettings Settings)>> read,
        Action<RuleMonitorReport> publish, Func<TimeSpan, CancellationToken, Task> delay, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(publish);
        ArgumentNullException.ThrowIfNull(delay);
        (int Id, DateTime Started, string Hash)? session = null;
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            var (scan, settings) = await read(cancellation).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            var current = (scan.ProcessId, scan.ProcessStartedUtc, scan.GameSha256);
            if (session is not null && session.Value != current)
                throw new InvalidOperationException("The game session or executable changed; monitoring stopped.");
            session = current;
            var report = Assess(scan, settings);
            cancellation.ThrowIfCancellationRequested();
            publish(report);
            await delay(TimeSpan.FromSeconds(settings.IntervalSeconds), cancellation).ConfigureAwait(false);
        }
    }
}
