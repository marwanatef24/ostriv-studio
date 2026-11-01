namespace OstrivStudio.Core;

// Derived from the original editor's BUILDING_CONFIG, not a verified current-game catalog.
public static class LegacyBuildingCatalog
{
    private static readonly Dictionary<int, string> Classes = new()
    {
        [1] = "building_granary", [2] = "building_granary_small",
        [3] = "building_warehouse", [4] = "building_camp_storage",
        [5] = "building_stone_storage", [6] = "building_trading_post",
        [7] = "building_trading_dock", [8] = "building_shop",
        [9] = "building_rowhouse_shop", [100] = "building_hay_barrack",
        [101] = "building_well", [102] = "building_stone_well",
        [103] = "building_grain_farm", [104] = "building_stone_mining_camp",
        [105] = "building_forestry", [106] = "building_hay_barrack_big",
        [200] = "building_cowshed", [201] = "building_pigsty",
        [202] = "building_chick_farm", [203] = "building_sheep_farm",
        [204] = "building_stable", [205] = "building_wagon_shed",
        [1000] = "building_finite_deposit_stone", [1001] = "building_finite_deposit_limestone",
        [2000] = "building_cart_shed", [2001] = "building_cart_parking",
        [2002] = "building_cart_parking_single", [4000] = "building_oil_workshop",
        [4001] = "building_brickworks", [4002] = "building_pottery",
        [4003] = "building_ashery", [4004] = "building_glassworks",
        [4005] = "building_soapworks"
    };
    public static string Resolve(int id) => Classes.GetValueOrDefault(id, $"legacy-id:{id}");
    public static IEnumerable<KeyValuePair<int, string>> All => Classes.OrderBy(x => x.Value);
}
