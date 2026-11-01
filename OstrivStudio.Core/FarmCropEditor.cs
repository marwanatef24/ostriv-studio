using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace OstrivStudio.Core;

public sealed record FarmFieldSnapshot(int Number, BuildingCandidate Candidate, ulong CropPointer, IReadOnlyList<int> Crops);
public sealed record FarmRotationSnapshot(DiscoveryResult Scan, BuildingCandidate Farm, ulong FieldPointer,
    IReadOnlyList<FarmFieldSnapshot> Fields);

// Layout independently observed on this executable, in three fields of one
// test settlement. Future crops can be reordered, or replaced with the small
// independently observed crop set. Pointers, counts and current crop stay unchanged.
public static class FarmCropEditor
{
    private const int FarmCount = 0x8A8, FarmPointer = 0x8B0, CropCount = 0x950, CropPointer = 0x958;
    private static readonly HashSet<int> KnownCrops = [6, 7, 8, 9, 10, 13, 83, 87, 179, 181];
    private static readonly HashSet<int> ReplaceableCrops = [6, 7, 8, 13];
    private static byte[] Read(FishBulkEditor.IMemory memory, ulong address, int count)
    {
        if (address < 0x10000 || address > (ulong)long.MaxValue - (uint)count) throw new InvalidOperationException("A farm pointer is invalid.");
        byte[] bytes = memory.Read(address, count);
        if (bytes.Length != count) throw new InvalidOperationException("A farm memory read was incomplete.");
        return bytes;
    }
    private static bool Overlap(ulong start, ulong end, ulong otherStart, ulong otherEnd) => start < otherEnd && otherStart < end;
    private static bool SameIdentity(byte[] first, byte[] second, DiscoveryResult scan, int listCount, int listPointer) =>
        first.AsSpan(0, 8).SequenceEqual(second.AsSpan(0, 8)) &&
        first.AsSpan(scan.CountOffset, 4).SequenceEqual(second.AsSpan(scan.CountOffset, 4)) &&
        first.AsSpan(scan.PointerOffset, 8).SequenceEqual(second.AsSpan(scan.PointerOffset, 8)) &&
        first.AsSpan(listCount, 4).SequenceEqual(second.AsSpan(listCount, 4)) &&
        first.AsSpan(listPointer, 8).SequenceEqual(second.AsSpan(listPointer, 8));
    public static FarmRotationSnapshot Capture(DiscoveryResult scan, BuildingCandidate farm, FishBulkEditor.IMemory memory)
    {
        if (!scan.Complete || scan.UnreadableChunks != 0 || scan.GameSha256 != FishBulkEditor.SupportedHash || scan.CountOffset != 408 || scan.PointerOffset != 416 ||
            farm.ClassName != "building_grain_farm" || !scan.Buildings.Any(b => ReferenceEquals(b, farm)) ||
            scan.Buildings.Select(b => b.Address).Distinct().Count() != scan.Buildings.Count)
            throw new InvalidOperationException("Select a grain farm from a complete scan of the tested game build.");
        byte[] farmHeader = Read(memory, farm.Address, FarmPointer + 8);
        if (BinaryPrimitives.ReadUInt64LittleEndian(farmHeader) != farm.Vtable ||
            BinaryPrimitives.ReadInt32LittleEndian(farmHeader.AsSpan(scan.CountOffset)) != farm.ResourceCount ||
            BinaryPrimitives.ReadUInt64LittleEndian(farmHeader.AsSpan(scan.PointerOffset)) != farm.ResourcePointer)
            throw new InvalidOperationException("The farm changed since the scan.");
        int count = BinaryPrimitives.ReadInt32LittleEndian(farmHeader.AsSpan(FarmCount));
        ulong fieldPointer = BinaryPrimitives.ReadUInt64LittleEndian(farmHeader.AsSpan(FarmPointer));
        if (count is < 1 or > 255) throw new InvalidOperationException("This farm has no verified field list.");
        byte[] fieldPointers = Read(memory, fieldPointer, checked(count * 8));
        var knownFields = scan.Buildings.Where(b => b.ClassName == "building_grain_field").ToDictionary(b => b.Address);
        var fields = new List<FarmFieldSnapshot>(); var seen = new HashSet<ulong>();
        var ranges = new List<(ulong Start, ulong End)>();
        for (int i = 0; i < count; i++)
        {
            ulong fieldAddress = BinaryPrimitives.ReadUInt64LittleEndian(fieldPointers.AsSpan(i * 8));
            if (!seen.Add(fieldAddress) || !knownFields.TryGetValue(fieldAddress, out var field))
                throw new InvalidOperationException("A farm field is missing from the reviewed building scan.");
            byte[] header = Read(memory, fieldAddress, CropPointer + 8);
            if (BinaryPrimitives.ReadUInt64LittleEndian(header) != field.Vtable ||
                BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(scan.CountOffset)) != field.ResourceCount ||
                BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(scan.PointerOffset)) != field.ResourcePointer)
                throw new InvalidOperationException("A field changed since the scan.");
            int cropCount = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(CropCount));
            ulong cropPointer = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(CropPointer));
            if (cropCount is < 1 or > 6) throw new InvalidOperationException("A field's crop rotation is unavailable or unsupported.");
            byte[] cropBytes = Read(memory, cropPointer, cropCount * 4);
            int[] crops = Enumerable.Range(0, cropCount).Select(j => BinaryPrimitives.ReadInt32LittleEndian(cropBytes.AsSpan(j * 4))).ToArray();
            if (crops.Any(id => !KnownCrops.Contains(id)) ||
                !SameIdentity(header, Read(memory, fieldAddress, header.Length), scan, CropCount, CropPointer) ||
                !cropBytes.AsSpan().SequenceEqual(Read(memory, cropPointer, cropBytes.Length)))
                throw new InvalidOperationException("A field's crop rotation is unknown or changed while reading.");
            ulong end = checked(cropPointer + (ulong)cropBytes.Length);
            if (Overlap(cropPointer, end, farm.Address, checked(farm.Address + FarmPointer + 8)) ||
                Overlap(cropPointer, end, fieldPointer, checked(fieldPointer + (ulong)fieldPointers.Length)) ||
                ranges.Any(r => Overlap(cropPointer, end, r.Start, r.End)) ||
                scan.UiCandidates.Any(ui => Overlap(cropPointer, end, ui.Address, checked(ui.Address + 8))) ||
                scan.Buildings.Any(b => Overlap(cropPointer, end, b.Address, checked(b.Address + (ulong)(b.ClassName == "building_grain_field" ? CropPointer + 8 : scan.PointerOffset + 8))) ||
                    (b.ResourceCount > 0 && b.ResourcePointer != 0 && Overlap(cropPointer, end, b.ResourcePointer, checked(b.ResourcePointer + (ulong)b.ResourceCount * 20)))))
                throw new InvalidOperationException("A crop array overlaps another observed object.");
            ranges.Add((cropPointer, end));
            fields.Add(new(i + 1, field, cropPointer, Array.AsReadOnly(crops)));
        }
        if (!SameIdentity(farmHeader, Read(memory, farm.Address, farmHeader.Length), scan, FarmCount, FarmPointer) ||
            !fieldPointers.AsSpan().SequenceEqual(Read(memory, fieldPointer, fieldPointers.Length)))
            throw new InvalidOperationException("The farm's field list changed while reading.");
        return new(scan, farm, fieldPointer, fields.AsReadOnly());
    }
    public static bool Changed(FarmFieldSnapshot field, IReadOnlyList<int> desired)
    {
        if (desired.Count != field.Crops.Count || desired.Count < 1 || desired[0] != field.Crops[0] || desired.Any(id => !KnownCrops.Contains(id)) ||
            desired.GroupBy(id => id).Any(g => g.Count() > field.Crops.Count(id => id == g.Key) && !ReplaceableCrops.Contains(g.Key)))
            throw new InvalidOperationException("Keep the current crop and field length. New future crops are limited to wheat, buckwheat, potato and fallow.");
        return !desired.SequenceEqual(field.Crops);
    }
    public static void ApplyChecked(FarmRotationSnapshot reviewed, int fieldNumber, IReadOnlyList<int> desired, FishBulkEditor.IMemory memory)
    {
        _ = ApplyBatchChecked(reviewed, new Dictionary<int, IReadOnlyList<int>> { [fieldNumber] = desired }, memory);
    }
    private static bool Same(FarmRotationSnapshot a, FarmRotationSnapshot b) => a.FieldPointer == b.FieldPointer && a.Fields.Count == b.Fields.Count &&
        a.Fields.Zip(b.Fields).All(p => p.First.Number == p.Second.Number && p.First.Candidate.Address == p.Second.Candidate.Address &&
            p.First.CropPointer == p.Second.CropPointer && p.First.Crops.SequenceEqual(p.Second.Crops));
    public static int CountChanges(FarmRotationSnapshot reviewed, IReadOnlyDictionary<int, IReadOnlyList<int>> desired)
    {
        if (desired.Count is < 1 or > 255) throw new InvalidOperationException("Select one or more reviewed fields.");
        int count = 0;
        foreach (var pair in desired)
        {
            var field = reviewed.Fields.SingleOrDefault(f => f.Number == pair.Key) ?? throw new InvalidOperationException("A selected field is unavailable.");
            if (Changed(field, pair.Value)) count++;
        }
        return count;
    }
    public static int ApplyBatchChecked(FarmRotationSnapshot reviewed, IReadOnlyDictionary<int, IReadOnlyList<int>> desired, FishBulkEditor.IMemory memory)
    {
        int changes = CountChanges(reviewed, desired);
        if (changes == 0) return 0;
        var expected = Capture(reviewed.Scan, reviewed.Farm, memory);
        if (!Same(expected, reviewed)) throw new InvalidOperationException("The farm or a crop rotation changed; scan again.");
        int attempted = 0;
        try
        {
            foreach (var pair in desired.OrderBy(p => p.Key))
            {
                var field = expected.Fields.Single(f => f.Number == pair.Key);
                if (!Changed(field, pair.Value)) continue;
                if (!Same(expected, Capture(reviewed.Scan, reviewed.Farm, memory))) throw new InvalidOperationException("A farm field changed before the edit.");
                byte[] replacement = new byte[(pair.Value.Count - 1) * 4];
                for (int i = 1; i < pair.Value.Count; i++) BinaryPrimitives.WriteInt32LittleEndian(replacement.AsSpan((i - 1) * 4), pair.Value[i]);
                attempted++;
                memory.Write(checked(field.CropPointer + 4), replacement);
                var fields = expected.Fields.Select(f => f.Number == pair.Key ? f with { Crops = Array.AsReadOnly(pair.Value.ToArray()) } : f).ToArray();
                expected = expected with { Fields = fields };
                if (!Same(expected, Capture(reviewed.Scan, reviewed.Farm, memory))) throw new InvalidOperationException("The crop rotation readback did not match.");
            }
        }
        catch (Exception ex) when (attempted > 0)
        { throw new PartialGameWriteException("The crop batch may be partial. Do not save; reload the verified backup. " + ex.Message, ex); }
        return changes;
    }
    private static Process CheckedProcess(DiscoveryResult scan)
    {
        using (var file = File.Open(scan.GamePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            if (Convert.ToHexString(SHA256.HashData(file)) != FishBulkEditor.SupportedHash) throw new InvalidOperationException("The game executable changed.");
        var process = Process.GetProcessById(scan.ProcessId);
        try
        {
            if (process.HasExited || process.StartTime.ToUniversalTime() != scan.ProcessStartedUtc ||
                !string.Equals(Path.GetFullPath(process.MainModule!.FileName), Path.GetFullPath(scan.GamePath), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The game session changed; scan again.");
            return process;
        }
        catch { process.Dispose(); throw; }
    }
    private sealed class ReadOnlyMemory : FishBulkEditor.IMemory, IDisposable
    {
        [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int id);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ReadProcessMemory(SafeProcessHandle process, IntPtr address, byte[] bytes, nuint size, out nuint read);
        private readonly SafeProcessHandle _handle;
        public ReadOnlyMemory(int id) { _handle = OpenProcess(0x400 | 0x10, false, id); if (_handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error()); }
        public byte[] Read(ulong address, int size)
        {
            var bytes = new byte[size];
            if (!ReadProcessMemory(_handle, new IntPtr(checked((long)address)), bytes, (nuint)size, out var count) || count != (nuint)size)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Farm memory could not be read.");
            return bytes;
        }
        public void Write(ulong address, byte[] data) => throw new InvalidOperationException("This farm inspection is read-only.");
        public void Dispose() => _handle.Dispose();
    }
    public static FarmRotationSnapshot CaptureFromGame(DiscoveryResult scan, BuildingCandidate farm)
    {
        using var process = CheckedProcess(scan);
        using var memory = new ReadOnlyMemory(scan.ProcessId);
        return Capture(scan, farm, memory);
    }
    private sealed class GuardedMemory(FishBulkEditor.IMemory inner, GamePauseControl pause) : FishBulkEditor.IMemory
    {
        public byte[] Read(ulong address, int size) => inner.Read(address, size);
        public void Write(ulong address, byte[] data) { pause.EnsurePaused(); inner.Write(address, data); }
    }
    public static void ApplyToGame(FarmRotationSnapshot reviewed, int fieldNumber, IReadOnlyList<int> desired, CancellationToken cancellation)
    {
        _ = ApplyBatchToGame(reviewed, new Dictionary<int, IReadOnlyList<int>> { [fieldNumber] = desired }, cancellation);
    }
    public static int ApplyBatchToGame(FarmRotationSnapshot reviewed, IReadOnlyDictionary<int, IReadOnlyList<int>> desired, CancellationToken cancellation)
    {
        if (CountChanges(reviewed, desired) == 0) return 0;
        using var process = CheckedProcess(reviewed.Scan);
        ulong moduleBase = checked((ulong)process.MainModule!.BaseAddress.ToInt64());
        using var memory = new ExistingRuleExecutor.ProcessMemory(reviewed.Scan.ProcessId);
        var image = BuildingDiscovery.LoadImage(reviewed.Scan.GamePath).Image;
        const int pauseFunction = 0x3f5ec0, checkedLength = 32;
        if (!image.AsSpan(pauseFunction, checkedLength).SequenceEqual(memory.Read(moduleBase + pauseFunction, checkedLength)))
            throw new InvalidOperationException("The running game's pause control differs from the tested build.");
        var pause = new GamePauseControl(memory, moduleBase);
        pause.Acquire(cancellation);
        int changed = 0;
        try
        {
            cancellation.ThrowIfCancellationRequested(); pause.EnsurePaused();
            changed = ApplyBatchChecked(reviewed, desired, new GuardedMemory(memory, pause));
            pause.Resume(); return changed;
        }
        catch (PartialGameWriteException) { throw; }
        catch (Exception ex) when (changed > 0)
        { throw new PartialGameWriteException("The crop rotation changed but the game state could not be restored. " + ex.Message, ex); }
        catch { pause.ResumeAfterPreflightFailure(); throw; }
    }
}
