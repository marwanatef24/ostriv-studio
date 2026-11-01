using System.Buffers.Binary;
using OstrivStudio.Core;

internal static class FishBulkTests
{
    private sealed class FakeMemory : FishBulkEditor.IMemory
    {
        public readonly Dictionary<ulong, byte> Bytes = [];
        public int Writes;
        public int FailAtWrite = -1;
        public void Put(ulong address, byte[] data) { for (int i = 0; i < data.Length; i++) Bytes[address + (uint)i] = data[i]; }
        public byte[] Read(ulong address, int size) => Enumerable.Range(0, size).Select(i => Bytes[address + (uint)i]).ToArray();
        public void Write(ulong address, byte[] bytes) { Writes++; if (Writes == FailAtWrite) throw new IOException("simulated write failure"); Put(address, bytes); }
    }
    private static (DiscoveryResult Scan, BuildingCandidate[] Buildings, FakeMemory Memory) Fixture()
    {
        var buildings = new[] {
            new BuildingCandidate("building_fishery", 0x1000, 0x9000, 0x3000, 1, [new(0, 27, 50, 0, 0, 0)], "candidate"),
            new BuildingCandidate("building_fishery", 0x2000, 0x9000, 0x4000, 1, [new(0, 27, 60, 0, 0, 0)], "candidate") };
        var memory = new FakeMemory();
        foreach (var b in buildings)
        {
            var header = new byte[424];
            BinaryPrimitives.WriteUInt64LittleEndian(header, b.Vtable);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(408), b.ResourceCount);
            BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(416), b.ResourcePointer);
            memory.Put(b.Address, header);
            var record = new byte[20];
            BinaryPrimitives.WriteInt32LittleEndian(record, 27);
            BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(4), BitConverter.SingleToInt32Bits(b.Resources[0].Amount));
            memory.Put(b.ResourcePointer, record);
        }
        return (new DiscoveryResult(42, DateTime.UtcNow, "test", FishBulkEditor.SupportedHash, 408, 416, buildings, 1, true, 0, "test"), buildings, memory);
    }
    public static void Run()
    {
        var (scan, buildings, memory) = Fixture();
        FishBulkEditor.ApplyChecked(scan, buildings, memory);
        if (memory.Writes != 2 || BitConverter.ToSingle(memory.Read(0x3004, 4)) != 51 || BitConverter.ToSingle(memory.Read(0x4004, 4)) != 61) throw new Exception("Bulk edit failed in fake memory");
        Console.WriteLine("PASS bulk edit writes and reads back both existing records");
        (scan, buildings, memory) = Fixture(); memory.Put(0x4004, BitConverter.GetBytes(999f));
        try { FishBulkEditor.ApplyChecked(scan, buildings, memory); throw new Exception("Stale resource accepted"); }
        catch (InvalidOperationException) when (memory.Writes == 0) { Console.WriteLine("PASS stale second building prevents all writes"); }
        (scan, buildings, memory) = Fixture(); memory.FailAtWrite = 2;
        try { FishBulkEditor.ApplyChecked(scan, buildings, memory); throw new Exception("Failure ignored"); }
        catch (InvalidOperationException ex) when (ex.Message.Contains("partially applied") && memory.Writes == 2) { Console.WriteLine("PASS partial batch failure is reported without retry"); }
        (scan, buildings, memory) = Fixture();
        try { FishBulkEditor.ApplyChecked(scan with { GameSha256 = "unknown" }, buildings, memory); throw new Exception("Unknown game accepted"); }
        catch (InvalidOperationException) when (memory.Writes == 0) { Console.WriteLine("PASS unsupported build cannot be written"); }
        (scan, buildings, memory) = Fixture();
        try { FishBulkEditor.ApplyChecked(scan, [buildings[0], buildings[0]], memory); throw new Exception("Duplicate target accepted"); }
        catch (InvalidOperationException) when (memory.Writes == 0) { Console.WriteLine("PASS duplicate target prevents all writes"); }

        (scan, buildings, memory) = Fixture();
        var profile = new ProfileDocument();
        profile.AutomationProfiles["My profile"].MaintenanceRules.Add(new() { ResourceId = 27, Target = 51, Enabled = true, BuildingType = "building_fishery" });
        if (SavedRulePlanner.CheckFishRule(profile, scan, [buildings[0]]) != 51) throw new Exception("Saved rule rejected");
        FishBulkEditor.ApplyChecked(scan, [buildings[0]], memory);
        if (memory.Writes != 1 || BitConverter.ToSingle(memory.Read(0x3004, 4)) != 51) throw new Exception("Saved target was not applied");
        Console.WriteLine("PASS matching saved fish rule reaches its target in fake memory");
        (scan, buildings, memory) = Fixture();
        buildings[1] = buildings[1] with { Resources = [new(0, 27, 50, 0, 0, 0)] };
        scan = scan with { Buildings = buildings };
        memory.Put(0x4004, BitConverter.GetBytes(50f));
        SavedRulePlanner.CheckFishRule(profile, scan, buildings);
        FishBulkEditor.ApplyChecked(scan, buildings, memory);
        if (memory.Writes != 2 || BitConverter.ToSingle(memory.Read(0x4004, 4)) != 51) throw new Exception("Saved bulk target was not applied");
        Console.WriteLine("PASS matching saved rule applies to two distinct docks in fake memory");
        (scan, buildings, memory) = Fixture();
        try { SavedRulePlanner.CheckFishRule(profile, scan, buildings); throw new Exception("Mismatched targets accepted"); }
        catch (InvalidOperationException) { Console.WriteLine("PASS saved rule rejects batches whose target is not one above every total"); }
        profile.AutomationProfiles["My profile"].MaintenanceRules[0].BuildingType = "building_granary";
        try { SavedRulePlanner.CheckFishRule(profile, scan, [buildings[0]]); throw new Exception("Wrong scope accepted"); }
        catch (InvalidOperationException) { Console.WriteLine("PASS saved rule rejects nonmatching building scopes"); }
        profile.AutomationProfiles["My profile"].MaintenanceRules[0].BuildingType = "";
        try { SavedRulePlanner.CheckFishRule(profile, scan with { GameSha256 = "unknown" }, [buildings[0]]); throw new Exception("Unknown game accepted"); }
        catch (InvalidOperationException) { Console.WriteLine("PASS saved rule rejects unsupported game builds"); }
        profile.AutomationProfiles["My profile"].MaintenanceRules[0].Enabled = false;
        try { SavedRulePlanner.CheckFishRule(profile, scan, [buildings[0]]); throw new Exception("Disabled rule accepted"); }
        catch (InvalidOperationException) { Console.WriteLine("PASS disabled saved rule cannot be applied"); }
        profile.AutomationProfiles["My profile"].MaintenanceRules[0].Enabled = true;
        profile.AutomationProfiles["My profile"].MaintenanceRules[0].Mode = MaintenanceMode.Across;
        try { SavedRulePlanner.CheckFishRule(profile, scan, [buildings[0]]); throw new Exception("Across rule accepted"); }
        catch (InvalidOperationException) { Console.WriteLine("PASS across-building rule is rejected by the per-building fish writer"); }
    }
}
