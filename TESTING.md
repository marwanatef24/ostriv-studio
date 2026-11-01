# Testing and evidence

The test suite exercises simulated memory, profile persistence, bulk planning, capacity rules, farm settings, trade-list validation, stale snapshots and partial-write failures. It does not prove that arbitrary game objects or a new executable are safe.

On the executable identified in [COMPATIBILITY.md](COMPATIBILITY.md), supervised tests in a disposable settlement observed:

- A generated wheat, buckwheat and potato draft was applied to one six-season field. The guarded writer and an independent fresh scan read back the generated order, and Ostriv displayed the matching crop icons. The other two fields were unchanged. The test did not save and reopen that change; pre-test autosaves were restored and compared with Steam's cache after the session. Generated preset saving and reopening were separately exercised in an isolated planner.
- One brick added to an existing brickworks record and shown in Ostriv.
- A big hay barrack capacity rule increased 7,000 to 7,001; a second cycle made no additional change. The game reset that capacity on reload, and a newly started monitor reapplied it.
- Ploughing changed for two grain-farm fields together and remained changed after the test save was reopened.
- A future-crop plan applied to three compatible fields in a backed-up settlement; memory readback passed for all three, and Ostriv showed the sequence on two inspected fields. This test did not save and reopen the edited settlement.
- An unchanged, visible flour export deal prepared an existing trading-post target, which Ostriv displayed after its panel was reopened. This adjusts a target rather than editing the deal.
- In another backed-up settlement, a trading dock's broadcloth export target increased from 3,000 to 3,001, one-time stock increased from 3,000 to 3,001, and a saved-rule test increased it to 3,002 with no repeat write on the next cycle. Ostriv displayed each result.
- The pause guard stopped and resumed a running game at 12× with the requested speed preserved.
- In the redesigned 0.2.0 workflow, a named crop preset was saved, the farm planner closed and reopened, and loading it restored a deliberately changed draft. This did not write crops to the game.
- The new export allocation preview rejected a missing flour export entry without writing. After that entry was added through Ostriv, the workflow backed up the loaded test save and five autosaves, increased its target from 0 to 8,319, and passed readback. Ostriv displayed 8,319 after reopening the post panel. Multi-building allocation, rounding and uncertain-write failures were tested in simulated memory; this live allocation used one post.
- Existing clothes, shoes and warm clothes were maintained at 100 in four records across two rowhouse shops. A fresh scan verified all four, and the next rule plan required no further writes. The game continued advancing and remained responsive. Missing retail records were skipped. An earlier version of this description incorrectly called resource 93 salt; it is warm clothes.

The 0.2.0 checks used an isolated copy of the 17-rule profile. The original profile's hash remained unchanged. All 39 pre-test saves were restored and hash-verified, including five autosaves; Steam reported successful upload and its cached hashes matched the restored autosaves. Test autosaves were retained separately for recovery. The release test suite passed 220 checks, including preset persistence, stale writers, retail limits and allocation guards.

For 0.2.4, the task home and shop-rule setup were exercised in a separate copy of the 17-rule profile; the setup added three rules and preserved the original 17. In a backed-up settlement, a temporary flour export appeared in the deal window, disappeared automatically when a city with no deals was selected, and reappeared without an automatic selection when switching back. No editor write was made during that deal test. All 40 pre-test saves were hash-verified after restoring autosaves, and the five restored autosave hashes matched Steam's local cache. The original profile remained unchanged. The automated console suite passed 238 checks.

The game remained responsive during these checks. Long-term play, every building and resource, a different game executable, other dock stock resources, and crash-free behavior are not established by them. Keep independent saves and verify changes in Ostriv.
