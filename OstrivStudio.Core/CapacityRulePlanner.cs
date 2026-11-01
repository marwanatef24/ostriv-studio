namespace OstrivStudio.Core;

public sealed record CapacityRuleRow(int RuleNumber, string BuildingType, decimal Target, int Candidates,
    int NeedsIncrease, string State, string Detail);
public sealed record CapacityRuleGroup(int RuleNumber, decimal Target, IReadOnlyList<BuildingCandidate> Buildings);
public sealed record CapacityRulePlan(IReadOnlyList<CapacityRuleRow> Rows, IReadOnlyList<CapacityRuleGroup> Groups);

// Capacity rules are minimums, never reductions. Matching objects remain
// session-only candidates and must be reviewed before enabling writes.
public static class CapacityRulePlanner
{
    public static CapacityRulePlan Create(DiscoveryResult scan, AutomationProfileSettings settings, bool validateWrites = false)
    {
        ArgumentNullException.ThrowIfNull(scan);
        ArgumentNullException.ThrowIfNull(settings);
        if (!scan.Complete || scan.UnreadableChunks != 0 || scan.Buildings.Select(b => b.Address).Distinct().Count() != scan.Buildings.Count)
            throw new InvalidOperationException("The building scan is incomplete or ambiguous.");
        if (settings.CapacityRules is null || settings.CapacityRules.Count > 10000)
            throw new InvalidDataException("The saved capacity rules are invalid.");
        var rows = new List<CapacityRuleRow>();
        var groups = new List<CapacityRuleGroup>();
        for (int i = 0; i < settings.CapacityRules.Count; i++)
        {
            var rule = settings.CapacityRules[i] ?? throw new InvalidDataException("A capacity rule is missing.");
            string type = rule.BuildingType?.Trim() ?? "";
            void Add(int candidates, int needs, string state, string detail) =>
                rows.Add(new(i + 1, type, rule.Capacity, candidates, needs, state, detail));
            if (!rule.Enabled) { Add(0, 0, "Disabled", "This capacity rule is switched off."); continue; }
            if (!CapacityObservation.SupportsClass(type)) { Add(0, 0, "Unsupported", "Only granary, warehouse and big hay barrack are verified."); continue; }
            if (settings.CapacityRules.Where((_, j) => j != i).Any(other => other is not null && other.Enabled &&
                string.Equals(other.BuildingType?.Trim(), type, StringComparison.OrdinalIgnoreCase)))
            { Add(0, 0, "Conflicting rules", "More than one enabled rule targets this building type."); continue; }
            if (rule.Capacity is <= 0 or > 1_000_000_000m || (decimal)(float)rule.Capacity != rule.Capacity)
            { Add(0, 0, "Invalid target", "Use a positive exactly representable capacity."); continue; }
            var matches = scan.Buildings.Where(b => string.Equals(b.ClassName, type, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length == 0) { Add(0, 0, "No candidates", "No matching building candidate was found."); continue; }
            if (matches.Length > 128 || matches.Any(b => b.ObservedCapacity is not float f || !float.IsFinite(f) || f <= 0))
            { Add(matches.Length, 0, "Unavailable", "A capacity observation is missing or there are more than 128 candidates."); continue; }
            var changes = matches.Where(b => (decimal)b.ObservedCapacity!.Value < rule.Capacity).ToArray();
            if (changes.Length == 0) { Add(matches.Length, 0, "At or above target", "Existing capacities are never reduced."); continue; }
            if (validateWrites) _ = CapacityEditor.Prepare(scan, changes, rule.Capacity);
            groups.Add(new(i + 1, rule.Capacity, changes));
            Add(matches.Length, changes.Length, "Below target", $"{changes.Length} candidate(s) can be increased while the monitor runs.");
        }
        return new(rows.AsReadOnly(), groups.AsReadOnly());
    }

    public static int ApplyToGame(CapacityRulePlan plan, DiscoveryResult scan, CancellationToken cancellation, Action? beforeGroup = null)
    {
        int applied = 0;
        try
        {
            foreach (var group in plan.Groups)
            {
                cancellation.ThrowIfCancellationRequested();
                beforeGroup?.Invoke();
                applied += CapacityEditor.ApplyToGame(scan, group.Buildings, group.Target, cancellation);
            }
            return applied;
        }
        catch (PartialGameWriteException) { throw; }
        catch (Exception ex) when (applied > 0)
        { throw new PartialGameWriteException("Some capacity rules were applied before automation stopped. Inspect the game and reload the backup if uncertain. " + ex.Message, ex); }
    }
}
