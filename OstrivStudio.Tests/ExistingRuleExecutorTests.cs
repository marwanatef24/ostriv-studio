using System.Buffers.Binary;
using System.Text.Json;
using OstrivStudio.Core;

internal static class ExistingRuleExecutorTests
{
    private sealed class Memory : FishBulkEditor.IMemory
    {
        public readonly Dictionary<ulong, byte> Bytes = [];
        public int Writes, FailOnWrite;
        public void Put(ulong address, byte[] bytes) { for (int i = 0; i < bytes.Length; i++) Bytes[address + (uint)i] = bytes[i]; }
        public byte[] Read(ulong address, int size) => Enumerable.Range(0, size).Select(i => Bytes[address + (uint)i]).ToArray();
        public void Write(ulong address, byte[] bytes) { Writes++; Put(address, bytes); if (Writes == FailOnWrite) throw new IOException("simulated uncertain write"); }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); Console.WriteLine("PASS " + message); }
    private static (ProfileDocument Profile, DiscoveryResult Scan, Memory Memory) Fixture()
    {
        var camp = new BuildingCandidate("building_camp_storage", 0x1000, 0x9000, 0x5000, 3,
            [new(0, 33, 1000, 0, 0, 0), new(1, 85, 1900, 0, 0, 0), new(2, 133, 1850, 0, 0, 0)], "candidate");
        var well = new BuildingCandidate("building_well", 0x2000, 0x9100, 0x6000, 1, [new(0, 14, 1544, 0, 0, 0)], "candidate");
        var granary = new BuildingCandidate("building_granary", 0x3000, 0x9200, 0x7000, 1, [new(0, 73, 147, 0, 0, 0)], "candidate");
        var scan = new DiscoveryResult(1, DateTime.UtcNow, "test", FishBulkEditor.SupportedHash, 408, 416, [camp, well, granary], 1, true, 0, "");
        var profile = new ProfileDocument();
        profile.AutomationProfiles[profile.SelectedProfile].MaintenanceRules = [
            new() { ResourceId=85, BuildingType="building_camp_storage", Target=2000, Enabled=true },
            new() { ResourceId=133, BuildingType="building_camp_storage", Target=2000, Enabled=true },
            new() { ResourceId=14, BuildingType="building_well", Target=5000, Enabled=true },
            new() { ResourceId=87, BuildingType="building_granary", Target=2000, Enabled=true }
        ];
        var memory = new Memory();
        foreach (var b in scan.Buildings)
        {
            var header = new byte[428]; BinaryPrimitives.WriteUInt64LittleEndian(header, b.Vtable);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(408), b.ResourceCount);
            BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(416), b.ResourcePointer);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(424), 64); memory.Put(b.Address, header);
            foreach (var r in b.Resources)
            {
                var record = new byte[20]; BinaryPrimitives.WriteInt32LittleEndian(record, r.Id);
                BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(4), BitConverter.SingleToInt32Bits(r.Amount));
                BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(8), r.Flags);
                BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(12), r.Details);
                BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(16), r.Extra);
                memory.Put(b.ResourcePointer + (ulong)r.Index * 20, record);
            }
            var empty = new byte[20]; BinaryPrimitives.WriteUInt32LittleEndian(empty.AsSpan(12), 0xbf800000);
            memory.Put(b.ResourcePointer + (ulong)b.ResourceCount * 20, empty);
        }
        return (profile, scan, memory);
    }
    public static void Run()
    {
        var catalog = JsonSerializer.Deserialize<List<ResourceDefinition>>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "resources.json")), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Check(catalog.Single(r => r.Id == 7).Name == "BUCKWHEAT" && catalog.Single(r => r.Id == 26).Name == "SALT" && catalog.Single(r => r.Id == 46).Name == "CLOTHES" &&
            catalog.Single(r => r.Id == 47).Name == "SHOES" && catalog.Single(r => r.Id == 93).Name == "WARM_CLOTHES",
            "retail resource IDs match the shipped resource catalog");
        var existingShopRules = new[] { new MaintenanceRule { BuildingType = " BUILDING_ROWHOUSE_SHOP ", ResourceId = 46, Target = 37, Enabled = false },
            new MaintenanceRule { BuildingType = "building_warehouse", ResourceId = 47, Target = 500, Enabled = true } };
        Check(MarketRulePlanner.MissingShopRules(existingShopRules).SequenceEqual([7, 47, 93]) &&
            existingShopRules[0].Target == 37 && !existingShopRules[0].Enabled && existingShopRules[1].Target == 500,
            "shop rule setup includes buckwheat and leaves existing settings intact");
        var allShopRules = new[] { 7, 46, 47, 93 }.Select(id => new MaintenanceRule { BuildingType = "building_rowhouse_shop", ResourceId = id });
        Check(MarketRulePlanner.MissingShopRules(allShopRules).Length == 0,
            "shop rule setup recognizes all four supported goods");
        Check(new[] {7,46,47,93}.All(id => ExistingRuleExecutor.SupportsExisting("building_rowhouse_shop", id)) &&
            !ExistingRuleExecutor.SupportsExisting("building_rowhouse_shop", 26) && !ExistingRuleExecutor.SupportsInsertion("building_rowhouse_shop", 46),
            "retail edits allow existing buckwheat, clothes, shoes and warm clothes; salt is blocked");
        Check(ExistingRuleExecutor.SupportsExisting("building_trading_dock", 92) && ExistingRuleExecutor.SupportsExisting("building_trading_dock", 46) &&
            !ExistingRuleExecutor.SupportsExisting("building_trading_dock", 87) && !ExistingRuleExecutor.SupportsInsertion("building_trading_dock", 92),
            "trading-dock stock support is limited to corroborated existing records");
        var (profile, scan, memory) = Fixture();
        var retail = scan.Buildings[2] with { ClassName = "building_rowhouse_shop", Resources = [new(0,46,50,0,0xbf800000,0)] };
        var retailProfile = new ProfileDocument();
        retailProfile.AutomationProfiles[retailProfile.SelectedProfile].MaintenanceRules = [new() { BuildingType=retail.ClassName, ResourceId=46, Target=101, Enabled=true }];
        var retailScan = scan with { Buildings = [retail] };
        Check(ExistingRuleExecutor.Prepare(retailProfile, retailScan).Entries.Count == 0,
            "retail minimums above 100 are blocked before writing");
        retailProfile.AutomationProfiles[retailProfile.SelectedProfile].MaintenanceRules[0].Target = 100;
        Check(ExistingRuleExecutor.Prepare(retailProfile, retailScan).Entries is [{Target:100,Insert:false}], "existing retail stock can be replenished to 100");
        try { ManualStockEditor.Prepare(retailScan, [retail], 46, 51); throw new Exception("Retail overfill accepted"); }
        catch (InvalidOperationException) { Console.WriteLine("PASS one-time retail additions cannot overfill the shelf target"); }
        var buckwheatShop = retail with { Resources = [new(0,7,74,0,0xbf800000,0)] };
        var buckwheatScan = scan with { Buildings = [buckwheatShop] };
        Check(ManualStockEditor.Prepare(buckwheatScan, [buckwheatShop], 7, 1).Entries is [{ Target: 75, Insert: false }],
            "existing buckwheat can be added to a rowhouse shelf");
        retailProfile.AutomationProfiles[retailProfile.SelectedProfile].MaintenanceRules[0].ResourceId = 7;
        Check(ExistingRuleExecutor.Prepare(retailProfile, retailScan).Entries.Count == 0 && !ExistingRuleExecutor.SupportsInsertion(retail.ClassName, 7),
            "a missing buckwheat shelf is never inserted by the saved rule");
        var plan = ExistingRuleExecutor.Prepare(profile, scan);
        Check(plan.Entries.Count == 4 && plan.Entries.Single(e => e.Record.Id == 87).Insert, "batch plans missing barley insertion alongside existing resources");
        Check(ExistingRuleExecutor.ApplyChecked(profile, scan, memory) == 4 && memory.Writes == 5 &&
            BitConverter.ToSingle(memory.Read(0x5018, 4)) == 2000 && BitConverter.ToSingle(memory.Read(0x502c, 4)) == 2000 &&
            BitConverter.ToSingle(memory.Read(0x6004, 4)) == 5000 && memory.Read(0x3000 + 408, 4).SequenceEqual(BitConverter.GetBytes(2)) &&
            BitConverter.ToInt32(memory.Read(0x7014, 4)) == 87 && BitConverter.ToSingle(memory.Read(0x7018, 4)) == 2000 &&
            BinaryPrimitives.ReadUInt32LittleEndian(memory.Read(0x7020, 4)) == 0xbf800000,
            "batch applies targets and publishes the initialized barley record after its contents");
        (profile, scan, memory) = Fixture(); memory.Put(0x3000 + 424, BitConverter.GetBytes(1));
        var result = ExistingRuleExecutor.ApplyCheckedDetailed(profile, scan, memory);
        Check(result.Applied == 3 && memory.Writes == 3 && result.Plan!.Skipped.Any(s => s.Contains("no verified spare")) &&
            BitConverter.ToInt32(memory.Read(0x3000 + 408, 4)) == 1,
            "an array without spare capacity skips that insertion and continues independent rules");
        (profile, scan, memory) = Fixture(); memory.Put(0x7014, BitConverter.GetBytes(87));
        result = ExistingRuleExecutor.ApplyCheckedDetailed(profile, scan, memory);
        Check(result.Applied == 4 && memory.Writes == 5 && result.Plan!.Skipped.Count == 0 &&
            BitConverter.ToInt32(memory.Read(0x3000 + 408, 4)) == 2 && BitConverter.ToSingle(memory.Read(0x7018, 4)) == 2000,
            "a stable allocated slot is fully replaced before its count is published");
        (profile, scan, memory) = Fixture();
        profile.AutomationProfiles[profile.SelectedProfile].MaintenanceRules.Add(new() { ResourceId=6, BuildingType="building_camp_storage", Target=1, Enabled=true });
        profile.AutomationProfiles[profile.SelectedProfile].MaintenanceRules.Add(new() { ResourceId=18, BuildingType="building_camp_storage", Target=1, Enabled=true });
        result = ExistingRuleExecutor.ApplyCheckedDetailed(profile, scan, memory);
        Check(result.Applied == 5 && result.Plan!.Skipped.Any(s => s.Contains("only one new record")) &&
            BitConverter.ToInt32(memory.Read(0x1000 + 408, 4)) == 4 && BitConverter.ToInt32(memory.Read(0x503c, 4)) == 6,
            "multiple missing resources in one building are queued one per cycle");
        (profile, scan, memory) = Fixture(); memory.Put(0x6004, BitConverter.GetBytes(1543f));
        try { ExistingRuleExecutor.ApplyChecked(profile, scan, memory); throw new Exception("Stale target accepted"); }
        catch (InvalidOperationException) when (memory.Writes == 0) { Console.WriteLine("PASS a stale building stops the whole existing-record batch before writes"); }
        (profile, scan, memory) = Fixture(); memory.FailOnWrite = 2;
        try { ExistingRuleExecutor.ApplyChecked(profile, scan, memory); throw new Exception("Partial failure ignored"); }
        catch (InvalidOperationException ex) when (memory.Writes == 2 && ex.Message.Contains("partially")) { Console.WriteLine("PASS an uncertain existing-record batch requires backup recovery"); }
        (profile, scan, memory) = Fixture(); memory.FailOnWrite = 5;
        try { ExistingRuleExecutor.ApplyChecked(profile, scan, memory); throw new Exception("Uncertain insertion ignored"); }
        catch (InvalidOperationException ex) when (memory.Writes == 5 && ex.Message.Contains("partially")) { Console.WriteLine("PASS uncertain insertion count requires backup recovery"); }
        (profile, scan, memory) = Fixture();
        try { ExistingRuleExecutor.Prepare(profile, scan with { GameSha256 = "unknown" }); throw new Exception("Unknown game build accepted"); }
        catch (InvalidOperationException) { Console.WriteLine("PASS existing-record batches reject an unknown game build"); }
        (profile, scan, memory) = Fixture();
        profile.AutomationProfiles[profile.SelectedProfile].MaintenanceRules.Add(new() { ResourceId=33, BuildingType="building_camp_storage", Target=2000, Enabled=true });
        plan = ExistingRuleExecutor.Prepare(profile, scan);
        Check(plan.Entries[0].Building.ClassName == "building_camp_storage" && plan.Entries[0].Target >= 1000 && plan.Entries[0].Record.Amount > 0 &&
            plan.Entries.Count(e => e.Building.ClassName == "building_camp_storage") == 3,
            "an existing nonempty camp item is replenished first when every anchor has a pending rule");
        (profile, scan, memory) = Fixture();
        var camp = scan.Buildings[0] with { Resources = [new(0,33,999,0,0,0), new(1,85,100,0,0,0), new(2,133,50,0,0,0)] };
        scan = scan with { Buildings = [camp, scan.Buildings[1], scan.Buildings[2]] };
        memory.Put(0x5004, BitConverter.GetBytes(999f)); memory.Put(0x5018, BitConverter.GetBytes(100f)); memory.Put(0x502c, BitConverter.GetBytes(50f));
        var campRules = profile.AutomationProfiles[profile.SelectedProfile].MaintenanceRules;
        campRules[0].Target = 500; campRules[1].Target = 500;
        campRules.Add(new() { ResourceId=33, BuildingType="building_camp_storage", Target=1000, Enabled=true });
        plan = ExistingRuleExecutor.Prepare(profile, scan);
        Check(plan.Entries[0].Record.Id == 33 && ExistingRuleExecutor.ApplyChecked(profile, scan, memory) == 5 &&
            BitConverter.ToSingle(memory.Read(0x5004, 4)) == 1000,
            "a nonempty below-threshold anchor is restored to its saved target before other camp edits");
        (profile, scan, memory) = Fixture();
        camp = scan.Buildings[0] with { Resources = [new(0,33,0,0,0,0), new(1,85,100,0,0,0), new(2,133,50,0,0,0)] };
        scan = scan with { Buildings = [camp, scan.Buildings[1], scan.Buildings[2]] };
        campRules = profile.AutomationProfiles[profile.SelectedProfile].MaintenanceRules;
        campRules[0].Target = 500; campRules[1].Target = 500;
        plan = ExistingRuleExecutor.Prepare(profile, scan);
        Check(plan.Entries.All(e => e.Building.ClassName != "building_camp_storage") && plan.Skipped.Count(s => s.Contains("anchor")) == 2,
            "each camp rule is skipped when no safe anchor or replenishment rule exists");
        (profile, scan, memory) = Fixture();
        var duplicate = scan.Buildings[1] with { Address = 0x4000, ResourcePointer = 0x8000 };
        memory.Put(duplicate.Address, memory.Read(0x2000, 428));
        memory.Put(duplicate.Address + 416, BitConverter.GetBytes(duplicate.ResourcePointer));
        memory.Put(duplicate.ResourcePointer, memory.Read(0x6000, 20));
        plan = ExistingRuleExecutor.Prepare(profile, scan with { Buildings = scan.Buildings.Append(duplicate).ToArray() });
        Check(plan.Entries.Count(e => e.Building.ClassName == "building_well") == 2 &&
            ExistingRuleExecutor.ApplyChecked(profile, scan with { Buildings = scan.Buildings.Append(duplicate).ToArray() }, memory) == 5 &&
            BitConverter.ToSingle(memory.Read(0x6004, 4)) == 5000 && BitConverter.ToSingle(memory.Read(0x8004, 4)) == 5000,
            "one saved rule updates both reviewed building candidates");
        (profile, scan, memory) = Fixture();
        camp = scan.Buildings[0] with { ResourceCount = 4, Resources = scan.Buildings[0].Resources.Append(new ResourceObservation(3, 85, 100, 0, 0, 0)).ToArray() };
        scan = scan with { Buildings = [camp, scan.Buildings[1], scan.Buildings[2]] };
        memory.Put(camp.Address + 408, BitConverter.GetBytes(4));
        var extra = new byte[20]; BinaryPrimitives.WriteInt32LittleEndian(extra, 85); BitConverter.GetBytes(100f).CopyTo(extra, 4); memory.Put(0x503c, extra);
        profile.AutomationProfiles[profile.SelectedProfile].MaintenanceRules[0].Target = 2100;
        plan = ExistingRuleExecutor.Prepare(profile, scan);
        Check(plan.Entries.Single(e => e.Record.Id == 85).Target == 2000 &&
            ExistingRuleExecutor.ApplyChecked(profile, scan, memory) == 4 &&
            BitConverter.ToSingle(memory.Read(0x5018, 4)) == 2000 && BitConverter.ToSingle(memory.Read(0x5040, 4)) == 100,
            "split stored records are totaled and only their first record is increased by the shortfall");
        (profile, scan, memory) = Fixture();
        duplicate = scan.Buildings[1] with { Address = 0x4000, ResourcePointer = 0x8000 };
        profile.AutomationProfiles[profile.SelectedProfile].MaintenanceRules[2].BuildingLimit = 1;
        plan = ExistingRuleExecutor.Prepare(profile, scan with { Buildings = scan.Buildings.Append(duplicate).ToArray() });
        Check(plan.Entries.All(e => e.Building.ClassName != "building_well") && plan.Skipped.Any(s => s.Contains("limited-building")),
            "a building limit does not silently pick an unverified candidate");
        (profile, scan, memory) = Fixture();
        duplicate = scan.Buildings[1] with { Address = 0x4000, ResourcePointer = 0x8000 };
        memory.Put(duplicate.Address, memory.Read(0x2000, 428));
        memory.Put(duplicate.Address + 416, BitConverter.GetBytes(duplicate.ResourcePointer));
        memory.Put(duplicate.ResourcePointer, memory.Read(0x6000, 20));
        scan = scan with { Buildings = [scan.Buildings[1], duplicate] };
        profile.AutomationProfiles[profile.SelectedProfile].MaintenanceRules = [new() { Enabled = true, ResourceId = 14, BuildingType = "building_well", Target = 3091, Mode = MaintenanceMode.Across }];
        plan = ExistingRuleExecutor.Prepare(profile, scan);
        Check(plan.Entries.Count == 2 && ExistingRuleExecutor.ApplyChecked(profile, scan, memory) == 2 &&
            BitConverter.ToSingle(memory.Read(0x6004, 4)) == 1546 && BitConverter.ToSingle(memory.Read(0x8004, 4)) == 1545,
            "across rule shares a three-unit shortfall across two existing buildings without overshoot");
        profile.AutomationProfiles[profile.SelectedProfile].MaintenanceRules[0].Target = 3000;
        Check(ExistingRuleExecutor.Prepare(profile, scan).Entries.Count == 0, "across rule does not reduce stock already above its combined target");
        profile.AutomationProfiles[profile.SelectedProfile].MaintenanceRules[0].Target = 3091;
        var missing = duplicate with { Resources = [new(0, 15, 0, 0, 0, 0)] };
        plan = ExistingRuleExecutor.Prepare(profile, scan with { Buildings = [scan.Buildings[0], missing] });
        Check(plan.Entries.Count == 0 && plan.Skipped.Any(s => s.Contains("existing stored record")),
            "across rule skips the entire rule when any candidate lacks an existing record");
        var fractional = duplicate with { Resources = [new(0, 14, 1544.5f, 0, 0, 0)] };
        plan = ExistingRuleExecutor.Prepare(profile, scan with { Buildings = [scan.Buildings[0], fractional] });
        Check(plan.Entries.Count == 0 && plan.Skipped.Any(s => s.Contains("whole-unit")),
            "across rule skips fractional records instead of making an imprecise write");
        (profile, scan, memory) = Fixture();
        var post = scan.Buildings[1] with { ClassName = "building_trading_post", Resources = [new(0, 12, 1544, 0, 0, 0)] };
        memory.Put(post.ResourcePointer, BitConverter.GetBytes(12));
        scan = scan with { Buildings = [post] };
        profile.AutomationProfiles[profile.SelectedProfile].MaintenanceRules = [new() { Enabled = true, ResourceId = 12, BuildingType = "building_trading_post", Target = 1545 }];
        Check(ExistingRuleExecutor.ApplyChecked(profile, scan, memory) == 1 && BitConverter.ToSingle(memory.Read(post.ResourcePointer + 4, 4)) == 1545,
            "a trading-post rule increases an existing reviewed resource record");
        (profile, scan, memory) = Fixture();
        var ashery = scan.Buildings[1] with { ClassName = "building_ashery", Resources = [new(0, 109, 1544, 0, 0, 0)] };
        memory.Put(ashery.ResourcePointer, BitConverter.GetBytes(109)); scan = scan with { Buildings = [ashery] };
        profile.AutomationProfiles[profile.SelectedProfile].MaintenanceRules = [
            new() { Enabled = true, ResourceId = 109, BuildingType = "building_ashery", Target = 1545 },
            new() { Enabled = true, ResourceId = 58, BuildingType = "building_ashery", Target = 100 }
        ];
        plan = ExistingRuleExecutor.Prepare(profile, scan);
        Check(plan.Entries.Count == 1 && plan.Skipped.Count == 1 && ExistingRuleExecutor.ApplyChecked(profile, scan, memory) == 1 &&
            BitConverter.ToSingle(memory.Read(ashery.ResourcePointer + 4, 4)) == 1545,
            "ashery automation only permits the observed existing potash record");
        (profile, scan, memory) = Fixture();
        var brickworks = scan.Buildings[1] with { ClassName = "building_brickworks", Resources = [new(0, 32, 1544, 0, 0, 0)] };
        memory.Put(brickworks.ResourcePointer, BitConverter.GetBytes(32)); scan = scan with { Buildings = [brickworks] };
        profile.AutomationProfiles[profile.SelectedProfile].MaintenanceRules = [new() { Enabled = true, ResourceId = 32, BuildingType = "building_brickworks", Target = 1545 }];
        Check(ExistingRuleExecutor.ApplyChecked(profile, scan, memory) == 1 && BitConverter.ToSingle(memory.Read(brickworks.ResourcePointer + 4, 4)) == 1545,
            "brickworks automation increases an existing brick record");
    }
}
