using System.Buffers.Binary;
using OstrivStudio.Core;

internal static class RetailAssignmentsTests
{
    private sealed class Memory : FishBulkEditor.IMemory
    {
        private readonly Dictionary<ulong, byte> _bytes = [];
        public ulong ChangedAddress; public int ReadsAtChangedAddress;
        public void Put(ulong address, byte[] bytes) { for (int i = 0; i < bytes.Length; i++) _bytes[address + (uint)i] = bytes[i]; }
        public byte[] Read(ulong address, int length)
        {
            var data = Enumerable.Range(0, length).Select(i => _bytes.GetValueOrDefault(address + (uint)i)).ToArray();
            if (address == ChangedAddress && ++ReadsAtChangedAddress == 2) data[0] ^= 1;
            return data;
        }
        public void Write(ulong address, byte[] data) => throw new Exception("Retail inspection attempted a write");
    }
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); Console.WriteLine("PASS " + message); }
    private static void Reject(Action action, string message)
    {
        try { action(); } catch (InvalidOperationException) { Console.WriteLine("PASS " + message); return; }
        throw new Exception(message);
    }
    private static (DiscoveryResult scan, BuildingCandidate[] shops, Memory memory) Fixture()
    {
        var shops = new[] {
            new BuildingCandidate("building_rowhouse_shop", 0x10000, 0x9000, 0x50000, 1, [new(0, 46, 50, 0, 0, 0)], "candidate"),
            new BuildingCandidate("building_rowhouse_shop", 0x12000, 0x9100, 0x60000, 1, [new(0, 11, 20, 0, 0, 0)], "candidate")
        };
        var scan = new DiscoveryResult(1, DateTime.UtcNow, "test", FishBulkEditor.SupportedHash, 408, 416, shops, 1, true, 0, "");
        var memory = new Memory();
        for (int i = 0; i < shops.Length; i++)
        {
            var b = shops[i]; var header = new byte[0x7c8];
            BinaryPrimitives.WriteUInt64LittleEndian(header, b.Vtable);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(408), b.ResourceCount);
            BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(416), b.ResourcePointer);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(0x7b8), 4);
            BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(0x7c0), (ulong)(0x30000 + i * 0x10000));
            memory.Put(b.Address, header);
            byte[] ids = new byte[16]; BinaryPrimitives.WriteInt32LittleEndian(ids, i == 0 ? 46 : 11);
            BinaryPrimitives.WriteInt32LittleEndian(ids.AsSpan(4), i == 0 ? 8 : 12); memory.Put((ulong)(0x30000 + i * 0x10000), ids);
        }
        return (scan, shops, memory);
    }
    public static void Run()
    {
        var (scan, shops, memory) = Fixture(); var snapshot = RetailAssignments.Capture(scan, shops, memory);
        Check(snapshot.Buildings.Count == 2 && snapshot.Buildings[0].SlotIds.SequenceEqual([46, 8, 0, 0]) &&
            snapshot.Buildings[0].AssignedResourceIds.SequenceEqual([46, 8]) && snapshot.Buildings[1].AssignedResourceIds.SequenceEqual([11, 12]),
            "store assignments are read without writing and preserve empty slots");
        Check(!RetailAssignments.SupportsClass("building_shop"), "unverified market stall assignments remain blocked");
        Reject(() => RetailAssignments.Capture(scan with { GameSha256 = "unknown" }, shops, memory), "shop assignments reject unknown game builds");
        Reject(() => RetailAssignments.Capture(scan with { Complete = false }, shops, memory), "shop assignments reject incomplete scans");
        Reject(() => RetailAssignments.Capture(scan, [shops[0], shops[0]], memory), "shop assignments reject duplicate candidates");
        (scan, shops, memory) = Fixture(); memory.Put(shops[0].Address + 0x7b8, BitConverter.GetBytes(9));
        Reject(() => RetailAssignments.Capture(scan, shops, memory), "shop assignments reject an excessive count");
        (scan, shops, memory) = Fixture(); memory.Put(0x30004, BitConverter.GetBytes(46));
        Reject(() => RetailAssignments.Capture(scan, shops, memory), "shop assignments reject duplicate resource IDs");
        (scan, shops, memory) = Fixture();
        Reject(() => RetailAssignments.Capture(scan with { UiCandidates = [new("uiDealList", 0x30004, 0x9999)] }, shops, memory), "shop assignments reject lists overlapping another observed object");
        (scan, shops, memory) = Fixture(); memory.ChangedAddress = 0x30000;
        Reject(() => RetailAssignments.Capture(scan, shops, memory), "shop assignments reject arrays that change during inspection");
        (scan, shops, memory) = Fixture(); memory.Put(shops[0].Address + 416, BitConverter.GetBytes(0x50010UL));
        Reject(() => RetailAssignments.Capture(scan, shops, memory), "shop assignments reject stale building identities");
    }
}
