# UnitLeaderTemplate.InitialAttributes Byte Layout

Status: **verified** (Jiangyu readback + live in-game confirmation, 2026-04-19)

## Contract

`UnitLeaderTemplate.InitialAttributes` is a 7-byte array. Each offset
corresponds to a `UnitLeaderAttribute` enum value, so the enum order is the
offset table:

| Offset | Attribute    |
|--------|--------------|
| 0      | Agility      |
| 1      | WeaponSkill  |
| 2      | Valour       |
| 3      | Toughness    |
| 4      | Vitality     |
| 5      | Precision    |
| 6      | Positioning  |

Each attribute is one byte. Storage range is 0–255; gameplay range is 0–100
(`UnitLeaderAttributes.MAX_VALUE = 100f`, `DEFAULT_VALUE = 50f`).

The in-game unit-leader display of Agility, WeaponSkill, etc. reads linearly
from this array at campaign init. The growth system snapshots these values
into the save at campaign start, so template edits only take effect on a
**new** campaign.

## Verification

Confirmed by Jiangyu against the live game, 2026-04-19:

1. `jiangyu templates inspect --type UnitLeaderTemplate --name squad_leader.darby`
   reported `InitialAttributes.count = 7` with elements
   `[88, 80, 70, 50, 40, 20, 75]` (Agility through Positioning, respectively).

2. A Jiangyu mod applied a template patch setting
   `UnitLeaderTemplate.squad_leader.darby.InitialAttributes.Agility = 100`
   via the compile-time sugar that rewrites attribute names to indexed byte
   writes.

3. The loader logged the sugar rewrite from `InitialAttributes.Agility` to
   `InitialAttributes[0]`, then applied the byte and verified readback
   matched the written value.

4. On a new campaign, Darby's Agility stat displayed 100; the other six
   attributes displayed their unchanged baselines (80, 70, 50, 40, 20, 75).
   This confirms:
   - the array length is 7 at runtime (not just the declared `byte[]`);
   - offset 0 drives the Agility display (and by implication, the enum order
     is the runtime offset order);
   - the byte-to-stat mapping is linear (byte 100 → displayed 100).

## Reference Sources

Sources imported from MenaceAssetPacker at
`/home/justin/dev/github.com/antistrategie/MenaceAssetPacker`:

- Declaration: `src/Menace.Modkit.App/bin/Debug/net10.0/out2/assets/Scripts/Assembly-CSharp/Menace/Strategy/UnitLeaderTemplate.cs:55-56`
  (`[NamedArray(typeof(UnitLeaderAttribute))] public byte[] InitialAttributes;`)
- Enum: `.../Menace/Strategy/UnitLeaderAttribute.cs`
- Range constants: `.../Menace/Strategy/UnitLeaderAttributes.cs:8-14`
- Schema: `generated/schema.json` — `templates.UnitLeaderTemplate.fields[]`
  (`"offset": "0xC8", "type": "byte[]"`) and `enums.UnitLeaderAttribute`.

## Jiangyu Implementation

Modders patch attributes by their integer offset (`InitialAttributes[0]`
through `InitialAttributes[6]`). There is no `.AttributeName` sugar — the
loader and compiler both expect canonical indexed paths. Use the
offset-to-attribute table above when authoring.

## Scope Limits

- The 7-byte layout and enum-order offset mapping are verified for the
  current MENACE build. If the `UnitLeaderAttribute` enum is extended in a
  future game update, Jiangyu's static offset table must be updated — a new
  attribute at `Last = 7` would not be reachable via the sugar until the
  table is extended.
- The byte value range is declared 0–255 but the game enforces 0–100 via
  `UnitLeaderAttributes.MAX_VALUE`. Writing bytes > 100 has not been tested;
  treat as out-of-range until someone needs that case.
- Other `[NamedArray(typeof(T))]` byte arrays elsewhere in the codebase are
  not covered by this finding — each would need its own enum mapping.
