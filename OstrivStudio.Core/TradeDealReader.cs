using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace OstrivStudio.Core;

public sealed record TradeDeal(int CityId, int ResourceId, uint Flags, int DealId, int Total, float Completed)
{
    public bool IsImport => (Flags & 1) != 0;
    public double Remaining => Math.Max(0, Total - (double)Completed);
    public string? TradingBuildingClass => CityId switch
    {
        1 or 2 or 3 => "building_trading_post",
        4 or 5 => "building_trading_dock",
        _ => null
    };
}

// Observed in the original editor and checked against one live deal on the
// tested build. This reads the currently open city list; it never writes deals.
public static class TradeDealReader
{
    private const int CountOffset = 0x250, PointerOffset = 0x258, ItemDealPointer = 0x250;
    private static byte[] Read(FishBulkEditor.IMemory memory, ulong address, int length)
    {
        if (address < 0x10000 || address > (ulong)long.MaxValue - (uint)length) throw new InvalidOperationException("A trade pointer is invalid.");
        var bytes = memory.Read(address, length);
        if (bytes.Length != length) throw new InvalidOperationException("A trade memory read was incomplete.");
        return bytes;
    }
    public static IReadOnlyList<TradeDeal> Capture(DiscoveryResult scan, FishBulkEditor.IMemory memory)
    {
        if (!scan.Complete || scan.UnreadableChunks != 0 || scan.GameSha256 != FishBulkEditor.SupportedHash)
            throw new InvalidOperationException("A complete scan of the tested game build is required.");
        var lists = scan.UiCandidates.Where(c => c.ClassName == "uiDealList").ToArray();
        if (lists.Length != 1) throw new InvalidOperationException("Open the world map and select a city with deals in Ostriv, then scan again.");
        var list = lists[0]; byte[] header = Read(memory, list.Address, PointerOffset + 8);
        if (BinaryPrimitives.ReadUInt64LittleEndian(header) != list.Vtable) throw new InvalidOperationException("The trade list changed; scan again.");
        int count = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(CountOffset));
        ulong pointer = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(PointerOffset));
        if (count is < 0 or > 128) throw new InvalidOperationException("The trade list contains an unsupported number of entries.");
        byte[] items = count == 0 ? [] : Read(memory, pointer, count * 8);
        var deals = new List<TradeDeal>();
        var observed = new List<(ulong Item, ulong Deal, byte[] ItemHeader, byte[] Data)>();
        for (int i = 0; i < count; i++)
        {
            ulong item = BinaryPrimitives.ReadUInt64LittleEndian(items.AsSpan(i * 8));
            byte[] itemHeader = Read(memory, item, ItemDealPointer + 8);
            ulong dealPointer = BinaryPrimitives.ReadUInt64LittleEndian(itemHeader.AsSpan(ItemDealPointer));
            byte[] data = Read(memory, dealPointer, 56);
            int city = BinaryPrimitives.ReadInt32LittleEndian(data), resource = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(4));
            uint flags = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8));
            int dealId = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(12)), total = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(52));
            float completed = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(48)));
            if (city is <= 0 or > 1000 || resource is <= 0 or > 65535 || total is < 0 or > 1_000_000_000 ||
                !float.IsFinite(completed) || completed < 0 || completed > total)
                throw new InvalidOperationException("A trade deal has an unsupported layout or value.");
            byte[] itemCheck = Read(memory, item, ItemDealPointer + 8), dataCheck = Read(memory, dealPointer, 56);
            if (!itemHeader.AsSpan(0, 8).SequenceEqual(itemCheck.AsSpan(0, 8)) ||
                !itemHeader.AsSpan(ItemDealPointer, 8).SequenceEqual(itemCheck.AsSpan(ItemDealPointer, 8)) ||
                !data.AsSpan(0, 16).SequenceEqual(dataCheck.AsSpan(0, 16)) || !data.AsSpan(48, 8).SequenceEqual(dataCheck.AsSpan(48, 8)))
                throw new InvalidOperationException("A trade deal changed while reading; refresh it.");
            deals.Add(new(city, resource, flags, dealId, total, completed));
            observed.Add((item, dealPointer, itemHeader, data));
        }
        void RequireSameList()
        {
            byte[] after = Read(memory, list.Address, PointerOffset + 8);
            if (!header.AsSpan(0, 8).SequenceEqual(after.AsSpan(0, 8)) ||
                !header.AsSpan(CountOffset, 4).SequenceEqual(after.AsSpan(CountOffset, 4)) ||
                !header.AsSpan(PointerOffset, 8).SequenceEqual(after.AsSpan(PointerOffset, 8)) ||
                (count != 0 && !items.AsSpan().SequenceEqual(Read(memory, pointer, items.Length))))
                throw new InvalidOperationException("The selected city's trade list changed; refresh it.");
        }
        RequireSameList();
        // Recheck earlier rows after all rows have been read. A deal can change
        // after its first local recheck while another row is being inspected.
        foreach (var row in observed)
        {
            byte[] itemCheck = Read(memory, row.Item, ItemDealPointer + 8), dataCheck = Read(memory, row.Deal, 56);
            if (!row.ItemHeader.AsSpan(0, 8).SequenceEqual(itemCheck.AsSpan(0, 8)) ||
                !row.ItemHeader.AsSpan(ItemDealPointer, 8).SequenceEqual(itemCheck.AsSpan(ItemDealPointer, 8)) ||
                !row.Data.AsSpan(0, 16).SequenceEqual(dataCheck.AsSpan(0, 16)) ||
                !row.Data.AsSpan(48, 8).SequenceEqual(dataCheck.AsSpan(48, 8)))
                throw new InvalidOperationException("A trade deal changed during the list review; refresh it.");
        }
        RequireSameList();
        return deals.AsReadOnly();
    }
    public static void RequireUnchanged(DiscoveryResult scan, FishBulkEditor.IMemory memory, TradeDeal reviewed)
        => RequireAllUnchanged(scan, memory, [reviewed]);

    // All selected deals must exist together in one stable capture. Separate
    // captures can each match a different city or state of a changing list.
    public static void RequireAllUnchanged(DiscoveryResult scan, FishBulkEditor.IMemory memory, IReadOnlyList<TradeDeal> reviewed)
    {
        if (reviewed.Count is < 1 or > 128 || reviewed.Distinct().Count() != reviewed.Count)
            throw new InvalidOperationException("Select distinct trade deals before editing.");
        var visible = Capture(scan, memory);
        if (reviewed.Any(deal => visible.Count(current => current == deal) != 1))
            throw new InvalidOperationException("A selected trade deal changed, is ambiguous, or is no longer visible. Refresh the deals before editing.");
    }
    private sealed class ReadOnlyMemory : FishBulkEditor.IMemory, IDisposable
    {
        [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int id);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ReadProcessMemory(SafeProcessHandle process, IntPtr address, byte[] data, nuint size, out nuint read);
        private readonly SafeProcessHandle _handle;
        public ReadOnlyMemory(int id) { _handle = OpenProcess(0x400 | 0x10, false, id); if (_handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error()); }
        public byte[] Read(ulong address, int size)
        {
            var data = new byte[size];
            if (!ReadProcessMemory(_handle, new IntPtr(checked((long)address)), data, (nuint)size, out var count) || count != (nuint)size)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Trade memory could not be read.");
            return data;
        }
        public void Write(ulong address, byte[] data) => throw new InvalidOperationException("Trade inspection is read-only.");
        public void Dispose() => _handle.Dispose();
    }
    public static IReadOnlyList<TradeDeal> CaptureFromGame(DiscoveryResult scan)
    {
        using (var file = File.Open(scan.GamePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            if (Convert.ToHexString(SHA256.HashData(file)) != FishBulkEditor.SupportedHash) throw new InvalidOperationException("The game executable changed.");
        using var process = Process.GetProcessById(scan.ProcessId);
        if (process.HasExited || process.StartTime.ToUniversalTime() != scan.ProcessStartedUtc ||
            !string.Equals(Path.GetFullPath(process.MainModule!.FileName), Path.GetFullPath(scan.GamePath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The game session changed; scan again.");
        using var memory = new ReadOnlyMemory(scan.ProcessId);
        return Capture(scan, memory);
    }
}
