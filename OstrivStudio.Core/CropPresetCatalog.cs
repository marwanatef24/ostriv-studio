namespace OstrivStudio.Core;

public sealed record CropRotationTemplate(string Name, IReadOnlyList<int> Cycle)
{
    // Rotate a whole-cycle suggestion around the field's current crop. Never
    // discard a crop silently or change the live rotation's allocation length.
    public int[] FutureFor(int currentCrop)
    {
        int start = Cycle.ToList().IndexOf(currentCrop);
        if (start < 0) throw new InvalidOperationException("This rotation does not contain the field's current crop.");
        return Enumerable.Range(1, Cycle.Count - 1).Select(i => Cycle[(start + i) % Cycle.Count]).ToArray();
    }
}

public static class CropPresetCatalog
{
    // Match a full rotation at any cyclic starting point. This compares crop
    // order only; it does not inspect ploughing or actual field fertility.
    public static IReadOnlyList<CropRotationTemplate> MatchPatterns(IReadOnlyList<int> rotation)
    {
        ArgumentNullException.ThrowIfNull(rotation);
        if (rotation.Count == 0) return Array.Empty<CropRotationTemplate>();
        return Array.AsReadOnly(Defaults.Where(template => template.Cycle.Count == rotation.Count &&
            Enumerable.Range(0, rotation.Count).Any(start =>
                Enumerable.Range(0, rotation.Count).All(i => template.Cycle[(start + i) % rotation.Count] == rotation[i])))
            .ToArray());
    }

    // Crop sequences are suggestions; the guarded field writer independently
    // checks length, current crop and supported introductions before accepting one.
    public static IReadOnlyList<CropRotationTemplate> Defaults { get; } = Array.AsReadOnly(new[]
    {
        Make("Potato ×3, buckwheat ×2", 8,7,8,7,8,13),
        Make("Potato ×2, buckwheat ×2, wheat", 8,7,8,7,6,13),
        Make("Potato ×2, buckwheat ×2, barley", 8,7,8,7,87,13),
        Make("Potato ×2, buckwheat, hemp, wheat", 8,7,8,9,6,13),
        Make("Potato ×2, buckwheat, hemp, barley", 8,7,8,9,87,13),
        Make("Buckwheat ×2, wheat", 7,6,7,13),
        Make("Buckwheat, wheat, hemp", 7,6,9,13),
        Make("Buckwheat, barley, hemp", 7,87,9,13),
        Make("Buckwheat, potato, wheat, flax", 7,8,6,83,13),
        Make("Buckwheat, potato, barley, flax", 7,8,87,83,13),
        Make("Buckwheat ×2, barley ×2", 7,87,7,87,13),
        Make("Sunflower", 10,13), Make("Sunflower, buckwheat", 10,7,13),
        Make("Sunflower, hemp", 10,9,13), Make("Sunflower, flax", 10,83,13),
        Make("Wheat ×2, flax", 6,83,6,13), Make("Wheat ×2, hemp ×2", 6,9,6,9,13),
        Make("Wheat, barley, flax", 6,87,83,13), Make("Wheat, barley, hemp", 6,87,9,13),
        Make("Barley ×2, flax", 87,83,87,13), Make("Barley ×2, hemp ×2", 87,9,87,9,13),
        Make("Hemp, wheat, barley, flax", 9,6,87,83,13),
        Make("Flax, wheat, barley, buckwheat", 83,6,87,7,13),
        Make("Hemp, wheat, barley, buckwheat", 9,6,87,7,13),
        Make("Wheat, hemp, barley, buckwheat, potato", 6,9,87,7,8,13)
    });
    private static CropRotationTemplate Make(string name, params int[] cycle) => new(name, Array.AsReadOnly(cycle));
}
