using System.Buffers.Binary;
using OstrivStudio.Core;

internal static class TradeDealReaderTests
{
    private sealed class Memory : FishBulkEditor.IMemory
    {
        private readonly Dictionary<ulong, byte> _bytes = [];
        public bool ChangeDealOnRead; private int _dealReads;
        public bool ChangeFirstDealDuringSecondRead;
        public bool ChangeListDuringFinalDealCheck; public int SecondDealReads;
        public int ListHeaderReads;
        public void Put(ulong address, byte[] bytes) { for (int i = 0; i < bytes.Length; i++) _bytes[address + (uint)i] = bytes[i]; }
        public byte[] Read(ulong address, int size)
        {
            if (address == 0x10000) ListHeaderReads++;
            var result = Enumerable.Range(0, size).Select(i => _bytes.GetValueOrDefault(address + (uint)i)).ToArray();
            if (ChangeDealOnRead && address == 0x40000 && ++_dealReads == 1) Put(0x40000 + 52, BitConverter.GetBytes(191));
            if (ChangeFirstDealDuringSecondRead && address == 0x60000) { ChangeFirstDealDuringSecondRead = false; Put(0x40000 + 52, BitConverter.GetBytes(191)); }
            if (ChangeListDuringFinalDealCheck && address == 0x60000 && ++SecondDealReads == 3) Put(0x10000 + 0x250, BitConverter.GetBytes(1));
            return result;
        }
        public void Write(ulong address, byte[] data) => throw new Exception("Trade read attempted a write");
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); Console.WriteLine("PASS " + message); }
    private static (DiscoveryResult Scan, Memory Memory) Fixture()
    {
        var scan = new DiscoveryResult(1, DateTime.UtcNow, "test", FishBulkEditor.SupportedHash, 408, 416, [], 1, true, 0, "")
            { UiCandidates = [new("uiDealList", 0x10000, 0x9000)] };
        var memory = new Memory();
        var header = new byte[0x260]; BinaryPrimitives.WriteUInt64LittleEndian(header, 0x9000);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(0x250), 1); BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(0x258), 0x20000); memory.Put(0x10000, header);
        memory.Put(0x20000, BitConverter.GetBytes(0x30000UL));
        var item = new byte[0x258]; BinaryPrimitives.WriteUInt64LittleEndian(item, 0x9100); BinaryPrimitives.WriteUInt64LittleEndian(item.AsSpan(0x250), 0x40000); memory.Put(0x30000, item);
        var deal = new byte[56]; BinaryPrimitives.WriteInt32LittleEndian(deal, 2); BinaryPrimitives.WriteInt32LittleEndian(deal.AsSpan(4), 12);
        BinaryPrimitives.WriteInt32LittleEndian(deal.AsSpan(12), 42); BitConverter.GetBytes(40f).CopyTo(deal, 48); BinaryPrimitives.WriteInt32LittleEndian(deal.AsSpan(52), 190); memory.Put(0x40000, deal);
        return (scan, memory);
    }
    public static void Run()
    {
        var (scan, memory) = Fixture();
        var row = TradeDealReader.Capture(scan, memory).Single();
        Check(row.CityId == 2 && row.ResourceId == 12 && !row.IsImport && row.Total == 190 && row.Completed == 40 && row.Remaining == 150,
            "trade reader decodes a visible export and its remaining quantity without writes");
        Check(row.TradingBuildingClass == "building_trading_post" && (row with { CityId = 4 }).TradingBuildingClass == "building_trading_dock" &&
            (row with { CityId = 99 }).TradingBuildingClass is null, "deal routing uses known cities and rejects unknown destinations");
        TradeDealReader.RequireUnchanged(scan, memory, row);
        memory.Put(0x40030, BitConverter.GetBytes(41f));
        try { TradeDealReader.RequireUnchanged(scan, memory, row); throw new Exception("Changed reviewed deal accepted"); }
        catch (InvalidOperationException) { Console.WriteLine("PASS a changed reviewed deal blocks the export target action"); }
        (scan, memory) = Fixture();
        memory.Put(0x40008, BitConverter.GetBytes(1));
        Check(TradeDealReader.Capture(scan, memory).Single().IsImport, "trade reader identifies an import deal");
        (scan, memory) = Fixture(); memory.ChangeDealOnRead = true;
        try { TradeDealReader.Capture(scan, memory); throw new Exception("Changed deal accepted"); }
        catch (InvalidOperationException) { Console.WriteLine("PASS trade reader rejects a deal that changes during inspection"); }
        (scan, memory) = Fixture(); memory.Put(0x40030, BitConverter.GetBytes(float.NaN));
        try { TradeDealReader.Capture(scan, memory); throw new Exception("Invalid completion accepted"); }
        catch (InvalidOperationException) { Console.WriteLine("PASS trade reader rejects an invalid deal amount"); }
        (scan, memory) = Fixture();
        try { TradeDealReader.Capture(scan with { UiCandidates = [] }, memory); throw new Exception("Missing trade UI accepted"); }
        catch (InvalidOperationException) { Console.WriteLine("PASS trade reader requires one reviewed trade list"); }
        (scan, memory) = Fixture();
        memory.Put(0x10000 + 0x250, BitConverter.GetBytes(2));
        memory.Put(0x20000 + 8, BitConverter.GetBytes(0x50000UL));
        var secondItem = new byte[0x258]; BinaryPrimitives.WriteUInt64LittleEndian(secondItem, 0x9100);
        BinaryPrimitives.WriteUInt64LittleEndian(secondItem.AsSpan(0x250), 0x60000); memory.Put(0x50000, secondItem);
        var secondDeal = new byte[56]; BinaryPrimitives.WriteInt32LittleEndian(secondDeal, 2);
        BinaryPrimitives.WriteInt32LittleEndian(secondDeal.AsSpan(4), 13); BinaryPrimitives.WriteInt32LittleEndian(secondDeal.AsSpan(12), 43);
        BinaryPrimitives.WriteInt32LittleEndian(secondDeal.AsSpan(52), 30); memory.Put(0x60000, secondDeal);
        var both = TradeDealReader.Capture(scan, memory).ToArray();
        memory.ChangeFirstDealDuringSecondRead = true;
        try { TradeDealReader.Capture(scan, memory); throw new Exception("Earlier changed deal accepted"); }
        catch (InvalidOperationException) { Console.WriteLine("PASS a deal changing while a later row is read invalidates the capture"); }
        memory.Put(0x40000 + 52, BitConverter.GetBytes(190));
        memory.ChangeListDuringFinalDealCheck = true;
        try { TradeDealReader.Capture(scan, memory); throw new Exception("Late list change accepted"); }
        catch (InvalidOperationException) { Console.WriteLine("PASS a list changing during final row checks invalidates the capture"); }
        memory.ChangeListDuringFinalDealCheck = false; memory.Put(0x10000 + 0x250, BitConverter.GetBytes(2));
        memory.ListHeaderReads = 0;
        TradeDealReader.RequireAllUnchanged(scan, memory, both);
        Check(memory.ListHeaderReads == 3, "selected deals are checked together in one guarded list capture");
        memory.Put(0x60000 + 52, BitConverter.GetBytes(31));
        try { TradeDealReader.RequireAllUnchanged(scan, memory, both); throw new Exception("Changed selected deal accepted"); }
        catch (InvalidOperationException) { Console.WriteLine("PASS a changed deal blocks the selected export batch"); }
        try { TradeDealReader.RequireAllUnchanged(scan, memory, [both[0], both[0]]); throw new Exception("Duplicate selected deal accepted"); }
        catch (InvalidOperationException) { Console.WriteLine("PASS duplicate selected deals are rejected before review"); }
    }
}
