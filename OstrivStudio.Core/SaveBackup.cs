using System.Security.Cryptography;
using System.Text.Json;

namespace OstrivStudio.Core;

public static class SaveBackup
{
    public sealed record BackupFile(string Original, string Copy);
    public sealed record SessionBackup(string Directory, string SelectedCopy, IReadOnlyList<BackupFile> Autosaves);
    private static byte[] Hash(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return SHA256.HashData(stream);
    }
    public static string CopyVerified(string source, string directory)
    {
        if (!string.Equals(Path.GetExtension(source), ".save", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Choose an Ostriv .save file.");
        var info = new FileInfo(source);
        if (!info.Exists || info.Length <= 0 || info.Length > 128 * 1024 * 1024) throw new InvalidDataException("The selected save has an unsupported size.");
        byte[] before = Hash(source);
        Directory.CreateDirectory(directory);
        string target = Path.Combine(directory, $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.save");
        using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None)) input.CopyTo(output);
        if (!before.AsSpan().SequenceEqual(Hash(source)) || !before.AsSpan().SequenceEqual(Hash(target))) throw new IOException("The save changed or its backup could not be verified. No edit was made.");
        return target;
    }

    // Autosaves can rotate while the game is paused. Make recoverable copies of
    // the selected save and all autosaves beside it before any memory write.
    public static SessionBackup CopySessionVerified(string source, string directory)
    {
        string selected = Path.GetFullPath(source);
        string saveDirectory = Path.GetDirectoryName(selected) ?? throw new InvalidDataException("The save folder is unavailable.");
        static string[] Autosaves(string folder)
        {
            var paths = Directory.EnumerateFiles(folder, "autosave*.save", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFullPath).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Take(101).ToArray();
            if (paths.Length > 100) throw new InvalidDataException("There are too many autosave files to back up safely.");
            return paths;
        }
        string[] before = Autosaves(saveDirectory);
        string session = Path.Combine(directory, $"session-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}");
        string selectedCopy = CopyVerified(selected, session);
        var copies = new List<BackupFile>();
        foreach (string original in before) copies.Add(new(original, CopyVerified(original, session)));
        if (!before.SequenceEqual(Autosaves(saveDirectory), StringComparer.OrdinalIgnoreCase) ||
            !Hash(selected).AsSpan().SequenceEqual(Hash(selectedCopy)) ||
            copies.Any(x => !Hash(x.Original).AsSpan().SequenceEqual(Hash(x.Copy))))
            throw new IOException("The selected save or autosaves changed during backup. No edit was made; try again.");
        var result = new SessionBackup(session, selectedCopy, copies.AsReadOnly());
        File.WriteAllText(Path.Combine(session, "backup-manifest.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        return result;
    }
}
