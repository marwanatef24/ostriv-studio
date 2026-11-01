using System.Globalization;

namespace OstrivStudio.Core;

public enum ResourceScope { StorageBuildings, AllCandidates }

public sealed record ResourceLocation(int ResourceId, string BuildingType, ulong BuildingAddress,
    double Stored, double Reserved, double Awaited, double Other);

public sealed record ResourceInsight(int ResourceId, int Candidates, double Stored,
    double Reserved, double Awaited, double Other);

public sealed record ResourceInsightReport(IReadOnlyList<ResourceInsight> Resources,
    IReadOnlyList<ResourceLocation> Locations, int CandidateCount, int SharedArrays);

// A scan contains candidates rather than a verified live-building registry.
// These values describe the scan; they are not authoritative town totals.
public static class ResourceInsights
{
    private static readonly HashSet<string> StorageTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "building_granary", "building_granary_small", "building_warehouse", "building_camp_storage",
        "building_stone_storage", "building_hay_barrack", "building_hay_barrack_big"
    };

    public static bool IsStorage(string name) => StorageTypes.Contains(name);
    public static bool Matches(int id, string name, string? query)
    {
        string text = query?.Trim() ?? "";
        if (text.Length == 0) return true;
        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int searchId)
            ? id == searchId : name.Contains(text, StringComparison.OrdinalIgnoreCase);
    }

    public static ResourceInsightReport Create(DiscoveryResult scan, ResourceScope scope)
    {
        ArgumentNullException.ThrowIfNull(scan);
        if (!Enum.IsDefined(scope)) throw new ArgumentOutOfRangeException(nameof(scope));
        if (scan.Buildings.Count > 10000 || scan.Buildings.Select(b => b.Address).Distinct().Count() != scan.Buildings.Count)
            throw new InvalidDataException("The scan contains duplicate building addresses.");
        var buildings = scan.Buildings.Where(b => scope == ResourceScope.AllCandidates || IsStorage(b.ClassName)).ToArray();
        int shared = buildings.Where(b => b.ResourceCount > 0 && b.ResourcePointer != 0)
            .GroupBy(b => b.ResourcePointer).Sum(g => Math.Max(0, g.Count() - 1));
        var locations = new List<ResourceLocation>();
        foreach (var building in buildings)
        {
            if (building.ResourceCount != building.Resources.Count || building.Resources.Any(r => r.Id is <= 0 or > 65535 ||
                !float.IsFinite(r.Amount) || r.Amount < 0 || r.Amount > 1_000_000_000f))
                throw new InvalidDataException("A building's resource snapshot is invalid.");
            foreach (var total in BuildingDiscovery.Summarize(building.Resources))
                locations.Add(new(total.Id, building.ClassName, building.Address, total.Stored, total.Reserved, total.Awaited, total.Other));
        }
        var resources = locations.GroupBy(x => x.ResourceId).Select(g => new ResourceInsight(g.Key, g.Count(),
            g.Sum(x => x.Stored), g.Sum(x => x.Reserved), g.Sum(x => x.Awaited), g.Sum(x => x.Other)))
            .OrderBy(x => x.ResourceId).ToArray();
        return new(resources, locations.AsReadOnly(), buildings.Length, shared);
    }
}

public sealed record ResourceSample(DateTime ObservedUtc, double Stored);

// History is session-only, built from completed manual scans; no game addresses
// or samples are written to a profile or reused across processes.
public sealed class ResourceSnapshotHistory
{
    private const int Limit = 60;
    private sealed record Snapshot(DateTime When, Dictionary<int, double> Storage, Dictionary<int, double> All);
    private readonly List<Snapshot> _samples = [];
    private (int Id, DateTime Started, string Hash)? _session;
    public int Count => _samples.Count;
    public bool MatchesSession(DiscoveryResult scan) => _session == (scan.ProcessId, scan.ProcessStartedUtc, scan.GameSha256);
    public void Clear() { _samples.Clear(); _session = null; }
    public bool Add(DiscoveryResult scan, DateTime observedUtc)
    {
        if (!scan.Complete || scan.UnreadableChunks != 0) return false;
        var session = (scan.ProcessId, scan.ProcessStartedUtc, scan.GameSha256);
        // Prepare before changing stored history, so a bad scan cannot erase it.
        var storage = ResourceInsights.Create(scan, ResourceScope.StorageBuildings);
        var all = ResourceInsights.Create(scan, ResourceScope.AllCandidates);
        if (storage.Resources.Count > 4096 || all.Resources.Count > 4096) throw new InvalidDataException("Too many resource types to record history.");
        if (_session is not null && _session.Value != session) _samples.Clear();
        _session = session;
        _samples.Add(new(observedUtc, storage.Resources.ToDictionary(r => r.ResourceId, r => r.Stored),
            all.Resources.ToDictionary(r => r.ResourceId, r => r.Stored)));
        if (_samples.Count > Limit) _samples.RemoveAt(0);
        return true;
    }
    public IReadOnlyList<ResourceSample> Series(int id, ResourceScope scope)
    {
        if (!Enum.IsDefined(scope)) throw new ArgumentOutOfRangeException(nameof(scope));
        return _samples.Select(s => new ResourceSample(s.When,
            (scope == ResourceScope.StorageBuildings ? s.Storage : s.All).GetValueOrDefault(id))).ToArray();
    }
}
