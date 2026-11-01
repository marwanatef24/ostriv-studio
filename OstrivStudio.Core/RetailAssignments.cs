using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace OstrivStudio.Core;

public sealed record RetailAssignment(BuildingCandidate Building, IReadOnlyList<int> SlotIds)
{
    public IEnumerable<int> AssignedResourceIds => SlotIds.Where(id => id != 0);
}
public sealed record RetailAssignmentSnapshot(DiscoveryResult Scan, IReadOnlyList<RetailAssignment> Buildings);

// Read-only observation of the assigned retail goods on the supported build.
// A visible assignment does not imply that adding an absent stock record is safe.
public static class RetailAssignments
{
    private const int CountOffset = 0x7b8, PointerOffset = 0x7c0, MaxSlots = 8;
    // Only rowhouse stores have been matched against the current game's UI.
    public static bool SupportsClass(string type) => type == "building_rowhouse_shop";

    private static byte[] Read(FishBulkEditor.IMemory memory, ulong address, int length)
    {
        if (address < 0x10000 || address > (ulong)long.MaxValue - (uint)length)
            throw new InvalidOperationException("A shop assignment pointer is invalid.");
        var bytes = memory.Read(address, length);
        if (bytes.Length != length) throw new InvalidOperationException("A shop assignment read was incomplete.");
        return bytes;
    }
    private static bool SameHeader(byte[] a, byte[] b, DiscoveryResult scan) =>
        a.AsSpan(0, 8).SequenceEqual(b.AsSpan(0, 8)) &&
        a.AsSpan(scan.CountOffset, 4).SequenceEqual(b.AsSpan(scan.CountOffset, 4)) &&
        a.AsSpan(scan.PointerOffset, 8).SequenceEqual(b.AsSpan(scan.PointerOffset, 8)) &&
        a.AsSpan(CountOffset, 4).SequenceEqual(b.AsSpan(CountOffset, 4)) &&
        a.AsSpan(PointerOffset, 8).SequenceEqual(b.AsSpan(PointerOffset, 8));
    private static bool Overlap(ulong a, ulong b, ulong c, ulong d) => a < d && c < b;

    public static RetailAssignmentSnapshot Capture(DiscoveryResult scan, IReadOnlyList<BuildingCandidate> selected, FishBulkEditor.IMemory memory)
    {
        if (!scan.Complete || scan.UnreadableChunks != 0 || scan.GameSha256 != FishBulkEditor.SupportedHash ||
            scan.CountOffset != 408 || scan.PointerOffset != 416 || selected.Count is < 1 or > 64 ||
            selected.Select(b => b.Address).Distinct().Count() != selected.Count ||
            scan.Buildings.Select(b => b.Address).Distinct().Count() != scan.Buildings.Count)
            throw new InvalidOperationException("Select distinct shops from a complete scan of the tested game build.");
        var results = new List<RetailAssignment>();
        var ranges = new List<(ulong Start, ulong End)>();
        foreach (var building in selected)
        {
            if (!SupportsClass(building.ClassName) || !scan.Buildings.Any(b => ReferenceEquals(b, building)) ||
                building.ResourceCount is < 0 or > 511 || building.ResourceCount != building.Resources.Count)
                throw new InvalidOperationException("Only shops from the reviewed scan are supported.");
            byte[] header = Read(memory, building.Address, PointerOffset + 8);
            if (BinaryPrimitives.ReadUInt64LittleEndian(header) != building.Vtable ||
                BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(scan.CountOffset)) != building.ResourceCount ||
                BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(scan.PointerOffset)) != building.ResourcePointer)
                throw new InvalidOperationException("A shop changed since the scan.");
            int count = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(CountOffset));
            ulong pointer = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(PointerOffset));
            if (count is < 0 or > MaxSlots) throw new InvalidOperationException("A shop has an unsupported assignment list.");
            byte[] data = count == 0 ? [] : Read(memory, pointer, checked(count * 4));
            if (count > 0)
            {
                ulong end = checked(pointer + (ulong)data.Length);
                if (ranges.Any(r => Overlap(pointer, end, r.Start, r.End)) ||
                    scan.UiCandidates.Any(ui => Overlap(pointer, end, ui.Address, checked(ui.Address + 8))) ||
                    scan.Buildings.Any(other => Overlap(pointer, end, other.Address, checked(other.Address + (ulong)(SupportsClass(other.ClassName) ? PointerOffset + 8 : scan.PointerOffset + 8))) ||
                        (other.ResourceCount > 0 && other.ResourcePointer != 0 && Overlap(pointer, end, other.ResourcePointer, checked(other.ResourcePointer + (ulong)other.ResourceCount * 20)))))
                    throw new InvalidOperationException("A shop assignment list overlaps another observed object.");
                ranges.Add((pointer, end));
            }
            var ids = Enumerable.Range(0, count).Select(i => BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(i * 4))).ToArray();
            if (ids.Any(id => id is < 0 or > 65535) || ids.Where(id => id != 0).Distinct().Count() != ids.Count(id => id != 0) ||
                !SameHeader(header, Read(memory, building.Address, header.Length), scan) ||
                (count > 0 && !data.AsSpan().SequenceEqual(Read(memory, pointer, data.Length))))
                throw new InvalidOperationException("A shop assignment list is invalid or changed while reading.");
            results.Add(new(building, Array.AsReadOnly(ids)));
        }
        return new(scan, results.AsReadOnly());
    }

    public static RetailAssignmentSnapshot CaptureFromGame(DiscoveryResult scan, IReadOnlyList<BuildingCandidate> selected)
    {
        using (var file = File.Open(scan.GamePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            if (Convert.ToHexString(SHA256.HashData(file)) != FishBulkEditor.SupportedHash) throw new InvalidOperationException("The game executable changed.");
        using var process = Process.GetProcessById(scan.ProcessId);
        if (process.HasExited || process.StartTime.ToUniversalTime() != scan.ProcessStartedUtc ||
            !string.Equals(Path.GetFullPath(process.MainModule!.FileName), Path.GetFullPath(scan.GamePath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The game session changed; scan again.");
        using var memory = new ReadOnlyMemory(scan.ProcessId);
        return Capture(scan, selected, memory);
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
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Shop assignment memory could not be read.");
            return bytes;
        }
        public void Write(ulong address, byte[] data) => throw new InvalidOperationException("Shop assignment inspection is read-only.");
        public void Dispose() => _handle.Dispose();
    }
}
