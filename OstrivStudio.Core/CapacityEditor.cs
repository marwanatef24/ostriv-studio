using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;

namespace OstrivStudio.Core;

// This is an opt-in, one-time increase for observed building classes on
// the exact tested executable. The caller must first back up the loaded save
// and autosaves. Candidate addresses do not prove that objects are live.
public static class CapacityEditor
{
    public sealed record Entry(BuildingCandidate Building, float Before, float After);
    public sealed record Plan(IReadOnlyList<Entry> Entries);

    public static Plan Prepare(DiscoveryResult scan, IReadOnlyList<BuildingCandidate> selected, decimal amount)
    {
        if (!scan.Complete || scan.UnreadableChunks != 0 || scan.GameSha256 != FishBulkEditor.SupportedHash || scan.CountOffset != 408 || scan.PointerOffset != 416)
            throw new InvalidOperationException("A complete scan of the tested game build is required.");
        if (selected.Count is < 1 or > 128 || scan.Buildings.Select(b => b.Address).Distinct().Count() != scan.Buildings.Count ||
            selected.Select(b => b.Address).Distinct().Count() != selected.Count)
            throw new InvalidOperationException("Select between one and 128 distinct candidates from the current scan.");
        if (amount is <= 0 or > 1_000_000_000m || !float.IsFinite((float)amount) || (decimal)(float)amount != amount)
            throw new InvalidOperationException("Capacity must be a positive, exactly representable number up to 1,000,000,000.");
        var entries = new List<Entry>();
        foreach (var b in selected)
        {
            if (!scan.Buildings.Any(x => ReferenceEquals(x, b)) || !CapacityObservation.SupportsClass(b.ClassName) ||
                b.ObservedCapacity is not float before || !float.IsFinite(before) || before <= 0 || b.ResourceCount != b.Resources.Count ||
                b.ResourceCount is < 0 or > 511 || (b.ResourceCount > 0 && b.ResourcePointer == 0))
                throw new InvalidOperationException("Capacity can only be changed for reviewed granary, warehouse or big hay barrack candidates with a valid capacity observation.");
            if ((decimal)before > amount) throw new InvalidOperationException("This action only increases capacity; reducing it is not yet supported.");
            ulong start = checked(b.Address + CapacityObservation.Offset), end = checked(start + 4);
            foreach (var other in scan.Buildings)
            {
                if (start < checked(other.Address + (ulong)scan.PointerOffset + 12) && other.Address < end)
                    throw new InvalidOperationException("A capacity field overlaps a candidate header.");
                if (other.ResourceCount is < 0 or > 511) throw new InvalidOperationException("A candidate has an invalid resource count.");
                if (other.ResourceCount > 0 && other.ResourcePointer != 0 && start < checked(other.ResourcePointer + (ulong)other.ResourceCount * 20) && other.ResourcePointer < end)
                    throw new InvalidOperationException("A capacity field overlaps a resource array.");
            }
            if ((decimal)before < amount) entries.Add(new(b, before, (float)amount));
        }
        return new(entries.AsReadOnly());
    }

    private sealed record State(byte[] Header, byte[] Resources, byte[] Capacity);
    private static State ReadStable(DiscoveryResult scan, Entry entry, FishBulkEditor.IMemory memory)
    {
        var b = entry.Building;
        int headerLength = scan.PointerOffset + 8;
        byte[] header = memory.Read(b.Address, headerLength);
        if (header.Length != headerLength || BinaryPrimitives.ReadUInt64LittleEndian(header) != b.Vtable ||
            BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(scan.CountOffset)) != b.ResourceCount ||
            BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(scan.PointerOffset)) != b.ResourcePointer)
            throw new InvalidOperationException("A building changed since the scan; scan again.");
        byte[] capacity = memory.Read(checked(b.Address + CapacityObservation.Offset), 4);
        if (!capacity.AsSpan().SequenceEqual(BitConverter.GetBytes(entry.Before)))
            throw new InvalidOperationException("A building's capacity changed since the scan; scan again.");
        byte[] resources = b.ResourceCount == 0 ? [] : memory.Read(b.ResourcePointer, checked(b.ResourceCount * 20));
        if (BuildingDiscovery.DecodeResources(resources, b.ResourceCount) is null ||
            !header.AsSpan().SequenceEqual(memory.Read(b.Address, headerLength)) ||
            !capacity.AsSpan().SequenceEqual(memory.Read(checked(b.Address + CapacityObservation.Offset), 4)) ||
            (b.ResourceCount != 0 && !resources.AsSpan().SequenceEqual(memory.Read(b.ResourcePointer, resources.Length))))
            throw new InvalidOperationException("A building changed while its capacity was being checked.");
        return new(header, resources, capacity);
    }

    public static int ApplyChecked(DiscoveryResult scan, IReadOnlyList<BuildingCandidate> selected, decimal amount, FishBulkEditor.IMemory memory)
    {
        ArgumentNullException.ThrowIfNull(memory);
        var plan = Prepare(scan, selected, amount);
        var states = plan.Entries.Select(e => ReadStable(scan, e, memory)).ToArray();
        int attempted = 0;
        try
        {
            for (int i = 0; i < plan.Entries.Count; i++)
            {
                var entry = plan.Entries[i]; var before = states[i];
                var current = ReadStable(scan, entry, memory);
                if (!before.Header.AsSpan().SequenceEqual(current.Header) || !before.Resources.AsSpan().SequenceEqual(current.Resources) ||
                    !before.Capacity.AsSpan().SequenceEqual(current.Capacity))
                    throw new InvalidOperationException("A building changed before the edit.");
                attempted++;
                memory.Write(checked(entry.Building.Address + CapacityObservation.Offset), BitConverter.GetBytes(entry.After));
                if (!BitConverter.GetBytes(entry.After).AsSpan().SequenceEqual(memory.Read(checked(entry.Building.Address + CapacityObservation.Offset), 4)) ||
                    !before.Header.AsSpan().SequenceEqual(memory.Read(entry.Building.Address, before.Header.Length)) ||
                    (before.Resources.Length > 0 && !before.Resources.AsSpan().SequenceEqual(memory.Read(entry.Building.ResourcePointer, before.Resources.Length))))
                    throw new InvalidOperationException("Capacity readback did not match.");
            }
        }
        catch (Exception ex) when (attempted > 0)
        { throw new PartialGameWriteException("The capacity batch may be partial. Do not save; reload the verified backup. " + ex.Message, ex); }
        return attempted;
    }

    private sealed class GuardedMemory(FishBulkEditor.IMemory inner, GamePauseControl pause) : FishBulkEditor.IMemory
    {
        public byte[] Read(ulong address, int size) => inner.Read(address, size);
        public void Write(ulong address, byte[] data) { pause.EnsurePaused(); inner.Write(address, data); }
    }

    public static int ApplyToGame(DiscoveryResult scan, IReadOnlyList<BuildingCandidate> selected, decimal amount, CancellationToken cancellation)
    {
        _ = Prepare(scan, selected, amount);
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
        var pause = new GamePauseControl(memory, moduleBase);
        pause.Acquire(cancellation);
        int applied = 0;
        try
        {
            cancellation.ThrowIfCancellationRequested();
            pause.EnsurePaused();
            applied = ApplyChecked(scan, selected, amount, new GuardedMemory(memory, pause));
            pause.Resume();
            return applied;
        }
        catch (PartialGameWriteException) { throw; }
        catch (Exception ex) when (applied > 0)
        { throw new PartialGameWriteException("Capacity changed but the game state could not be restored. Inspect the game and reload the backup if uncertain. " + ex.Message, ex); }
        catch { pause.ResumeAfterPreflightFailure(); throw; }
    }
}
