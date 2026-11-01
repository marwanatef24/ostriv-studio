# Original editor trade-deal investigation

REA 6.8.0 inspected the original editor's `DealWindow` managed members without executing it. The artifact SHA-256 is `d8f70c64c93cfca82a93270842b7297f4a386f8a9e5fdc0d524216b1d92867f3`; the selected-type evidence is `ev_7aa51aae533d60274d5c9cdec5ac51f0144393b956c6277b8ee1193b12914ba0`.

The inspected `ReadDeals` calls memory-read methods. The transfer, split and distribute methods call `BUILDING.AddAllowedResource`; reconstructed code indicates they use export deal quantities to set building resource targets. This static evidence is limited to the inspected type and does not establish that writing a deal field is safe in a current game. Direct deal writes remain unsupported pending separate validation.
