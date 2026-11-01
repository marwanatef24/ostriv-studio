using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace OstrivStudio.Core;

public sealed record TradingStockLimit(int Index, int ResourceId, float Target);
public sealed record TradingPostLimits(BuildingCandidate Building, ulong Pointer, IReadOnlyList<TradingStockLimit> Limits, string Bytes);
public sealed record TradingLimitsSnapshot(DiscoveryResult Scan, IReadOnlyList<TradingPostLimits> Posts);

// Only existing trading-post and trading-dock export-stock targets on the independently observed
// executable are supported. This does not add slots or edit trade deals.
public static class TradingStockLimits
{
    private const int CountOffset = 0x800, PointerOffset = 0x808, RecordSize = 20;
    public static bool SupportsClass(string name) => name is "building_trading_post" or "building_trading_dock";
    private static byte[] Read(FishBulkEditor.IMemory memory, ulong address, int size)
    {
        if (address < 0x10000 || address > (ulong)long.MaxValue - (uint)size) throw new InvalidOperationException("An export-stock pointer is invalid.");
        byte[] result = memory.Read(address, size);
        if (result.Length != size) throw new InvalidOperationException("An export-stock read was incomplete.");
        return result;
    }
    private static bool Overlaps(ulong start, ulong end, ulong otherStart, ulong otherEnd) => start < otherEnd && otherStart < end;
    private static bool SameIdentity(byte[] first, byte[] second, DiscoveryResult scan) =>
        first.AsSpan(0, 8).SequenceEqual(second.AsSpan(0, 8)) &&
        first.AsSpan(scan.CountOffset, 4).SequenceEqual(second.AsSpan(scan.CountOffset, 4)) &&
        first.AsSpan(scan.PointerOffset, 8).SequenceEqual(second.AsSpan(scan.PointerOffset, 8)) &&
        first.AsSpan(CountOffset, 4).SequenceEqual(second.AsSpan(CountOffset, 4)) &&
        first.AsSpan(PointerOffset, 8).SequenceEqual(second.AsSpan(PointerOffset, 8));

    public static TradingLimitsSnapshot Capture(DiscoveryResult scan, IReadOnlyList<BuildingCandidate> selected, FishBulkEditor.IMemory memory)
    {
        if (!scan.Complete || scan.UnreadableChunks != 0 || scan.GameSha256 != FishBulkEditor.SupportedHash || scan.CountOffset != 408 || scan.PointerOffset != 416 ||
            selected.Count is < 1 or > 64 || selected.Select(b => b.Address).Distinct().Count() != selected.Count ||
            scan.Buildings.Select(b => b.Address).Distinct().Count() != scan.Buildings.Count)
            throw new InvalidOperationException("Select distinct trading buildings from a complete scan of the tested game build.");
        var posts = new List<TradingPostLimits>(); var ranges = new List<(ulong Start, ulong End)>();
        foreach (var b in selected)
        {
            if (!SupportsClass(b.ClassName) || !scan.Buildings.Any(x => ReferenceEquals(x, b)) ||
                b.ResourceCount is < 0 or > 511 || b.ResourceCount != b.Resources.Count)
                throw new InvalidOperationException("Only reviewed trading-post and trading-dock candidates are supported.");
            byte[] header = Read(memory, b.Address, PointerOffset + 8);
            if (BinaryPrimitives.ReadUInt64LittleEndian(header) != b.Vtable ||
                BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(scan.CountOffset)) != b.ResourceCount ||
                BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(scan.PointerOffset)) != b.ResourcePointer)
                throw new InvalidOperationException("A trading building changed since the scan.");
            int count = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(CountOffset));
            ulong pointer = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(PointerOffset));
            if (count is < 1 or > 128) throw new InvalidOperationException("A trading building has no supported export-stock list.");
            byte[] bytes = Read(memory, pointer, checked(count * RecordSize));
            var limits = new List<TradingStockLimit>();
            for (int i = 0; i < count; i++)
            {
                int id = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(i * RecordSize));
                float amount = BitConverter.ToSingle(bytes, i * RecordSize + 4);
                if (id is <= 0 or > 65535 || !float.IsFinite(amount) || amount < 0 || amount > 1_000_000_000)
                    throw new InvalidOperationException("An export-stock entry has an unsupported value.");
                limits.Add(new(i, id, amount));
            }
            if (limits.Select(x => x.ResourceId).Distinct().Count() != limits.Count ||
                !SameIdentity(header, Read(memory, b.Address, header.Length), scan) ||
                !bytes.AsSpan().SequenceEqual(Read(memory, pointer, bytes.Length)))
                throw new InvalidOperationException("An export-stock list is ambiguous or changed while reading.");
            ulong end = checked(pointer + (ulong)bytes.Length);
            if (ranges.Any(r => Overlaps(pointer, end, r.Start, r.End)) ||
                scan.UiCandidates.Any(ui => Overlaps(pointer, end, ui.Address, checked(ui.Address + 8))) || scan.Buildings.Any(other =>
                Overlaps(pointer, end, other.Address, checked(other.Address + (ulong)(SupportsClass(other.ClassName) ? PointerOffset + 8 : scan.PointerOffset + 8))) ||
                (other.ResourceCount > 0 && other.ResourcePointer != 0 && Overlaps(pointer, end, other.ResourcePointer, checked(other.ResourcePointer + (ulong)other.ResourceCount * RecordSize)))))
                throw new InvalidOperationException("An export-stock list overlaps another observed object.");
            ranges.Add((pointer, end));
            posts.Add(new(b, pointer, limits.AsReadOnly(), Convert.ToBase64String(bytes)));
        }
        return new(scan, posts.AsReadOnly());
    }

    private static void ValidateTarget(int resourceId, decimal target)
    {
        if (resourceId is <= 0 or > 65535 || target is < 0 or > 1_000_000m || decimal.Truncate(target) != target || (decimal)(float)target != target)
            throw new InvalidOperationException("Choose an existing resource and a whole-unit target between 0 and 1,000,000.");
    }
    public static int CountChanges(TradingLimitsSnapshot snapshot, int resourceId, decimal target)
    {
        ValidateTarget(resourceId, target);
        if (snapshot.Posts.Count == 0 || snapshot.Posts.Any(p => p.Limits.Count(x => x.ResourceId == resourceId) != 1))
            throw new InvalidOperationException("Every selected trading building must already have this export-stock entry.");
        return snapshot.Posts.Count(p => (decimal)p.Limits.Single(x => x.ResourceId == resourceId).Target < target);
    }
    private static bool Same(TradingLimitsSnapshot a, TradingLimitsSnapshot b) => a.Posts.Count == b.Posts.Count &&
        a.Posts.Zip(b.Posts).All(p => p.First.Building.Address == p.Second.Building.Address && p.First.Pointer == p.Second.Pointer && p.First.Bytes == p.Second.Bytes);

    public static int ApplyChecked(TradingLimitsSnapshot reviewed, int resourceId, decimal target, FishBulkEditor.IMemory memory)
    {
        int changes = CountChanges(reviewed, resourceId, target);
        var selected = reviewed.Posts.Select(p => p.Building).ToArray();
        var expected = Capture(reviewed.Scan, selected, memory);
        if (!Same(reviewed, expected)) throw new InvalidOperationException("Export-stock settings changed since review; open them again.");
        int attempted = 0;
        try
        {
            for (int i = 0; i < expected.Posts.Count; i++)
            {
                var post = expected.Posts[i]; var entry = post.Limits.Single(x => x.ResourceId == resourceId);
                if ((decimal)entry.Target >= target) continue;
                if (!Same(expected, Capture(reviewed.Scan, selected, memory))) throw new InvalidOperationException("Export-stock settings changed before the edit.");
                attempted++;
                memory.Write(checked(post.Pointer + (ulong)(entry.Index * RecordSize + 4)), BitConverter.GetBytes((float)target));
                byte[] revised = Convert.FromBase64String(post.Bytes);
                BitConverter.GetBytes((float)target).CopyTo(revised, entry.Index * RecordSize + 4);
                var next = expected.Posts.ToArray();
                next[i] = post with { Bytes = Convert.ToBase64String(revised), Limits = post.Limits.Select(x => x.Index == entry.Index ? x with { Target = (float)target } : x).ToArray() };
                expected = expected with { Posts = next };
                if (!Same(expected, Capture(reviewed.Scan, selected, memory))) throw new InvalidOperationException("Export-stock readback did not match.");
            }
        }
        catch (Exception ex) when (attempted > 0)
        { throw new PartialGameWriteException("Export-stock changes may be partial. Do not save; reload the verified backup. " + ex.Message, ex); }
        return changes;
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
            var bytes = new byte[size];
            if (!ReadProcessMemory(_handle, new IntPtr(checked((long)address)), bytes, (nuint)size, out var count) || count != (nuint)size)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Export-stock memory could not be read.");
            return bytes;
        }
        public void Write(ulong address, byte[] data) => throw new InvalidOperationException("This inspection is read-only.");
        public void Dispose() => _handle.Dispose();
    }
    public static TradingLimitsSnapshot CaptureFromGame(DiscoveryResult scan, IReadOnlyList<BuildingCandidate> selected)
    {
        using var process = CheckedProcess(scan);
        using var memory = new ReadOnlyMemory(scan.ProcessId);
        return Capture(scan, selected, memory);
    }
    private sealed class GuardedMemory(FishBulkEditor.IMemory inner, GamePauseControl pause) : FishBulkEditor.IMemory
    {
        public byte[] Read(ulong address, int size) => inner.Read(address, size);
        public void Write(ulong address, byte[] data) { pause.EnsurePaused(); inner.Write(address, data); }
    }
    public static int ApplyToGame(TradingLimitsSnapshot reviewed, int resourceId, decimal target, CancellationToken cancellation, TradeDeal? requiredDeal = null)
    {
        _ = CountChanges(reviewed, resourceId, target);
        if (requiredDeal is not null && (requiredDeal.IsImport || requiredDeal.ResourceId != resourceId ||
            requiredDeal.TradingBuildingClass is null || reviewed.Posts.Any(p => p.Building.ClassName != requiredDeal.TradingBuildingClass)))
            throw new InvalidOperationException("Choose the resource and trading building for the reviewed export deal.");
        cancellation.ThrowIfCancellationRequested();
        using var process = CheckedProcess(reviewed.Scan);
        ulong moduleBase = checked((ulong)process.MainModule!.BaseAddress.ToInt64());
        using var memory = new ExistingRuleExecutor.ProcessMemory(reviewed.Scan.ProcessId);
        var image = BuildingDiscovery.LoadImage(reviewed.Scan.GamePath).Image;
        const int pauseFunction = 0x3f5ec0, checkedLength = 32;
        if (!image.AsSpan(pauseFunction, checkedLength).SequenceEqual(memory.Read(moduleBase + pauseFunction, checkedLength)))
            throw new InvalidOperationException("The running game's pause control differs from the tested build.");
        var pause = new GamePauseControl(memory, moduleBase);
        pause.Acquire(cancellation); int applied = 0;
        try
        {
            cancellation.ThrowIfCancellationRequested(); pause.EnsurePaused();
            if (requiredDeal is not null) TradeDealReader.RequireUnchanged(reviewed.Scan, memory, requiredDeal);
            applied = ApplyChecked(reviewed, resourceId, target, new GuardedMemory(memory, pause));
            pause.Resume(); return applied;
        }
        catch (PartialGameWriteException) { throw; }
        catch (Exception ex) when (applied > 0)
        { throw new PartialGameWriteException("Export stock changed but the game state could not be restored. Reload the backup if uncertain. " + ex.Message, ex); }
        catch { pause.ResumeAfterPreflightFailure(); throw; }
    }
}
