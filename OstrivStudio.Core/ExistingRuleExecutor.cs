using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace OstrivStudio.Core;

public sealed class PartialGameWriteException(string message, Exception inner) : InvalidOperationException(message, inner);

// Experimental: existing records in tested classes and selected nonperishable
// resources in an allocated spare slot. A caller
// must obtain a verified save backup first.
public static class ExistingRuleExecutor
{
    private static readonly HashSet<string> Classes = new(StringComparer.OrdinalIgnoreCase)
    { "building_camp_storage", "building_granary", "building_well", "building_stone_mining_camp", "building_warehouse", "building_fishery", "building_trading_post", "building_ashery", "building_brickworks" };
    public static bool SupportsExisting(string type, int id) => type.Trim().ToLowerInvariant() switch
    {
        "building_ashery" => id == 109,
        "building_brickworks" => id == 32,
        // Only existing broadcloth and clothes records were corroborated in
        // a live trading dock on the supported executable.
        "building_trading_dock" => id is 92 or 46,
        var name => Classes.Contains(name)
    };
    private static readonly HashSet<int> CampInsertions = [6, 9, 10, 18, 33, 36, 69, 80, 85, 92, 109, 123, 133];
    public static bool SupportsInsertion(string type, int id) => type.ToLowerInvariant() switch
    {
        "building_camp_storage" => CampInsertions.Contains(id),
        "building_granary" => id == 87,
        "building_warehouse" => id is 36 or 48,
        "building_well" => id == 14,
        "building_stone_mining_camp" => id is 41 or 65,
        _ => false
    };
    public sealed record Entry(int RuleNumber, BuildingCandidate Building, ResourceObservation Record, float Target, bool Insert = false);
    public sealed record Plan(IReadOnlyList<Entry> Entries, IReadOnlyList<string> Skipped);

    public static Plan Prepare(ProfileDocument profile, DiscoveryResult scan)
    {
        if (!scan.Complete || scan.UnreadableChunks != 0 || scan.GameSha256 != FishBulkEditor.SupportedHash || scan.CountOffset != 408 || scan.PointerOffset != 416)
            throw new InvalidOperationException("A complete scan of the tested game build is required.");
        if (!profile.AutomationProfiles.TryGetValue(profile.SelectedProfile, out var settings))
            throw new InvalidOperationException("The selected profile is unavailable.");
        var assessment = RuleMonitor.Assess(scan, settings);
        var entries = new List<Entry>(); var skipped = new List<string>();
        for (int i = 0; i < settings.MaintenanceRules.Count; i++)
        {
            var rule = settings.MaintenanceRules[i];
            if (!rule.Enabled) continue;
            string prefix = $"Rule {i + 1} (resource {rule.ResourceId}): ";
            if (assessment.Rows[i].State == "Conflicting rules") { skipped.Add(prefix + "conflicting rule"); continue; }
            if (rule.Target is null || rule.BuildingLimit == 0 || !SupportsExisting(rule.BuildingType, rule.ResourceId))
            { skipped.Add(prefix + "unsupported target, building, mode or limit"); continue; }
            var matches = scan.Buildings.Where(b => string.Equals(b.ClassName, rule.BuildingType.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length == 0) { skipped.Add(prefix + "no matching building candidates"); continue; }
            if (rule.BuildingLimit > 0 && matches.Length > rule.BuildingLimit)
            { skipped.Add(prefix + "limited-building selection is not verified"); continue; }
            if (rule.Mode == MaintenanceMode.Across)
            {
                var across = AcrossRulePlanner.Create(rule, matches);
                if (across.Unsupported is not null) { skipped.Add(prefix + across.Unsupported); continue; }
                foreach (var acrossTarget in across.Targets)
                    entries.Add(new(i + 1, acrossTarget.Building, acrossTarget.Record, acrossTarget.RecordTarget));
                continue;
            }
            float target = (float)rule.Target.Value;
            if (!float.IsFinite(target) || (decimal)target != rule.Target.Value)
            { skipped.Add(prefix + "target cannot be represented exactly"); continue; }
            foreach (var b in matches)
            {
            string buildingPrefix = prefix + $"candidate 0x{b.Address:X}: ";
            if (b.ResourceCount != b.Resources.Count || b.ResourceCount is < 0 or > 511 || b.ResourcePointer == 0 ||
                b.Resources.Where((r, index) => r.Index != index || !float.IsFinite(r.Amount) || r.Amount < 0 || r.Amount > 1_000_000_000f).Any())
            { skipped.Add(buildingPrefix + "resource layout is empty or invalid"); continue; }
            var records = b.Resources.Where(r => r.Id == rule.ResourceId && r.Status == 0).ToArray();
            // Only selected TYPE_0 resources allowed by the reconstructed
            // editor's building masks are admitted for insertion.
            if (records.Length == 0 && SupportsInsertion(b.ClassName, rule.ResourceId) &&
                !b.Resources.Any(r => r.Id == rule.ResourceId) && target > 0)
            {
                entries.Add(new(i + 1, b, new(b.ResourceCount, rule.ResourceId, 0, 0, 0xbf800000, 0), target, true));
                continue;
            }
            if (records.Length == 0) { skipped.Add(buildingPrefix + "no supported stored record or verified insertion"); continue; }
            // The game can split stock into multiple stored records. The old
            // editor increases the first by the shortfall in their combined
            // total; keep reserved and awaited records untouched.
            decimal total = records.Sum(r => (decimal)r.Amount);
            if (total >= rule.Target.Value) continue;
            decimal firstTarget = (decimal)records[0].Amount + rule.Target.Value - total;
            float newFirst = (float)firstTarget;
            if (firstTarget > 1_000_000_000m || !float.IsFinite(newFirst) || (decimal)newFirst != firstTarget)
            { skipped.Add(buildingPrefix + "the required record amount cannot be represented exactly"); continue; }
            entries.Add(new(i + 1, b, records[0], newFirst));
            }
        }
        foreach (var campGroup in entries.Where(e => e.Building.ClassName == CampRuleExecutor.BuildingType).GroupBy(e => e.Building.Address).ToArray())
        {
            var campEntries = campGroup.ToArray();
            var camp = campEntries[0].Building; var changing = campEntries.Select(e => e.Record.Id).ToHashSet();
            if (!camp.Resources.Any(r => r.Status == 0 && !changing.Contains(r.Id) && float.IsFinite(r.Amount) && r.Amount >= 1000f))
            {
                // When no unchanged anchor exists, top up an already nonempty
                // item to at least 1,000 before changing other camp items.
                var anchor = campEntries.Where(e => !e.Insert && e.Record.Amount > 0 && e.Target >= 1000f)
                    .OrderByDescending(e => e.Record.Amount).FirstOrDefault();
                if (anchor is null)
                {
                    entries.RemoveAll(e => e.Building.Address == camp.Address);
                    foreach (var entry in campEntries)
                        skipped.Add($"Rule {entry.RuleNumber} (resource {entry.Record.Id}): camp candidate 0x{camp.Address:X} needs a nonempty anchor of 1,000 or a rule that tops one up first.");
                }
                else
                {
                    entries.Remove(anchor);
                    entries.Insert(0, anchor);
                }
            }
        }
        return new(entries.AsReadOnly(), skipped.AsReadOnly());
    }

    private static byte[] Encode(ResourceObservation r)
    {
        var data = new byte[20]; BinaryPrimitives.WriteInt32LittleEndian(data, r.Id);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4), BitConverter.SingleToInt32Bits(r.Amount));
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), r.Flags);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), r.Details);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16), r.Extra); return data;
    }
    private static byte[] Header(FishBulkEditor.IMemory memory, DiscoveryResult scan, BuildingCandidate b, bool insert)
    {
        int length = scan.PointerOffset + (insert ? 12 : 8);
        byte[] data = memory.Read(b.Address, length);
        if (data.Length != length || BinaryPrimitives.ReadUInt64LittleEndian(data) != b.Vtable ||
            BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(scan.CountOffset)) != b.ResourceCount ||
            BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(scan.PointerOffset)) != b.ResourcePointer)
            throw new InvalidOperationException("A building changed since the scan.");
        if (insert)
        {
            int allocated = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(424));
            if (allocated <= b.ResourceCount || allocated > 4096 || allocated % 64 != 0)
                throw new InvalidOperationException("The granary has no verified spare resource slot.");
        }
        return data;
    }
    private static Plan QualifyInsertions(Plan plan, DiscoveryResult scan, FishBulkEditor.IMemory memory)
    {
        var entries = new List<Entry>(); var skipped = plan.Skipped.ToList();
        var reserved = new HashSet<ulong>();
        foreach (var entry in plan.Entries)
        {
            if (!entry.Insert) { entries.Add(entry); continue; }
            var b = entry.Building;
            string reason = "";
            if (reserved.Contains(b.Address)) reason = "only one new record per building can be added in a cycle";
            else
            {
                byte[] header = Header(memory, scan, b, false);
                int allocated = BinaryPrimitives.ReadInt32LittleEndian(memory.Read(checked(b.Address + 424), 4));
                if (allocated <= b.ResourceCount || allocated > 4096 || allocated % 64 != 0) reason = "no verified spare resource slot";
                else
                {
                    byte[] slot = memory.Read(checked(b.ResourcePointer + (ulong)b.ResourceCount * 20), 20);
                    if (!header.AsSpan().SequenceEqual(Header(memory, scan, b, false)))
                        throw new InvalidOperationException("A building changed while its spare slot was checked.");
                    if (!slot.AsSpan().SequenceEqual(memory.Read(checked(b.ResourcePointer + (ulong)b.ResourceCount * 20), 20)))
                        throw new InvalidOperationException("A spare resource slot changed while it was checked.");
                }
            }
            if (reason.Length != 0)
                skipped.Add($"Rule {entry.RuleNumber} (resource {entry.Record.Id}): candidate 0x{b.Address:X}: {reason}.");
            else { reserved.Add(b.Address); entries.Add(entry); }
        }
        return new(entries.AsReadOnly(), skipped.AsReadOnly());
    }
    public static int ApplyChecked(ProfileDocument profile, DiscoveryResult scan, FishBulkEditor.IMemory memory) =>
        ApplyCheckedDetailed(profile, scan, memory).Applied;

    public static LiveRuleResult ApplyCheckedDetailed(ProfileDocument profile, DiscoveryResult scan, FishBulkEditor.IMemory memory)
    {
        ArgumentNullException.ThrowIfNull(memory);
        var plan = QualifyInsertions(Prepare(profile, scan), scan, memory);
        return ApplyPlannedChecked(plan, scan, memory);
    }

    // The caller must construct a validated plan from the complete scan. The
    // full scan remains available here for overlap and identity checks.
    internal static LiveRuleResult ApplyPlannedChecked(Plan plan, DiscoveryResult scan, FishBulkEditor.IMemory memory)
    {
        if (plan.Entries.Count == 0) return new(scan, 0, plan);
        var buildings = plan.Entries.Select(e => e.Building).DistinctBy(b => b.Address).ToArray();
        bool Inserts(BuildingCandidate b) => plan.Entries.Any(e => e.Building.Address == b.Address && e.Insert);
        var ranges = buildings.Select(b => (Start: b.ResourcePointer, End: checked(b.ResourcePointer + (ulong)(b.ResourceCount + (Inserts(b) ? 1 : 0)) * 20))).OrderBy(r => r.Start).ToArray();
        if (ranges.Zip(ranges.Skip(1)).Any(p => p.First.End > p.Second.Start) ||
            ranges.Any(r => scan.Buildings.Any(b => r.Start < checked(b.Address + (ulong)scan.PointerOffset + 12) && b.Address < r.End)) ||
            buildings.Where(Inserts).Any(b => scan.Buildings.Where(other => other.Address != b.Address && other.ResourcePointer != 0).Any(other =>
                b.ResourcePointer < checked(other.ResourcePointer + (ulong)other.ResourceCount * 20) &&
                other.ResourcePointer < checked(b.ResourcePointer + (ulong)(b.ResourceCount + 1) * 20))))
            throw new InvalidOperationException("Resource arrays overlap each other or a building header.");
        var headers = new Dictionary<ulong, byte[]>(); var expected = new Dictionary<ulong, byte[]>();
        foreach (var b in buildings)
        {
            bool insert = Inserts(b);
            headers[b.Address] = Header(memory, scan, b, insert);
            byte[] bytes = b.Resources.SelectMany(Encode).ToArray();
            if (!bytes.AsSpan().SequenceEqual(memory.Read(b.ResourcePointer, bytes.Length)))
                throw new InvalidOperationException("Resources changed since the scan.");
            if (insert)
            {
                byte[] slot = memory.Read(checked(b.ResourcePointer + (ulong)b.ResourceCount * 20), 20);
                if (slot.Length != 20) throw new InvalidOperationException("A spare resource slot could not be read.");
                bytes = bytes.Concat(slot).ToArray();
            }
            expected[b.Address] = bytes;
        }
        int attempts = 0;
        try
        {
            foreach (var entry in plan.Entries)
            {
                var b = entry.Building; var bytes = expected[b.Address];
                if (!headers[b.Address].AsSpan().SequenceEqual(memory.Read(b.Address, headers[b.Address].Length)) ||
                    !bytes.AsSpan().SequenceEqual(memory.Read(b.ResourcePointer, bytes.Length)))
                    throw new InvalidOperationException("A building or its resources changed before an edit.");
                attempts++;
                if (entry.Insert)
                {
                    int offset = checked(entry.Record.Index * 20);
                    byte[] record = Encode(entry.Record with { Amount = entry.Target });
                    memory.Write(checked(b.ResourcePointer + (ulong)offset), record); record.CopyTo(bytes, offset);
                    if (!headers[b.Address].AsSpan().SequenceEqual(memory.Read(b.Address, headers[b.Address].Length)) ||
                        !bytes.AsSpan().SequenceEqual(memory.Read(b.ResourcePointer, bytes.Length)))
                        throw new InvalidOperationException("The new resource record failed readback.");
                    byte[] count = BitConverter.GetBytes(b.ResourceCount + 1);
                    memory.Write(checked(b.Address + (ulong)scan.CountOffset), count); count.CopyTo(headers[b.Address], scan.CountOffset);
                }
                else
                {
                    int offset = checked(entry.Record.Index * 20 + 4);
                    byte[] value = BitConverter.GetBytes(entry.Target);
                    memory.Write(checked(b.ResourcePointer + (ulong)offset), value); value.CopyTo(bytes, offset);
                }
                if (!headers[b.Address].AsSpan().SequenceEqual(memory.Read(b.Address, headers[b.Address].Length)) ||
                    !bytes.AsSpan().SequenceEqual(memory.Read(b.ResourcePointer, bytes.Length)))
                    throw new InvalidOperationException("Memory readback did not match.");
            }
        }
        catch (Exception ex) when (attempts > 0)
        { throw new PartialGameWriteException("The batch may be partially applied. Do not retry or save; reload the verified backup. " + ex.Message, ex); }
        return new(scan, attempts, plan);
    }
    public static int ApplyToGame(ProfileDocument profile, DiscoveryResult scan) => ApplyToGameDetailed(profile, scan).Applied;
    public static LiveRuleResult ApplyToGameDetailed(ProfileDocument profile, DiscoveryResult scan)
    {
        var initial = Prepare(profile, scan);
        if (initial.Entries.Count == 0) return new(scan, 0, initial);
        using (var file = File.Open(scan.GamePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            if (Convert.ToHexString(SHA256.HashData(file)) != FishBulkEditor.SupportedHash) throw new InvalidOperationException("The executable changed.");
        using var process = Process.GetProcessById(scan.ProcessId);
        if (process.HasExited || process.StartTime.ToUniversalTime() != scan.ProcessStartedUtc ||
            !string.Equals(Path.GetFullPath(process.MainModule!.FileName), Path.GetFullPath(scan.GamePath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The game session changed; scan again.");
        using var memory = new ProcessMemory(scan.ProcessId);
        return ApplyCheckedDetailed(profile, scan, memory);
    }
    internal sealed class ProcessMemory : FishBulkEditor.IMemory, IDisposable
    {
        [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int id);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ReadProcessMemory(SafeProcessHandle process, IntPtr address, byte[] data, nuint size, out nuint read);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool WriteProcessMemory(SafeProcessHandle process, IntPtr address, byte[] data, nuint size, out nuint written);
        private readonly SafeProcessHandle _handle;
        public ProcessMemory(int id) { _handle = OpenProcess(0x400 | 0x10 | 0x20 | 0x08, false, id); if (_handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error()); }
        public byte[] Read(ulong address, int size)
        {
            if (size == 0) return [];
            var data = new byte[size];
            if (!ReadProcessMemory(_handle, new IntPtr(checked((long)address)), data, (nuint)size, out var read) || read != (nuint)size) throw new Win32Exception(Marshal.GetLastWin32Error(), "Game memory could not be read.");
            return data;
        }
        public void Write(ulong address, byte[] data)
        {
            if (!WriteProcessMemory(_handle, new IntPtr(checked((long)address)), data, (nuint)data.Length, out var written) || written != (nuint)data.Length) throw new Win32Exception(Marshal.GetLastWin32Error(), "The game memory write did not complete.");
        }
        public void Dispose() => _handle.Dispose();
    }
}
