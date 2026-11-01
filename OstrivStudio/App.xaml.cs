using System.Windows;
using System.IO;
using OstrivStudio.Core;
using Velopack;
namespace OstrivStudio;
public partial class App : Application
{
    [STAThread]
    private static void Main(string[] args)
    {
        VelopackApp.Build().SetAutoApplyOnStartup(false).Run();
        // Earlier previews launched by the packaged Codex desktop app can have
        // Windows-redirected local data. Preserve that profile on a first launch
        // outside the package, without replacing an existing standalone profile.
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OSTRIV_STUDIO_DATA_DIR")))
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string oldProfile = Path.Combine(local, "Packages", "OpenAI.Codex_2p2nqsd0c76g0", "LocalCache", "Local", "OstrivStudio", "profiles.json");
            string newProfile = Path.Combine(local, "OstrivStudio", "profiles.json");
            try { ProfileStore.CopyIfMissing(oldProfile, newProfile); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Text.Json.JsonException)
            { MessageBox.Show("Your earlier profile could not be copied. The original file has been preserved.\n\n" + ex.Message, "Profile migration", MessageBoxButton.OK, MessageBoxImage.Warning); }
        }
        var app = new App(); app.InitializeComponent(); app.Run();
    }
}
