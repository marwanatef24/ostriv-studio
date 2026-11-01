using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Diagnostics;
using Velopack;
using Velopack.Sources;

namespace OstrivStudio;

public static class UpdateService
{
    private const string Repository = "https://github.com/marwanatef24/ostriv-studio";
    private static bool _checking;

    public static async Task CheckAsync(Window owner, bool interactive)
    {
        if (_checking) return;
        _checking = true;
        try
        {
            // Experimental releases are marked as GitHub pre-releases until
            // broader game compatibility has been verified.
            var manager = new UpdateManager(new GithubSource(Repository, null, true));
            if (!manager.IsInstalled)
            {
                if (interactive) MessageBox.Show(owner, "Automatic updates are available after installing Ostriv Studio with its setup file.", "Updates", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var update = await manager.CheckForUpdatesAsync();
            if (update is null)
            {
                if (interactive) MessageBox.Show(owner, "You have the latest available release.", "Updates", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (!owner.IsLoaded) return;
            new UpdateAvailableWindow(manager, update) { Owner = owner }.ShowDialog();
        }
        catch (Exception ex)
        {
            if (interactive && owner.IsLoaded) MessageBox.Show(owner, "Could not check for updates. Your current installation is unchanged.\n\n" + ex.Message, "Updates unavailable", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        finally { _checking = false; }
    }

    private sealed class UpdateAvailableWindow : Window
    {
        private readonly UpdateManager _manager;
        private readonly UpdateInfo _update;
        private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) };
        private readonly Button _install = new() { Content = "Download and install", MinWidth = 160 };
        private bool _busy;
        public UpdateAvailableWindow(UpdateManager manager, UpdateInfo update)
        {
            _manager = manager; _update = update;
            Title = "Ostriv Studio update"; Width = 640; Height = 520; MinWidth = 520; MinHeight = 400;
            WindowStartupLocation = WindowStartupLocation.CenterOwner; Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F2F5F9")); Foreground = new SolidColorBrush(Color.FromRgb(23, 37, 58));
            var root = new DockPanel { Margin = new Thickness(26) };
            var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
            top.Children.Add(new TextBlock { Text = $"Version {update.TargetFullRelease.Version} is available", FontSize = 24, FontWeight = FontWeights.SemiBold });
            top.Children.Add(new TextBlock { Text = "Review what changed. Installing restarts Ostriv Studio; your profiles are stored separately.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 7, 0, 10), Foreground = new SolidColorBrush(Color.FromRgb(86, 102, 125)) });
            var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            footer.Children.Add(new Button { Content = "Later", IsCancel = true }); footer.Children.Add(_install); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
            DockPanel.SetDock(_status, Dock.Bottom); root.Children.Add(_status);
            var notes = new TextBox { Text = string.IsNullOrWhiteSpace(update.TargetFullRelease.NotesMarkdown) ? "No release notes were provided." : update.TargetFullRelease.NotesMarkdown,
                IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, AcceptsReturn = true };
            root.Children.Add(notes); Content = root;
            _install.Click += Install_Click;
            Closing += (_, e) => { if (_busy) e.Cancel = true; };
        }
        private async void Install_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            try
            {
                string? executable = Environment.ProcessPath;
                foreach (var process in Process.GetProcessesByName(Process.GetCurrentProcess().ProcessName))
                {
                    using (process)
                    {
                        if (process.Id != Environment.ProcessId && string.Equals(process.MainModule?.FileName, executable, StringComparison.OrdinalIgnoreCase))
                        {
                            _status.Text = "Close the other installed copy of Ostriv Studio before updating.";
                            return;
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
            { _status.Text = "Could not check for other running copies: " + ex.Message; return; }
            _busy = true; _install.IsEnabled = false; _status.Text = "Downloading update…";
            try
            {
                await _manager.DownloadUpdatesAsync(_update, progress => Dispatcher.BeginInvoke(() => _status.Text = $"Downloading update… {progress}%"));
                _status.Text = "Download verified. Restarting to install…";
                _manager.ApplyUpdatesAndRestart(_update);
            }
            catch (Exception ex)
            { _status.Text = "Update was not installed: " + ex.Message; _busy = false; _install.IsEnabled = true; }
        }
    }
}
