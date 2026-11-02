using System.Security.Cryptography;
using System.Text.Json;

namespace OstrivStudio.Core;

public static class SaveBackup
{
    public sealed record BackupFile(string Original, string Copy);
    public sealed record SessionBackup(string Directory, string SelectedCopy, IReadOnlyList<BackupFile> Autosaves);
    public sealed record SaveDirectoryBackup(string Directory, string SourceDirectory, IReadOnlyList<BackupFile> Files);
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

    // A loaded save cannot be inferred reliably from game memory. Capture all
    // top-level saves instead; the manifest retains the exact original names.
    // This cannot capture unsaved state in the running game's memory.
    public static SaveDirectoryBackup CopyAllSavesVerified(string saveDirectory, string directory)
    {
        string sourceDir = Path.GetFullPath(saveDirectory);
        string backupRoot = Path.GetFullPath(directory);
        if (!Directory.Exists(sourceDir)) throw new DirectoryNotFoundException("The Ostriv save folder was not found.");
        string sourcePrefix = Path.TrimEndingDirectorySeparator(sourceDir) + Path.DirectorySeparatorChar;
        string backupPrefix = Path.TrimEndingDirectorySeparator(backupRoot) + Path.DirectorySeparatorChar;
        if (string.Equals(sourcePrefix, backupPrefix, StringComparison.OrdinalIgnoreCase) ||
            backupPrefix.StartsWith(sourcePrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The backup folder must be outside the Ostriv save folder.");

        static string[] SaveFiles(string folder)
        {
            var files = Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly)
                .Where(path => string.Equals(Path.GetExtension(path), ".save", StringComparison.OrdinalIgnoreCase))
                .Select(Path.GetFullPath).OrderBy(path => path, StringComparer.OrdinalIgnoreCase).Take(1001).ToArray();
            if (files.Length == 0) throw new InvalidDataException("No Ostriv save files were found. Save the game first to create a recovery point.");
            if (files.Length > 1000) throw new InvalidDataException("There are too many save files to back up safely.");
            return files;
        }

        string[] originals = SaveFiles(sourceDir);
        var hashes = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (string original in originals)
        {
            var info = new FileInfo(original);
            if (!info.Exists || info.Length <= 0 || info.Length > 128 * 1024 * 1024)
                throw new InvalidDataException("A save has an unsupported size. No game edit was made.");
            hashes.Add(original, Hash(original));
        }

        string session = Path.Combine(backupRoot, $"session-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}");
        var copies = new List<BackupFile>(originals.Length);
        foreach (string original in originals) copies.Add(new(original, CopyVerified(original, session)));

        void VerifyAll()
        {
            if (!originals.SequenceEqual(SaveFiles(sourceDir), StringComparer.OrdinalIgnoreCase) ||
                copies.Any(file => !hashes[file.Original].AsSpan().SequenceEqual(Hash(file.Original)) ||
                    !hashes[file.Original].AsSpan().SequenceEqual(Hash(file.Copy))))
                throw new IOException("The save files changed during backup. No game edit was made; try again.");
        }
        VerifyAll();
        var result = new SaveDirectoryBackup(session, sourceDir, copies.AsReadOnly());
        byte[] manifest = JsonSerializer.SerializeToUtf8Bytes(result, new JsonSerializerOptions { WriteIndented = true });
        string stagedManifest = Path.Combine(session, "backup-manifest.partial");
        using (var output = new FileStream(stagedManifest, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        { output.Write(manifest); output.Flush(true); }
        if (!manifest.AsSpan().SequenceEqual(File.ReadAllBytes(stagedManifest))) throw new IOException("The backup manifest could not be verified. No game edit was made.");
        VerifyAll();
        File.Move(stagedManifest, Path.Combine(session, "backup-manifest.json"));
        return result;
    }
}
