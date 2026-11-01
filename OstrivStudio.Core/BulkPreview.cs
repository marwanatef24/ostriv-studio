namespace OstrivStudio.Core;

// A planning aid only: addresses and plans are never persisted or written.
public sealed record BulkPreviewRow(string ClassName, ulong Address, double Stored, int StoredRecords, string Result);
public sealed record BulkPreviewResult(int ResourceId, float Target, IReadOnlyList<BulkPreviewRow> Rows);

public static class BulkPreview
{
    public static BulkPreviewResult Create(IEnumerable<BuildingCandidate> buildings, int resourceId, float target)
    {
        ArgumentNullException.ThrowIfNull(buildings);
        if (resourceId <= 0 || resourceId > 65535) throw new ArgumentOutOfRangeException(nameof(resourceId));
        if (!float.IsFinite(target) || target < 0 || target > 1_000_000_000) throw new ArgumentOutOfRangeException(nameof(target));
        var rows = new List<BulkPreviewRow>();
        var addresses = new HashSet<ulong>();
        foreach (var building in buildings)
        {
            if (!addresses.Add(building.Address)) throw new ArgumentException("Duplicate building in selection.", nameof(buildings));
            var stored = building.Resources.Where(r => r.Id == resourceId && r.Status == 0).ToArray();
            double total = stored.Sum(r => (double)r.Amount);
            string result = stored.Length switch
            {
                0 => "Not present: insertion is not verified",
                1 => "One stored record: still requires verification",
                _ => "Multiple stored records: distribution is not verified"
            };
            rows.Add(new(building.ClassName, building.Address, total, stored.Length, result));
        }
        return new(resourceId, target, rows);
    }
}
