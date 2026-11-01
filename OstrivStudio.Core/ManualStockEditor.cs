using System.Diagnostics;
using System.Security.Cryptography;

namespace OstrivStudio.Core;

// One-time addition to existing stored records. It never creates a profile
// rule or inserts a missing resource and keeps the full scan for overlap checks.
public static class ManualStockEditor
{
    private static bool Overlap(ulong a, ulong b, ulong c, ulong d) => a < d && c < b;

    public static ExistingRuleExecutor.Plan Prepare(DiscoveryResult scan, IReadOnlyList<BuildingCandidate> selected, int resourceId, decimal amount)
    {
        if (!scan.Complete || scan.UnreadableChunks != 0 || scan.GameSha256 != FishBulkEditor.SupportedHash || scan.CountOffset != 408 || scan.PointerOffset != 416 ||
            scan.Buildings.Select(b => b.Address).Distinct().Count() != scan.Buildings.Count)
            throw new InvalidOperationException("A complete scan of the tested game build is required.");
        if (selected.Count is < 1 or > 128 || selected.Select(b => b.Address).Distinct().Count() != selected.Count ||
            resourceId is <= 0 or > 65535 || amount is <= 0 or > 1_000_000_000m || decimal.Truncate(amount) != amount)
            throw new InvalidOperationException("Select distinct buildings and enter a positive whole-unit amount.");
        var entries = new List<ExistingRuleExecutor.Entry>();
        foreach (var b in selected)
        {
            if (!scan.Buildings.Any(x => ReferenceEquals(x, b)) || !ExistingRuleExecutor.SupportsExisting(b.ClassName, resourceId) ||
                b.ResourceCount != b.Resources.Count || b.ResourceCount is <= 0 or > 511 || b.ResourcePointer == 0 ||
                b.Resources.Where((r, i) => r.Index != i || !float.IsFinite(r.Amount) || r.Amount < 0 || r.Amount > 1_000_000_000f).Any())
                throw new InvalidOperationException("One selected building or resource is not supported for a one-time addition.");
            var stored = b.Resources.Where(r => r.Id == resourceId && r.Status == 0).ToArray();
            if (stored.Length == 0) throw new InvalidOperationException("Every selected building must already contain this stored resource.");
            decimal next = (decimal)stored[0].Amount + amount;
            if (next > 1_000_000_000m || (decimal)(float)next != next)
                throw new InvalidOperationException("The resulting quantity cannot be represented exactly.");
            if (b.ClassName == CampRuleExecutor.BuildingType &&
                !b.Resources.Any(r => r.Status == 0 && r.Id != resourceId && float.IsFinite(r.Amount) && r.Amount >= 1000f) &&
                !(stored[0].Amount > 0 && next >= 1000))
                throw new InvalidOperationException("Camp storage requires a nonempty 1,000-unit anchor before adding stock.");
            ulong start = b.ResourcePointer, end = checked(start + (ulong)b.ResourceCount * 20);
            if (scan.UiCandidates.Any(ui => Overlap(start, end, ui.Address, checked(ui.Address + 8))) ||
                scan.Buildings.Any(other => other.Address != b.Address && other.ResourceCount > 0 && other.ResourcePointer != 0 &&
                    Overlap(start, end, other.ResourcePointer, checked(other.ResourcePointer + (ulong)other.ResourceCount * 20))))
                throw new InvalidOperationException("A selected resource array overlaps another observed object.");
            entries.Add(new(1, b, stored[0], (float)next));
        }
        return new(entries.AsReadOnly(), []);
    }

    public static int ApplyChecked(DiscoveryResult scan, IReadOnlyList<BuildingCandidate> selected, int resourceId, decimal amount, FishBulkEditor.IMemory memory)
        => ExistingRuleExecutor.ApplyPlannedChecked(Prepare(scan, selected, resourceId, amount), scan, memory).Applied;

    private sealed class GuardedMemory(FishBulkEditor.IMemory inner, GamePauseControl pause) : FishBulkEditor.IMemory
    {
        public byte[] Read(ulong address, int size) => inner.Read(address, size);
        public void Write(ulong address, byte[] data) { pause.EnsurePaused(); inner.Write(address, data); }
    }

    public static int ApplyToGame(DiscoveryResult scan, IReadOnlyList<BuildingCandidate> selected, int resourceId, decimal amount, CancellationToken cancellation)
    {
        var reviewed = Prepare(scan, selected, resourceId, amount);
        cancellation.ThrowIfCancellationRequested();
        using (var file = File.Open(scan.GamePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            if (Convert.ToHexString(SHA256.HashData(file)) != FishBulkEditor.SupportedHash) throw new InvalidOperationException("The game executable changed.");
        using var process = Process.GetProcessById(scan.ProcessId);
        if (process.HasExited || process.StartTime.ToUniversalTime() != scan.ProcessStartedUtc ||
            !string.Equals(Path.GetFullPath(process.MainModule!.FileName), Path.GetFullPath(scan.GamePath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The game session changed; scan again.");
        ulong moduleBase = checked((ulong)process.MainModule.BaseAddress.ToInt64());
        using var memory = new ExistingRuleExecutor.ProcessMemory(scan.ProcessId);
        var image = BuildingDiscovery.LoadImage(scan.GamePath).Image;
        const int pauseFunction = 0x3f5ec0, checkedLength = 32;
        if (!image.AsSpan(pauseFunction, checkedLength).SequenceEqual(memory.Read(moduleBase + pauseFunction, checkedLength)))
            throw new InvalidOperationException("The running game's pause control differs from the tested build.");
        var control = new GamePauseControl(memory, moduleBase);
        control.Acquire(cancellation);
        int applied = 0;
        try
        {
            var settings = new AutomationProfileSettings { MaintenanceRules = selected.Select(b => b.ClassName).Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(type => new MaintenanceRule { Enabled = true, BuildingType = type, ResourceId = resourceId, Target = 0 }).ToList() };
            var fresh = LiveRuleExecutor.Refresh(scan, settings, memory);
            var freshSelected = selected.Select(b => fresh.Buildings.Single(x => x.Address == b.Address && x.Vtable == b.Vtable && x.ClassName == b.ClassName)).ToArray();
            var current = Prepare(fresh, freshSelected, resourceId, amount);
            if (reviewed.Entries.Count != current.Entries.Count || reviewed.Entries.Zip(current.Entries).Any(p =>
                p.First.Building.Address != p.Second.Building.Address || p.First.Building.ResourcePointer != p.Second.Building.ResourcePointer ||
                !p.First.Building.Resources.SequenceEqual(p.Second.Building.Resources) || p.First.Record != p.Second.Record || p.First.Target != p.Second.Target))
                throw new InvalidOperationException("The selected stock changed since the preview; scan again.");
            cancellation.ThrowIfCancellationRequested(); control.EnsurePaused();
            applied = ExistingRuleExecutor.ApplyPlannedChecked(current, fresh, new GuardedMemory(memory, control)).Applied;
            control.Resume(); return applied;
        }
        catch (PartialGameWriteException) { throw; }
        catch (Exception ex) when (applied > 0)
        { throw new PartialGameWriteException("Stock changed but the game state could not be restored. Inspect the game and reload the backup if uncertain. " + ex.Message, ex); }
        catch { control.ResumeAfterPreflightFailure(); throw; }
    }
}
