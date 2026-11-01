using System.Buffers.Binary;
using OstrivStudio.Core;

internal static class FarmCropEditorTests
{
    private sealed class Memory : FishBulkEditor.IMemory
    {
        private readonly Dictionary<ulong, byte> _bytes = [];
        public int Writes, FailOnWrite; public bool Fail, FlapUnrelatedByte;
        public void Put(ulong at, byte[] data) { for (int i = 0; i < data.Length; i++) _bytes[at + (uint)i] = data[i]; }
        public byte[] Read(ulong at, int size)
        {
            var result = Enumerable.Range(0, size).Select(i => _bytes.GetValueOrDefault(at + (uint)i)).ToArray();
            if (FlapUnrelatedByte && at == 0x10000) _bytes[at + 0x40] = (byte)(1 - _bytes.GetValueOrDefault(at + 0x40));
            return result;
        }
        public void Write(ulong at, byte[] data) { Writes++; Put(at, data); if (Fail || Writes == FailOnWrite) throw new IOException("uncertain write"); }
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); Console.WriteLine("PASS " + message); }
    private static (DiscoveryResult Scan, BuildingCandidate Farm, Memory Memory) Fixture()
    {
        var farm = new BuildingCandidate("building_grain_farm", 0x10000, 0x9000, 0, 0, [], "sample");
        var first = new BuildingCandidate("building_grain_field", 0x20000, 0x9100, 0, 0, [], "sample");
        var second = new BuildingCandidate("building_grain_field", 0x30000, 0x9100, 0, 0, [], "sample");
        var scan = new DiscoveryResult(1, DateTime.UtcNow, "test", FishBulkEditor.SupportedHash, 408, 416, [farm, first, second], 1, true, 0, "");
        var memory = new Memory();
        var farmHeader = new byte[0x8B8]; BinaryPrimitives.WriteUInt64LittleEndian(farmHeader, farm.Vtable);
        BinaryPrimitives.WriteInt32LittleEndian(farmHeader.AsSpan(0x8A8), 2); BinaryPrimitives.WriteUInt64LittleEndian(farmHeader.AsSpan(0x8B0), 0x50000); memory.Put(farm.Address, farmHeader);
        var pointers = new byte[16]; BinaryPrimitives.WriteUInt64LittleEndian(pointers, first.Address); BinaryPrimitives.WriteUInt64LittleEndian(pointers.AsSpan(8), second.Address); memory.Put(0x50000, pointers);
        foreach (var item in new[] { (Field: first, Pointer: 0x60000UL), (Field: second, Pointer: 0x70000UL) })
        {
            var header = new byte[0x960]; BinaryPrimitives.WriteUInt64LittleEndian(header, item.Field.Vtable);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(0x950), 4); BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(0x958), item.Pointer); memory.Put(item.Field.Address, header);
            var crops = new byte[16]; int[] values = [7, 8, 13, 8];
            for (int i = 0; i < values.Length; i++) BinaryPrimitives.WriteInt32LittleEndian(crops.AsSpan(i * 4), values[i]); memory.Put(item.Pointer, crops);
        }
        return (scan, farm, memory);
    }
    public static void Run()
    {
        var (scan, farm, memory) = Fixture();
        memory.FlapUnrelatedByte = true;
        var reviewed = FarmCropEditor.Capture(scan, farm, memory);
        Check(reviewed.Fields.Count == 2 && reviewed.Fields[0].Crops.SequenceEqual([7, 8, 13, 8]), "farm inspection links reviewed fields and crop sequences");
        FarmCropEditor.ApplyChecked(reviewed, 1, [7, 13, 8, 8], memory);
        Check(memory.Writes == 1 && FarmCropEditor.Capture(scan, farm, memory).Fields[0].Crops.SequenceEqual([7, 13, 8, 8]) &&
            FarmCropEditor.Capture(scan, farm, memory).Fields[1].Crops.SequenceEqual([7, 8, 13, 8]),
            "farm edit reorders only future crops of the selected field");
        (scan, farm, memory) = Fixture(); reviewed = FarmCropEditor.Capture(scan, farm, memory);
        try { FarmCropEditor.ApplyChecked(reviewed, 1, [8, 7, 13, 8], memory); throw new Exception("Current crop changed"); }
        catch (InvalidOperationException) when (memory.Writes == 0) { Console.WriteLine("PASS farm edit refuses to change the current crop"); }
        try { FarmCropEditor.ApplyChecked(reviewed, 1, [7, 13, 9, 8], memory); throw new Exception("Unverified crop accepted"); }
        catch (InvalidOperationException) when (memory.Writes == 0) { Console.WriteLine("PASS farm edit refuses to introduce an unverified crop"); }
        memory.Put(0x70004, BitConverter.GetBytes(13));
        try { FarmCropEditor.ApplyChecked(reviewed, 1, [7, 13, 8, 8], memory); throw new Exception("Stale field accepted"); }
        catch (InvalidOperationException) when (memory.Writes == 0) { Console.WriteLine("PASS a changed field blocks the farm edit before any write"); }
        (scan, farm, memory) = Fixture(); reviewed = FarmCropEditor.Capture(scan, farm, memory); memory.Fail = true;
        try { FarmCropEditor.ApplyChecked(reviewed, 1, [7, 13, 8, 8], memory); throw new Exception("Uncertain farm write ignored"); }
        catch (PartialGameWriteException) when (memory.Writes == 1) { Console.WriteLine("PASS an uncertain farm write requires backup recovery"); }
        (scan, farm, memory) = Fixture(); reviewed = FarmCropEditor.Capture(scan, farm, memory);
        FarmCropEditor.ApplyChecked(reviewed, 1, [7, 13, 6, 8], memory);
        Check(memory.Writes == 1 && FarmCropEditor.Capture(scan, farm, memory).Fields[0].Crops.SequenceEqual([7, 13, 6, 8]),
            "farm edit replaces a future crop with observed wheat while preserving the current crop");
        (scan, farm, memory) = Fixture(); reviewed = FarmCropEditor.Capture(scan, farm, memory);
        var batch = new Dictionary<int, IReadOnlyList<int>> { [1] = new[] { 7, 13, 8, 8 }, [2] = new[] { 7, 13, 8, 8 } };
        Check(FarmCropEditor.ApplyBatchChecked(reviewed, batch, memory) == 2 && memory.Writes == 2 &&
            FarmCropEditor.Capture(scan, farm, memory).Fields.All(f => f.Crops.SequenceEqual([7, 13, 8, 8])),
            "farm batch applies a reviewed rotation to both matching fields");
        (scan, farm, memory) = Fixture(); reviewed = FarmCropEditor.Capture(scan, farm, memory);
        batch[2] = new[] { 8, 7, 13, 8 };
        try { FarmCropEditor.ApplyBatchChecked(reviewed, batch, memory); throw new Exception("Invalid second field accepted"); }
        catch (InvalidOperationException) when (memory.Writes == 0) { Console.WriteLine("PASS an invalid field blocks the whole farm batch before writes"); }
        (scan, farm, memory) = Fixture(); reviewed = FarmCropEditor.Capture(scan, farm, memory);
        batch[2] = new[] { 7, 13, 8, 8 }; memory.FailOnWrite = 2;
        try { FarmCropEditor.ApplyBatchChecked(reviewed, batch, memory); throw new Exception("Partial farm batch ignored"); }
        catch (PartialGameWriteException) when (memory.Writes == 2) { Console.WriteLine("PASS an uncertain farm batch requires backup recovery"); }
        (scan, farm, memory) = Fixture();
        try { FarmCropEditor.Capture(scan with { GameSha256 = "unknown" }, farm, memory); throw new Exception("Unknown game accepted"); }
        catch (InvalidOperationException) { Console.WriteLine("PASS farm inspection rejects an unsupported game build"); }
        (scan, farm, memory) = Fixture();
        try { FarmCropEditor.Capture(scan with { UiCandidates = [new("uiDealList", 0x60004, 0x9999)] }, farm, memory); throw new Exception("Overlapping UI candidate accepted"); }
        catch (InvalidOperationException) when (memory.Writes == 0) { Console.WriteLine("PASS farm inspection rejects a crop array overlapping a scanned UI object"); }
        (scan, farm, memory) = Fixture();
        memory.Put(0x209F8, BitConverter.GetBytes(0xA5u)); memory.Put(0x309F8, BitConverter.GetBytes(0x11u));
        var plough = FarmPloughEditor.Capture(FarmCropEditor.Capture(scan, farm, memory), memory);
        Check(FarmPloughEditor.ApplyChecked(plough, [1, 2], false, memory) == 2 &&
            BitConverter.ToUInt32(memory.Read(0x209F8, 4)) == 0xA4 && BitConverter.ToUInt32(memory.Read(0x309F8, 4)) == 0x10,
            "plough settings apply to both fields without changing other flag bits");
        (scan, farm, memory) = Fixture();
        memory.Put(0x209F8, BitConverter.GetBytes(1u)); memory.Put(0x309F8, BitConverter.GetBytes(1u));
        plough = FarmPloughEditor.Capture(FarmCropEditor.Capture(scan, farm, memory), memory);
        memory.Put(0x309F8, BitConverter.GetBytes(0u));
        try { FarmPloughEditor.ApplyChecked(plough, [1, 2], false, memory); throw new Exception("Stale plough setting accepted"); }
        catch (InvalidOperationException) when (memory.Writes == 0) { Console.WriteLine("PASS a stale plough setting blocks the whole batch before writes"); }
        (scan, farm, memory) = Fixture();
        memory.Put(0x209F8, BitConverter.GetBytes(1u)); memory.Put(0x309F8, BitConverter.GetBytes(1u));
        plough = FarmPloughEditor.Capture(FarmCropEditor.Capture(scan, farm, memory), memory); memory.FailOnWrite = 2;
        try { FarmPloughEditor.ApplyChecked(plough, [1, 2], false, memory); throw new Exception("Partial plough edit ignored"); }
        catch (PartialGameWriteException) when (memory.Writes == 2) { Console.WriteLine("PASS an uncertain plough batch requires backup recovery"); }
    }
}
