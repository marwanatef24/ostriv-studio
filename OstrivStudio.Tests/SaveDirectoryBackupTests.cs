using System.Text.Json;
using OstrivStudio.Core;

internal static class SaveDirectoryBackupTests
{
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
        Console.WriteLine("PASS " + name);
    }
    private static void Throws<T>(Action action, string name) where T : Exception
    {
        try { action(); }
        catch (T) { Console.WriteLine("PASS " + name); return; }
        throw new Exception(name);
    }

    public static void Run(string directory)
    {
        string source = Path.Combine(directory, "automatic-saves");
        string destination = Path.Combine(directory, "automatic-backups");
        Directory.CreateDirectory(source);
        string manual = Path.Combine(source, "my-town.save");
        string other = Path.Combine(source, "second-town.SAVE");
        string automatic = Path.Combine(source, "autosave.save");
        File.WriteAllBytes(manual, [1, 2, 3]);
        File.WriteAllBytes(other, [4, 5, 6]);
        File.WriteAllBytes(automatic, [7, 8, 9]);
        File.WriteAllText(Path.Combine(source, "notes.txt"), "ignored");
        Directory.CreateDirectory(Path.Combine(source, "subfolder"));
        File.WriteAllBytes(Path.Combine(source, "subfolder", "ignored.save"), [10]);

        var first = SaveBackup.CopyAllSavesVerified(source, destination);
        Check(first.Files.Count == 3 && first.Files.All(file => File.ReadAllBytes(file.Original).SequenceEqual(File.ReadAllBytes(file.Copy))) &&
            first.Files.Select(file => Path.GetFileName(file.Original)).ToHashSet(StringComparer.OrdinalIgnoreCase)
                .SetEquals(["my-town.save", "second-town.SAVE", "autosave.save"]),
            "automatic backup preserves all top-level saves and skips unrelated files");
        var manifest = JsonSerializer.Deserialize<SaveBackup.SaveDirectoryBackup>(File.ReadAllBytes(Path.Combine(first.Directory, "backup-manifest.json")));
        Check(manifest is not null && manifest.SourceDirectory == Path.GetFullPath(source) && manifest.Files.Count == 3 &&
            manifest.Files.All(file => File.Exists(file.Original) && File.Exists(file.Copy)),
            "automatic backup manifest maps every original save to its verified copy");
        var second = SaveBackup.CopyAllSavesVerified(source, destination);
        Check(first.Directory != second.Directory && first.Files.All(file => File.Exists(file.Copy)) && File.ReadAllBytes(manual).SequenceEqual(new byte[] { 1, 2, 3 }),
            "automatic backup does not overwrite earlier backups or source saves");
        Throws<InvalidDataException>(() => SaveBackup.CopyAllSavesVerified(source, Path.Combine(source, "backups")),
            "automatic backup cannot place its copies inside the source folder");
        string empty = Path.Combine(directory, "empty-saves"); Directory.CreateDirectory(empty);
        Throws<InvalidDataException>(() => SaveBackup.CopyAllSavesVerified(empty, destination),
            "automatic backup requires a saved recovery point");
        Throws<DirectoryNotFoundException>(() => SaveBackup.CopyAllSavesVerified(Path.Combine(directory, "missing-saves"), destination),
            "automatic backup reports a missing save directory");
        var noSaves = SaveBackup.CopyAllSavesVerified(empty, destination, allowNoSaves: true);
        Check(noSaves.Files.Count == 0 && JsonSerializer.Deserialize<SaveBackup.SaveDirectoryBackup>(
            File.ReadAllBytes(Path.Combine(noSaves.Directory, "backup-manifest.json")))?.Files.Count == 0,
            "a new game can proceed without existing saves and records an empty recovery manifest");
        var missing = SaveBackup.CopyAllSavesVerified(Path.Combine(directory, "missing-saves"), destination, allowNoSaves: true);
        Check(missing.Files.Count == 0 && File.Exists(Path.Combine(missing.Directory, "backup-manifest.json")),
            "a new game can proceed before its save folder exists");
        using (var locked = new FileStream(manual, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Throws<IOException>(() => SaveBackup.CopyAllSavesVerified(source, destination),
                "automatic backup fails without touching locked source saves");
        File.WriteAllBytes(Path.Combine(source, "empty.save"), []);
        Throws<InvalidDataException>(() => SaveBackup.CopyAllSavesVerified(source, destination),
            "automatic backup refuses an invalid or empty save");
    }
}
