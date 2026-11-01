using System.Buffers.Binary;
using OstrivStudio.Core;

internal static class LiveRuleTests
{
    private sealed class Memory : FishBulkEditor.IMemory
    {
        public readonly Dictionary<ulong, byte> Bytes = [];
        public ulong PauseAddress, Base;
        public int Writes;
        public void Put(ulong address, byte[] bytes) { for (int i = 0; i < bytes.Length; i++) Bytes[address + (uint)i] = bytes[i]; }
        public byte[] Read(ulong address, int size) => Enumerable.Range(0, size).Select(i => Bytes[address + (uint)i]).ToArray();
        public void Write(ulong address, byte[] data)
        {
            Writes++; Put(address, data);
            if (address == PauseAddress)
            {
                Put(Base + 0x653160, BitConverter.GetBytes(data[0] == 1 ? 0d : 1d));
                Put(Base + 0x67a690, BitConverter.GetBytes(data[0] == 1 ? 0d : 0.02d));
            }
        }
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); Console.WriteLine("PASS " + message); }
    private static Memory Timing(bool paused)
    {
        var memory = new Memory { Base = 0x1000000, PauseAddress = 0x3000000 + 0x11bf98 };
        memory.Put(memory.Base + 0x680a80, BitConverter.GetBytes(0x3000000UL));
        memory.Put(memory.Base + 0x653868, BitConverter.GetBytes(1d));
        memory.Put(memory.Base + 0x653160, BitConverter.GetBytes(paused ? 0d : 1d));
        memory.Put(memory.Base + 0x67a690, BitConverter.GetBytes(paused ? 0d : 0.02d));
        memory.Put(memory.Base + 0x653170, BitConverter.GetBytes(1234d));
        memory.Put(memory.PauseAddress, [paused ? (byte)1 : (byte)0]);
        return memory;
    }
    public static void Run()
    {
        var memory = Timing(false); var pause = new GamePauseControl(memory, memory.Base);
        pause.Acquire(CancellationToken.None); pause.EnsurePaused(); pause.Resume();
        Check(memory.Writes == 2 && memory.Read(memory.PauseAddress, 1)[0] == 0 && BitConverter.ToDouble(memory.Read(memory.Base + 0x653868, 8)) == 1,
            "live-play control pauses and restores the original speed");
        memory = Timing(true); pause = new GamePauseControl(memory, memory.Base);
        pause.Acquire(CancellationToken.None); pause.Resume();
        Check(memory.Writes == 0 && memory.Read(memory.PauseAddress, 1)[0] == 1, "a game already paused is not resumed by the editor");
        memory = Timing(false); memory.Put(memory.Base + 0x653868, BitConverter.GetBytes(12d)); pause = new GamePauseControl(memory, memory.Base);
        pause.Acquire(CancellationToken.None); pause.EnsurePaused(); pause.Resume();
        Check(memory.Writes == 2 && BitConverter.ToDouble(memory.Read(memory.Base + 0x653868, 8)) == 12,
            "12x speed is preserved through a pause and resume");
        memory = Timing(false); memory.Put(memory.Base + 0x653868, BitConverter.GetBytes(99d)); pause = new GamePauseControl(memory, memory.Base);
        try { pause.Acquire(CancellationToken.None); throw new Exception("Unrecognized speed accepted"); } catch (InvalidOperationException) { }
        Check(memory.Writes == 0, "an unrecognized speed is rejected without changing the game");
        memory = Timing(false); pause = new GamePauseControl(memory, memory.Base);
        pause.Acquire(CancellationToken.None); memory.Put(memory.Base + 0x653868, BitConverter.GetBytes(5d));
        try { pause.EnsurePaused(); throw new Exception("Changed speed accepted"); } catch (InvalidOperationException) { }
        pause.ResumeAfterPreflightFailure();
        Check(memory.Writes == 1 && memory.Read(memory.PauseAddress, 1)[0] == 1, "changed player speed is not overwritten on failure");
        memory = Timing(false); pause = new GamePauseControl(memory, memory.Base);
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            try { pause.Acquire(cancelled.Token); throw new Exception("Cancellation ignored"); } catch (OperationCanceledException) { }
        }
        Check(memory.Writes == 0, "a cancelled edit does not change game state");

        memory = new Memory();
        var b = new BuildingCandidate("building_well", 0x1000, 0x9000, 0x3000, 1, [new(0,14,20,0,0,0)], "candidate");
        var scan = new DiscoveryResult(1, DateTime.UtcNow, "test", FishBulkEditor.SupportedHash, 408, 416, [b], 1, true, 0, "");
        var header = new byte[424]; BinaryPrimitives.WriteUInt64LittleEndian(header, b.Vtable);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(408), 1); BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(416), b.ResourcePointer); memory.Put(b.Address, header);
        var record = new byte[20]; BinaryPrimitives.WriteInt32LittleEndian(record, 14); BitConverter.GetBytes(19f).CopyTo(record, 4); memory.Put(b.ResourcePointer, record);
        var settings = new AutomationProfileSettings { MaintenanceRules = [new() { Enabled = true, BuildingType = "building_well", ResourceId = 14, Target = 30 }] };
        var fresh = LiveRuleExecutor.Refresh(scan, settings, memory);
        Check(fresh.Buildings[0].Resources[0].Amount == 19 && scan.Buildings[0].Resources[0].Amount == 20,
            "live play refreshes the amount after pausing without mutating the reviewed snapshot");
        memory.Put(b.Address + 416, BitConverter.GetBytes(0x4000UL));
        try { LiveRuleExecutor.Refresh(scan, settings, memory); throw new Exception("Changed resource pointer accepted"); }
        catch (InvalidOperationException) { Console.WriteLine("PASS live play rejects a changed resource pointer"); }
    }
}
