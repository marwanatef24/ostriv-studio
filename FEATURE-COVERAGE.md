# Feature coverage

This is an experimental, partial implementation informed by the original editor's [published feature description](https://steamcommunity.com/app/773790/discussions/0/3080998989598591168/) and REA inspection. An old-editor feature does not establish a safe layout for another game build. Writes are limited to the exact executable in [COMPATIBILITY.md](COMPATIBILITY.md).

| Area | Current support and limits |
| --- | --- |
| Buildings and resources | Search candidate buildings and stored resources; view a resource breakdown and session-only history. Scans can contain stale or overlapping candidates and do not prove a complete inventory. |
| One-time stock | Increase an existing supported stored resource in selected buildings. Creating missing records through this action and arbitrary reductions are unsupported. |
| Saved rules | Persist named profiles and run supported `Each` or `Across` minimums while the reviewed monitor remains open. Existing camp, granary, warehouse, well, stone mining camp, fishing dock and trading-post records are supported; trading-dock broadcloth and clothes, ashery potash and brickworks brick have narrower support. Some nonperishable missing records can be inserted with additional checks. Other classes and resource combinations are skipped. |
| Capacity | Increase an existing granary, warehouse or big-hay-barrack capacity once, or reapply a saved minimum while the rule monitor is running. The tested hay capacity reset after a game reload. Decreases and other building types are unsupported. |
| Farms | Inspect a grain farm, reorder future crops, replace a future crop with wheat, buckwheat, potato or fallow, copy compatible future crops, and change selected fields' ploughing. Save named future-crop presets locally or review 25 built-in suggestions. Suggestions must match the field length and current crop; introductions outside the tested replacement set are blocked. Current crops, field activation, fertility and other controls are unsupported. |
| Trade | Inspect visible city deals, locate stock, increase existing post/dock export targets, or select exports and split their combined remaining quantities across route-compatible buildings or assign resources in turn. Higher targets remain unchanged; missing entries block the batch. Direct deal mutation, creating export entries and automatic fulfillment are unsupported. |
| Markets | Add stock once or maintain a saved minimum for existing clothes, shoes and salt in rowhouse shops, up to the observed 100-unit target. Missing retail records, other retail goods, market stalls and automatic discovery of assigned goods remain unsupported. |
| Other buildings | Carts, wagons, money, date and many special building settings are not implemented. |

The original editor's full distribution, threshold and building-selection behavior is not reproduced. Profiles survive editor restarts, but the runner must be explicitly reviewed and started again. See [TESTING.md](TESTING.md) for what has actually been tested.
