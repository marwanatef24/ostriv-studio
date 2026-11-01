using OstrivStudio.Core;

internal static class ResourceInsightsTests
{
    private static void Check(bool value, string text) { if (!value) throw new Exception(text); Console.WriteLine("PASS " + text); }
    public static void Run()
    {
        DateTime started = DateTime.UtcNow;
        var granary = new BuildingCandidate("building_granary", 0x1000, 1, 0x3000, 4,
            [new(0,6,10,0,0,0), new(1,6,3,0,0,0), new(2,6,2,1,0,0), new(3,27,4,256,0,0)], "candidate");
        var camp = new BuildingCandidate("building_camp_storage", 0x2000, 1, 0x4000, 2,
            [new(0,6,20,0,0,0), new(1,27,1,42,0,0)], "candidate");
        var fishery = new BuildingCandidate("building_fishery", 0x3000, 1, 0x5000, 1, [new(0,6,100,0,0,0)], "candidate");
        var scan = new DiscoveryResult(10, started, "test", "hash", 408, 416, [granary, camp, fishery], 1, true, 0, "");
        var storage = ResourceInsights.Create(scan, ResourceScope.StorageBuildings);
        var wheat = storage.Resources.Single(x => x.ResourceId == 6);
        Check(storage.CandidateCount == 2 && wheat.Candidates == 2 && wheat.Stored == 33 && wheat.Reserved == 2,
            "resource overview totals split records per candidate and separates reserved stock");
        var fish = storage.Resources.Single(x => x.ResourceId == 27);
        Check(fish.Awaited == 4 && fish.Other == 1 && fish.Stored == 0, "overview separates awaited and unknown status stock");
        Check(ResourceInsights.Create(scan, ResourceScope.AllCandidates).Resources.Single(x => x.ResourceId == 6).Stored == 133,
            "all-candidate scope includes non-storage buildings");
        Check(ResourceInsights.Matches(27, "Fish", "27") && !ResourceInsights.Matches(127, "Fish oil", "27") &&
            ResourceInsights.Matches(27, "Fish", " FIS "), "resource search matches IDs exactly and names case-insensitively");
        var alias = fishery with { ResourcePointer = camp.ResourcePointer };
        Check(ResourceInsights.Create(scan with { Buildings = [camp, alias] }, ResourceScope.AllCandidates).SharedArrays == 1,
            "overview warns about shared resource arrays");
        try { ResourceInsights.Create(scan with { Buildings = [camp, camp] }, ResourceScope.AllCandidates); throw new Exception("Duplicate candidate accepted"); }
        catch (InvalidDataException) { Console.WriteLine("PASS overview rejects duplicate building addresses"); }
        var history = new ResourceSnapshotHistory();
        Check(history.Add(scan, started) && !history.Add(scan with { Complete = false }, started.AddSeconds(1)) && history.Count == 1,
            "history accepts complete scans and ignores partial scans");
        var next = scan with { Buildings = [granary with { Resources = [new(0,6,5,0,0,0), new(1,6,3,0,0,0), new(2,6,2,1,0,0), new(3,27,4,256,0,0)] }, camp, fishery] };
        history.Add(next, started.AddSeconds(10));
        Check(history.Series(6, ResourceScope.StorageBuildings).Select(s => s.Stored).SequenceEqual([33d,28d]) &&
            history.Series(27, ResourceScope.StorageBuildings).All(s => s.Stored == 0),
            "history uses recorded stored amounts in the selected scope");
        var newSession = scan with { ProcessId = 11 };
        Check(!history.MatchesSession(newSession), "history does not attach old samples to a different game session");
        history.Add(newSession, started.AddSeconds(20));
        Check(history.Count == 1 && history.MatchesSession(newSession), "a new game process resets in-memory history");
        for (int i = 0; i < 70; i++) history.Add(newSession, started.AddSeconds(21 + i));
        Check(history.Count == 60, "resource history is bounded to 60 completed scans");
        history.Clear();
        Check(history.Count == 0 && !history.MatchesSession(newSession), "clearing history removes samples and session association");
        string csv = ResourceLedgerCsv.Create([new("All candidates", new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc), false, 1,
            "=wheat, \"premium\"", 6, "Granary · 1", 1.25, 2, 3, 4)]);
        Check(csv.Contains("\"'=wheat, \"\"premium\"\"\"") && csv.Contains(",No,1,") &&
            csv.Contains(",6,\"Granary · 1\",1.25,2,3,4\r\n"),
            "ledger export escapes spreadsheet formulas, quotes and decimal values and marks partial scans");
    }
}
