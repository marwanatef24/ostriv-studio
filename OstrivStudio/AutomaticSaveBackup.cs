using System.IO;
using OstrivStudio.Core;

namespace OstrivStudio;

// The loaded save cannot be identified reliably. Back up the save directory
// without asking the player to choose a file; unsaved game state is not copied.
internal static class AutomaticSaveBackup
{
    public static Task<SaveBackup.SaveDirectoryBackup> CreateAsync(IProgress<int>? retrying = null, CancellationToken cancellation = default)
    {
        string root = Environment.GetEnvironmentVariable("OSTRIV_STUDIO_DATA_DIR") ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OstrivStudio");
        string saves = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Saved Games", "Ostriv", "save");
        return BackupRetry.RunAsync(
            () => Task.Run(() => SaveBackup.CopyAllSavesVerified(saves, Path.Combine(root, "Backups"), allowNoSaves: true)),
            attempt => retrying?.Report(attempt), Task.Delay, cancellation);
    }
}
