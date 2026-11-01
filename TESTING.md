# Testing and evidence

The test suite exercises simulated memory, profile persistence, bulk planning, capacity rules, farm settings, trade-list validation, stale snapshots and partial-write failures. It does not prove that arbitrary game objects or a new executable are safe.

On the executable identified in [COMPATIBILITY.md](COMPATIBILITY.md), supervised tests in a disposable settlement observed:

- One brick added to an existing brickworks record and shown in Ostriv.
- A big hay barrack capacity rule increased 7,000 to 7,001; a second cycle made no additional change. The game reset that capacity on reload, and a newly started monitor reapplied it.
- Ploughing changed for two grain-farm fields together and remained changed after the test save was reopened.
- An unchanged, visible flour export deal prepared an existing trading-post target, which Ostriv displayed after its panel was reopened. This is not direct deal editing or dock support.
- The pause guard stopped and resumed a running game at 12× with the requested speed preserved.

The game remained responsive during these checks. Long-term play, every building and resource, a different game executable, direct trading-dock edits, and crash-free behavior are not established by them. Keep independent saves and verify changes in Ostriv.
