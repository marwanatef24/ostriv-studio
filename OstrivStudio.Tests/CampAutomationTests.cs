using System.Buffers.Binary;
using OstrivStudio.Core;

internal static class CampAutomationTests
{
    private sealed class Memory : FishBulkEditor.IMemory
    {
        public readonly Dictionary<ulong, byte> Bytes = [];
        public int Writes;
        public void Put(ulong address, byte[] data) { for (int i = 0; i < data.Length; i++) Bytes[address + (uint)i] = data[i]; }
        public byte[] Read(ulong address, int size) => Enumerable.Range(0, size).Select(i => Bytes[address + (uint)i]).ToArray();
        public void Write(ulong address, byte[] data) { Writes++; Put(address, data); }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); Console.WriteLine("PASS " + message); }
    public static async Task Run()
    {
        var memory = new Memory();
        var header = new byte[424];
        BinaryPrimitives.WriteUInt64LittleEndian(header, 0x9000);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(408), 3);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(416), 0x3000);
        memory.Put(0x1000, header);
        var resources = new[] { new ResourceObservation(0, 92, 1999, 0, 0, 0), new ResourceObservation(1, 33, 1000, 0, 0, 0), new ResourceObservation(2, 69, 2000, 0, 0, 0) };
        foreach (var resource in resources)
        {
            byte[] row = new byte[20]; BinaryPrimitives.WriteInt32LittleEndian(row, resource.Id);
            BinaryPrimitives.WriteInt32LittleEndian(row.AsSpan(4), BitConverter.SingleToInt32Bits(resource.Amount));
            memory.Put(0x3000 + (ulong)resource.Index * 20, row);
        }
        var started = DateTime.UtcNow;
        DiscoveryResult Snapshot(int pid = 42, ulong pointer = 0x3000)
        {
            var current = BuildingDiscovery.DecodeResources(memory.Read(0x3000, 60), 3)!;
            var camp = new BuildingCandidate(CampRuleExecutor.BuildingType, 0x1000, 0x9000, pointer, 3, current, "candidate");
            return new(pid, started, "test", FishBulkEditor.SupportedHash, 408, 416, [camp], 1, true, 0, "");
        }
        var profile = new ProfileDocument();
        var settings = profile.AutomationProfiles[profile.SelectedProfile]; settings.IntervalSeconds = 5;
        foreach (var (id, target) in new[] { (92, 2000), (33, 1000), (69, 2000) })
            settings.MaintenanceRules.Add(new MaintenanceRule { ResourceId = id, BuildingType = CampRuleExecutor.BuildingType, Target = target, Enabled = true });
        int scans = 0, calls = 0; var ticks = new List<CampAutomationTick>();
        using (var cts = new CancellationTokenSource())
        {
            try
            {
                await CampAutomation.RunAsync(_ => { scans++; return Task.FromResult((Snapshot(), profile)); },
                    (p, scan, buildings, _) => { calls++; return Task.FromResult(CampRuleExecutor.ApplyBatchChecked(p, scan, buildings, memory)); },
                    ticks.Add, (_, _) => { if (ticks.Count == 2) cts.Cancel(); return Task.CompletedTask; }, cts.Token);
                throw new Exception("Runner failed to stop");
            }
            catch (OperationCanceledException) { }
        }
        Check(scans == 2 && calls == 1 && memory.Writes == 1 && ticks.Select(t => t.Applied).SequenceEqual([1, 0]) &&
            BitConverter.ToSingle(memory.Read(0x3004, 4)) == 2000, "camp automation applies a saved rule once and rechecks without repeating a satisfied write");
        int readCount = 0, writes = 0;
        try
        {
            await CampAutomation.RunAsync(_ => Task.FromResult((Snapshot(++readCount == 1 ? 42 : 43), profile)),
                (_, _, _, _) => { writes++; return Task.FromResult(0); }, _ => { }, (_, _) => Task.CompletedTask, CancellationToken.None);
            throw new Exception("Changed process was accepted");
        }
        catch (InvalidOperationException) { }
        Check(readCount == 2 && writes == 0, "camp automation stops on a changed game process");
        readCount = 0;
        try
        {
            await CampAutomation.RunAsync(_ => Task.FromResult((Snapshot(42, ++readCount == 1 ? 0x3000UL : 0x4000UL), profile)),
                (_, _, _, _) => { writes++; return Task.FromResult(0); }, _ => { }, (_, _) => Task.CompletedTask, CancellationToken.None);
            throw new Exception("Changed camp identity was accepted");
        }
        catch (InvalidOperationException) { }
        Check(readCount == 2 && writes == 0, "camp automation stops when the storage array changes");
        memory.Put(0x3004, BitConverter.GetBytes(1999f));
        readCount = 0; writes = 0;
        try
        {
            await CampAutomation.RunAsync(_ => { readCount++; return Task.FromResult((Snapshot(), profile)); },
                (_, _, _, _) => { writes++; throw new IOException("simulated uncertain write"); },
                _ => { }, (_, _) => Task.CompletedTask, CancellationToken.None);
            throw new Exception("Write failure was ignored");
        }
        catch (IOException) { }
        Check(readCount == 1 && writes == 1, "camp automation stops without retrying an uncertain write");
    }
}
