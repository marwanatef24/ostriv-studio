using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace OstrivStudio.Core;

// One narrow, supervised maintenance operation on the tested build. No
// addresses are persisted and no unverified resource is written.
public static class CampRuleExecutor
{
    public const string BuildingType = "building_camp_storage";
    public const int ResourceId = 92;
    public const decimal ValidatedTarget = 2000m;
    public sealed record Plan(BuildingCandidate Building, ResourceObservation Record, bool NeedsWrite);
    public sealed record BatchEntry(string ResourceName, ResourceObservation Record, float Target);
    public sealed record BatchPlan(BuildingCandidate Building, IReadOnlyList<BatchEntry> Entries, IReadOnlyList<string> Skipped);

    // The game removes camp storage after depletion. Require another stored
    // item to remain as an anchor, and refuse ambiguous camp candidates.
    private static void CheckCampAnchor(DiscoveryResult snapshot, BuildingCandidate building, IEnumerable<int> changedIds)
    {
        if (snapshot.Buildings.Count(b => b.ClassName == BuildingType) != 1)
            throw new InvalidOperationException("The camp-storage candidate is ambiguous; no edit was made.");
        var changed = changedIds.ToHashSet();
        if (!building.Resources.Any(r => r.Status == 0 && !changed.Contains(r.Id) && float.IsFinite(r.Amount) && r.Amount >= 1000f))
            throw new InvalidOperationException("A separate nonempty camp-storage anchor of at least 1,000 is required.");
    }

    // These targets correspond to existing records observed in the disposable
    // test settlement. This is deliberately not a general resource writer.
    private static readonly (int Id, string Name, int Target)[] BatchResources =
        [(92, "Broadcloth", 2000), (33, "Iron", 1000), (69, "Horse tack", 2000)];

    public static Plan Prepare(ProfileDocument profile, DiscoveryResult? snapshot, IReadOnlyList<BuildingCandidate> selected)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(selected);
        if (snapshot is null || !snapshot.Complete || snapshot.UnreadableChunks != 0 ||
            snapshot.GameSha256 != FishBulkEditor.SupportedHash || snapshot.CountOffset != 408 || snapshot.PointerOffset != 416)
            throw new InvalidOperationException("A complete scan of the tested game build is required.");
        if (selected.Count != 1 || selected[0].ClassName != BuildingType || !snapshot.Buildings.Any(x => ReferenceEquals(x, selected[0])))
            throw new InvalidOperationException("Select one camp-storage candidate from the current scan.");
        if (!profile.AutomationProfiles.TryGetValue(profile.SelectedProfile, out var settings))
            throw new InvalidOperationException("The selected profile is unavailable.");
        var rules = settings.MaintenanceRules.Where(r => r.Enabled && r.ResourceId == ResourceId &&
            (string.IsNullOrWhiteSpace(r.BuildingType) || string.Equals(r.BuildingType.Trim(), BuildingType, StringComparison.OrdinalIgnoreCase))).ToArray();
        if (rules.Length != 1) throw new InvalidOperationException("Exactly one enabled broadcloth rule must match camp storage.");
        var rule = rules[0];
        if (rule.Mode != MaintenanceMode.Each || rule.BuildingLimit is < -1 or 0 || rule.Target != ValidatedTarget)
            throw new InvalidOperationException("This action currently supports an Each rule with a target of 2,000 broadcloth and at least one building.");
        var building = selected[0];
        if (building.ResourceCount != building.Resources.Count || building.ResourceCount is <= 0 or > 511)
            throw new InvalidOperationException("The candidate resource layout is incomplete.");
        var records = building.Resources.Where(r => r.Id == ResourceId && r.Status == 0).ToArray();
        if (records.Length != 1 || records[0].Index < 0 || records[0].Index >= building.ResourceCount || !float.IsFinite(records[0].Amount))
            throw new InvalidOperationException("The candidate must contain one existing stored broadcloth record.");
        float amount = records[0].Amount;
        if (amount >= 2000f) return new(building, records[0], false);
        if (amount != 1999f) throw new InvalidOperationException("Only the validated 1,999 to 2,000 adjustment is supported.");
        return new(building, records[0], true);
    }

    public static BatchPlan PrepareBatch(ProfileDocument profile, DiscoveryResult? snapshot, IReadOnlyList<BuildingCandidate> selected)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(selected);
        if (snapshot is null || !snapshot.Complete || snapshot.UnreadableChunks != 0 ||
            snapshot.GameSha256 != FishBulkEditor.SupportedHash || snapshot.CountOffset != 408 || snapshot.PointerOffset != 416)
            throw new InvalidOperationException("A complete scan of the tested game build is required.");
        if (selected.Count != 1 || selected[0].ClassName != BuildingType || !snapshot.Buildings.Any(x => ReferenceEquals(x, selected[0])))
            throw new InvalidOperationException("Select one camp-storage candidate from the current scan.");
        if (!profile.AutomationProfiles.TryGetValue(profile.SelectedProfile, out var settings))
            throw new InvalidOperationException("The selected profile is unavailable.");
        var building = selected[0];
        if (building.ResourceCount != building.Resources.Count || building.ResourceCount is <= 0 or > 511)
            throw new InvalidOperationException("The candidate resource layout is incomplete.");
        var entries = new List<BatchEntry>();
        var skipped = new List<string>();
        foreach (var (id, name, target) in BatchResources)
        {
            var rules = settings.MaintenanceRules.Where(r => r.Enabled && r.ResourceId == id &&
                (string.IsNullOrWhiteSpace(r.BuildingType) || string.Equals(r.BuildingType.Trim(), BuildingType, StringComparison.OrdinalIgnoreCase))).ToArray();
            if (rules.Length == 0) { skipped.Add($"{name}: no matching enabled rule"); continue; }
            if (rules.Length != 1) throw new InvalidOperationException($"More than one enabled {name} rule matches camp storage.");
            var rule = rules[0];
            if (rule.Mode != MaintenanceMode.Each || rule.BuildingLimit is < -1 or 0 || rule.Target != target)
            { skipped.Add($"{name}: unsupported target, mode, or building limit"); continue; }
            var records = building.Resources.Where(r => r.Id == id && r.Status == 0).ToArray();
            if (records.Length != 1 || records[0].Index < 0 || records[0].Index >= building.ResourceCount || !float.IsFinite(records[0].Amount))
            { skipped.Add($"{name}: one existing stored record is required"); continue; }
            if (records[0].Amount >= target) { skipped.Add($"{name}: target already met"); continue; }
            if (records[0].Amount != target - 1) { skipped.Add($"{name}: only a one-unit increase is supported"); continue; }
            entries.Add(new(name, records[0], target));
        }
        return new(building, entries.AsReadOnly(), skipped.AsReadOnly());
    }
    private static byte[] Encode(ResourceObservation r)
    {
        var data = new byte[20];
        BinaryPrimitives.WriteInt32LittleEndian(data, r.Id);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4), BitConverter.SingleToInt32Bits(r.Amount));
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), r.Flags);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), r.Details);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16), r.Extra);
        return data;
    }
    private static byte[] Header(FishBulkEditor.IMemory memory, DiscoveryResult snapshot, BuildingCandidate building)
    {
        var header = memory.Read(building.Address, snapshot.PointerOffset + 8);
        if (header.Length != snapshot.PointerOffset + 8 || BinaryPrimitives.ReadUInt64LittleEndian(header) != building.Vtable ||
            BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(snapshot.CountOffset)) != building.ResourceCount ||
            BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(snapshot.PointerOffset)) != building.ResourcePointer)
            throw new InvalidOperationException("The selected building changed since the scan.");
        return header;
    }
    private static void CheckRecords(FishBulkEditor.IMemory memory, BuildingCandidate building)
    {
        var bytes = memory.Read(building.ResourcePointer, checked(building.ResourceCount * 20));
        if (bytes.Length != building.ResourceCount * 20) throw new InvalidOperationException("The resource array could not be read completely.");
        for (int i = 0; i < building.Resources.Count; i++)
            if (building.Resources[i].Index != i || !bytes.AsSpan(i * 20, 20).SequenceEqual(Encode(building.Resources[i])))
                throw new InvalidOperationException("Resources changed since the scan.");
    }
    public static bool ApplyChecked(ProfileDocument profile, DiscoveryResult snapshot, IReadOnlyList<BuildingCandidate> selected, FishBulkEditor.IMemory memory)
    {
        ArgumentNullException.ThrowIfNull(memory);
        var plan = Prepare(profile, snapshot, selected);
        if (!plan.NeedsWrite) return false;
        var b = plan.Building;
        ulong end = checked(b.ResourcePointer + (ulong)b.ResourceCount * 20);
        if (b.ResourcePointer < checked(b.Address + (ulong)snapshot.PointerOffset + 8) && b.Address < end)
            throw new InvalidOperationException("The resource array overlaps the building header.");
        var header = Header(memory, snapshot, b);
        CheckRecords(memory, b);
        if (!header.AsSpan().SequenceEqual(Header(memory, snapshot, b))) throw new InvalidOperationException("The building changed during validation.");
        CheckRecords(memory, b);
        ulong recordAddress = checked(b.ResourcePointer + (ulong)plan.Record.Index * 20);
        try
        {
            memory.Write(checked(recordAddress + 4), BitConverter.GetBytes(2000f));
            var expected = Encode(plan.Record); BitConverter.GetBytes(2000f).CopyTo(expected, 4);
            if (!memory.Read(recordAddress, 20).AsSpan().SequenceEqual(expected) || !header.AsSpan().SequenceEqual(Header(memory, snapshot, b)))
                throw new InvalidOperationException("Memory readback did not match.");
        }
        catch (Exception ex)
        { throw new InvalidOperationException("A write may have occurred. Do not save; reload the verified backup. " + ex.Message, ex); }
        return true;
    }
    public static bool ApplyToGame(ProfileDocument profile, DiscoveryResult snapshot, IReadOnlyList<BuildingCandidate> selected)
    {
        var plan = Prepare(profile, snapshot, selected);
        if (!plan.NeedsWrite) return false;
        CheckCampAnchor(snapshot, plan.Building, [plan.Record.Id]);
        using (var file = File.Open(snapshot.GamePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            if (Convert.ToHexString(SHA256.HashData(file)) != FishBulkEditor.SupportedHash) throw new InvalidOperationException("The game executable changed.");
        using var process = Process.GetProcessById(snapshot.ProcessId);
        if (process.HasExited || process.StartTime.ToUniversalTime() != snapshot.ProcessStartedUtc ||
            !string.Equals(Path.GetFullPath(process.MainModule!.FileName), Path.GetFullPath(snapshot.GamePath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The game session changed; scan again.");
        using var memory = new ProcessMemory(snapshot.ProcessId);
        return ApplyChecked(profile, snapshot, selected, memory);
    }

    public static int ApplyBatchChecked(ProfileDocument profile, DiscoveryResult snapshot, IReadOnlyList<BuildingCandidate> selected, FishBulkEditor.IMemory memory)
    {
        ArgumentNullException.ThrowIfNull(memory);
        var plan = PrepareBatch(profile, snapshot, selected);
        if (plan.Entries.Count == 0) return 0;
        var b = plan.Building;
        ulong end = checked(b.ResourcePointer + (ulong)b.ResourceCount * 20);
        if (b.ResourcePointer < checked(b.Address + (ulong)snapshot.PointerOffset + 8) && b.Address < end)
            throw new InvalidOperationException("The resource array overlaps the building header.");
        var expectedHeader = Header(memory, snapshot, b);
        CheckRecords(memory, b);
        var expectedRecords = b.Resources.SelectMany(Encode).ToArray();
        if (!expectedHeader.AsSpan().SequenceEqual(Header(memory, snapshot, b)) ||
            !expectedRecords.AsSpan().SequenceEqual(memory.Read(b.ResourcePointer, expectedRecords.Length)))
            throw new InvalidOperationException("The building or resources changed during validation.");
        int attempts = 0;
        try
        {
            foreach (var entry in plan.Entries)
            {
                if (!expectedHeader.AsSpan().SequenceEqual(Header(memory, snapshot, b)) ||
                    !expectedRecords.AsSpan().SequenceEqual(memory.Read(b.ResourcePointer, expectedRecords.Length)))
                    throw new InvalidOperationException("The building or resources changed before the next edit.");
                int offset = checked(entry.Record.Index * 20 + 4);
                byte[] amount = BitConverter.GetBytes(entry.Target);
                attempts++;
                memory.Write(checked(b.ResourcePointer + (ulong)offset), amount);
                amount.CopyTo(expectedRecords, offset);
                if (!expectedHeader.AsSpan().SequenceEqual(Header(memory, snapshot, b)) ||
                    !expectedRecords.AsSpan().SequenceEqual(memory.Read(b.ResourcePointer, expectedRecords.Length)))
                    throw new InvalidOperationException("Memory readback did not match.");
            }
        }
        catch (Exception ex) when (attempts > 0)
        { throw new InvalidOperationException("The batch may be partially applied. Do not retry or save; reload the verified backup. " + ex.Message, ex); }
        return attempts;
    }

    public static int ApplyBatchToGame(ProfileDocument profile, DiscoveryResult snapshot, IReadOnlyList<BuildingCandidate> selected)
    {
        var plan = PrepareBatch(profile, snapshot, selected);
        if (plan.Entries.Count == 0) return 0;
        CheckCampAnchor(snapshot, plan.Building, plan.Entries.Select(e => e.Record.Id));
        using (var file = File.Open(snapshot.GamePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            if (Convert.ToHexString(SHA256.HashData(file)) != FishBulkEditor.SupportedHash) throw new InvalidOperationException("The game executable changed.");
        using var process = Process.GetProcessById(snapshot.ProcessId);
        if (process.HasExited || process.StartTime.ToUniversalTime() != snapshot.ProcessStartedUtc ||
            !string.Equals(Path.GetFullPath(process.MainModule!.FileName), Path.GetFullPath(snapshot.GamePath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The game session changed; scan again.");
        using var memory = new ProcessMemory(snapshot.ProcessId);
        return ApplyBatchChecked(profile, snapshot, selected, memory);
    }
    private sealed class ProcessMemory : FishBulkEditor.IMemory, IDisposable
    {
        [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int id);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ReadProcessMemory(SafeProcessHandle process, IntPtr address, byte[] data, nuint size, out nuint read);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool WriteProcessMemory(SafeProcessHandle process, IntPtr address, byte[] data, nuint size, out nuint written);
        private readonly SafeProcessHandle _handle;
        public ProcessMemory(int id) { _handle = OpenProcess(0x400 | 0x10 | 0x20 | 0x08, false, id); if (_handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error()); }
        public byte[] Read(ulong address, int size)
        {
            var data = new byte[size];
            if (!ReadProcessMemory(_handle, new IntPtr(checked((long)address)), data, (nuint)size, out var read) || read != (nuint)size)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Game memory could not be read.");
            return data;
        }
        public void Write(ulong address, byte[] data)
        {
            if (!WriteProcessMemory(_handle, new IntPtr(checked((long)address)), data, (nuint)data.Length, out var written) || written != (nuint)data.Length)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "The memory write did not complete.");
        }
        public void Dispose() => _handle.Dispose();
    }
}
