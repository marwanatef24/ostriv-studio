using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Reflection.PortableExecutable;
using System.Text;

namespace OstrivStudio.Core;

// The entries represent candidates, not proven, stable game objects. Addresses
// are session-scoped and must never be persisted or used for a write.
public sealed record BuildingCandidate(string ClassName, ulong Address, ulong Vtable, ulong ResourcePointer, int ResourceCount, IReadOnlyList<ResourceObservation> Resources, string Status, float? ObservedCapacity = null);
public sealed record ResourceObservation(int Index, int Id, float Amount, uint Flags, uint Details, uint Extra)
{
    public uint Status => Flags & 0xffff;
    public string StatusLabel => Status switch { 0 => "Stored", 1 => "Reserved", 256 => "Awaited", _ => $"Unknown ({Status})" };
}
public sealed record UiClassCandidate(string ClassName, ulong Address, ulong Vtable);
public sealed record DiscoveryResult(int ProcessId, DateTime ProcessStartedUtc, string GamePath, string GameSha256, int CountOffset, int PointerOffset, IReadOnlyList<BuildingCandidate> Buildings, long BytesScanned, bool Complete, int UnreadableChunks, string Note)
{
    public IReadOnlyList<UiClassCandidate> UiCandidates { get; init; } = [];
}
public sealed record ResourceTotals(int Id, double Stored, double Reserved, double Awaited, double Other);

public static class BuildingDiscovery
{
    public static ulong? FindExactVtable(byte[] image, ulong preferredImageBase, string typeName)
    {
        if (typeName.Length is < 1 or > 80 || typeName.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_')) throw new ArgumentException("Invalid RTTI class name.", nameof(typeName));
        byte[] marker = Encoding.ASCII.GetBytes(".?AV" + typeName + "@@");
        var descriptors = new HashSet<uint>();
        for (int position = 0; position <= image.Length - marker.Length; position++)
            if (position >= 16 && image.AsSpan(position, marker.Length).SequenceEqual(marker)) descriptors.Add((uint)(position - 16));
        if (descriptors.Count != 1) return null;
        var locators = new HashSet<ulong>();
        for (int i = 0; i <= image.Length - 24; i += 4)
            if (BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(i)) == 1 && BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(i + 4)) == 0 &&
                descriptors.Contains(BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(i + 12))) && BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(i + 20)) == i)
                locators.Add(preferredImageBase + (uint)i);
        var tables = new List<ulong>();
        for (int i = 0; i <= image.Length - 16; i += 8)
            if (locators.Contains(BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(i)))) tables.Add((ulong)i + 8);
        return tables.Count == 1 ? tables[0] : null;
    }
    public static IReadOnlyList<ResourceTotals> Summarize(IEnumerable<ResourceObservation> observations) => observations.GroupBy(x => x.Id)
        .Select(g => new ResourceTotals(g.Key, g.Where(x => x.Status == 0).Sum(x => (double)x.Amount),
            g.Where(x => x.Status == 1).Sum(x => (double)x.Amount), g.Where(x => x.Status == 256).Sum(x => (double)x.Amount),
            g.Where(x => x.Status is not (0 or 1 or 256)).Sum(x => (double)x.Amount))).ToArray();
    public static Dictionary<ulong, string> FindVtables(byte[] image, ulong preferredImageBase)
    {
        var descriptorRvas = new Dictionary<uint, string>();
        ReadOnlySpan<byte> marker = ".?AVbuilding_"u8;
        int pos = 0;
        while (pos < image.Length)
        {
            int hit = image.AsSpan(pos).IndexOf(marker);
            if (hit < 0) break;
            hit += pos; pos = hit + marker.Length;
            if (hit < 16) continue;
            int end = Array.IndexOf(image, (byte)'@', hit + 4);
            if (end < 0 || end - hit > 100) continue;
            string name = Encoding.ASCII.GetString(image, hit + 4, end - hit - 4);
            if (name.Skip("building_".Length).Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_')) continue;
            descriptorRvas[(uint)(hit - 16)] = name;
        }
        var locators = new Dictionary<ulong, string>();
        for (int i = 0; i <= image.Length - 24; i += 4)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(i)) != 1 || BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(i + 4)) != 0) continue;
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(i + 12));
            uint self = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(i + 20));
            if (self == i && descriptorRvas.TryGetValue(type, out var name)) locators[preferredImageBase + (uint)i] = name;
        }
        var candidates = new Dictionary<string, List<ulong>>(StringComparer.Ordinal);
        for (int i = 0; i <= image.Length - 16; i += 8)
        {
            ulong ptr = BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(i));
            if (!locators.TryGetValue(ptr, out var name)) continue;
            if (!candidates.TryGetValue(name, out var list)) candidates[name] = list = [];
            list.Add((ulong)i + 8);
        }
        // Reject ambiguity; never silently pick the first candidate table.
        return candidates.Where(x => x.Value.Count == 1).ToDictionary(x => x.Value[0], x => x.Key);
    }

    public static (byte[] Image, ulong PreferredBase, int TextRva, int TextLength) LoadImage(string path)
    {
        using var file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (file.Length <= 0 || file.Length > 128 * 1024 * 1024) throw new InvalidDataException("Unsupported executable size.");
        using var pe = new PEReader(file);
        var h = pe.PEHeaders.PEHeader ?? throw new InvalidDataException("Not a PE executable.");
        if (h.Magic != PEMagic.PE32Plus || pe.PEHeaders.CoffHeader.Machine != Machine.Amd64 || h.SizeOfImage <= 0 || h.SizeOfImage > 128 * 1024 * 1024)
            throw new InvalidDataException("Expected a bounded 64-bit Windows executable.");
        var text = pe.PEHeaders.SectionHeaders.Where(x => x.Name == ".text").ToArray();
        if (text.Length != 1 || text[0].VirtualSize <= 0 || text[0].SizeOfRawData <= 0) throw new InvalidDataException("Missing or ambiguous code section.");
        byte[] image = new byte[h.SizeOfImage];
        foreach (var section in pe.PEHeaders.SectionHeaders)
        {
            if (section.VirtualAddress < 0 || section.SizeOfRawData < 0 || section.VirtualAddress > image.Length || section.SizeOfRawData > image.Length - section.VirtualAddress)
                throw new InvalidDataException("Invalid PE section bounds.");
            if (section.SizeOfRawData == 0 || section.VirtualSize <= 0) continue;
            var bytes = pe.GetSectionData(section.VirtualAddress).GetContent(0, Math.Min(section.SizeOfRawData, section.VirtualSize));
            bytes.CopyTo(image, section.VirtualAddress);
        }
        int textSize = Math.Min(text[0].SizeOfRawData, text[0].VirtualSize);
        return (image, h.ImageBase, text[0].VirtualAddress, textSize);
    }

    public static IReadOnlyList<ResourceObservation>? DecodeResources(ReadOnlySpan<byte> data, int count)
    {
        if (count < 0 || count > 511 || data.Length != count * 20) return null;
        var result = new List<ResourceObservation>(count);
        for (int i = 0; i < count; i++)
        {
            var row = data.Slice(i * 20, 20);
            int id = BinaryPrimitives.ReadInt32LittleEndian(row);
            float amount = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(row[4..]));
            if (id <= 0 || id > 65535 || !float.IsFinite(amount) || amount < 0 || amount > 1_000_000_000) return null;
            uint flags = BinaryPrimitives.ReadUInt32LittleEndian(row[8..]);
            uint details = BinaryPrimitives.ReadUInt32LittleEndian(row[12..]);
            uint extra = BinaryPrimitives.ReadUInt32LittleEndian(row[16..]);
            result.Add(new(i, id, amount, flags, details, extra));
        }
        return new ReadOnlyCollection<ResourceObservation>(result);
    }
}
