using OstrivStudio.Core;

internal static class TradeAllocationPlannerTests
{
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); Console.WriteLine("PASS " + message); }
    public static void Run()
    {
        var buildings = new[] { new BuildingCandidate("building_trading_post", 0x10000, 1, 0, 0, [], "test"), new BuildingCandidate("building_trading_post", 0x20000, 1, 0, 0, [], "test") };
        TradeDeal[] deals = [new(2, 12, 0, 1, 100, 10.5f), new(2, 12, 0, 2, 11, 0), new(2, 46, 0, 3, 40, 0), new(2, 87, 1, 4, 500, 0)];
        var split = TradeAllocationPlanner.Create(deals, buildings, TradeAllocationMode.Split);
        Check(split.Count == 4 && split.Where(p => p.ResourceId == 12).Sum(p => p.Minimum) == 101 && split.Where(p => p.ResourceId == 46).Sum(p => p.Minimum) == 40,
            "export allocation combines repeated resources, rounds once and excludes imports");
        Check(split.Single(p => p.ResourceId == 12 && p.BuildingAddress == 0x10000).Minimum == 51 && split.Single(p => p.ResourceId == 12 && p.BuildingAddress == 0x20000).Minimum == 50,
            "split export allocation preserves remainder across distinct buildings");
        var assigned = TradeAllocationPlanner.Create(deals, buildings, TradeAllocationMode.Distribute);
        Check(assigned.Count == 2 && assigned[0].BuildingAddress == 0x10000 && assigned[1].BuildingAddress == 0x20000,
            "export resources can be assigned to buildings in turn");
        foreach (var invalid in new[] { deals.Select(d => d with { CityId = 99 }).ToArray(), deals.Select(d => d with { CityId = 4 }).ToArray(), new[] { deals[0], deals[0] } })
        {
            try { TradeAllocationPlanner.Create(invalid, buildings, TradeAllocationMode.Split); throw new Exception("Invalid allocation accepted"); }
            catch (InvalidOperationException) { }
        }
        Console.WriteLine("PASS export allocation rejects unknown routes, incompatible buildings and duplicate deals");
    }
}
