# Feature coverage

This is an experimental, partial implementation informed by the original editor's [published feature description](https://steamcommunity.com/app/773790/discussions/0/3080998989598591168/) and REA inspection. An old-editor feature does not establish a safe layout for another game build. Writes are limited to the exact executable in [COMPATIBILITY.md](COMPATIBILITY.md).

| Area | Current support and limits |
| --- | --- |
| Buildings and resources | Search candidate buildings and stored resources; view a resource breakdown and session-only history. Scans can contain stale or overlapping candidates and do not prove a complete inventory. |
| One-time stock | Increase an existing supported stored resource in selected buildings. Creating missing records through this action and arbitrary reductions are unsupported. |
| Saved rules | Persist named profiles and run supported `Each` or `Across` minimums while the reviewed monitor remains open. Existing camp, granary, warehouse, well, stone mining camp, fishing dock and trading-post records are supported; ashery potash and brickworks brick have narrower support. Some nonperishable missing records can be inserted with additional checks. Other classes and resource combinations are skipped. |
| Capacity | Increase an existing granary, warehouse or big-hay-barrack capacity once, or reapply a saved minimum while the rule monitor is running. The tested hay capacity reset after a game reload. Decreases and other building types are unsupported. |
| Farms | Inspect a grain farm, reorder future crops, replace a future crop with wheat, buckwheat, potato or fallow, and enable or disable ploughing on selected fields. Current crops, field activation, fertility and other controls are unsupported. |
| Trade | Inspect the selected city's visible deals, locate candidate stock, increase existing trading-post export targets and prepare a target from an unchanged export deal. Trading docks, editing deals directly, creating export entries and automatic deal fulfillment are unsupported. |
| Other buildings | Market controls, carts, wagons, money, date and many special building settings are not implemented. |

The original editor's full distribution, threshold and building-selection behavior is not reproduced. Profiles survive editor restarts, but the runner must be explicitly reviewed and started again. See [TESTING.md](TESTING.md) for what has actually been tested.
