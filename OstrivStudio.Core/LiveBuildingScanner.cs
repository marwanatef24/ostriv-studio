using System.Buffers.Binary;
using System.Buffers;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using System.Security.Cryptography;

namespace OstrivStudio.Core;

public static class LiveBuildingScanner
{
    private const uint Query = 0x400, Read = 0x10, Commit = 0x1000, Private = 0x20000, Guard = 0x100;
    private const int ChunkSize = 1024 * 1024;
    private const long DefaultLimit = 8L * 1024 * 1024 * 1024;
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryInfo
    {
        public IntPtr BaseAddress, AllocationBase;
        public uint AllocationProtect;
        public UIntPtr RegionSize;
        public uint State, Protect, Type;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct SystemInfo
    {
        public ushort Architecture, Reserved;
        public uint PageSize;
        public IntPtr MinimumAddress, MaximumAddress, ActiveMask;
        public uint ProcessorCount, ProcessorType, AllocationGranularity;
        public ushort ProcessorLevel, ProcessorRevision;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int id);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nuint VirtualQueryEx(SafeProcessHandle process, IntPtr address, out MemoryInfo info, nuint length);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadProcessMemory(SafeProcessHandle process, IntPtr address, byte[] buffer, nuint size, out nuint read);
    [DllImport("kernel32.dll")]
    private static extern void GetNativeSystemInfo(out SystemInfo info);

    private static byte[]? TryRead(SafeProcessHandle handle, ulong address, int size)
    {
        if (address > long.MaxValue || size < 0 || size > ChunkSize) return null;
        var buffer = new byte[size];
        return ReadProcessMemory(handle, new IntPtr((long)address), buffer, (nuint)size, out nuint read) && read == (nuint)size ? buffer : null;
    }
    private static int UniqueOffset(byte[] code, string name, int operand)
    {
        var pattern = Patterns.Known.Single(p => p.Name == name).Pattern.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s == "??" ? -1 : Convert.ToInt32(s, 16)).ToArray();
        int anchor = Array.FindIndex(pattern, n => n >= 0), found = -1;
        for (int i = 0; i <= code.Length - pattern.Length; i++)
        {
            if (code[i + anchor] != pattern[anchor]) continue;
            bool match = true;
            for (int j = 0; j < pattern.Length; j++) if (pattern[j] >= 0 && code[i + j] != pattern[j]) { match = false; break; }
            if (!match) continue;
            if (found >= 0) throw new InvalidDataException($"{name} signature is ambiguous; resource reading is disabled.");
            found = i;
        }
        if (found < 0) throw new InvalidDataException($"{name} signature was not found; resource reading is disabled.");
        int offset = BinaryPrimitives.ReadInt32LittleEndian(code.AsSpan(found + operand, 4));
        if (offset < 32 || offset > 16384) throw new InvalidDataException($"{name} offset is outside the expected range.");
        return offset;
    }
    public static DiscoveryResult Scan(string path, CancellationToken cancellation = default, IProgress<long>? progress = null, long maxBytes = DefaultLimit)
    {
        if (maxBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        string fileHash;
        using (var file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) fileHash = Convert.ToHexString(SHA256.HashData(file));
        var image = BuildingDiscovery.LoadImage(path);
        var candidates = BuildingDiscovery.FindVtables(image.Image, image.PreferredBase);
        if (candidates.Count == 0) throw new InvalidDataException("No unambiguous building class tables were found.");
        if (BuildingDiscovery.FindExactVtable(image.Image, image.PreferredBase, "uiDealList") is ulong dealTable)
            candidates.TryAdd(dealTable, "uiDealList");
        int? processId = null;
        var processes = Process.GetProcessesByName("ostriv");
        try
        {
            foreach (var p in processes)
            {
                try { if (string.Equals(Path.GetFullPath(p.MainModule!.FileName), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)) { processId = p.Id; break; } }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { }
            }
        }
        finally { foreach (var p in processes) p.Dispose(); }
        using var process = Process.GetProcessById(processId ?? throw new InvalidOperationException("Start Ostriv and load a game before scanning."));
        var module = process.MainModule ?? throw new InvalidOperationException("The game module is unavailable.");
        var started = process.StartTime.ToUniversalTime();
        ulong moduleBase = checked((ulong)module.BaseAddress.ToInt64());
        if (module.ModuleMemorySize < image.Image.Length) throw new InvalidDataException("The live module is smaller than the executable image.");
        using var handle = OpenProcess(Query | Read, false, process.Id);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        byte[]? liveText = TryRead(handle, moduleBase + (uint)image.TextRva, image.TextLength);
        // The code section is usually larger than one scan chunk. Read it in bounded pieces.
        if (image.TextLength > ChunkSize)
        {
            liveText = new byte[image.TextLength];
            for (int off = 0; off < liveText.Length; off += ChunkSize)
            {
                cancellation.ThrowIfCancellationRequested();
                var part = TryRead(handle, moduleBase + (uint)image.TextRva + (uint)off, Math.Min(ChunkSize, liveText.Length - off))
                    ?? throw new InvalidOperationException("Could not read the running game's code section.");
                part.CopyTo(liveText, off);
            }
        }
        if (liveText is null || !liveText.AsSpan().SequenceEqual(image.Image.AsSpan(image.TextRva, image.TextLength)))
            throw new InvalidDataException("Running code does not match the executable on disk. Building scan stopped.");
        int countOffset = UniqueOffset(liveText, "Resource count", 9), pointerOffset = UniqueOffset(liveText, "Resource pointer", 10);
        if (pointerOffset != countOffset + 8 || countOffset % 4 != 0) throw new InvalidDataException("Resource offsets failed layout validation.");
        var tables = new Dictionary<ulong, string>();
        foreach (var item in candidates)
        {
            var row = TryRead(handle, moduleBase + item.Key - 8, 16);
            if (row is null) continue;
            ulong expected = moduleBase + (BinaryPrimitives.ReadUInt64LittleEndian(image.Image.AsSpan((int)item.Key - 8)) - image.PreferredBase);
            ulong col = BinaryPrimitives.ReadUInt64LittleEndian(row), function = BinaryPrimitives.ReadUInt64LittleEndian(row.AsSpan(8));
            if (col == expected && function >= moduleBase + (uint)image.TextRva && function < moduleBase + (uint)image.TextRva + (uint)image.TextLength)
                tables.Add(moduleBase + item.Key, item.Value);
        }
        if (tables.Count == 0) throw new InvalidDataException("Live class tables did not match the game image.");
        // Almost every aligned word in private game memory is not a class
        // table. Reject values outside the actual table range before hashing.
        // This keeps the complete scan and all existing verification intact.
        ulong minTable = tables.Keys.Min(), maxTable = tables.Keys.Max();
        var hits = new Dictionary<ulong, string>();
        long scanned = 0, lastReported = 0; int failures = 0; bool complete = true; ulong cursor = 0;
        GetNativeSystemInfo(out var system);
        ulong maxAddress = checked((ulong)system.MaximumAddress.ToInt64());
        byte[] buffer = ArrayPool<byte>.Shared.Rent(ChunkSize);
        try
        {
            while (cursor <= maxAddress)
            {
                cancellation.ThrowIfCancellationRequested();
                if (process.HasExited) throw new InvalidOperationException("The game closed while scanning.");
                if (VirtualQueryEx(handle, new IntPtr((long)cursor), out var region, (nuint)Marshal.SizeOf<MemoryInfo>()) == 0) { complete = false; failures++; break; }
                ulong start = checked((ulong)region.BaseAddress.ToInt64()), size = region.RegionSize.ToUInt64();
                if (size == 0 || start + size <= cursor) { complete = false; break; }
                cursor = start + size;
                uint protection = region.Protect & 0xff;
                if (region.State != Commit || region.Type != Private || (region.Protect & Guard) != 0 || protection is not (0x04 or 0x08 or 0x40 or 0x80)) continue;
                for (ulong off = 0; off < size; off += ChunkSize)
                {
                    cancellation.ThrowIfCancellationRequested();
                    int count = (int)Math.Min((ulong)ChunkSize, size - off);
                    if (scanned + count > maxBytes) { complete = false; break; }
                    bool ok = ReadProcessMemory(handle, new IntPtr(checked((long)(start + off))), buffer, (nuint)count, out nuint bytesRead) && bytesRead == (nuint)count;
                    scanned += count;
                    if (!ok) { failures++; continue; }
                    for (int i = 0; i <= count - 8; i += 8)
                    {
                        ulong value = BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(i, 8));
                        if (value < minTable || value > maxTable || !tables.TryGetValue(value, out var name)) continue;
                        hits.TryAdd(start + off + (uint)i, name);
                        if (hits.Count >= 10000) { complete = false; break; }
                    }
                    if (scanned - lastReported >= 64L * 1024 * 1024) { progress?.Report(scanned); lastReported = scanned; }
                    if (!complete) break;
                }
                if (!complete) break;
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
        var buildings = new List<BuildingCandidate>();
        var uiCandidates = new List<UiClassCandidate>();
        foreach (var hit in hits.OrderBy(x => x.Value).ThenBy(x => x.Key))
        {
            cancellation.ThrowIfCancellationRequested();
            if (hit.Value == "uiDealList")
            {
                var table = TryRead(handle, hit.Key, 8);
                if (table is not null && tables.TryGetValue(BinaryPrimitives.ReadUInt64LittleEndian(table), out var uiName) && uiName == hit.Value)
                    uiCandidates.Add(new(uiName, hit.Key, BinaryPrimitives.ReadUInt64LittleEndian(table)));
                continue;
            }
            var header = TryRead(handle, hit.Key, Math.Max(pointerOffset + 8, countOffset + 4));
            if (header is null || !tables.TryGetValue(BinaryPrimitives.ReadUInt64LittleEndian(header), out var name) || name != hit.Value) continue;
            int resourceCount = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(countOffset));
            ulong resourcePtr = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(pointerOffset));
            if (resourceCount < 0 || resourceCount > 511) continue;
            byte[]? data = resourceCount == 0 ? [] : TryRead(handle, resourcePtr, resourceCount * 20);
            if (data is null) continue;
            var resources = BuildingDiscovery.DecodeResources(data, resourceCount);
            if (resources is null) continue;
            var check = TryRead(handle, hit.Key, Math.Max(pointerOffset + 8, countOffset + 4));
            if (check is null || !header.AsSpan().SequenceEqual(check)) continue;
            if (resourceCount > 0)
            {
                var confirm = TryRead(handle, resourcePtr, resourceCount * 20);
                if (confirm is null || !data.AsSpan().SequenceEqual(confirm)) continue;
            }
            float? observedCapacity = null;
            if (fileHash == FishBulkEditor.SupportedHash && CapacityObservation.SupportsClass(name))
            {
                var firstCapacity = TryRead(handle, checked(hit.Key + CapacityObservation.Offset), 4);
                var secondCapacity = TryRead(handle, checked(hit.Key + CapacityObservation.Offset), 4);
                var finalHeader = TryRead(handle, hit.Key, Math.Max(pointerOffset + 8, countOffset + 4));
                if (firstCapacity is not null && secondCapacity is not null && finalHeader is not null && header.AsSpan().SequenceEqual(finalHeader))
                    observedCapacity = CapacityObservation.Decode(fileHash, name, firstCapacity, secondCapacity);
            }
            buildings.Add(new(name, hit.Key, BinaryPrimitives.ReadUInt64LittleEndian(header), resourcePtr, resourceCount, resources, "Candidate; not verified for editing", observedCapacity));
        }
        if (process.HasExited || process.StartTime.ToUniversalTime() != started) throw new InvalidOperationException("The game session changed during the scan.");
        return new DiscoveryResult(process.Id, started, path, fileHash, countOffset, pointerOffset, buildings, scanned, complete && failures == 0, failures,
            "Candidates were checked against live class tables and a possible resource layout; the game does not provide stable IDs here. No memory was written.") { UiCandidates = uiCandidates.AsReadOnly() };
    }
}
