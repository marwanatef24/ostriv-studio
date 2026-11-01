using System.Runtime.InteropServices;
using System.Security.Cryptography;
using OstrivStudio.Core;

internal static class LegacyImportTests
{
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out IntPtr db, int flags, IntPtr vfs);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_exec(IntPtr db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, IntPtr callback, IntPtr arg, IntPtr error);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_close_v2(IntPtr db);
    private static void Check(bool value, string message)
    {
        if (!value) throw new Exception(message);
        Console.WriteLine("PASS " + message);
    }
    private static void CreateFixture(string path)
    {
        if (sqlite3_open_v2(path, out var db, 0x2 | 0x4, IntPtr.Zero) != 0) throw new Exception("Could not create fixture");
        try
        {
            const string sql = """
                PRAGMA user_version=6;
                CREATE TABLE BLD_CAPACITY_TABLE (BLD_ID INTEGER NOT NULL PRIMARY KEY, CAPACITY FLOAT NOT NULL, ENABLED INTEGER NOT NULL);
                CREATE TABLE RES_MAINTAINED_TABLE (RES_ID INTEGER NOT NULL, AMOUNT FLOAT NOT NULL, BLD_TYPE INTEGER NOT NULL, BLD_CNT INTEGER NOT NULL, MAINTAINED INTEGER NOT NULL, MAINTAIN_TYPE INTEGER NOT NULL DEFAULT 0, PRIMARY KEY (RES_ID, BLD_TYPE));
                INSERT INTO RES_MAINTAINED_TABLE VALUES (27, 51, 4, -1, 1, 0), (27, 110, 3, 2, 0, 1), (92, 20, 9999, 1, 1, 0);
                INSERT INTO BLD_CAPACITY_TABLE VALUES (4, 10000, 1);
                """;
            if (sqlite3_exec(db, sql, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero) != 0) throw new Exception("Could not create original preference fixture");
        }
        finally { sqlite3_close_v2(db); }
    }
    public static void Run(string dir)
    {
        string dbPath = Path.Combine(dir, "fixture.userpref"); CreateFixture(dbPath);
        byte[] before = SHA256.HashData(File.ReadAllBytes(dbPath));
        var imported = LegacyRuleImport.Read(dbPath);
        Check(before.SequenceEqual(SHA256.HashData(File.ReadAllBytes(dbPath))) && imported.SourceSha256 == Convert.ToHexString(before), "import reads original database without modifying it");
        Check(imported.Rules.Count == 3 && imported.Capacities.Count == 1 && imported.Rules.Count(x => x.ResourceId == 27) == 2, "import retains two building-specific rules for the same resource and capacity rules");
        var each = imported.Rules.Single(x => x.ResourceId == 27 && x.LegacyBuildingId == 4);
        var across = imported.Rules.Single(x => x.ResourceId == 27 && x.LegacyBuildingId == 3);
        Check(each.Enabled && each.Target == 51 && each.BuildingLimit == -1 && each.Mode == MaintenanceMode.Each && each.BuildingType == "building_camp_storage" &&
              !across.Enabled && across.Target == 110 && across.BuildingLimit == 2 && across.Mode == MaintenanceMode.Across && across.BuildingType == "building_warehouse",
              "import preserves enabled state, target, building, limit and each/across mode");
        Check(imported.Rules.Single(x => x.LegacyBuildingId == 9999).BuildingType == "legacy-id:9999", "unknown original building IDs are retained explicitly");
        Check(imported.Capacities[0].Capacity == 10000 && imported.Capacities[0].Enabled && imported.Capacities[0].LegacyBuildingId == 4, "import preserves capacity target and switch");
        var profile = new ProfileDocument();
        profile.AutomationProfiles["My profile"].MaintenanceRules = imported.Rules.ToList();
        profile.AutomationProfiles["My profile"].CapacityRules = imported.Capacities.ToList();
        string profilePath = Path.Combine(dir, "imported-profiles.json"); ProfileStore.Save(profilePath, profile);
        var reopened = ProfileStore.Load(profilePath).AutomationProfiles["My profile"];
        Check(reopened.MaintenanceRules.Count == 3 && reopened.CapacityRules.Count == 1 && reopened.MaintenanceRules.Single(x => x.Mode == MaintenanceMode.Across).BuildingLimit == 2, "rich rules and capacity settings survive profile restart");
        string validProfile = File.ReadAllText(profilePath);
        profile.AutomationProfiles["My profile"].MaintenanceRules.Add(new() { ResourceId = 27, BuildingType = "BUILDING_WAREHOUSE" });
        try { ProfileStore.Save(profilePath, profile); throw new Exception("Duplicate rule accepted"); }
        catch (InvalidDataException) { }
        Check(File.ReadAllText(profilePath) == validProfile, "duplicate building/resource rule cannot replace valid settings");
        string activePath = Path.Combine(dir, "active.userpref"); File.Copy(dbPath, activePath);
        File.WriteAllText(activePath + "-journal", "synthetic journal");
        try { LegacyRuleImport.Read(activePath); throw new Exception("Active journal accepted"); }
        catch (InvalidDataException) { Console.WriteLine("PASS active original database journal is rejected"); }
        string zeroPath = Path.Combine(dir, "zero.userpref"); File.Copy(dbPath, zeroPath);
        if (sqlite3_open_v2(zeroPath, out var zeroDb, 0x2, IntPtr.Zero) != 0) throw new Exception("Could not open zero-limit fixture");
        try { if (sqlite3_exec(zeroDb, "UPDATE RES_MAINTAINED_TABLE SET BLD_CNT=0 WHERE RES_ID=27 AND BLD_TYPE=4", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero) != 0) throw new Exception("Could not update fixture"); }
        finally { sqlite3_close_v2(zeroDb); }
        Check(LegacyRuleImport.Read(zeroPath).Rules.Single(x => x.LegacyBuildingId == 4).BuildingLimit == 0, "zero-building original rule is preserved");
        string oldV2Path = Path.Combine(dir, "v2.json");
        File.WriteAllText(oldV2Path, """{"Version":2,"SelectedProfile":"Old","Profiles":{"Old":{"27":51}},"AutomationProfiles":{"Old":{"IntervalSeconds":45,"Rules":{"27":{"Enabled":true,"BuildingType":"building_fishery"}}}}}""");
        var v2 = ProfileStore.Load(oldV2Path);
        Check(v2.Version == 3 && v2.AutomationProfiles["Old"].MaintenanceRules is [{ ResourceId: 27, Target: 51m, Enabled: true, BuildingType: "building_fishery", BuildingLimit: -1, Mode: MaintenanceMode.Each }], "version 2 targets and switches migrate into rich rules");
    }
}
