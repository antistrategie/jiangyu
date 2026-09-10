# Standing portrait textures

Status: verified with Unity 6000.0.72f1 bundle compilation and MENACE runtime inspection on 2026-09-10.

## Contract

Texture additions referenced by `SpeakerTemplate.StandLookLeftImage`,
`StandLookRightImage`, or `StandLookRightInactiveImage` use the standing
portrait policy. Selection follows KDL field usage through
`PortraitTexturePolicy`, including fully qualified template type names.
Replacement textures are excluded.

The Unity build retains the texture's encoded pixels and sets its
serialised `m_IsReadable` flag to false. The player can sample the texture
on the GPU without retaining a CPU-readable pixel copy. Resolution,
compression, mipmaps, trilinear filtering and the `-0.5` mip bias are
independent of this flag. A portrait reused elsewhere is also non-readable
there, so CPU pixel access requires a separate readable copy.

The texture bake policy participates in the input hash. Changing it
invalidates the generated texture cache even when the source image and
development version are unchanged.

## Baked storage verification

- All 54 WOMENACE standing portraits retain 10,726,912 encoded bytes per
  generated asset with `m_IsReadable: 0`.
- Runtime inspection finds all 54 as non-readable DXT5 textures at
  2192 by 3668, with 12 mip levels and active mipmap limit zero.
- Their reported CPU pixel memory drops from 552.4 MiB to zero. The
  texture counter remains 7.0 GB. The per-object profiler value measures
  the readable storage here, not graphics memory or total process use.
- At the same startup checkpoint, process resident and peak resident
  memory fall from 3.4 GB to 2.9 GB. Unity allocated memory falls from
  2.3 GB to 1.8 GB, and reserved memory from 2.5 GB to 1.9 GB.
- Both runs use the same headless gamescope wrapper, loader, mods and
  settings. Only the 18 portrait bundles differ. Template patches finish
  at 13.109 s and 13.232 s respectively, so this measures a RAM reduction
  without a local loading-time improvement. Headless splash input delays
  are excluded from the comparison.
- Loading an existing campaign and opening the squad viewer displays
  `klukai__stand_look_right` at the same layout size and appearance in
  before/after compositor captures, with no unreadable-texture errors.

These local measurements do not establish the loading-time improvement
on an 8 GB Windows laptop or distinguish its disk and paging costs.

Unity documents the CPU copy and GPU operations in
[Texture.isReadable](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/Texture-isReadable.html).

## Deferred assignment contract

`deferStandingPortraits` is an explicit manifest opt-in, defaulting to
false. The loader defers direct, top-level `set` operations assigning
bundled additions to the three speaker fields above. It validates the
writable field and indexed asset before queuing the assignment. Texture
replacements and template clone sources remain eager. A clone source
must hold its assigned fields before a child copies them.

The pending assignment is keyed by the speaker's native pointer and
field. A later deferred assignment replaces it, and a successful eager
assignment or clear discards it. A runtime field change also takes
precedence over the queued assignment. Resolution consumes the pending
entry before applying the normal conversion, setter and readback path.
`LazyBundleAssets` loads and retains each texture once, including assets
shared by multiple speakers.

These native entry points are patched before deferral is enabled. If a
hook cannot be installed, template patching stays eager.

| Entry point | RVA | Field read |
| --- | --- | --- |
| `BaseUnitLeader.GetStandingImage` | `0x5BD970` | `StandLookRightInactiveImage` when `StrategyState.Get().Roster.IsPermanentlyDead(leader)` is true, otherwise `StandLookRightImage` |
| `ConversationUIScreen.ShowRole` | `0x82CC10` | Role position `Left` reads `StandLookRightImage`, `Right` reads `StandLookLeftImage`, `Auto` assigns neither |
| `EventDialog.ShowRole` | `0x7DC820` | Same mapping as the conversation screen |
| `SelectedUnitPanel.SetActor` | `0x8153E0` | The leader branch calls `GetStandingImage`. The other branch reads the actor speaker's `StandLookRightImage` |

The speaker fields occupy native offsets `0xC8`, `0xD0` and `0xD8` for
left, right and inactive artwork. Their IL2CPP wrapper property getters
are field accessors, so patching those managed getters would not
intercept native field reads. The entry points and direction mapping
above are verified from the current `GameAssembly.dll` bodies, using the
metadata assembly only for signatures and RVAs. `StoryFactionWindow`
reads a separate `StoryFactionTemplate.FactionWindow` texture and is
outside this policy.

Custom UI requests the appropriate field through
`Jiangyu.Game.Ui.Portraits.GetStanding` on the main thread. Raw speaker
fields retain their inherited values until requested. Existing mods
remain eager unless they opt in. The helper also works with eager fields.

## Deferred loading verification

Matched runs use the same loader, WOMENACE code, bundles, save files,
settings and 1600 by 900 headless gamescope wrapper. Only the deployed
manifest's opt-in differs. Both use warm caches.

| Main-menu measurement | Eager | Deferred |
| --- | --- | --- |
| Loaded WOMENACE standing textures | 54 | 0 |
| Process resident memory | 3.168 GiB | 3.160 GiB |
| Discrete GPU driver memory | 8.448 GiB | 7.782 GiB |
| Process appearance to native splash | 19.09 s | 19.29 s |

The driver's `drm-memory-vram` allocation counter falls by 682.2 MiB.
This is distinct from the 552.4 MiB encoded texture payload and from
Unity's texture counter. Local initial loading time and process RAM are
essentially unchanged. These measurements do not predict timings or GPU
allocations on the reported laptop.

Loading an existing campaign still leaves all WOMENACE standing
portraits unloaded. Selecting Klukai in the squad viewer loads only her
right portrait. Selecting OTs-14 loads only her right portrait, and
returning to Klukai reuses the existing texture. Procurement loads the
Sextans and OTs-14 left portraits. Its reward pool reuses those two,
leaving four loaded portraits after these checks. Each has one first-use
load in the runtime log. Compositor captures confirm the artwork appears
correctly, with the same dimensions, DXT5 format, mip levels and active
mipmap limit as the eager assets.

Native conversation, event and inactive-portrait branch selection is
verified by disassembly, without changing a campaign to exercise those
branches. The live checks cover the squad viewer and procurement UI.
Saves and settings remain unchanged, and the game exits after inspection.
