using System.Security.Cryptography;
using System.Text.Json;

namespace OstrivStudio.Core;

public sealed record CropPreset(string Name, int[] FutureCrops)
{
    public override string ToString() => Name;
}

// App-owned future-crop plans. No process addresses or current crops are saved.
public static class CropPresetStore
{
    private static readonly HashSet<int> Known = [6, 7, 8, 9, 10, 13, 83, 87, 179, 181];
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    public sealed record Snapshot(List<CropPreset> Presets, string? Hash);
    private static void Validate(IReadOnlyList<CropPreset> presets)
    {
        if (presets.Count > 200) throw new InvalidDataException("At most 200 crop presets can be saved.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in presets)
            if (p is null || string.IsNullOrWhiteSpace(p.Name) || p.Name.Length > 80 || p.Name.Any(char.IsControl) ||
                p.Name != p.Name.Trim() || !names.Add(p.Name) || p.FutureCrops is null || p.FutureCrops.Length is < 1 or > 5 || p.FutureCrops.Any(id => !Known.Contains(id)))
                throw new InvalidDataException("A crop preset has an invalid name or future crop sequence.");
    }
    public static Snapshot Load(string path)
    {
        if (!File.Exists(path)) return new([], null);
        if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException("The crop preset file is too large.");
        byte[] data = File.ReadAllBytes(path);
        var presets = JsonSerializer.Deserialize<List<CropPreset>>(data) ?? throw new InvalidDataException("The crop preset file is empty.");
        Validate(presets);
        return new(presets, Convert.ToHexString(SHA256.HashData(data)));
    }
    public static Snapshot Save(string path, Snapshot reviewed, IReadOnlyList<CropPreset> presets)
    {
        Validate(presets);
        string directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        // Serializes writers from installed copies of this app.
        using var lease = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var current = Load(path);
        if (current.Hash != reviewed.Hash) throw new InvalidOperationException("Crop presets changed in another window. Reopen the farm planner before saving.");
        string temp = Path.Combine(directory, $".{Guid.NewGuid():N}.crop-presets.tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(stream, presets, Options); stream.Flush(true); }
            _ = Load(temp);
            File.Move(temp, path, true);
            return Load(path);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
