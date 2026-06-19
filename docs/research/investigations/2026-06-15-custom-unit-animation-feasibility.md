# Custom Unit Animation Feasibility

How a tactical unit binds its model, skeleton, animation controller, and
animation behaviour, and what a mod needs to supply to ship a new unit with
its own rig and animations (custom-authored or imported from another Unity
game). The worked target is a bipedal, non-humanoid mech.

## Method

- Template data: `~/.local/share/jiangyu/cache/template-values.json`
  (`template-index` format v8, assembly hash
  `00fa9d51925565305a4c60780c925a44dcddc9acc566a5c4f041071dd195ee26`, 7898
  instances).
- Asset data: `~/.local/share/jiangyu/cache/asset-index.json` (format v9,
  176394 assets).
- Rig: `jiangyu assets export model` on `el.construct_soldier_t1`
  (`resources.assets:43309`), glTF node and skin inspection.
- Field selection and reference following via `jiangyu_templates_search`,
  `jiangyu_templates_inspect`, and direct cache queries.
- All facts here are structural (serialised data and rig shape). Runtime
  driver behaviour is named but not executed. The one runtime-only contract
  (animator parameter names) is called out as open.

## Binding chain

A unit is a `Menace.Tactical.EntityTemplate` (top-level contract in
`docs/research/verified/entitytemplate-contract.md`). 267 instances span
infantry, enemy, structure, vehicle, walker, and construct categories. Three
serialised fields carry the entire visual and animation binding:

| Concern | Field | Type | Notes |
|---|---|---|---|
| Model, skeleton, Animator | `Prefabs` | `List<GameObject>` | Populated on 267/267 EntityTemplates. The body the player sees. See `2026-05-14-template-prefab-fields.md`. |
| Animator controller | prefab's own `Animator`, or `OverrideAnimatorController` | `RuntimeAnimatorController` | Override is null on most units and set on 17 vanilla units (every `player_squad.*` to `aco.human_soldier`, some enemy pirates to `aco.human_soldier_civilian_child`). |
| Driver behaviour | `AnimatorTemplate` | `Menace.Tactical.ElementAnimatorTemplate` | 31 instances. Behaviour config, not a clip set. |

`ElementAnimatorTemplate` carries no clips and no controller. Inspected
fields (`animator.walker_light`): `SpeedBlendTime`, `StanceDelay`,
`DeathBehaviour` (`Ragdoll`), `HitAnimations`, `Aiming`, `AimSpeed`,
`UseRootMotionAiming`, `HumanIK`, `AngleMapping`, turning curves, IK offsets.
It configures how the driver moves the rig. The named set covers
`animator.human`, `animator.human_ai`, `animator.vehicle*`,
`animator.walker_light`, `animator.walker_medium`, and a `animator.construct_*`
family of eight.

The asset index holds 133 `AnimatorController`, 1192 `AnimationClip`, 77
`Avatar`, and 16 `AnimatorOverrideController` assets. Each non-trivial unit
shape ships its own controller. A custom unit with its own controller is the
native pattern, not an exception.

## Walker is procedural, construct is clip-driven

Walkers drive locomotion partly in code. The state `Handled_In_Walker_Controller`
appears on walker controllers only (7 occurrences, none elsewhere), and a
walker is a composite of `aco.*_walker_chassis_modular`,
`aco.*_walker_turret_*`, and `aco.walker_pilot` bound to the
`ModularVehicle` mount transforms. None of that transfers to a differently
shaped body.

Constructs are fully clip-driven. 258 construct clips cover the full
character vocabulary (`idle`, `idle_bored_*`, `run_start`, `run_stop`,
`turn_inplace_90/180_l/r`, `hit_f/b/l/r`, `vault`, `attack`, `walk_start`,
additive aim layers `rifle_add_in/loop/out`), and no `Handled_In_*` state
exists for them. The construct family is the correct base for a bipedal,
non-humanoid mech: bipedal, generic-rigged, no procedural locomotion to
reproduce.

Construct EntityTemplates are clonable (`docs/research/verified/template-cloning.md`).
`enemy.construct_soldier_tier1` (`resources.assets:125452`) resolves:

- `Prefabs[0]` to `el.construct_soldier_t1` (`43309`)
- `AnimatorTemplate` to `animator.construct_soldier` (`121486`)
- `AnimationSoundTemplate` to `construct_soldier_animation_sounds` (`121473`)
- `MovementType` to `movement_type.construct_soldier_t1` (`126998`)
- `OverrideAnimatorController` null
- `ElementsMin`/`ElementsMax` 3, `Scale` (1,1)
- `AttachedPrefabs[0]` `{ IsLight, AttachmentPointName "evil_eye_socket", Prefab "EvilEyeContainer" }`

## Rig contract

`el.construct_soldier_t1` exports as a 48-joint generic skeleton (generic
avatar, not Humanoid):

```
Root -> Pelvis -> Spine_01 -> Spine_02 -> Neck -> Head
  arms: Clavicle_L/R -> UpperArm -> LowerArm -> Hand (+ fingers on Hand_R)
  legs: Thigh -> Calf -> Ankle -> Foot -> Ball (+ Piston_Leg_L/R)
  armour and cabling: Hip_Box_Alt, Shoulder_Box_Alt, Torso_Box_Alt,
                      Hose_Back, Guts_Low/Mid/Down
  collision: *_ColliderRotator helpers
```

`Piston_Leg`, the `*_Box_Alt` plates, the hoses and cabling, the camera
bones, and the collider-rotators have no Humanoid-avatar equivalent, so a
Humanoid avatar cannot map this rig. It is bipedal but generic.

The deformation skeleton can be whatever a model needs. What the game looks
up by name are the functional transforms:

| Transform | Used by |
|---|---|
| `Hand_L`, `Hand_R` | weapon `Items` grip and attach |
| `muzzle`, `muzzle2` | weapon fire point, muzzle flash, projectile origin |
| `dust01`, `dust02` | footstep dust spawn |
| `Camera_Base`, `Camera_Top` | unit camera framing |
| `evil_eye_socket` | `AttachedPrefabs` (optional, clearable in a clone) |

A custom prefab needs these named transforms placed sensibly. This is the
same class of functional-transform remap that `HumanoidPrefabMirror` already
performs for soldier additions (`Footprints`, `Ragdoll`, footstep-dust
containers).

## Sound, movement, and squad-size contracts

`AnimationSoundTemplate.SoundTriggers` is keyed by animation-event names the
clips fire (`idle_var_01`, `idle_var_02`, `idle_var_03`, `walk_hydraulic`,
left and right footstep and hydraulic events) mapping to
`{ bankId, itemId }`. Missing events produce silence, never a fault. A clone
may retune the trigger keys to a custom clip's events.

`MovementType` (`movement_type.construct_soldier_t1`) holds locomotion speeds
to match against clip speeds. Its internals are not in the value cache and
need a separate inspect.

A construct soldier spawns as 3 elements (`ElementsMin`/`Max` 3). A single
large mech sets both to 1, as vehicles do (`MaxElements` 1).

## Build path

The path reuses two verified primitives end to end. The only new authoring is
a prefab and a controller.

1. Author the mech prefab in the `unity/` project: generic-rigged model with
   its own `Animator`, controller, clips, and the functional transforms
   above. Build to a bundle via the addition-prefab batchmode path.
2. `templateClones`: clone `enemy.construct_soldier_tier1` to a new id.
3. `templatePatches` on the clone:
   - `set "Prefabs" index=0 asset="<mech>"` (addition prefab)
   - `set "ElementsMin" 1`, `set "ElementsMax" 1`
   - `Properties` (hitpoints, armour, vision), `Items` (weapons),
     `Title`, `Badge`, `Scale`
   - keep `AnimatorTemplate` as `animator.construct_soldier`, or clone and
     tune it
   - `clear "AttachedPrefabs"` if not carrying the `evil_eye_socket` extra

`asset=` on `Prefabs` is compile-time validated. `FileSystemAssetAdditionsCatalog`
maps the `GameObject` element type to the prefab category and indexes
`unity/Assets/Prefabs/**/*.prefab` plus `assets/additions/prefabs/`, so the
reference resolves before the bundle build.

The controller may be baked onto the prefab's `Animator`, leaving
`OverrideAnimatorController` null. The model, clips, and controller may be
imported from another Unity game or authored from scratch. The wiring is
identical either way. The provenance of the animation is not a contract.

## Animator parameter contract

The construct driver sets animator parameters by name (`SetFloat`, `SetBool`,
`SetTrigger`). A shipped controller missing one of those names takes the call
as a silent no-op, leaving the unit frozen in its default state with no error,
so a custom controller must expose the same parameters.

Recovered offline from `aco.construct_soldier_t1` (`resources.assets:9422`)
via UnityPy. The built controller strips the editor `m_AnimatorParameters`
array, so names come from the controller's own string table (`m_TOS`),
filtered against state, layer, transition, and clip names. Types are inferred
by naming convention and confirm at authoring time (Unity rejects a mismatch).

Driver-set parameters (34 total):

- Locomotion: `Speed` (float), `LocomotionAngle` (float), `Rotation` (float),
  `Acceleration_Sign` (float), `Is_Slow_Speed` (bool), `Movement_Initialized`
  (bool)
- Aim and fire: `Aiming` (bool), `Aiming_Weapon_1`, `Aim_In_Picker` (int),
  `Shoot_Single` (trigger), `Is_Shooting_01_Active` (bool)
- Hit reactions: `Hit` (trigger), `HitX`/`HitZ`/`HitX_02`/`HitZ_02` (float),
  `Is_Hit_Reaction_01..04` (bool)
- Idle and flavour: `Is_Idle_Active` (bool), `Bored` (trigger),
  `BoredAnimation`/`Pick_Idle_Base`/`Pick_Mod_Anim`/`AnimationPicker` (int),
  `IdleCycleOffset` (float)
- Other: `Vault` (trigger), `VaultPicker` (int), `Overloaded` (bool),
  `IsSquadLeader` (bool)

`GravityWeight`, `Blend`, and `Crv_Left_Foot_Front` are blend-tree or clip
curve parameters, not driver inputs.

State graph: one base locomotion layer (idle-stance-buffer, bored, vault,
turn-in-place, walk and run start-loop-stop, an aim sub-machine, an overload
sub-machine) plus additive layers for hit reactions (four), shooting (two),
and light/overload. The minimal subset to get a unit idling and locomoting is
`Speed` and `Movement_Initialized`, with `Aiming` for the aim pose.

Extraction note: a repeatable Jiangyu controller-parameter dump (reading the
runtime controller and `m_TOS` via the vendored AssetRipper, exposed as an
inspector) is worth building if this becomes a per-family or per-game-update
contract check. The one-off UnityPy read covers the single unit here.

## Validation

Slice 1 confirmed in-game 2026-06-15 (dev loader `0.6.0`, tactical mission).
A template-only mod (`ConstructSlice`) cloned `enemy.construct_soldier_tier1`
to `construct_slice.test_construct` with `ElementsMin`/`ElementsMax` set to 1.
`Units.Spawn("construct_slice.test_construct", Player, [4,4])` over the bridge
returned a live actor. The scene showed
`/Actors/el.construct_soldier_t1(Clone)/Soldier_Mesh_LOD0..2`, 48 bones rooted
at `Root` (matching the offline export), playing its idle. A cloned
`EntityTemplate` registers, spawns, resolves `Prefabs` into a rigged model,
and animates from the inherited `AnimatorTemplate` with no controller work.
Unit shape is irrelevant to the mechanism: the construct soldier stands in for
any body in the bipedal, generic-rig, clip-driven family a custom unit
belongs to.

Slice 1 reused the vanilla prefab and its baked controller, so two custom
pieces stay unproven:

- a modder-supplied addition prefab in `Prefabs`. The humanoid leader
  additions already serve modder prefabs as unit visuals, so this is largely
  covered. Confirm once against a construct-family entity.
- a custom `AnimatorController` authored to the construct parameter contract.

## Custom actor prefab requires Footprints and Ragdoll

Confirmed in-game 2026-06-15. A test mod (`construct-controller-test`) clones
`enemy.construct_soldier_tier1` and points `Prefabs` at a modder-built addition
prefab carrying a hand-authored `AnimatorController` (params `Speed`,
`Movement_Initialized`, `Aiming`, Idle and Walk states, placeholder spin
clip). The authoring pipeline works end to end: the custom controller and
clips build, bundle, and deploy via `jiangyu compile`.

The spawn fails. `Units.Spawn` throws `NullReferenceException` in
`Menace.Tactical.ElementAnimator..ctor(Element, Animator,
ElementAnimatorTemplate)`, both with a bare primitive and with the full
exported construct rig (skeleton, skinned mesh, generic avatar).

Root cause: the vanilla `el.construct_soldier_t1` prefab root carries two
MENACE script components a modder Unity project cannot author,
`Menace.Tactical.Footprints` and `Menace.Tactical.Ragdoll`, plus a
`BoxCollider` and `Rigidbody` on `Pelvis`. `ElementAnimator` construction
(death behaviour is `Ragdoll`) dereferences the ragdoll setup and null-refs
when it is absent or unpopulated (`Ragdoll.GetCenterRigidbody()` returns null
without `m_Parts`).

Jiangyu already mirrors exactly these two components at load time.
`HumanoidPrefabMirror` copies `Ragdoll` (template, `m_SkeletalRoot`, and
`m_Parts` rigidbodies remapped by bone name) and `Footprints` (`m_Foots`
transforms remapped by relative path) from a reference prefab onto an addition
carrying a `__jiangyu_ref:<name>` sentinel child. The remap is bone-name based
and rig-agnostic, so it works for any addition whose bones match the
reference, including a construct or mech built on the construct rig.

The gap: `HumanoidMirrorScheduler` originally enqueued the mirror only for
prefabs whose `Animator.avatar.isHuman` was true. Constructs, walkers, and mechs
use generic avatars and were skipped, so they never received `Footprints` or
`Ragdoll`.

## Mirror generalised, and the full requirement chain

The mirror gate was generalised: `HumanoidMirrorScheduler` now enqueues any
addition carrying a reference sentinel (`HumanoidPrefabMirror.HasReferenceSentinel`)
rather than only humanoid-avatar ones. Confirmed in-game 2026-06-15, the
construct addition was picked up and logged `component-mirrored`. The gate
change is correct and necessary (uncommitted working-tree change in
`Jiangyu.Loader`).

It is not sufficient. The retry surfaced the complete set of requirements a
custom actor prefab must meet, beyond carrying the controller:

1. **Footprints and Ragdoll components.** Supplied by the runtime mirror
   (`HumanoidPrefabMirror`), now reachable for generic rigs.
2. **Per-bone Rigidbody, Collider, CharacterJoint.** `BakeHumanoid` copies
   these from a reference soldier prefab onto the baked rig at bake time (three
   passes: rigidbody, colliders, joints, with `connectedBody` remapped). The
   runtime mirror only remaps `Ragdoll.m_Parts` onto rigidbodies that already
   exist on the addition's bones, it does not create them. A prefab built
   straight from a glTF (this test) has no rigidbodies, so `m_Parts` cannot
   populate, `Ragdoll.GetCenterRigidbody()` stays null, and
   `ElementAnimator..ctor` null-refs. Runtime component-adding on bundle
   prefabs is limited, so this copy belongs at bake time as it does for
   soldiers.
3. **No reference-name collision.** The exported construct glTF names its root
   node `el.construct_soldier_t1`, the same name as the reference prefab, so
   `FindReferencePrefab` matched the addition's own node (which has no Ragdoll)
   instead of the vanilla. The bake must rename internal nodes, or the lookup
   must prefer a non-addition source.

## Proven end to end

Confirmed in-game 2026-06-15. The test prefab was rebuilt from the extracted
vanilla construct (`jiangyu unity import-prefab el.construct_soldier_t1`, which
carries the skeleton, skinned meshes, 14 colliders, 13 character joints,
rigidbodies, and the Footprints/Ragdoll components), with its root renamed off
the reference name, a `__jiangyu_ref:el.construct_soldier_t1` sentinel added,
and a hand-authored AnimatorController swapped onto its Animator. Compiled,
bundled, deployed.

Loader log on the validated run:

    Registered addition prefab: test_construct_ctrl (...; queued for component mirror)
    Humanoid mirror on 'test_construct_ctrl': configured from reference 'el.construct_soldier_t1'.

The clone spawned with no `NullReferenceException`. Standing, the unit ran the
controller's Idle state (a deliberate upper-body scale throb), confirming
MENACE drives a modder-authored controller. Ordered to move (`Units.Move`), it
transitioned to the Walk state (a larger balloon) and settled back to Idle on
arrival, confirming MENACE's construct driver sets `Speed` and the modder
controller reacts. The full custom-actor and custom-controller path works end
to end.

Working recipe for a custom actor:

1. `jiangyu unity import-prefab <vanilla-reference>` for a rig with physics and
   the Footprints/Ragdoll components.
2. Build the addition from that prefab: swap in a custom AnimatorController
   (parameters per the contract above), rename any node off the reference name,
   add a `__jiangyu_ref:<reference>` sentinel. Declare the reference in
   `importedPrefabs`.
3. Clone the matching EntityTemplate, point `Prefabs` at the addition.
4. The generalised runtime mirror fills Footprints/Ragdoll field data from the
   loaded reference; the custom controller drives the unit.

## Next step

Productise the recipe as a construct bake command parallel to `BakeHumanoid`
(rig in, custom or referenced controller, physics from the reference, sentinel
out) so a modder need not hand-assemble it. For a real mech, replace the
construct mesh with the mech model skinned to a compatible rig and author the
controller's clips against the driver contract (`Speed`, `Aiming`, `Hit`,
etc.). The loader-side generic-rig mirror (uncommitted in `Jiangyu.Loader`) is
the only engine-side change required and should be kept.

## Cross-reference

- `docs/research/verified/entitytemplate-contract.md`
- `docs/research/verified/template-cloning.md`
- `2026-05-14-template-prefab-fields.md`
- `2026-04-16-compiler-bind-pose-retargeting.md`
- `2026-04-16-blender-gltf-roundtrip-skin-compatibility.md`
