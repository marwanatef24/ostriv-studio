using System.Text.Json;

namespace OstrivStudio.Core;

// This master switch is separate from the user's profiles and rule data.
public static class AutomationControlStore
{
    private sealed record Settings(bool Enabled);

    public static bool Load(string path)
    {
        if (!File.Exists(path)) return true;
        using var stream = File.OpenRead(path);
        return (JsonSerializer.Deserialize<Settings>(stream) ?? throw new InvalidDataException("Automation settings are empty.")).Enabled;
    }

    public static void Save(string path, bool enabled)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, $".{Guid.NewGuid():N}.automation.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(stream, new Settings(enabled)); stream.Flush(true); }
            if (Load(temporary) != enabled) throw new InvalidDataException("Automation setting could not be verified.");
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
