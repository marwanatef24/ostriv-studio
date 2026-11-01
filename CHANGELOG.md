# Changelog

## 0.2.6 — original-inspired resource workspace

- Open directly to a single workspace with building search, stock, one-time and saved actions, and building-specific tools together.
- Select all displayed buildings or all displayed instances of the same type; see a stock mix for the selected building and open the resource overview from the toolbar.
- Identify built-in crop rotation patterns at any starting point. Pattern matching checks crop order only, not ploughing or fertility.
- Direct deal editing, additional market goods and other unsupported building controls remain unavailable. See [feature coverage](FEATURE-COVERAGE.md).

## 0.2.5 — clearer bulk controls and shop inspection

- Simplified the building selection controls and explained the difference between adding once and saving a minimum rule.
- View assigned goods and empty slots for selected rowhouse shops on the tested build. The list is read-only and shows stored and reserved amounts from the last scan.
- Added one-time additions and saved minimums for existing buckwheat records in rowhouse shops, up to 100. One addition and an each-building rule were tested in a backed-up settlement; missing records are skipped.
- Strengthened bulk export allocation checks so all selected deals must match one guarded capture before writes begin. The capture is not an atomic snapshot.
- Direct deal editing, creating missing shop records, other retail goods and many building features remain unverified and unavailable. See [feature coverage](FEATURE-COVERAGE.md).

## 0.2.4 — task home and shop rules

- Added a task-based home screen for resources, farms, trading, markets, capacity and saved rules.
- Added a guided setup that creates missing rules for supported rowhouse shop goods without replacing existing rules. Start the saved-rule monitor to apply them.
- Trade deals refresh automatically as the selected city changes, clearing stale selections before allowing further actions. This was checked against a temporary live deal on the supported build.
- Corrected an earlier labeling error: resource 93 is warm clothes, not salt. Salt remains unsupported.
- Direct deal editing, missing shop records and many building features are still unavailable. See [feature coverage](FEATURE-COVERAGE.md).

## 0.2.3 — simpler controls and fertility preview

- Simplified the main building, resource and action flow, hid advanced building filters until needed, and made market shortcuts find rowhouse shops and select the exact existing item.
- Added a reviewed bulk fertility reset for selected grain-farm fields on the supported game build. REA revealed the original editor's three-nutrient reset; current offsets were independently compared with two in-game field tooltips. A three-field reset passed memory readback and two fields showed 100% for each nutrient in the game. The test was not saved and reopened; the original autosaves were restored.
- Direct trade-deal changes, missing market records, other building-specific controls and automatic fertility rules remain unsupported. See [feature coverage](FEATURE-COVERAGE.md).

## 0.2.2 — crop plan generator preview

- Generate a field-length crop rotation from selected crops and an optional ploughing assumption, then review it as a draft and optionally save it as a preset.
- Current crop and length remain unchanged. Wheat, buckwheat and potato can be introduced; other modeled crops are offered only when already in the field and remain subject to existing write guards.
- The planning model assumes full fertility and uses values from the original editor. It does not read actual fertility or guarantee yields. One generated plan was applied and checked on a backed-up test field on the supported game build; it was not saved and reopened in the game.

## 0.2.1 — task workspace preview

- Added workspace navigation and separate capacity, farm, trade and market layouts, including shortcuts to supported shop goods.
- Choose individual compatible export deals for a bulk allocation, rather than always allocating every visible export. The existing review, backup and guarded write checks remain in place.
- This remains an experimental partial editor. The new selection flow has automated and desktop-preview checks, but was not retested against a running game for this release. Missing market goods, direct deal editing and many building features are still unsupported; see [feature coverage](FEATURE-COVERAGE.md).

## 0.2.0 — redesigned experimental preview

- Rebuilt the main workflow: searchable building browser, dedicated inspector, and Resources, Building, Farms, Trading and Markets tabs.
- Made Add once and Keep a minimum explicit choices with contextual previews. Added Select shown and Clear selection for bulk work.
- Rebuilt saved rules around profile settings, search and bulk actions. Updated operational windows and the resource ledger to a consistent light theme.
- Added 25 built-in crop rotation suggestions and named local presets. Suggestions align with the current crop and must pass existing field-length and crop safety checks. Custom presets persist separately from automation profiles and reject stale concurrent saves.
- Added export allocation from visible deals: combine repeated resources, split remaining quantities across selected route-compatible buildings, or assign resources in turn. Preview every target, preserve higher targets and block missing entries.
- Added one-time additions and saved minimums for existing clothes, shoes and warm clothes in rowhouse shops, limited to the observed 100-unit target. Earlier notes incorrectly identified warm clothes as salt. Other retail goods and missing records remain unsupported.
- Corrected capacity guidance: supported saved minimums reapply while the monitor runs.

## 0.1.4 — experimental preview

- Apply a field's future crop plan across compatible fields with different current crops and rotations; each current crop remains unchanged.
- Clarified rule scopes in the saved-rules screen and improved the farm rotation controls.

## 0.1.3 — experimental preview

- Added a building-group filter and clearer category labels in building results.
- Refreshed the building workspace colors and improved contrast between one-time and saved actions.

## 0.1.2 — experimental preview

- Added existing export-target editing for trading docks, including bulk changes across selected posts and docks.
- Added one-time additions and saved rules for existing broadcloth and clothes records in trading docks.
- Filtered deal-building choices by the selected city's route and blocked unknown city mappings.

## 0.1.1 — experimental preview

- Preserved profiles from earlier Codex-hosted previews when first launched as a standalone app.
- Fixed the pause guard to recognize the tested game's 12× speed setting.
- Always verify the simulation is paused before running saved resource rules.

## 0.1.0 — internal experimental preview

- Redesigned the building workspace with searchable stock summaries and separate actions for a one-time addition and a saved stock rule.
- Added guarded one-time additions to existing stored resources in supported buildings.
- Added capacity-rule execution for granaries, warehouses and big hay barracks while the rule monitor is running.
- Added a trading-post action that prepares an existing export target from the remaining quantity of a visible deal.
- Added one-field and multi-field plough settings alongside crop rotation controls.
- Added a Windows installer and update checks with release notes. Profiles remain in the user's local application-data directory.

This preview is restricted to the exact game executable listed in [COMPATIBILITY.md](COMPATIBILITY.md). Direct deal editing, many building/resource combinations and other farm controls are not implemented. Candidate identity and safe behavior cannot be guaranteed; back up saves before use.
