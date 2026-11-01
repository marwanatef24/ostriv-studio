namespace OstrivStudio.Core;

public sealed record MaintenancePreviewRow(int ResourceId, string BuildingType, ulong Address, decimal Current, decimal Target, decimal Increase, string Assessment);
public sealed record MaintenancePreviewResult(IReadOnlyList<MaintenancePreviewRow> Rows, IReadOnlyList<string> Notices);

// Read-only illustration of enabled rules. A candidate is not proof of a live
// building, resource compatibility, capacity, or a safe write.
public static class MaintenancePreview
{
    public static MaintenancePreviewResult Create(DiscoveryResult snapshot, AutomationProfileSettings settings)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(settings);
        if (!snapshot.Complete || snapshot.UnreadableChunks != 0) throw new InvalidOperationException("Complete a full building scan before previewing rules.");
        if (snapshot.Buildings.Count > 10000 || snapshot.Buildings.Select(x => x.Address).Distinct().Count() != snapshot.Buildings.Count)
            throw new InvalidOperationException("The scan contains too many or duplicate candidate buildings.");
        var rows = new List<MaintenancePreviewRow>();
        var notices = new List<string>();
        bool truncated = false;
        foreach (var rule in settings.MaintenanceRules.Where(r => r.Enabled))
        {
            if (rule.Target is null) { notices.Add($"Resource {rule.ResourceId}: no target is set."); continue; }
            if (rule.Target is < 0 or > 1000000000m || rule.BuildingLimit is < -1 or > 100000 || !Enum.IsDefined(rule.Mode))
                throw new InvalidDataException("A saved rule is invalid.");
            if (rule.BuildingLimit == 0) { notices.Add($"Resource {rule.ResourceId}: the rule is limited to zero buildings."); continue; }
            var matches = snapshot.Buildings
                .Where(b => string.IsNullOrWhiteSpace(rule.BuildingType) || string.Equals(b.ClassName, rule.BuildingType.Trim(), StringComparison.OrdinalIgnoreCase))
                .Select(b => (Building: b, Stored: b.Resources.Where(r => r.Id == rule.ResourceId && r.Status == 0).ToArray()))
                .Select(x => (x.Building, x.Stored, Total: x.Stored.Sum(r => (double)r.Amount)))
                .OrderByDescending(x => x.Total).ThenBy(x => x.Building.Address)
                .Take(rule.BuildingLimit < 0 ? int.MaxValue : rule.BuildingLimit).ToArray();
            if (matches.Length == 0) { notices.Add($"Resource {rule.ResourceId}: no candidate matches {rule.BuildingType}."); continue; }
            var across = rule.Mode == MaintenanceMode.Across ? AcrossRulePlanner.Create(rule, matches.Select(x => x.Building).ToArray()) : null;
            if (across?.Unsupported is not null)
                notices.Add($"Resource {rule.ResourceId}: across automation will skip this rule because {across.Unsupported}.");
            var acrossChanges = across?.Targets.ToDictionary(x => x.Building.Address, x => x.Increase);
            foreach (var match in matches)
            {
                if (rows.Count >= 20000) { truncated = true; break; }
                if (!double.IsFinite(match.Total) || match.Total < 0 || match.Total > 1000000000d)
                { notices.Add($"Resource {rule.ResourceId}: a candidate has an invalid stored amount."); continue; }
                decimal current = (decimal)match.Total;
                decimal target = rule.Mode == MaintenanceMode.Each ? rule.Target.Value :
                    across?.Unsupported is null ? current + acrossChanges!.GetValueOrDefault(match.Building.Address) : rule.Target.Value / matches.Length;
                decimal increase = Math.Max(0, target - current);
                string assessment = match.Stored.Length == 0 ? "Not present; insertion unverified" :
                    increase == 0 ? "At or above target" : "Increase shown; write unverified";
                rows.Add(new(rule.ResourceId, match.Building.ClassName, match.Building.Address, current, target, increase, assessment));
            }
            if (truncated) break;
        }
        var conflicts = rows.GroupBy(r => (r.ResourceId, r.Address)).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        if (conflicts.Count != 0)
        {
            for (int i = 0; i < rows.Count; i++) if (conflicts.Contains((rows[i].ResourceId, rows[i].Address)))
                rows[i] = rows[i] with { Assessment = "Conflicting rules; resolve first" };
            notices.Add("Some rules overlap the same resource and candidate.");
        }
        if (truncated) notices.Add("Preview stopped at 20,000 rows; not all matches are shown.");
        if (!settings.MaintenanceRules.Any(r => r.Enabled)) notices.Add("Enable at least one rule in the selected profile.");
        return new(rows, notices);
    }
}
