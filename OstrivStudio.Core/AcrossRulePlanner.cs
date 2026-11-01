namespace OstrivStudio.Core;

// Conservative across-building maintenance for existing whole-unit records.
// A combined shortfall is shared across candidates; no amount is removed and
// the planned combined increase is exactly the shortfall.
public static class AcrossRulePlanner
{
    private const int LargestExactInteger = 16_777_216;
    public sealed record Target(BuildingCandidate Building, ResourceObservation Record, float RecordTarget, decimal Stored, decimal Increase);
    public sealed record Result(IReadOnlyList<Target> Targets, string? Unsupported);

    public static Result Create(MaintenanceRule rule, IReadOnlyList<BuildingCandidate> buildings)
    {
        if (rule.Target is null || rule.Target < 0 || rule.Target > LargestExactInteger || decimal.Truncate(rule.Target.Value) != rule.Target)
            return new([], "across targets must be whole units no greater than 16,777,216");
        if (buildings.Count == 0) return new([], "no matching building candidates");
        if (buildings.Select(b => b.Address).Distinct().Count() != buildings.Count)
            return new([], "duplicate building candidates");
        var rows = new List<(BuildingCandidate Building, ResourceObservation First, decimal Total)>();
        decimal combined = 0;
        foreach (var building in buildings.OrderBy(b => b.Address))
        {
            if (building.ResourceCount != building.Resources.Count || building.ResourceCount is <= 0 or > 511 || building.ResourcePointer == 0 ||
                building.Resources.Where((r, i) => r.Index != i || !float.IsFinite(r.Amount) || r.Amount < 0 || r.Amount > 1_000_000_000f).Any())
                return new([], "a candidate has an invalid resource layout");
            var stored = building.Resources.Where(r => r.Id == rule.ResourceId && r.Status == 0).ToArray();
            if (stored.Length == 0) return new([], "across rules require an existing stored record in every candidate");
            if (stored.Any(r => r.Amount != MathF.Truncate(r.Amount)))
                return new([], "across rules currently require whole-unit stored records");
            decimal total = stored.Sum(r => (decimal)r.Amount);
            combined += total;
            rows.Add((building, stored[0], total));
        }
        if (combined >= rule.Target.Value) return new([], null);
        int shortfall = checked((int)(rule.Target.Value - combined));
        int each = shortfall / rows.Count, remainder = shortfall % rows.Count;
        var targets = new List<Target>();
        for (int i = 0; i < rows.Count; i++)
        {
            int increase = each + (i < remainder ? 1 : 0);
            if (increase == 0) continue;
            decimal recordTarget = (decimal)rows[i].First.Amount + increase;
            if (recordTarget > LargestExactInteger || (decimal)(float)recordTarget != recordTarget)
                return new([], "an across target cannot be represented exactly");
            targets.Add(new(rows[i].Building, rows[i].First, (float)recordTarget, rows[i].Total, increase));
        }
        return new(targets.AsReadOnly(), null);
    }
}
