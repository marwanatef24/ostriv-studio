using System.Text.Json;

namespace OstrivStudio.Core;

public sealed record ResourceDefinition(int Id, string Name, string Category);
public sealed class AutomationRuleSettings
{
    public bool Enabled { get; set; }
    // Empty means every building type. This is a class name, not a transient address.
    public string BuildingType { get; set; } = "";
}
public sealed class AutomationProfileSettings
{
    public int IntervalSeconds { get; set; } = 30;
    // Version 2 fields are retained as migration evidence; version 3 uses MaintenanceRules.
    public Dictionary<int, AutomationRuleSettings> Rules { get; set; } = [];
    public List<MaintenanceRule> MaintenanceRules { get; set; } = [];
    public List<CapacityRule> CapacityRules { get; set; } = [];
}
public enum MaintenanceMode { Each = 0, Across = 1 }
public sealed class MaintenanceRule
{
    public int ResourceId { get; set; }
    public string BuildingType { get; set; } = "";
    public decimal? Target { get; set; }
    public bool Enabled { get; set; }
    public int BuildingLimit { get; set; } = -1;
    public MaintenanceMode Mode { get; set; }
    public int? LegacyBuildingId { get; set; }
}
public sealed class CapacityRule
{
    public int LegacyBuildingId { get; set; }
    public string BuildingType { get; set; } = "";
    public decimal Capacity { get; set; }
    public bool Enabled { get; set; }
}
public sealed class ProfileDocument
{
    public int Version { get; set; } = 3;
    public string SelectedProfile { get; set; } = "My profile";
    public string GamePath { get; set; } = "";
    public Dictionary<string, Dictionary<int, decimal>> Profiles { get; set; } = new(StringComparer.OrdinalIgnoreCase) { ["My profile"] = [] };
    public Dictionary<string, AutomationProfileSettings> AutomationProfiles { get; set; } = new(StringComparer.OrdinalIgnoreCase) { ["My profile"] = new() };
}

public static class ProfileStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    // Copy an older installation's profile only when the destination is absent.
    // Validation happens before the atomic move and the source is never changed.
    public static bool CopyIfMissing(string source, string destination)
    {
        if (string.Equals(Path.GetFullPath(source), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase) ||
            File.Exists(destination) || !File.Exists(source)) return false;
        var sourceInfo = new FileInfo(source);
        if (sourceInfo.Length > 50 * 1024 * 1024) throw new InvalidDataException("The profile file is too large to import.");
        string directory = Path.GetDirectoryName(Path.GetFullPath(destination))!;
        Directory.CreateDirectory(directory);
        string temp = Path.Combine(directory, $".{Guid.NewGuid():N}.profile-import.tmp");
        try
        {
            File.Copy(source, temp, false);
            _ = Load(temp);
            try { File.Move(temp, destination, false); return true; }
            catch (IOException) when (File.Exists(destination)) { return false; }
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public static ProfileDocument Load(string path)
    {
        if (!File.Exists(path)) return new();
        using var stream = File.OpenRead(path);
        var state = JsonSerializer.Deserialize<ProfileDocument>(stream, Options) ?? throw new InvalidDataException("Profile file is empty.");
        if (state.Version is not (1 or 2 or 3) || state.Profiles is null || string.IsNullOrWhiteSpace(state.SelectedProfile))
            throw new InvalidDataException("Profile file format is unsupported or incomplete.");
        int originalVersion = state.Version;
        var profiles = new Dictionary<string, Dictionary<int, decimal>>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in state.Profiles)
        {
            if (string.IsNullOrWhiteSpace(entry.Key) || entry.Value is null || entry.Value.Any(r => r.Key <= 0 || r.Value < 0 || r.Value > 1000000000m))
                throw new InvalidDataException("Profile contains an invalid name, resource or amount.");
            profiles.Add(entry.Key, entry.Value);
        }
        if (!profiles.ContainsKey(state.SelectedProfile)) throw new InvalidDataException("Selected profile does not exist.");
        state.Profiles = profiles;
        if (state.Version == 1)
        {
            // Existing targets remain intact. Migration never enables automation.
            state.AutomationProfiles = new(StringComparer.OrdinalIgnoreCase);
        }
        if (state.AutomationProfiles is null) throw new InvalidDataException("Automation settings are incomplete.");
        var automations = new Dictionary<string, AutomationProfileSettings>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in state.AutomationProfiles)
        {
            if (!profiles.ContainsKey(entry.Key) || entry.Value is null || entry.Value.IntervalSeconds is < 5 or > 86400 || entry.Value.Rules is null ||
                entry.Value.MaintenanceRules is null || entry.Value.CapacityRules is null)
                throw new InvalidDataException("Automation profile is invalid.");
            foreach (var rule in entry.Value.Rules)
            {
                if (rule.Key is <= 0 or > 65535 || rule.Value is null || rule.Value.BuildingType is null || rule.Value.BuildingType.Length > 100 ||
                    rule.Value.BuildingType.Any(char.IsControl)) throw new InvalidDataException("Automation rule is invalid.");
            }
            automations.Add(entry.Key, entry.Value);
        }
        foreach (string name in profiles.Keys) automations.TryAdd(name, new());
        if (originalVersion < 3)
        {
            foreach (var entry in profiles)
            {
                var automation = automations[entry.Key];
                foreach (int id in entry.Value.Keys.Union(automation.Rules.Keys).Order())
                {
                    automation.Rules.TryGetValue(id, out var oldRule);
                    automation.MaintenanceRules.Add(new MaintenanceRule {
                        ResourceId = id,
                        Target = entry.Value.TryGetValue(id, out decimal target) ? target : null,
                        Enabled = originalVersion == 2 && oldRule?.Enabled == true,
                        BuildingType = oldRule?.BuildingType ?? ""
                    });
                }
            }
        }
        foreach (var automation in automations.Values)
        {
            if (automation.MaintenanceRules.Count > 10000 || automation.CapacityRules.Count > 10000)
                throw new InvalidDataException("The profile contains too many rules.");
            var keys = new HashSet<(int, string)>();
            foreach (var rule in automation.MaintenanceRules)
            {
                if (rule is null || rule.ResourceId is <= 0 or > 65535 || rule.BuildingType is null ||
                    rule.BuildingType.Length > 100 || rule.BuildingType.Any(char.IsControl) ||
                    rule.Target is < 0 or > 1000000000m || rule.BuildingLimit is < -1 or > 100000 ||
                    !Enum.IsDefined(rule.Mode) || rule.LegacyBuildingId is <= 0 ||
                    !keys.Add((rule.ResourceId, rule.BuildingType.Trim().ToUpperInvariant())))
                    throw new InvalidDataException("A maintenance rule is invalid or duplicates another resource and building type.");
            }
            var capacityKeys = new HashSet<int>();
            foreach (var rule in automation.CapacityRules)
            {
                if (rule is null || rule.LegacyBuildingId <= 0 || rule.BuildingType is null ||
                    rule.BuildingType.Length > 100 || rule.BuildingType.Any(char.IsControl) ||
                    rule.Capacity is < 0 or > 1000000000m || !capacityKeys.Add(rule.LegacyBuildingId))
                    throw new InvalidDataException("A capacity rule is invalid or duplicated.");
            }
        }
        state.AutomationProfiles = automations;
        state.Version = 3;
        return state;
    }

    public static void Save(string path, ProfileDocument state)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(dir);
        string temp = Path.Combine(dir, $".{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(stream, state, Options); stream.Flush(true); }
            // Validate serialized data before replacing the previous file.
            _ = Load(temp);
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
