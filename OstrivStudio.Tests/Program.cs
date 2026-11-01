using OstrivStudio.Core;
using System.Buffers.Binary;

static void Check(bool condition, string message) { if (!condition) throw new Exception(message); Console.WriteLine("PASS " + message); }
static void Throws<T>(Action action, string message) where T : Exception
{
    try { action(); } catch (T) { Console.WriteLine("PASS " + message); return; }
    throw new Exception(message);
}

Check(Patterns.CountMatches([0xAB, 0x01, 0xAB, 0x02, 0xAB], "AB ?? AB") == 2, "wildcards and overlapping matches");
Check(Patterns.CountMatches([0xAB, 0x01], "AB ?? AB") == 0, "short input is not a match");
Check(Patterns.CountMatches([0x00, 0xAA, 0xBB], "AA BB") == 1, "match at final possible position");
Throws<ArgumentException>(() => Patterns.CountMatches([1, 2], "?? ??"), "unanchored patterns rejected");
Check(Patterns.Known.Length == 20, "all compatibility probes loaded");
var libraryPaths = SteamGameLocator.ParseLibraryPaths("\"libraryfolders\" { \"0\" { \"path\" \"C:\\\\Steam\" } \"1\" { \"path\" \"D:\\\\SteamLibrary\" } }");
Check(libraryPaths.SequenceEqual([@"C:\Steam", @"D:\SteamLibrary"]), "Steam library paths are unescaped and discovered across drives");
FishBulkTests.Run();
CampRuleTests.Run();
MaintenancePreviewTests.Run();
await RuleMonitorTests.Run();
await CampAutomationTests.Run();
ExistingRuleExecutorTests.Run();
CapacityEditorTests.Run();
ManualStockEditorTests.Run();
await CapacityRulePlannerTests.Run();
TradingStockLimitsTests.Run();
RetailAssignmentsTests.Run();
TradeAllocationPlannerTests.Run();
FarmCropEditorTests.Run();
CropPlanGeneratorTests.Run();
TradeDealReaderTests.Run();
LiveRuleTests.Run();
ResourceInsightsTests.Run();
await ExistingRuleAutomationTests.Run();
byte[] resource = new byte[20];
BinaryPrimitives.WriteInt32LittleEndian(resource, 6);
BinaryPrimitives.WriteInt32LittleEndian(resource.AsSpan(4), BitConverter.SingleToInt32Bits(1250.5f));
Check(BuildingDiscovery.DecodeResources(resource, 1) is [{ Id: 6, Amount: 1250.5f }], "decode bounded resource record");
BinaryPrimitives.WriteInt32LittleEndian(resource.AsSpan(4), BitConverter.SingleToInt32Bits(float.NaN));
Check(BuildingDiscovery.DecodeResources(resource, 1) is null, "reject non-finite resource amount");
Check(BuildingDiscovery.DecodeResources(resource.AsSpan(0, 19), 1) is null, "reject incomplete resource record");
var totals = BuildingDiscovery.Summarize([new(0, 27, 248, 0, 0, 0), new(1, 27, 25, 256, 0, 0), new(2, 27, 5, 1, 0, 0), new(3, 27, 2, 42, 0, 0)]).Single();
Check(totals.Stored == 248 && totals.Awaited == 25 && totals.Reserved == 5 && totals.Other == 2, "classify stored, awaited, reserved and unknown records separately");
var one = new BuildingCandidate("building_fishery", 0x1000, 1, 2, 2, [new(0, 27, 12, 0, 0, 0), new(1, 27, 9, 256, 0, 0)], "candidate");
var many = new BuildingCandidate("building_fishery", 0x2000, 1, 2, 2, [new(0, 27, 3, 0, 0, 0), new(1, 27, 4, 0, 0, 0)], "candidate");
var missing = new BuildingCandidate("building_fishery", 0x3000, 1, 2, 1, [new(0, 27, 10, 256, 0, 0)], "candidate");
var bulk = BulkPreview.Create([one, many, missing], 27, 20);
Check(bulk.Rows[0].Stored == 12 && bulk.Rows[0].StoredRecords == 1 && bulk.Rows[1].Stored == 7 && bulk.Rows[1].StoredRecords == 2 && bulk.Rows[2].StoredRecords == 0, "bulk preview separates stored records from awaited and flags missing or multiple records");
Throws<ArgumentException>(() => BulkPreview.Create([one, one], 27, 20), "bulk preview rejects duplicate candidates");
Throws<ArgumentOutOfRangeException>(() => BulkPreview.Create([one], 27, float.NaN), "bulk preview rejects non-finite targets");
Throws<ArgumentOutOfRangeException>(() => BulkPreview.Create([one], 0, 1), "bulk preview rejects invalid resource IDs");
byte[] image = new byte[2048];
System.Text.Encoding.ASCII.GetBytes(".?AVbuilding_fishery@ent@@\0").CopyTo(image, 0x110);
BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x200), 1);
BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x20c), 0x100);
BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x214), 0x200);
BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(0x300), 0x140000200UL);
var tables = BuildingDiscovery.FindVtables(image, 0x140000000UL);
Check(tables.TryGetValue(0x308, out var className) && className == "building_fishery", "find primary class table from structured RTTI");
BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(0x380), 0x140000200UL);
Check(BuildingDiscovery.FindVtables(image, 0x140000000UL).Count == 0, "ambiguous class tables are rejected");
byte[] uiImage = new byte[2048];
System.Text.Encoding.ASCII.GetBytes(".?AVuiDealList@@\0").CopyTo(uiImage, 0x110);
BinaryPrimitives.WriteUInt32LittleEndian(uiImage.AsSpan(0x200), 1);
BinaryPrimitives.WriteUInt32LittleEndian(uiImage.AsSpan(0x20c), 0x100);
BinaryPrimitives.WriteUInt32LittleEndian(uiImage.AsSpan(0x214), 0x200);
BinaryPrimitives.WriteUInt64LittleEndian(uiImage.AsSpan(0x300), 0x140000200UL);
Check(BuildingDiscovery.FindExactVtable(uiImage, 0x140000000UL, "uiDealList") == 0x308, "find the exact trade-list class table from structured RTTI");
BinaryPrimitives.WriteUInt64LittleEndian(uiImage.AsSpan(0x380), 0x140000200UL);
Check(BuildingDiscovery.FindExactVtable(uiImage, 0x140000000UL, "uiDealList") is null, "ambiguous trade-list class tables are rejected");

if (args.Length != 1) throw new Exception("Expected an isolated test directory path.");
var dir = Path.GetFullPath(args[0]); Directory.CreateDirectory(dir);
var path = Path.Combine(dir, "profiles.json");
Check(!File.Exists(path), "isolated test profile does not already exist");
var saveSource = Path.Combine(dir, "source.save");
File.WriteAllBytes(saveSource, [0, 1, 2, 3, 4]);
var savedCopy = SaveBackup.CopyVerified(saveSource, Path.Combine(dir, "backups"));
Check(File.ReadAllBytes(saveSource).SequenceEqual(File.ReadAllBytes(savedCopy)), "verified backup preserves all source bytes");
Throws<InvalidDataException>(() => SaveBackup.CopyVerified(Path.Combine(dir, "not-a-save.txt"), Path.Combine(dir, "backups")), "backup rejects non-save files");
var testSaveDir = Path.Combine(dir, "game-saves"); Directory.CreateDirectory(testSaveDir);
var loadedSave = Path.Combine(testSaveDir, "selected.save");
var firstAutosave = Path.Combine(testSaveDir, "autosave.save");
var previousAutosave = Path.Combine(testSaveDir, "autosave_previous2.save");
File.WriteAllBytes(loadedSave, [9, 8, 7]);
File.WriteAllBytes(firstAutosave, [4, 5, 6]);
File.WriteAllBytes(previousAutosave, [1, 3, 5]);
File.WriteAllText(Path.Combine(testSaveDir, "autosave_notes.txt"), "not a save");
var sessionBackup = SaveBackup.CopySessionVerified(loadedSave, Path.Combine(dir, "session-backups"));
Check(File.ReadAllBytes(sessionBackup.SelectedCopy).SequenceEqual(File.ReadAllBytes(loadedSave)) && sessionBackup.Autosaves.Count == 2 &&
    sessionBackup.Autosaves.All(x => File.ReadAllBytes(x.Copy).SequenceEqual(File.ReadAllBytes(x.Original))),
    "session backup preserves the selected save and each autosave without touching source files");
var manifestText = File.ReadAllText(Path.Combine(sessionBackup.Directory, "backup-manifest.json"));
Check(sessionBackup.Autosaves.All(x => manifestText.Contains(Path.GetFileName(x.Original))) && manifestText.Contains(Path.GetFileName(sessionBackup.SelectedCopy)),
    "session backup manifest identifies the original and backup files for recovery");
Throws<InvalidDataException>(() => SaveBackup.CopySessionVerified(Path.Combine(testSaveDir, "autosave_notes.txt"), Path.Combine(dir, "session-backups")),
    "session backup rejects a non-save selection");
var document = new ProfileDocument { GamePath = @"D:\Games\Ostriv\ostriv.exe" };
document.Profiles["My profile"][6] = 123.5m;
ProfileStore.Save(path, document);
var read = ProfileStore.Load(path);
Check(read.GamePath == document.GamePath && read.Profiles["My profile"][6] == 123.5m, "profile persists target and path");
var migratedCopy = Path.Combine(dir, "copied-profile", "profiles.json");
Check(ProfileStore.CopyIfMissing(path, migratedCopy) && ProfileStore.Load(migratedCopy).Profiles["My profile"][6] == 123.5m,
    "first-run profile copy preserves saved rules");
var existingCopy = File.ReadAllBytes(migratedCopy);
Check(!ProfileStore.CopyIfMissing(path, migratedCopy) && File.ReadAllBytes(migratedCopy).SequenceEqual(existingCopy),
    "profile migration never replaces an existing destination");
var invalidSource = Path.Combine(dir, "invalid-profile.json"); File.WriteAllText(invalidSource, "not json");
var invalidDestination = Path.Combine(dir, "invalid-copy", "profiles.json");
Throws<System.Text.Json.JsonException>(() => ProfileStore.CopyIfMissing(invalidSource, invalidDestination), "invalid profile migration is rejected");
Check(!File.Exists(invalidDestination), "failed profile migration leaves no destination");
read.AutomationProfiles["My profile"].IntervalSeconds = 45;
read.AutomationProfiles["My profile"].Rules[6] = new AutomationRuleSettings { Enabled = true, BuildingType = "building_fishery" };
read.AutomationProfiles["My profile"].Rules[5000] = new AutomationRuleSettings { Enabled = false, BuildingType = "future_type" };
read.Profiles.Add("Other", new() { [27] = 50m });
read.AutomationProfiles.Add("Other", new() { IntervalSeconds = 90, Rules = new() { [27] = new() { Enabled = true } } });
ProfileStore.Save(path, read);
var ruleReload = ProfileStore.Load(path);
Check(ruleReload.AutomationProfiles["My profile"].IntervalSeconds == 45 && ruleReload.AutomationProfiles["My profile"].Rules[6].Enabled && ruleReload.AutomationProfiles["My profile"].Rules[6].BuildingType == "building_fishery" && ruleReload.AutomationProfiles["My profile"].Rules.ContainsKey(5000) && ruleReload.AutomationProfiles["Other"].IntervalSeconds == 90, "automation rules, scope and per-profile intervals persist");
var legacyPath = Path.Combine(dir, "legacy.json");
File.WriteAllText(legacyPath, "{\"Version\":1,\"SelectedProfile\":\"Old\",\"GamePath\":\"\",\"Profiles\":{\"Old\":{\"27\":42}}}");
var migrated = ProfileStore.Load(legacyPath);
Check(migrated.Version == 3 && migrated.Profiles["Old"][27] == 42m && migrated.AutomationProfiles["Old"].MaintenanceRules is [{ ResourceId: 27, Target: 42m, Enabled: false }], "legacy targets migrate without enabling automation");
ProfileStore.Save(legacyPath, migrated);
Check(ProfileStore.Load(legacyPath).Profiles["Old"][27] == 42m, "migrated profile survives save and reload");
read.Profiles["My profile"][6] = 0m;
ProfileStore.Save(path, read);
Check(ProfileStore.Load(path).Profiles["My profile"][6] == 0m, "zero target survives replacement");
var before = File.ReadAllText(path);
read.Profiles["My profile"][6] = -1m;
Throws<InvalidDataException>(() => ProfileStore.Save(path, read), "invalid targets rejected before replacement");
Check(File.ReadAllText(path) == before, "failed save preserves previous profile");
read.Profiles["My profile"][6] = 0m;
read.AutomationProfiles["My profile"].IntervalSeconds = 2;
Throws<InvalidDataException>(() => ProfileStore.Save(path, read), "invalid automation interval rejected");
Check(File.ReadAllText(path) == before, "invalid automation settings preserve the prior file");
LegacyImportTests.Run(dir);
CropPresetStoreTests.Run(dir);
Throws<InvalidDataException>(() => GameInspector.Inspect(path), "inspector rejects non-game filename");
