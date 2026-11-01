using System.Buffers.Binary;
using OstrivStudio.Core;

internal static class TradingStockLimitsTests
{
    private sealed class Memory : FishBulkEditor.IMemory
    {
        private readonly Dictionary<ulong, byte> _bytes = [];
        public int Writes, FailOnWrite;
        public void Put(ulong address, byte[] bytes) { for (int i = 0; i < bytes.Length; i++) _bytes[address + (uint)i] = bytes[i]; }
        public byte[] Read(ulong address, int size) => Enumerable.Range(0, size).Select(i => _bytes.GetValueOrDefault(address + (uint)i)).ToArray();
        public void Write(ulong address, byte[] bytes) { Writes++; Put(address, bytes); if (Writes == FailOnWrite) throw new IOException("uncertain write"); }
    }
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); Console.WriteLine("PASS " + message); }
    private static (DiscoveryResult Scan, BuildingCandidate[] Posts, Memory Memory) Fixture()
    {
        var posts = new[] {
            new BuildingCandidate("building_trading_post", 0x10000, 0x9000, 0x50000, 1, [new(0, 12, 10, 0, 0, 0)], "sample", null),
            new BuildingCandidate("building_trading_dock", 0x12000, 0x9100, 0x60000, 1, [new(0, 12, 20, 0, 0, 0)], "sample", null)
        };
        var scan = new DiscoveryResult(1, DateTime.UtcNow, "test", FishBulkEditor.SupportedHash, 408, 416, posts, 1, true, 0, "");
        var memory = new Memory();
        for (int i = 0; i < posts.Length; i++)
        {
            var b = posts[i]; ulong list = (ulong)(0x30000 + i * 0x10000);
            var header = new byte[0x810];
            BinaryPrimitives.WriteUInt64LittleEndian(header, b.Vtable);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(408), b.ResourceCount);
            BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(416), b.ResourcePointer);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(0x800), 2);
            BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(0x808), list);
            memory.Put(b.Address, header);
            byte[] records = new byte[40];
            BinaryPrimitives.WriteInt32LittleEndian(records, 46); BitConverter.GetBytes(10000f).CopyTo(records, 4);
            BinaryPrimitives.WriteInt32LittleEndian(records.AsSpan(20), 12); BitConverter.GetBytes(i == 0 ? 5000f : 6000f).CopyTo(records, 24);
            memory.Put(list, records);
        }
        return (scan, posts, memory);
    }
    public static void Run()
    {
        var (scan, posts, memory) = Fixture();
        var snapshot = TradingStockLimits.Capture(scan, posts, memory);
        Check(snapshot.Posts.Count == 2 && snapshot.Posts[0].Limits.Single(x => x.ResourceId == 12).Target == 5000f,
            "post and dock export targets are decoded from the reviewed lists");
        foreach (var deal in new[] {
            new TradeDeal(2, 12, 0, 1, 7000, 0), // A post route cannot target a mixed post/dock selection.
            new TradeDeal(99, 12, 0, 1, 7000, 0), // An unknown route cannot select any building.
            new TradeDeal(4, 12, 1, 1, 7000, 0) // An import is not an export target.
        })
        {
            try { TradingStockLimits.ApplyToGame(snapshot, 12, 7000, CancellationToken.None, deal); throw new Exception("Invalid deal accepted"); }
            catch (InvalidOperationException e) when (e.Message.Contains("reviewed export deal") && memory.Writes == 0) { }
        }
        Console.WriteLine("PASS mismatched, unknown and import deals are rejected before process access");
        Check(TradingStockLimits.ApplyChecked(snapshot, 12, 7000, memory) == 2 && memory.Writes == 2 &&
            BitConverter.ToSingle(memory.Read(0x30018, 4)) == 7000 && BitConverter.ToSingle(memory.Read(0x40018, 4)) == 7000,
            "export targets increase across a reviewed post and dock");
        (scan, posts, memory) = Fixture(); snapshot = TradingStockLimits.Capture(scan, posts, memory);
        ExportAllocation[] allocation = [new(posts[0].Address, 12, 7001), new(posts[1].Address, 12, 7002)];
        Check(TradingStockLimits.ApplyAllocationsChecked(snapshot, allocation, memory) == 2 &&
            BitConverter.ToSingle(memory.Read(0x30018, 4)) == 7001 && BitConverter.ToSingle(memory.Read(0x40018, 4)) == 7002,
            "export allocation writes different reviewed targets and verifies both");
        (scan, posts, memory) = Fixture(); snapshot = TradingStockLimits.Capture(scan, posts, memory);
        var increment = TradingStockLimits.CreateIncrementPlan(snapshot, 12, 25);
        Check(increment.Select(x => x.Minimum).SequenceEqual([5025m, 6025m]) &&
            TradingStockLimits.ApplyIncrementChecked(snapshot, 12, 25, memory) == 2 &&
            BitConverter.ToSingle(memory.Read(0x30018, 4)) == 5025 && BitConverter.ToSingle(memory.Read(0x40018, 4)) == 6025,
            "bulk export addition increases each building from its own existing target");
        (scan, posts, memory) = Fixture(); snapshot = TradingStockLimits.Capture(scan, posts, memory);
        foreach (decimal amount in new[] { 0m, -1m, 0.5m, 1_000_001m, 999_000m })
        {
            try { TradingStockLimits.ApplyIncrementChecked(snapshot, 12, amount, memory); throw new Exception("Invalid or excessive export addition accepted"); }
            catch (InvalidOperationException) when (memory.Writes == 0) { }
        }
        try { TradingStockLimits.ApplyIncrementChecked(snapshot, 87, 1, memory); throw new Exception("Missing export addition accepted"); }
        catch (InvalidOperationException) when (memory.Writes == 0) { }
        Console.WriteLine("PASS invalid additions, resulting targets above the limit and missing export entries block all writes");
        memory.Put(0x40018, BitConverter.GetBytes(6001f));
        try { TradingStockLimits.ApplyIncrementChecked(snapshot, 12, 25, memory); throw new Exception("Stale export addition accepted"); }
        catch (InvalidOperationException) when (memory.Writes == 0) { Console.WriteLine("PASS stale export settings block a bulk addition before writes"); }
        (scan, posts, memory) = Fixture(); snapshot = TradingStockLimits.Capture(scan, posts, memory); memory.FailOnWrite = 2;
        try { TradingStockLimits.ApplyIncrementChecked(snapshot, 12, 25, memory); throw new Exception("Partial export addition ignored"); }
        catch (PartialGameWriteException) when (memory.Writes == 2) { Console.WriteLine("PASS uncertain bulk export addition requires backup recovery"); }
        (scan, posts, memory) = Fixture(); snapshot = TradingStockLimits.Capture(scan, posts, memory);
        try { TradingStockLimits.ApplyAllocationsChecked(snapshot, [allocation[0], new(posts[1].Address, 87, 100)], memory); throw new Exception("Missing allocated entry accepted"); }
        catch (InvalidOperationException) when (memory.Writes == 0) { Console.WriteLine("PASS missing allocated export entry blocks every write"); }
        memory.Put(0x40018, BitConverter.GetBytes(6001f));
        try { TradingStockLimits.ApplyAllocationsChecked(snapshot, allocation, memory); throw new Exception("Stale allocation accepted"); }
        catch (InvalidOperationException) when (memory.Writes == 0) { Console.WriteLine("PASS stale export allocation prevents every write"); }
        (scan, posts, memory) = Fixture(); snapshot = TradingStockLimits.Capture(scan, posts, memory); memory.FailOnWrite = 2;
        try { TradingStockLimits.ApplyAllocationsChecked(snapshot, allocation, memory); throw new Exception("Partial allocation ignored"); }
        catch (PartialGameWriteException) { Console.WriteLine("PASS uncertain export allocation requires backup recovery"); }
        (scan, posts, memory) = Fixture(); snapshot = TradingStockLimits.Capture(scan, posts, memory);
        Check(TradingStockLimits.ApplyChecked(snapshot, 12, 5500, memory) == 1 && memory.Writes == 1 && BitConverter.ToSingle(memory.Read(0x40018, 4)) == 6000,
            "export targets already above the minimum are not reduced");
        (scan, posts, memory) = Fixture(); snapshot = TradingStockLimits.Capture(scan, posts, memory);
        memory.Put(0x40018, BitConverter.GetBytes(6001f));
        try { TradingStockLimits.ApplyChecked(snapshot, 12, 7000, memory); throw new Exception("Stale export list accepted"); }
        catch (InvalidOperationException) when (memory.Writes == 0) { Console.WriteLine("PASS stale export settings block the batch before writes"); }
        (scan, posts, memory) = Fixture(); snapshot = TradingStockLimits.Capture(scan, posts, memory);
        try { TradingStockLimits.ApplyChecked(snapshot, 87, 100, memory); throw new Exception("Missing export accepted"); }
        catch (InvalidOperationException) when (memory.Writes == 0) { Console.WriteLine("PASS missing export entries are not inserted"); }
        (scan, posts, memory) = Fixture(); snapshot = TradingStockLimits.Capture(scan, posts, memory); memory.FailOnWrite = 2;
        try { TradingStockLimits.ApplyChecked(snapshot, 12, 7000, memory); throw new Exception("Partial export failure ignored"); }
        catch (PartialGameWriteException) when (memory.Writes == 2) { Console.WriteLine("PASS uncertain export writes require backup recovery"); }
        (scan, posts, memory) = Fixture();
        try { TradingStockLimits.Capture(scan with { GameSha256 = "unknown" }, posts, memory); throw new Exception("Unknown game accepted"); }
        catch (InvalidOperationException) { Console.WriteLine("PASS export targets reject an unsupported game build"); }
        try { TradingStockLimits.Capture(scan with { UiCandidates = [new("uiDealList", 0x30004, 0x9999)] }, posts, memory); throw new Exception("Overlapping UI candidate accepted"); }
        catch (InvalidOperationException) { Console.WriteLine("PASS export targets reject overlap with a reviewed UI object"); }
        memory.Put(0x30014, BitConverter.GetBytes(46));
        try { TradingStockLimits.Capture(scan, posts, memory); throw new Exception("Duplicate export IDs accepted"); }
        catch (InvalidOperationException) { Console.WriteLine("PASS duplicate export entries are rejected"); }
    }
}
