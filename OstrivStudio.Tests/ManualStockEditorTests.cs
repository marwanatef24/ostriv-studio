using System.Buffers.Binary;
using OstrivStudio.Core;

internal static class ManualStockEditorTests
{
    private sealed class Memory : FishBulkEditor.IMemory
    {
        private readonly Dictionary<ulong, byte> _bytes = [];
        public int Writes, FailOnWrite;
        public void Put(ulong address, byte[] value) { for (int i = 0; i < value.Length; i++) _bytes[address + (uint)i] = value[i]; }
        public byte[] Read(ulong address, int size) => Enumerable.Range(0, size).Select(i => _bytes.GetValueOrDefault(address + (uint)i)).ToArray();
        public void Write(ulong address, byte[] data) { Writes++; Put(address, data); if (Writes == FailOnWrite) throw new IOException("uncertain write"); }
    }
    private static (DiscoveryResult Scan, BuildingCandidate[] Buildings, Memory Memory) Fixture()
    {
        var buildings = new[] {
            new BuildingCandidate("building_warehouse", 0x10000, 0x9000, 0x30000, 1, [new(0, 33, 50, 0, 0, 0)], "sample"),
            new BuildingCandidate("building_warehouse", 0x20000, 0x9000, 0x40000, 1, [new(0, 33, 80, 0, 0, 0)], "sample") };
        var scan = new DiscoveryResult(1, DateTime.UtcNow, "test", FishBulkEditor.SupportedHash, 408, 416, buildings, 1, true, 0, "");
        var memory = new Memory();
        foreach (var b in buildings)
        {
            var header = new byte[424]; BinaryPrimitives.WriteUInt64LittleEndian(header, b.Vtable);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(408), 1); BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(416), b.ResourcePointer); memory.Put(b.Address, header);
            var record = new byte[20]; BinaryPrimitives.WriteInt32LittleEndian(record, 33);
            BitConverter.GetBytes(b.Resources[0].Amount).CopyTo(record, 4); memory.Put(b.ResourcePointer, record);
        }
        return (scan, buildings, memory);
    }
    public static void Run()
    {
        var (scan, buildings, memory) = Fixture();
        if (ManualStockEditor.ApplyChecked(scan, buildings, 33, 5, memory) != 2 ||
            BitConverter.ToSingle(memory.Read(0x30004, 4)) != 55 || BitConverter.ToSingle(memory.Read(0x40004, 4)) != 85)
            throw new Exception("One-time addition did not apply to each selected building");
        Console.WriteLine("PASS one-time addition increases existing stock in both selected buildings by the requested amount");
        (scan, buildings, memory) = Fixture(); memory.Put(0x40004, BitConverter.GetBytes(81f));
        try { ManualStockEditor.ApplyChecked(scan, buildings, 33, 5, memory); throw new Exception("Stale quantity accepted"); }
        catch (InvalidOperationException) when (memory.Writes == 0) { Console.WriteLine("PASS stale stock blocks the one-time batch before any write"); }
        (scan, buildings, memory) = Fixture();
        try { ManualStockEditor.ApplyChecked(scan, buildings, 87, 5, memory); throw new Exception("Missing resource accepted"); }
        catch (InvalidOperationException) when (memory.Writes == 0) { Console.WriteLine("PASS a one-time addition does not create an unverified resource record"); }
        (scan, buildings, memory) = Fixture();
        try { ManualStockEditor.Prepare(scan with { UiCandidates = [new("uiDealList", 0x30004, 0x9999)] }, buildings, 33, 5); throw new Exception("Overlap accepted"); }
        catch (InvalidOperationException) { Console.WriteLine("PASS a one-time addition rejects an overlapping UI object"); }
        (scan, buildings, memory) = Fixture(); memory.FailOnWrite = 2;
        try { ManualStockEditor.ApplyChecked(scan, buildings, 33, 5, memory); throw new Exception("Uncertain write ignored"); }
        catch (PartialGameWriteException) when (memory.Writes == 2) { Console.WriteLine("PASS an uncertain one-time addition requires backup recovery"); }
    }
}
