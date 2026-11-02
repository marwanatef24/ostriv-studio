# Original editor stock writes

REA 6.8 inspected the original managed `BUILDING` type (artifact SHA-256 `d8f70c64c93cfca82a93270842b7297f4a386f8a9e5fdc0d524216b1d92867f3`, member evidence `ev_7ada1d030871be05460f5509fb922e64dc0d2f061725a8051c192cdcc1c774d2`). Its `SetResourceAmount` calls methods for the stored total and first and all resource records, and contains memory-write calls. `AddResource` also references the resource lifetime configuration and writes both resource data and the resource count. These are static call relationships, not proof of the control flow or safety of those writes on another game build.

Ostriv Studio's exact-set action is narrower: on the tested build it only changes one existing stored record when no other record for that item is present; it does not write zero or insert an item. A one-unit bark decrease passed live memory readback. Multi-record, reserved and missing-resource cases remain unavailable until their behavior can be independently validated.
