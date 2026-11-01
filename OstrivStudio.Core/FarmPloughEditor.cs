using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace OstrivStudio.Core;

public sealed record FarmPloughField(int Number, ulong Address, uint Flags)
{
    public bool Enabled => (Flags & 1) != 0;
}
public sealed record FarmPloughSnapshot(FarmRotationSnapshot Farm, IReadOnlyList<FarmPloughField> Fields);

// The low bit at this offset was independently observed when toggling two
// fields through Ostriv's UI on the exact supported executable.
public static class FarmPloughEditor
{
    public const int PloughOffset = 0x9f8;
    private static bool Overlap(ulong a, ulong b, ulong c, ulong d) => a < d && c < b;
    private static bool SameFarm(FarmRotationSnapshot a, FarmRotationSnapshot b) => a.FieldPointer == b.FieldPointer && a.Fields.Count == b.Fields.Count &&
        a.Fields.Zip(b.Fields).All(p => p.First.Number == p.Second.Number && p.First.Candidate.Address == p.Second.Candidate.Address &&
            p.First.CropPointer == p.Second.CropPointer && p.First.Crops.SequenceEqual(p.Second.Crops));
    private static bool Same(FarmPloughSnapshot a, FarmPloughSnapshot b) => SameFarm(a.Farm, b.Farm) && a.Fields.SequenceEqual(b.Fields);

    public static FarmPloughSnapshot Capture(FarmRotationSnapshot reviewed, FishBulkEditor.IMemory memory)
    {
        var farm = FarmCropEditor.Capture(reviewed.Scan, reviewed.Farm, memory);
        if (!SameFarm(reviewed, farm)) throw new InvalidOperationException("The farm or its crop rotations changed; scan again.");
        var fields = new List<FarmPloughField>();
        foreach (var field in farm.Fields)
        {
            ulong start = checked(field.Candidate.Address + PloughOffset), end = checked(start + 4);
            if (Overlap(start, end, farm.Farm.Address, checked(farm.Farm.Address + 0x8B8)) ||
                Overlap(start, end, farm.FieldPointer, checked(farm.FieldPointer + (ulong)farm.Fields.Count * 8)) ||
                farm.Scan.UiCandidates.Any(ui => Overlap(start, end, ui.Address, checked(ui.Address + 8))) ||
                farm.Fields.Any(other => Overlap(start, end, other.CropPointer, checked(other.CropPointer + (ulong)other.Crops.Count * 4))) ||
                farm.Scan.Buildings.Any(b => (b.Address != field.Candidate.Address && Overlap(start, end, b.Address,
                    checked(b.Address + (ulong)(b.ClassName == "building_grain_field" ? PloughOffset + 4 : farm.Scan.PointerOffset + 8)))) ||
                    (b.ResourceCount > 0 && b.ResourcePointer != 0 && Overlap(start, end, b.ResourcePointer, checked(b.ResourcePointer + (ulong)b.ResourceCount * 20)))))
                throw new InvalidOperationException("A field setting overlaps another observed object.");
            byte[] first = memory.Read(start, 4), second = memory.Read(start, 4);
            if (first.Length != 4 || !first.AsSpan().SequenceEqual(second)) throw new InvalidOperationException("A plough setting changed while reading.");
            fields.Add(new(field.Number, field.Candidate.Address, BinaryPrimitives.ReadUInt32LittleEndian(first)));
        }
        if (!SameFarm(farm, FarmCropEditor.Capture(reviewed.Scan, reviewed.Farm, memory)))
            throw new InvalidOperationException("The farm changed while reading its plough settings.");
        return new(farm, fields.AsReadOnly());
    }
    public static int ApplyChecked(FarmPloughSnapshot reviewed, IReadOnlyList<int> fieldNumbers, bool enabled, FishBulkEditor.IMemory memory)
    {
        if (fieldNumbers.Count is < 1 or > 255 || fieldNumbers.Distinct().Count() != fieldNumbers.Count ||
            fieldNumbers.Any(n => reviewed.Fields.All(f => f.Number != n))) throw new InvalidOperationException("Select distinct reviewed fields.");
        var expected = Capture(reviewed.Farm, memory);
        if (!Same(reviewed, expected)) throw new InvalidOperationException("A field setting changed since review; open it again.");
        int attempted = 0;
        try
        {
            foreach (int number in fieldNumbers.Order())
            {
                var field = expected.Fields.Single(f => f.Number == number);
                if (field.Enabled == enabled) continue;
                if (!Same(expected, Capture(reviewed.Farm, memory))) throw new InvalidOperationException("A field changed before the edit.");
                uint next = enabled ? field.Flags | 1u : field.Flags & ~1u;
                attempted++; memory.Write(checked(field.Address + PloughOffset), BitConverter.GetBytes(next));
                expected = expected with { Fields = expected.Fields.Select(f => f.Number == number ? f with { Flags = next } : f).ToArray() };
                if (!Same(expected, Capture(reviewed.Farm, memory))) throw new InvalidOperationException("The field setting did not pass readback.");
            }
        }
        catch (Exception ex) when (attempted > 0)
        { throw new PartialGameWriteException("The plough batch may be partial. Do not save; reload the verified backup. " + ex.Message, ex); }
        return attempted;
    }
    private static Process CheckedProcess(DiscoveryResult scan)
    {
        using (var file = File.Open(scan.GamePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            if (Convert.ToHexString(SHA256.HashData(file)) != FishBulkEditor.SupportedHash) throw new InvalidOperationException("The game executable changed.");
        var process = Process.GetProcessById(scan.ProcessId);
        try
        {
            if (process.HasExited || process.StartTime.ToUniversalTime() != scan.ProcessStartedUtc ||
                !string.Equals(Path.GetFullPath(process.MainModule!.FileName), Path.GetFullPath(scan.GamePath), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The game session changed; scan again.");
            return process;
        }
        catch { process.Dispose(); throw; }
    }
    private sealed class ReadOnlyMemory : FishBulkEditor.IMemory, IDisposable
    {
        [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int id);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ReadProcessMemory(SafeProcessHandle process, IntPtr address, byte[] bytes, nuint size, out nuint read);
        private readonly SafeProcessHandle _handle;
        public ReadOnlyMemory(int id) { _handle = OpenProcess(0x400 | 0x10, false, id); if (_handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error()); }
        public byte[] Read(ulong address, int size)
        {
            byte[] bytes = new byte[size];
            if (!ReadProcessMemory(_handle, new IntPtr(checked((long)address)), bytes, (nuint)size, out nuint read) || read != (nuint)size)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Farm memory could not be read.");
            return bytes;
        }
        public void Write(ulong address, byte[] data) => throw new InvalidOperationException("This inspection is read-only.");
        public void Dispose() => _handle.Dispose();
    }
    private sealed class GuardedMemory(FishBulkEditor.IMemory inner, GamePauseControl pause) : FishBulkEditor.IMemory
    {
        public byte[] Read(ulong address, int size) => inner.Read(address, size);
        public void Write(ulong address, byte[] data) { pause.EnsurePaused(); inner.Write(address, data); }
    }
    public static FarmPloughSnapshot CaptureFromGame(FarmRotationSnapshot farm)
    {
        using var process = CheckedProcess(farm.Scan); using var memory = new ReadOnlyMemory(farm.Scan.ProcessId);
        return Capture(farm, memory);
    }
    public static int ApplyToGame(FarmPloughSnapshot reviewed, IReadOnlyList<int> fields, bool enabled, CancellationToken cancellation)
    {
        using var process = CheckedProcess(reviewed.Farm.Scan);
        ulong moduleBase = checked((ulong)process.MainModule!.BaseAddress.ToInt64());
        using var memory = new ExistingRuleExecutor.ProcessMemory(reviewed.Farm.Scan.ProcessId);
        var image = BuildingDiscovery.LoadImage(reviewed.Farm.Scan.GamePath).Image;
        const int pauseFunction = 0x3f5ec0, checkedLength = 32;
        if (!image.AsSpan(pauseFunction, checkedLength).SequenceEqual(memory.Read(moduleBase + pauseFunction, checkedLength)))
            throw new InvalidOperationException("The running game's pause control differs from the tested build.");
        var pause = new GamePauseControl(memory, moduleBase); pause.Acquire(cancellation); int applied = 0;
        try
        {
            cancellation.ThrowIfCancellationRequested(); pause.EnsurePaused();
            applied = ApplyChecked(reviewed, fields, enabled, new GuardedMemory(memory, pause));
            pause.Resume(); return applied;
        }
        catch (PartialGameWriteException) { throw; }
        catch (Exception ex) when (applied > 0)
        { throw new PartialGameWriteException("Plough settings changed but the game state could not be restored. Reload the backup if uncertain. " + ex.Message, ex); }
        catch { pause.ResumeAfterPreflightFailure(); throw; }
    }
}
