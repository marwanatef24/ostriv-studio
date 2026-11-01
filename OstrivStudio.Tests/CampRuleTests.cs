using System.Buffers.Binary;
using OstrivStudio.Core;

internal static class CampRuleTests
{
    private sealed class FakeMemory : FishBulkEditor.IMemory
    {
        public Dictionary<ulong, byte> Bytes { get; } = [];
        public int Writes;
        public bool FailAfterWrite;
        public int FailOnWrite;
        public Action<FakeMemory>? AfterWrite;
        public void Put(ulong address, byte[] data) { for (int i = 0; i < data.Length; i++) Bytes[address + (uint)i] = data[i]; }
        public byte[] Read(ulong address, int size) => Enumerable.Range(0, size).Select(i => Bytes[address + (uint)i]).ToArray();
        public void Write(ulong address, byte[] bytes) { Writes++; Put(address, bytes); AfterWrite?.Invoke(this); if (FailAfterWrite || Writes == FailOnWrite) throw new IOException("simulated uncertain write"); }
    }
    private static (ProfileDocument Profile, DiscoveryResult Scan, BuildingCandidate Building, FakeMemory Memory) Fixture()
    {
        var b = new BuildingCandidate("building_camp_storage", 0x1000, 0x9000, 0x3000, 1, [new(0, 92, 1999, 0, 0, 0)], "candidate");
        var scan = new DiscoveryResult(42, DateTime.UtcNow, "test", FishBulkEditor.SupportedHash, 408, 416, [b], 1, true, 0, "");
        var profile = new ProfileDocument();
        profile.AutomationProfiles["My profile"].MaintenanceRules.Add(new() { ResourceId = 92, BuildingType = "building_camp_storage", Enabled = true, Target = 2000 });
        var memory = new FakeMemory();
        var header = new byte[424];
        BinaryPrimitives.WriteUInt64LittleEndian(header, b.Vtable);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(408), b.ResourceCount);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(416), b.ResourcePointer);
        memory.Put(b.Address, header);
        var record = new byte[20];
        BinaryPrimitives.WriteInt32LittleEndian(record, 92);
        BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(4), BitConverter.SingleToInt32Bits(1999));
        memory.Put(b.ResourcePointer, record);
        return (profile, scan, b, memory);
    }
    private static void Check(bool value, string label) { if (!value) throw new Exception(label); Console.WriteLine("PASS " + label); }
    private static (ProfileDocument Profile, DiscoveryResult Scan, BuildingCandidate Building, FakeMemory Memory) BatchFixture()
    {
        var b = new BuildingCandidate("building_camp_storage", 0x1000, 0x9000, 0x3000, 3,
            [new(0, 92, 2000, 0, 0, 0), new(1, 33, 999, 0, 0, 0), new(2, 69, 1999, 0, 0, 0)], "candidate");
        var scan = new DiscoveryResult(42, DateTime.UtcNow, "test", FishBulkEditor.SupportedHash, 408, 416, [b], 1, true, 0, "");
        var profile = new ProfileDocument();
        foreach (var (id, target) in new[] { (92, 2000), (33, 1000), (69, 2000) })
            profile.AutomationProfiles["My profile"].MaintenanceRules.Add(new() { ResourceId = id, BuildingType = "building_camp_storage", Enabled = true, Target = target });
        var memory = new FakeMemory();
        var header = new byte[424];
        BinaryPrimitives.WriteUInt64LittleEndian(header, b.Vtable);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(408), b.ResourceCount);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(416), b.ResourcePointer);
        memory.Put(b.Address, header);
        foreach (var r in b.Resources)
        {
            var record = new byte[20];
            BinaryPrimitives.WriteInt32LittleEndian(record, r.Id);
            BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(4), BitConverter.SingleToInt32Bits(r.Amount));
            memory.Put(b.ResourcePointer + (ulong)r.Index * 20, record);
        }
        return (profile, scan, b, memory);
    }
    public static void Run()
    {
        var (profile, scan, b, memory) = Fixture();
        try { CampRuleExecutor.ApplyToGame(profile, scan, [b]); throw new Exception("A camp without an anchor was accepted"); }
        catch (InvalidOperationException ex) when (ex.Message.Contains("anchor")) { Console.WriteLine("PASS live camp writes require a separate nonempty anchor"); }
        Check(CampRuleExecutor.ApplyChecked(profile, scan, [b], memory) && memory.Writes == 1 && BitConverter.ToSingle(memory.Read(0x3004, 4)) == 2000, "saved broadcloth rule writes and verifies the supported one-unit change");
        (profile, scan, b, memory) = Fixture();
        memory.Put(0x3004, BitConverter.GetBytes(1998f));
        try { CampRuleExecutor.ApplyChecked(profile, scan, [b], memory); throw new Exception("Stale record accepted"); }
        catch (InvalidOperationException) when (memory.Writes == 0) { Console.WriteLine("PASS stale camp resource prevents the rule write"); }
        (profile, scan, b, memory) = Fixture();
        try { CampRuleExecutor.ApplyChecked(profile, scan with { GameSha256 = "unknown" }, [b], memory); throw new Exception("Unsupported build accepted"); }
        catch (InvalidOperationException) when (memory.Writes == 0) { Console.WriteLine("PASS camp rule rejects unsupported game builds"); }
        (profile, scan, b, memory) = Fixture();
        profile.AutomationProfiles["My profile"].MaintenanceRules[0].Target = 3000;
        try { CampRuleExecutor.ApplyChecked(profile, scan, [b], memory); throw new Exception("Unverified target accepted"); }
        catch (InvalidOperationException) when (memory.Writes == 0) { Console.WriteLine("PASS camp rule rejects unverified targets"); }
        (profile, scan, b, memory) = Fixture();
        profile.AutomationProfiles["My profile"].MaintenanceRules[0].Enabled = false;
        try { CampRuleExecutor.ApplyChecked(profile, scan, [b], memory); throw new Exception("Disabled rule accepted"); }
        catch (InvalidOperationException) when (memory.Writes == 0) { Console.WriteLine("PASS disabled broadcloth rule cannot write"); }
        (profile, scan, b, memory) = Fixture(); memory.FailAfterWrite = true;
        try { CampRuleExecutor.ApplyChecked(profile, scan, [b], memory); throw new Exception("Write failure ignored"); }
        catch (InvalidOperationException ex) when (memory.Writes == 1 && ex.Message.Contains("reload")) { Console.WriteLine("PASS uncertain camp write reports reload instead of retry"); }
        (profile, scan, b, memory) = Fixture();
        var met = b with { Resources = [new(0, 92, 2000, 0, 0, 0)] };
        scan = scan with { Buildings = [met] };
        Check(!CampRuleExecutor.ApplyChecked(profile, scan, [met], memory) && memory.Writes == 0, "a satisfied broadcloth rule makes no write");

        (profile, scan, b, memory) = BatchFixture();
        var batch = CampRuleExecutor.PrepareBatch(profile, scan, [b]);
        Check(batch.Entries.Count == 2 && batch.Skipped.Any(x => x.Contains("already met")), "batch selects only supported saved rules needing a one-unit increase");
        Check(CampRuleExecutor.ApplyBatchChecked(profile, scan, [b], memory) == 2 && memory.Writes == 2 &&
            BitConverter.ToSingle(memory.Read(0x3018, 4)) == 1000 && BitConverter.ToSingle(memory.Read(0x302c, 4)) == 2000,
            "camp batch applies and reads back two separate saved rules");
        (profile, scan, b, memory) = BatchFixture();
        memory.Put(0x3018, BitConverter.GetBytes(998f));
        try { CampRuleExecutor.ApplyBatchChecked(profile, scan, [b], memory); throw new Exception("Stale batch accepted"); }
        catch (InvalidOperationException) when (memory.Writes == 0) { Console.WriteLine("PASS stale batch resource prevents all writes"); }
        (profile, scan, b, memory) = BatchFixture(); memory.FailOnWrite = 2;
        try { CampRuleExecutor.ApplyBatchChecked(profile, scan, [b], memory); throw new Exception("Partial failure ignored"); }
        catch (InvalidOperationException ex) when (memory.Writes == 2 && ex.Message.Contains("partially") && ex.Message.Contains("reload"))
        { Console.WriteLine("PASS partial camp batch requires backup reload and no retry"); }
        (profile, scan, b, memory) = BatchFixture();
        profile.AutomationProfiles["My profile"].MaintenanceRules.Add(new() { ResourceId = 33, BuildingType = "", Enabled = true, Target = 1000 });
        try { CampRuleExecutor.ApplyBatchChecked(profile, scan, [b], memory); throw new Exception("Conflicting rules accepted"); }
        catch (InvalidOperationException) when (memory.Writes == 0) { Console.WriteLine("PASS conflicting camp rules prevent all writes"); }
        (profile, scan, b, memory) = BatchFixture();
        profile.AutomationProfiles["My profile"].MaintenanceRules.Single(r => r.ResourceId == 33).Target = 5000;
        Check(CampRuleExecutor.PrepareBatch(profile, scan, [b]).Entries is [{ ResourceName: "Horse tack" }], "unverified targets are skipped in a camp batch");
        (profile, scan, b, memory) = BatchFixture();
        try { CampRuleExecutor.ApplyBatchChecked(profile, scan with { GameSha256 = "unknown" }, [b], memory); throw new Exception("Unsupported batch build accepted"); }
        catch (InvalidOperationException) when (memory.Writes == 0) { Console.WriteLine("PASS camp batch rejects unsupported game builds"); }
        (profile, scan, b, memory) = BatchFixture();
        memory.AfterWrite = m => { if (m.Writes == 1) m.Put(0x302c, BitConverter.GetBytes(1998f)); };
        try { CampRuleExecutor.ApplyBatchChecked(profile, scan, [b], memory); throw new Exception("Concurrent change ignored"); }
        catch (InvalidOperationException ex) when (memory.Writes == 1 && ex.Message.Contains("partially"))
        { Console.WriteLine("PASS concurrent resource change stops the batch after the first write"); }
    }
}
