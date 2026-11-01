using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace OstrivStudio.Core;

// This is intentionally limited to the one layout tested in a real save.
// A game pause cannot be verified by this code. Multi-process memory writes are
// not atomic; on any uncertain result the caller must reload the backed-up save.
public static class FishBulkEditor
{
    public const string SupportedHash = "D66B8F19FFCA57845B3085F94E01B8CA7685D02211E7EDADAECF579591B6D771";
    private sealed record Entry(BuildingCandidate Building, ResourceObservation Fish, ulong RecordAddress);
    public interface IMemory
    {
        byte[] Read(ulong address, int size);
        void Write(ulong address, byte[] bytes);
    }

    private static List<Entry> Validate(DiscoveryResult snapshot, IReadOnlyList<BuildingCandidate> buildings)
    {
        if (!snapshot.Complete || snapshot.UnreadableChunks != 0 || snapshot.GameSha256 != SupportedHash || snapshot.CountOffset != 408 || snapshot.PointerOffset != 416)
            throw new InvalidOperationException("This scan or game build is not supported for fish edits.");
        if (buildings.Count is < 1 or > 2) throw new InvalidOperationException("Select one or two fishing docks.");
        var entries = new List<Entry>(); var objects = new HashSet<ulong>(); var arrays = new HashSet<ulong>();
        foreach (var b in buildings)
        {
            if (!snapshot.Buildings.Any(x => ReferenceEquals(x, b)) || b.ClassName != "building_fishery" || !objects.Add(b.Address) || !arrays.Add(b.ResourcePointer) ||
                b.ResourceCount != b.Resources.Count || b.ResourceCount is <= 0 or > 511)
                throw new InvalidOperationException("Select distinct fishing docks from the current scan.");
            var fish = b.Resources.FirstOrDefault(r => r.Id == 27 && r.Status == 0)
                ?? throw new InvalidOperationException("Every selected dock must already contain stored fish.");
            if (fish.Index < 0 || fish.Index >= b.ResourceCount || !float.IsFinite(fish.Amount) || fish.Amount < 0 || fish.Amount > 10000 || fish.Amount + 1f - fish.Amount != 1f)
                throw new InvalidOperationException("The stored fish amount cannot be edited safely.");
            entries.Add(new(b, fish, checked(b.ResourcePointer + (ulong)fish.Index * 20)));
        }
        // A malformed or aliased array must never make two edits touch one record.
        var ranges = entries.Select(e => (Start: e.Building.ResourcePointer, End: checked(e.Building.ResourcePointer + (ulong)e.Building.ResourceCount * 20))).OrderBy(x => x.Start).ToArray();
        if (ranges.Zip(ranges.Skip(1)).Any(pair => pair.First.End > pair.Second.Start)) throw new InvalidOperationException("Resource arrays overlap.");
        if (ranges.Any(r => entries.Any(e => r.Start < checked(e.Building.Address + (ulong)snapshot.PointerOffset + 8) && e.Building.Address < r.End)))
            throw new InvalidOperationException("A resource array overlaps a building header.");
        return entries;
    }
    private static byte[] Encode(ResourceObservation r)
    {
        var data = new byte[20];
        BinaryPrimitives.WriteInt32LittleEndian(data, r.Id);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4), BitConverter.SingleToInt32Bits(r.Amount));
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), r.Flags);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), r.Details);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16), r.Extra);
        return data;
    }
    private static byte[] Header(IMemory memory, DiscoveryResult snapshot, Entry e)
    {
        var header = memory.Read(e.Building.Address, snapshot.PointerOffset + 8);
        if (header.Length != snapshot.PointerOffset + 8 || BinaryPrimitives.ReadUInt64LittleEndian(header) != e.Building.Vtable ||
            BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(snapshot.CountOffset)) != e.Building.ResourceCount ||
            BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(snapshot.PointerOffset)) != e.Building.ResourcePointer)
            throw new InvalidOperationException("A building changed since the scan.");
        return header;
    }
    private static void CheckRecords(IMemory memory, Entry e)
    {
        byte[] data = memory.Read(e.Building.ResourcePointer, checked(e.Building.ResourceCount * 20));
        if (data.Length != e.Building.ResourceCount * 20) throw new InvalidOperationException("An incomplete resource array was read.");
        for (int i = 0; i < e.Building.Resources.Count; i++)
        {
            var r = e.Building.Resources[i];
            if (r.Index != i || !data.AsSpan(i * 20, 20).SequenceEqual(Encode(r))) throw new InvalidOperationException("Resources changed since the scan.");
        }
    }

    // Exposed for deterministic tests; production creates its own process handle.
    public static void ApplyChecked(DiscoveryResult snapshot, IReadOnlyList<BuildingCandidate> buildings, IMemory memory)
    {
        ArgumentNullException.ThrowIfNull(memory);
        var entries = Validate(snapshot, buildings);
        var headers = new List<byte[]>();
        foreach (var e in entries) { headers.Add(Header(memory, snapshot, e)); CheckRecords(memory, e); }
        int attempts = 0;
        try
        {
            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                if (!headers[i].AsSpan().SequenceEqual(Header(memory, snapshot, e))) throw new InvalidOperationException("A building changed during validation.");
                CheckRecords(memory, e);
                attempts++;
                memory.Write(checked(e.RecordAddress + 4), BitConverter.GetBytes(e.Fish.Amount + 1f));
                byte[] expected = Encode(e.Fish); BitConverter.GetBytes(e.Fish.Amount + 1f).CopyTo(expected, 4);
                if (!memory.Read(e.RecordAddress, 20).AsSpan().SequenceEqual(expected) || !headers[i].AsSpan().SequenceEqual(Header(memory, snapshot, e)))
                    throw new InvalidOperationException("Readback did not match.");
            }
        }
        catch (Exception ex) when (attempts > 0)
        {
            throw new InvalidOperationException("The batch may be partially applied. Do not retry or save; reload the backup. " + ex.Message, ex);
        }
    }
    public static void ApplyToGame(DiscoveryResult snapshot, IReadOnlyList<BuildingCandidate> buildings)
    {
        Validate(snapshot, buildings);
        using (var file = File.Open(snapshot.GamePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            if (Convert.ToHexString(SHA256.HashData(file)) != SupportedHash) throw new InvalidOperationException("The game executable changed.");
        using var process = Process.GetProcessById(snapshot.ProcessId);
        if (process.HasExited || process.StartTime.ToUniversalTime() != snapshot.ProcessStartedUtc ||
            !string.Equals(Path.GetFullPath(process.MainModule!.FileName), Path.GetFullPath(snapshot.GamePath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The game session changed; scan again.");
        using var memory = new ProcessMemory(snapshot.ProcessId);
        ApplyChecked(snapshot, buildings, memory);
    }
    private sealed class ProcessMemory : IMemory, IDisposable
    {
        [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int id);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ReadProcessMemory(SafeProcessHandle process, IntPtr address, byte[] data, nuint size, out nuint read);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool WriteProcessMemory(SafeProcessHandle process, IntPtr address, byte[] data, nuint size, out nuint written);
        private readonly SafeProcessHandle _handle;
        public ProcessMemory(int id) { _handle = OpenProcess(0x400 | 0x10 | 0x20 | 0x08, false, id); if (_handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error()); }
        public byte[] Read(ulong address, int size)
        {
            var data = new byte[size];
            if (!ReadProcessMemory(_handle, new IntPtr(checked((long)address)), data, (nuint)size, out var read) || read != (nuint)size) throw new Win32Exception(Marshal.GetLastWin32Error(), "Game memory could not be read.");
            return data;
        }
        public void Write(ulong address, byte[] data)
        {
            if (!WriteProcessMemory(_handle, new IntPtr(checked((long)address)), data, (nuint)data.Length, out var written) || written != (nuint)data.Length) throw new Win32Exception(Marshal.GetLastWin32Error(), "The game memory write did not complete.");
        }
        public void Dispose() => _handle.Dispose();
    }
}
