using OstrivStudio.Core;

internal static class CropPlanGeneratorTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); Console.WriteLine("PASS " + message); }
    private static FarmFieldSnapshot Field(params int[] crops) => new(1, new BuildingCandidate("building_grain_field", 0x20000, 1, 0, 0, [], "test"), 0x30000, crops);
    public static void Run()
    {
        var field = Field(6, 13, 13, 13, 13, 13);
        int[]? plan = CropPlanGenerator.Suggest(field, [7, 8], true);
        Check(plan is not null && plan.Length == 6 && plan[0] == 6 && plan.Contains(7) && plan.Contains(8), "crop generator keeps the current crop and includes every requested crop");
        Check(plan is not null && FarmCropEditor.Changed(field, plan), "generated plan passes the existing crop write guard");
        Check(plan is not null && CropPlanGenerator.Suggest(field, [7, 8], true)!.SequenceEqual(plan), "crop suggestions are deterministic");
        Check(CropPlanGenerator.Suggest(Field(6, 13), [6, 7], false) is null, "crop generator reports an impossible two-season request without dropping a crop");
        Check(CropPlanGenerator.Suggest(Field(6, 13), [6], false) is [6, 13], "crop generator rejects a repeatedly depleting all-wheat cycle");
        Check(CropPlanGenerator.Suggest(Field(6), [6], false) is null, "single-season field cannot generate a rotation");
        var barleyField = Field(87, 6, 7, 13, 13, 13);
        var existing = CropPlanGenerator.Suggest(barleyField, [87, 7], true);
        Check(existing is not null && existing.Contains(87) && existing.Count(c => c == 87) <= 1 && FarmCropEditor.Changed(barleyField, existing),
            "generator can reuse an existing unintroducible crop without increasing its count");
        Check(CropPlanGenerator.Suggest(Field(9, 10, 83, 87, 179, 181), [6, 7, 9, 10, 83, 87, 179], false) is null, "generator promptly rejects more requested crops than seasons");
        try { CropPlanGenerator.Suggest(field, [6, 6], false); throw new Exception("Duplicate crops accepted"); }
        catch (ArgumentException) { Console.WriteLine("PASS crop generator rejects duplicate crop selections"); }
        try { CropPlanGenerator.Suggest(field, [87], false); throw new Exception("Unsupported crop introduction accepted"); }
        catch (ArgumentException) { Console.WriteLine("PASS crop generator rejects unverified crop introductions"); }
    }
}
