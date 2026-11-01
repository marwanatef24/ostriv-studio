namespace OstrivStudio.Core;

// Only the one previously validated writer is exposed here. This does not schedule writes.
public static class SavedRulePlanner
{
    public static decimal CheckFishRule(ProfileDocument profile, DiscoveryResult? snapshot, IReadOnlyList<BuildingCandidate> buildings)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(buildings);
        if (snapshot is null || !snapshot.Complete || snapshot.UnreadableChunks != 0 || snapshot.GameSha256 != FishBulkEditor.SupportedHash || snapshot.CountOffset != 408 || snapshot.PointerOffset != 416)
            throw new InvalidOperationException("A complete scan of the tested game build is required.");
        if (!profile.AutomationProfiles.TryGetValue(profile.SelectedProfile, out var settings))
            throw new InvalidOperationException("The selected profile could not be found.");
        var matching = settings.MaintenanceRules.Where(r => r.ResourceId == 27 && r.Enabled && r.Target.HasValue &&
            (string.IsNullOrWhiteSpace(r.BuildingType) || string.Equals(r.BuildingType.Trim(), "building_fishery", StringComparison.OrdinalIgnoreCase))).ToArray();
        if (matching.Length == 0)
            throw new InvalidOperationException("Enable the fish rule and set a target in the selected profile first.");
        if (matching.Length != 1) throw new InvalidOperationException("More than one enabled fish rule matches these docks. Disable the conflicting rule first.");
        var rule = matching[0];
        if (rule.Mode != MaintenanceMode.Each || (rule.BuildingLimit >= 0 && buildings.Count > rule.BuildingLimit))
            throw new InvalidOperationException("This action requires an each-building rule whose limit covers the selected docks.");
        decimal target = rule.Target!.Value;
        if (buildings.Count is < 1 or > 2) throw new InvalidOperationException("Select one or two fishing docks.");
        var unique = new HashSet<ulong>();
        foreach (var building in buildings)
        {
            if (!snapshot.Buildings.Any(x => ReferenceEquals(x, building)) || !unique.Add(building.Address) ||
                building.ClassName != "building_fishery" ||
                (!string.IsNullOrWhiteSpace(rule.BuildingType) && !string.Equals(rule.BuildingType.Trim(), building.ClassName, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Every selected candidate must match the fish rule and be a distinct fishing dock from the current scan.");
            var fish = building.Resources.Where(r => r.Id == 27 && r.Status == 0).ToArray();
            if (fish.Length == 0 || fish.Any(r => !float.IsFinite(r.Amount) || r.Amount < 0 || r.Amount > 10000))
                throw new InvalidOperationException("Each dock must contain valid stored fish records.");
            var total = fish.Sum(r => (decimal)r.Amount);
            if (target != total + 1m) throw new InvalidOperationException("The saved target must be exactly one fish above the stored total for every selected dock.");
        }
        return target;
    }
}
