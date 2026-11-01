using OstrivStudio.Core;

internal static class CropPresetStoreTests
{
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); Console.WriteLine("PASS " + message); }
    public static void Run(string directory)
    {
        Check(CropPresetCatalog.Defaults.Count == 25 && CropPresetCatalog.Defaults.Select(p => p.Name).Distinct().Count() == 25,
            "25 named built-in crop rotation suggestions are available");
        foreach (var p in CropPresetCatalog.Defaults)
            foreach (int current in p.Cycle.Distinct())
            {
                var rotated = new[] { current }.Concat(p.FutureFor(current)).Order().ToArray();
                if (!rotated.SequenceEqual(p.Cycle.Order())) throw new Exception("A built-in rotation lost a crop while aligning");
            }
        Console.WriteLine("PASS every built-in rotation preserves all crops when aligned to each possible current crop");
        try { CropPresetCatalog.Defaults[0].FutureFor(999); throw new Exception("Unknown current crop accepted"); }
        catch (InvalidOperationException) { Console.WriteLine("PASS a built-in rotation cannot silently discard an unmatched current crop"); }
        string path = Path.Combine(directory, "crop-presets.json");
        var empty = CropPresetStore.Load(path);
        var saved = CropPresetStore.Save(path, empty, [new("Winter plan", [6, 13, 8])]);
        var reopened = CropPresetStore.Load(path);
        Check(reopened.Presets.Single().FutureCrops.SequenceEqual([6, 13, 8]) && reopened.Hash == saved.Hash,
            "named crop presets survive reopening independently of automation profiles");
        try { CropPresetStore.Save(path, empty, []); throw new Exception("Stale writer accepted"); }
        catch (InvalidOperationException) { Check(CropPresetStore.Load(path).Hash == saved.Hash, "stale preset writer preserves the newer file"); }
        foreach (var invalid in new[] { new CropPreset("", [6]), new CropPreset("bad", [999]), new CropPreset("bad", []), new CropPreset("bad", [6, 6, 6, 6, 6, 6]) })
        {
            try { CropPresetStore.Save(path, saved, [invalid]); throw new Exception("Invalid preset accepted"); }
            catch (InvalidDataException) { }
        }
        Check(CropPresetStore.Load(path).Hash == saved.Hash, "invalid presets cannot replace valid saved plans");
        try { CropPresetStore.Save(path, saved, [new("Plan", [6]), new("plan", [8])]); throw new Exception("Duplicate preset accepted"); }
        catch (InvalidDataException) { Console.WriteLine("PASS crop preset names cannot collide ignoring case"); }
        var removed = CropPresetStore.Save(path, saved, []);
        Check(removed.Presets.Count == 0 && CropPresetStore.Load(path).Presets.Count == 0, "deleting a preset persists without changing game data");
    }
}
