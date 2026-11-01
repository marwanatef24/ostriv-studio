using System.Buffers.Binary;
using OstrivStudio.Core;

internal static class CapacityEditorTests
{
    private sealed class Memory : FishBulkEditor.IMemory
    {
        private readonly Dictionary<ulong, byte> _bytes = [];
        public int Writes, FailOnWrite;
        public void Put(ulong address, byte[] bytes) { for (int i = 0; i < bytes.Length; i++) _bytes[address + (uint)i] = bytes[i]; }
        public byte[] Read(ulong address, int size) => Enumerable.Range(0, size).Select(i => _bytes.GetValueOrDefault(address + (uint)i)).ToArray();
        public void Write(ulong address, byte[] bytes) { Writes++; Put(address, bytes); if (Writes == FailOnWrite) throw new IOException("uncertain write"); }
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); Console.WriteLine("PASS " + message); }
    private static (DiscoveryResult Scan, BuildingCandidate[] Buildings, Memory Memory) Fixture()
    {
        var buildings = new[] {
            new BuildingCandidate("building_granary", 0x10000, 0x9000, 0x30000, 1, [new(0, 87, 100, 0, 0, 0)], "sample", 20000),
            new BuildingCandidate("building_warehouse", 0x20000, 0x9100, 0x40000, 1, [new(0, 33, 50, 0, 0, 0)], "sample", 10000)
        };
        var scan = new DiscoveryResult(1, DateTime.UtcNow, "test", FishBulkEditor.SupportedHash, 408, 416, buildings, 1, true, 0, "");
        var memory = new Memory();
        foreach (var b in buildings)
        {
            var header = new byte[424];
            BinaryPrimitives.WriteUInt64LittleEndian(header, b.Vtable);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(408), b.ResourceCount);
            BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(416), b.ResourcePointer);
            memory.Put(b.Address, header);
            memory.Put(b.Address + CapacityObservation.Offset, BitConverter.GetBytes(b.ObservedCapacity!.Value));
            var record = new byte[20]; BinaryPrimitives.WriteInt32LittleEndian(record, b.Resources[0].Id);
            BitConverter.GetBytes(b.Resources[0].Amount).CopyTo(record, 4); memory.Put(b.ResourcePointer, record);
        }
        return (scan, buildings, memory);
    }
    public static void Run()
    {
        var (scan, buildings, memory) = Fixture();
        Check(CapacityEditor.ApplyChecked(scan, buildings, 22000, memory) == 2 && memory.Writes == 2 &&
            BitConverter.ToSingle(memory.Read(0x104D0, 4)) == 22000 && BitConverter.ToSingle(memory.Read(0x204D0, 4)) == 22000,
            "capacity editor increases and reads back both reviewed buildings");
        (scan, buildings, memory) = Fixture();
        try { CapacityEditor.ApplyChecked(scan, buildings, 15000, memory); throw new Exception("Capacity reduction accepted"); }
        catch (InvalidOperationException) when (memory.Writes == 0) { Console.WriteLine("PASS capacity reduction is rejected before any write"); }
        (scan, buildings, memory) = Fixture(); memory.Put(0x204D0, BitConverter.GetBytes(9999f));
        try { CapacityEditor.ApplyChecked(scan, buildings, 22000, memory); throw new Exception("Stale capacity accepted"); }
        catch (InvalidOperationException) when (memory.Writes == 0) { Console.WriteLine("PASS stale capacity blocks the complete batch before any write"); }
        (scan, buildings, memory) = Fixture(); memory.FailOnWrite = 2;
        try { CapacityEditor.ApplyChecked(scan, buildings, 22000, memory); throw new Exception("Partial capacity failure ignored"); }
        catch (PartialGameWriteException) when (memory.Writes == 2) { Console.WriteLine("PASS an uncertain capacity write requires backup recovery and is not retried"); }
        (scan, buildings, memory) = Fixture();
        try { CapacityEditor.Prepare(scan with { GameSha256 = "unknown" }, buildings, 22000); throw new Exception("Unsupported build accepted"); }
        catch (InvalidOperationException) { Console.WriteLine("PASS capacity editor rejects an unsupported game build"); }
        try { CapacityEditor.Prepare(scan, [buildings[0], buildings[0]], 22000); throw new Exception("Duplicate candidate accepted"); }
        catch (InvalidOperationException) { Console.WriteLine("PASS capacity editor rejects duplicate candidates"); }
        (scan, buildings, memory) = Fixture();
        var hay = buildings[0] with { ClassName = "building_hay_barrack_big", ObservedCapacity = 7000f };
        memory.Put(hay.Address + CapacityObservation.Offset, BitConverter.GetBytes(7000f));
        scan = scan with { Buildings = [hay] };
        Check(CapacityEditor.ApplyChecked(scan, [hay], 7001, memory) == 1 && BitConverter.ToSingle(memory.Read(hay.Address + CapacityObservation.Offset, 4)) == 7001,
            "big hay barrack capacity can be increased and read back");
        (scan, buildings, memory) = Fixture();
        var unreviewed = buildings[0] with { ClassName = "building_camp_storage" };
        try { CapacityEditor.Prepare(scan with { Buildings = [unreviewed] }, [unreviewed], 22000); throw new Exception("Unsupported class accepted"); }
        catch (InvalidOperationException) { Console.WriteLine("PASS capacity editor rejects unverified building classes"); }
    }
}
