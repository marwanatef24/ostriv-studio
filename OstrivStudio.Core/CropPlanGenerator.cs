namespace OstrivStudio.Core;

// A planning model informed by the original editor. It does not read actual
// field fertility and does not establish the game's current nutrient rules.
public static class CropPlanGenerator
{
    private const int Fallow = 13;
    private static readonly Dictionary<int, (int Red, int Green, int Blue)> Effects = new()
    {
        [6] = (-50, -60, 0), [7] = (0, -20, -60), [8] = (-50, -30, 0),
        [9] = (-10, 0, -60), [10] = (-100, -60, -30), [13] = (95, 95, 95),
        [83] = (-5, 0, -80), [87] = (-50, -50, 0),
        [179] = (-55, -60, 0), [181] = (-55, -50, 0)
    };
    private static readonly HashSet<int> Introducible = [6, 7, 8];

    // Requires every requested crop; never silently drops one. The current crop
    // and allocation length are fixed, and Changed provides the final guard.
    public static int[]? Suggest(FarmFieldSnapshot field, IReadOnlyList<int> crops, bool assumePlough)
    {
        if (crops.Count is < 1 or > 9 || crops.Distinct().Count() != crops.Count ||
            crops.Any(c => c == Fallow || !Effects.ContainsKey(c) || (!Introducible.Contains(c) && !field.Crops.Contains(c))))
            throw new ArgumentException("Select one or more distinct available crops.", nameof(crops));
        if (field.Crops.Count is < 2 or > 6 || !Effects.ContainsKey(field.Crops[0])) return null;
        if (crops.Count > field.Crops.Count) return null;
        var choices = crops.Append(Fallow).ToArray();
        int[] candidate = new int[field.Crops.Count]; candidate[0] = field.Crops[0];
        int[]? best = null; int bestHarvest = -1, bestAlternations = -1;
        void Search(int index)
        {
            if (index < candidate.Length)
            {
                foreach (int crop in choices) { candidate[index] = crop; Search(index + 1); }
                return;
            }
            if (crops.Any(c => !candidate.Contains(c))) return;
            try { _ = FarmCropEditor.Changed(field, candidate); }
            catch (InvalidOperationException) { return; }
            if (!RepeatsWithoutDepletion(candidate, assumePlough)) return;
            int harvest = candidate.Count(c => c != Fallow);
            int alternations = Enumerable.Range(1, candidate.Length - 1).Count(i => candidate[i] != candidate[i - 1]);
            if (harvest > bestHarvest || (harvest == bestHarvest && alternations > bestAlternations))
            { best = candidate.ToArray(); bestHarvest = harvest; bestAlternations = alternations; }
        }
        Search(1);
        return best;
    }

    private static bool RepeatsWithoutDepletion(IReadOnlyList<int> plan, bool plough)
    {
        int red = 100, green = 100, blue = 100;
        var boundaries = new HashSet<(int, int, int)> { (red, green, blue) };
        for (int cycle = 0; cycle < 256; cycle++)
        {
            foreach (int crop in plan)
            {
                if (plough) { red = Math.Min(100, red + 15); green = Math.Min(100, green + 15); blue = Math.Min(100, blue + 15); }
                var effect = Effects[crop];
                red = Math.Min(100, red + effect.Red); green = Math.Min(100, green + effect.Green); blue = Math.Min(100, blue + effect.Blue);
                if (red < 0 || green < 0 || blue < 0) return false;
            }
            if (!boundaries.Add((red, green, blue))) return true;
        }
        return false; // An unproven cycle is not presented as a modeled match.
    }
}
