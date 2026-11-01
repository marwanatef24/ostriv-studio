using OstrivStudio.Core;

internal static class MaintenancePreviewTests
{
    private static void Check(bool value, string label)
    {
        if (!value) throw new Exception(label);
        Console.WriteLine("PASS " + label);
    }
    public static void Run()
    {
        var camp1 = new BuildingCandidate("building_camp_storage", 0x1000, 1, 2, 1, [new(0, 27, 50, 0, 0, 0)], "candidate");
        var camp2 = new BuildingCandidate("building_camp_storage", 0x2000, 1, 2, 1, [new(0, 27, 100, 0, 0, 0)], "candidate");
        var granary = new BuildingCandidate("building_granary", 0x3000, 1, 2, 1, [new(0, 27, 20, 256, 0, 0)], "candidate");
        var scan = new DiscoveryResult(1, DateTime.UtcNow, "test", "hash", 0, 0, [camp1, camp2, granary], 1, true, 0, "");
        var settings = new AutomationProfileSettings { MaintenanceRules = [
            new() { ResourceId = 27, Target = 120, Enabled = true, BuildingType = "building_camp_storage", BuildingLimit = 2 },
            new() { ResourceId = 27, Target = 100, Enabled = false, BuildingType = "building_granary" }
        ] };
        var result = MaintenancePreview.Create(scan, settings);
        Check(result.Rows.Count == 2 && result.Rows.All(x => x.BuildingType == "building_camp_storage") && result.Rows.Single(x => x.Address == 0x1000).Increase == 70 &&
              result.Rows.Single(x => x.Address == 0x2000).Increase == 20, "each-building preview respects scope, amount and disabled rules");
        settings.MaintenanceRules[0].Mode = MaintenanceMode.Across;
        settings.MaintenanceRules[0].Target = 180;
        result = MaintenancePreview.Create(scan, settings);
        Check(result.Rows.Count == 2 && result.Rows.Single(x => x.Address == 0x2000).Increase == 15 &&
              result.Rows.Single(x => x.Address == 0x1000).Increase == 15 && result.Rows.Sum(x => x.Increase) == 30,
              "across preview shares only the combined shortfall and never plans reductions");
        settings.MaintenanceRules[0].BuildingLimit = 1;
        result = MaintenancePreview.Create(scan, settings);
        Check(result.Rows.Count == 1 && result.Rows[0].Address == 0x2000 && result.Rows[0].Target == 180, "preview observes the building limit deterministically");
        settings.MaintenanceRules.Add(new() { ResourceId = 27, Target = 40, Enabled = true, BuildingType = "building_granary" });
        result = MaintenancePreview.Create(scan, settings);
        Check(result.Rows.Single(x => x.Address == 0x3000).Assessment.Contains("Not present"), "preview does not mistake awaited stock for stored resource");
        settings.MaintenanceRules.Add(new() { ResourceId = 27, Target = 150, Enabled = true, BuildingType = "" });
        result = MaintenancePreview.Create(scan, settings);
        Check(result.Rows.Count(x => x.Assessment.Contains("Conflicting")) >= 2 && result.Notices.Any(x => x.Contains("overlap")), "overlapping rules are flagged instead of presented as independent writes");
        try { MaintenancePreview.Create(scan with { Complete = false }, settings); throw new Exception("Incomplete scan accepted"); }
        catch (InvalidOperationException) { Console.WriteLine("PASS preview rejects an incomplete scan"); }
        settings.MaintenanceRules.Clear();
        settings.MaintenanceRules.Add(new() { ResourceId = 27, Target = 100, Enabled = true, BuildingLimit = 0 });
        result = MaintenancePreview.Create(scan, settings);
        Check(result.Rows.Count == 0 && result.Notices.Any(x => x.Contains("zero buildings")), "zero-building rule is preserved but produces no preview actions");
    }
}
