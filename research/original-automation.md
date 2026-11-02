# Original editor automation research

REA 6.8 managed-code inspection of `Ostriv_Resource_Editor.dll` (SHA-256 `d8f70c64c93cfca82a93270842b7297f4a386f8a9e5fdc0d524216b1d92867f3`) provided the following static evidence:

- `ev_b603d6e4ea9cc5b6e5dcdd0751ebe7849b5593290c59139bf00a98652cb5e5ca`: the main window starts process discovery on load. Discovery, process observation, resource monitoring, and building scans run in separate workers. Its resource worker sleeps 100 ms between iterations and runs maintenance every ten iterations.
- `ev_f5f0b0377371ec3ea899b529feff94c23483f0b90218045569daca7985c95d2b`: resource refresh iterates the editor's known building instances. Maintenance has a lock, a global switch, and per-building-type switches. Individual resource maintain controls are associated with a building instance, unlike Ostriv Studio's current type-wide Keep rule.
- `ev_73a16ce78423a5a82dfcb38efdb4d9cf0ed0d2aacfc2988681067c5e15d76b2c`: the original defaults global maintenance and building-type switches to on and presents enable/disable-all controls.
- `ev_5bfe3459c81ea7c7bc80cf1893ee793f220972f803f0a7be1283be01e91b830a`: the original's building manager locks and copies its internal instance dictionary. This does not prove those addresses remain live objects in another game version or session.

These observations explain the original's background behavior. They do not establish safe memory offsets, object lifetimes, or support for later Ostriv builds. Ostriv Studio still uses full verified discovery for each automation cycle; its master switch and write coordinator do not imply instance-level rule support or coordination with other editors or older versions.
