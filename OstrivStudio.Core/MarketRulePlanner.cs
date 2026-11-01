namespace OstrivStudio.Core;

// The shop presets only create missing rules. Existing rule settings are never replaced.
public static class MarketRulePlanner
{
    public static int[] MissingShopRules(IEnumerable<MaintenanceRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var existing = rules.Where(r => r.BuildingType.Trim().Equals("building_rowhouse_shop", StringComparison.OrdinalIgnoreCase))
            .Select(r => r.ResourceId).ToHashSet();
        return new[] { 7, 46, 47, 93 }.Where(id => !existing.Contains(id)).ToArray();
    }
}
