# Feature coverage

This is an experimental, partial implementation informed by the original editor's [published feature description](https://steamcommunity.com/app/773790/discussions/0/3080998989598591168/) and REA inspection. An old-editor feature does not establish a safe layout for another game build. Writes are limited to the exact executable in [COMPATIBILITY.md](COMPATIBILITY.md).

| Area | Current support and limits |
| --- | --- |
| Buildings and resources | Search candidate buildings and stored resources; view a resource breakdown and session-only history. Scans can contain stale or overlapping candidates and do not prove a complete inventory. |
| One-time stock | Add an amount, raise to a minimum, or set an exact positive amount for eligible existing stock across selected buildings. Exact replacement requires one stored record and no other record of that resource; missing, split, reserved and zero targets are unsupported. |
| Saved rules | Persist named profiles and run supported `Each` or `Across` minimums in the background. Existing camp, granary, warehouse, well, stone mining camp, fishing dock and trading-post records are supported; see the narrower production-building list below. Trading-dock broadcloth and clothes and a few rowhouse-shop goods are also supported. Some nonperishable missing records can be inserted with additional checks. Other classes and combinations are skipped. |
| Capacity | Increase an existing granary, warehouse or big-hay-barrack capacity once, or reapply a saved minimum while the rule monitor is running. The tested hay capacity reset after a game reload. Decreases and other building types are unsupported. |
| Farms | Inspect a grain farm, reorder future crops, replace a future crop with wheat, buckwheat, potato or fallow, copy compatible future crops, change selected fields' ploughing, and restore all three nutrients to 100% for selected fields. Save named future-crop presets, review 25 built-in suggestions, or generate a draft from selected crops. The generator can introduce wheat, buckwheat and potato; other crops can be selected only when already present and remain subject to the write guard. The generator assumes full starting fertility and ploughing choices using values from the original editor; it does not use observed nutrient values or guarantee game behavior. Suggestions must match the field length and current crop. Current crops, field activation, automatic fertility rules and other controls are unsupported. |
| Trade | Inspect visible city deals, locate stock, raise existing post/dock export targets to a minimum, add an amount to each existing target, or select exports and split their combined remaining quantities across route-compatible buildings or assign resources in turn. Missing entries block the batch. The add-to-target mode has simulated-memory tests but no live write test. Direct deal mutation, creating export entries and automatic fulfillment are unsupported. |
| Markets | Inspect assigned goods and empty slots in selected rowhouse shops. Existing buckwheat, clothes, shoes, warm clothes and glassware in rowhouse shops, and existing flour in shops, can be added once or maintained up to the 100-unit target. Salt, missing retail records, other retail goods and automatic stocking of every assigned good remain unsupported. |
| Other buildings | Carts, wagons, money, date and many special building settings are not implemented. |

The original editor's full distribution, threshold and building-selection behavior is not reproduced. Enabled profiles start automatically when a compatible game is found. See [TESTING.md](TESTING.md) for what has actually been tested.

Existing stored records are supported for these additional production-building items on the tested game build:

| Building | Items |
| --- | --- |
| Ashery | Ash, bark, firewood, water, lye, potash |
| Boatyard | Wood |
| Brickworks | Brick, charcoal, sand, clay, water |
| Cartwright | Wood, metal parts |
| Forestry | Wood, logs, firewood, bark |
| Glassworks | Firewood, sand, potash, glassware, window glass |
| Limeworks | Lime, water, quick lime |
| Oil workshop | Sunflower, sunflower oil, linseed, hemp seeds, hemp oil, linseed oil |
| Shoemaker | Shoes, leather, threads |
| Smithy | Iron, metal parts, horseshoes, charcoal |
| Thatchery | Thatch, reeds |

These combinations require existing stored records; inserting missing records in these production buildings is not supported. Exact **Set** additionally requires a single stored record without reservations or other records of the same item.
