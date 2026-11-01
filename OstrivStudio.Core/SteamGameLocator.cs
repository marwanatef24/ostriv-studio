using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace OstrivStudio.Core;

public static partial class SteamGameLocator
{
    [GeneratedRegex("\"path\"\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LibraryPathPattern();

    public static IReadOnlyList<string> ParseLibraryPaths(string text) => LibraryPathPattern().Matches(text)
        .Select(m => m.Groups[1].Value.Replace("\\\\", "\\", StringComparison.Ordinal)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    public static string? Find()
    {
        foreach (var process in Process.GetProcessesByName("ostriv"))
        {
            using (process)
            {
                try { string? path = process.MainModule?.FileName; if (path is not null && File.Exists(path)) return path; }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
            }
        }
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            string? steam = key?.GetValue("SteamPath") as string;
            if (string.IsNullOrWhiteSpace(steam)) return null;
            string libraries = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
            var paths = File.Exists(libraries) ? ParseLibraryPaths(File.ReadAllText(libraries)).Prepend(steam) : [steam];
            foreach (string library in paths)
            {
                string candidate = Path.Combine(library, "steamapps", "common", "Ostriv", "ostriv.exe");
                if (File.Exists(candidate)) return candidate;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException) { }
        return null;
    }
}
