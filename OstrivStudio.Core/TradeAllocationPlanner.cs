namespace OstrivStudio.Core;

public enum TradeAllocationMode { Split = 0, Distribute = 1 }
public sealed record ExportAllocation(ulong BuildingAddress, int ResourceId, decimal Minimum);

public static class TradeAllocationPlanner
{
    public static IReadOnlyList<ExportAllocation> Create(IReadOnlyList<TradeDeal> deals, IReadOnlyList<BuildingCandidate> buildings, TradeAllocationMode mode)
    {
        if (!Enum.IsDefined(mode) || buildings.Count is < 1 or > 64 || buildings.Select(b => b.Address).Distinct().Count() != buildings.Count || deals.Count is < 1 or > 128 ||
            deals.Distinct().Count() != deals.Count) throw new InvalidOperationException("Choose distinct trading buildings and reviewed deals.");
        var exports = deals.Where(d => !d.IsImport && d.Remaining > 0).ToArray();
        if (exports.Length == 0) throw new InvalidOperationException("No remaining export deals are available.");
        if (exports.Any(d => d.TradingBuildingClass is null || d.ResourceId is <= 0 or > 65535 || !float.IsFinite(d.Completed) || d.Completed < 0 || d.Total < d.Completed || d.Remaining > 1_000_000 ||
            buildings.Any(b => b.ClassName != d.TradingBuildingClass)))
            throw new InvalidOperationException("The selected buildings must match every export deal's route.");
        var totals = exports.GroupBy(d => d.ResourceId).OrderBy(g => g.Key)
            .Select(g => (Resource: g.Key, Amount: (long)Math.Ceiling(g.Sum(d => d.Remaining)))).ToArray();
        var result = new List<ExportAllocation>();
        for (int i = 0; i < totals.Length; i++)
        {
            var item = totals[i];
            if (item.Amount > 1_000_000) throw new InvalidOperationException("The combined export quantity exceeds the supported maximum.");
            if (mode == TradeAllocationMode.Distribute)
                result.Add(new(buildings[i % buildings.Count].Address, item.Resource, item.Amount));
            else
                for (int b = 0; b < buildings.Count; b++)
                {
                    long amount = item.Amount / buildings.Count + (b < item.Amount % buildings.Count ? 1 : 0);
                    if (amount > 0) result.Add(new(buildings[b].Address, item.Resource, amount));
                }
        }
        return result;
    }
}
