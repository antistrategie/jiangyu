# Compiler-Side Bind-Pose Retargeting

**Date**: 2026-04-16
**Status**: investigation complete, implemented
**Scope**: v1 design for compiler-side bind-pose normalisation when authored skinned models
have the same bone names and hierarchy as the game skeleton but a different rest pose
and/or moderate proportion drift

## Context

Jiangyu's proven skinned replacement path requires the authored model (.gltf or .glb) to
carry the exact same rest pose and bone proportions as the game skeleton. The compiler reads authored
inverse bind matrices (IBMs), applies vertex space transforms (mirror X, scale to
cm-space), and passes them through to Unity. If a modder edits the rest pose (e.g.
straightens arms from A-pose to T-pose for easier authoring) or adjusts bone lengths to
fit a different character design, the compiled mesh's bind pose no longer matches what
the game's animation clips expect, producing incorrect deformation at runtime.

This investigation scopes a narrow v1 feature: compiler-side bind-pose normalisation
that rebinds authored vertices onto the game's expected bind pose before the existing
vertex-space conversion, without touching animation data or supporting different rigs.

## What this is not

- Not full animation retargeting. Animation curves are untouched.
- Not support for different rigs, hierarchies, or bone name remapping.
- Not a replacement for the current proven mesh-replacement path. The current path
  (exact bind-pose match) remains the default zero-overhead case.

## v1 supported scope

- Same bone names (exact string match between authored and game skeleton)
- Same hierarchy (same parent-child relationships)
- Changed rest-pose rotations (e.g. T-pose authored against A-pose game skeleton)
- Moderate proportion drift (different bone lengths, within reason)
- Authored glTF-family sources (.gltf and .glb), via the `CleanedSkinned` vertex space path
- Explicit opt-in for v1 (flag or manifest field, not automatic detection)

## v1 out-of-scope

- Different bone hierarchies (reparented bones, different tree structure)
- Different bone names (semantic/fuzzy matching, name remapping)
- Added or removed bones (authored skeleton must be an exact name+hierarchy match)
- Animation retargeting (only the mesh bind pose is retargeted)
- IK constraints, bone constraints, or procedural bones
- Non-linear deformation correction (volume preservation, corrective shapes)
- Extreme proportion changes that would cause mesh intersection or require topology edits
- Raw prefab dump inputs (only the authored/`CleanedSkinned` path)

## The retarget operation

The core operation is a per-vertex weighted rebind using the standard linear blend
skinning retarget. This is the same math used by Mixamo, Blender's armature transfer,
and engine retargeting tools.

### Definitions

```
IBM_authored[i]  = authored inverse bind matrix for joint i (from the glTF skin)
IBM_game[i]      = game inverse bind matrix for joint i (from the reference source)
B_game[i]        = inverse(IBM_game[i])  — game joint world-space rest transform
```

### Per-vertex operation

For each vertex `v_authored` with joint indices `(j0, j1, j2, j3)` and weights
`(w0, w1, w2, w3)`:

```
// Per-joint retarget matrix: authored-local-space -> game-model-space
R[i] = B_game[i] * IBM_authored[i]

// Blended retarget matrix for this vertex
M = w0*R[j0] + w1*R[j1] + w2*R[j2] + w3*R[j3]

v_game = M * v_authored
```

Intuitively: `IBM_authored[i] * v` takes the vertex into joint i's local space as the
modder defined it. `B_game[i]` then places it back into model space using the game's rest
pose. Blended by weights, this preserves each vertex's offset from its influencing joints
while re-expressing it in the game skeleton's configuration.

### Normals and tangents

Normals receive the same blended retarget matrix, applied as the inverse-transpose of
the upper-left 3x3. For rest-pose rotation and mild proportion changes the standard
matrix is sufficient (near-orthonormal). Tangent directions transform the same way;
the handedness sign (w component) passes through unchanged.

### Weights and joint indices

Unchanged. The retarget does not alter skinning weights or joint assignments.

### Output IBMs

The compiled mesh receives the **game's** IBMs, not the authored ones.

### Identity case

When authored IBMs match game IBMs (within tolerance), all per-joint retarget matrices
`R[i]` reduce to identity and vertex data passes through unchanged. This must remain
the default zero-overhead path — no retarget computation when it is not needed.

## Reference skeleton sourcing

How the compiler obtains the game's IBMs is a **product decision**, not just an
implementation detail. There are two viable approaches, with a real tradeoff.

### Option A: reference model path

The compiler accepts a path to a Jiangyu-exported clean reference model (the same
export the modder started from). The game IBMs and joint hierarchy are read from that
glTF-family file (.gltf or .glb).

**Advantages**:
- Simpler workflow alignment — the modder already has this file
- No game data access required at compile time
- Portable — the reference model can be committed or shared alongside the mod source
- Consistent with Jiangyu's existing "export then author" workflow

**Disadvantages**:
- The reference model is a copy-in-time — if the game updates its skeleton, the
  reference becomes stale
- The modder must keep the reference model around and point the compiler at it
- Jiangyu trusts the reference model's IBMs as ground truth without re-verifying
  against current game data

### Option B: live game-data path

The compiler reads IBMs directly from the game's compiled mesh assets at compile time,
using the existing `MeshContractExtractor` infrastructure (which already reads
`m_BindPose` and `m_BoneNameHashes` from Unity mesh assets via AssetsTools.NET).

**Advantages**:
- Stronger source of truth — always reads the current game skeleton
- No stale reference risk
- Aligns with Jiangyu's "derive from live game data" research principle

**Disadvantages**:
- Heavier compile-time dependency — requires indexed game data to be available
- Slower compilation (game asset lookup)
- `MeshContractExtractor` currently runs as a post-build pass, not a pre-extraction
  input; using it for pre-extraction IBM sourcing would need pipeline reordering or a
  separate early-read path

### Recommendation

For the initial investigation pass, option A (reference model path) was the simpler v1
design because it aligned with the existing export-first workflow and kept the retarget
math isolated from live game-data lookup.

### Implemented outcome

The implemented v1 does **not** require the modder to supply a reference model. Jiangyu
now auto-derives the reference skeleton contract from the replacement target alias and
stores the compiler-owned exported reference under `.jiangyu/bind-pose-references/`.

That makes bind-pose retargeting an authored-model feature, not a second-file workflow:

- modders provide only the edited replacement `model.gltf` or `model.glb`
- Jiangyu exports and caches the clean target reference itself
- the compiler retargets authored vertices in source space before the existing vertex
  space conversion

This keeps the workflow narrow while still grounding the retarget in a known-good target
contract.

## Integration point in the compiler

The retarget operation slots into `GlbMeshBundleCompiler.ExtractMesh` at a specific
point in the existing pipeline:

```
Read glTF/GLB → parse skin → extract vertex data + authored IBMs
                                    ↓
                    [bind-pose retarget]  ← new step
                                    ↓
              vertex space mode transform (mirror X, 100x scale)
                                    ↓
                        write to staging format
```

The retarget operates in authored model space. The existing `CleanedSkinned` space
conversion (mirror X, scale to cm-space) applies after, unchanged.

### Code locations

| Concern | Location |
|---------|----------|
| Retarget math | new `BindPoseRetargetService` in `src/Jiangyu.Core/Glb/` |
| Call site | `GlbMeshBundleCompiler.ExtractMesh` (~line 520, after vertex/IBM read, before space conversion) |
| Joint name matching | extend or reuse `GlbCharacterContract` joint-path utilities |
| Game IBM reading (option B, later) | `MeshContractExtractor` (already reads `m_BindPose`) |
| Reference model IBM reading (option A) | load reference .gltf/.glb via `ModelRoot.Load`, extract skin IBMs by joint name |

### Service shape

`BindPoseRetargetService` should be a stateless pure-math service:

- Input: authored positions, normals, tangents, joint indices, weights, authored IBMs,
  game IBMs (indexed by joint name)
- Output: retargeted positions, normals, tangents
- No I/O, no logging dependency, no side effects
- Testable with programmatic fixtures

## Tests needed

All testable with programmatic SharpGLTF fixtures (no game data required), consistent
with the existing test approach in `Jiangyu.Core.Tests/Glb/`:

1. **Identity retarget** — authored IBMs == game IBMs → positions/normals unchanged
   (within float epsilon). Confirms the zero-overhead identity case.
2. **Pure rotation retarget** — e.g. upper arm rotated 45deg (T-pose vs A-pose) →
   vertices reposition correctly relative to game rest pose.
3. **Proportion drift** — bone chain 10% longer → vertices redistribute to maintain
   relative joint offsets.
4. **Normal correctness** — retargeted normals remain unit-length and geometrically
   consistent with retargeted positions.
5. **Tangent correctness** — tangent directions transform correctly, handedness sign
   preserved.
6. **Weight/index passthrough** — joint indices and weights are identical before and
   after retarget.
7. **Joint name mismatch** — missing joint in either skeleton produces a clear error,
   not silent corruption.

## Risk notes

- This feature touches the vertex data path, which is foundation-critical. The identity
  case (no retarget needed) must be the default path with zero overhead.
- The retarget math itself is standard, but integration ordering matters — retarget
  must happen before the existing space conversion, not after.
- Large proportion changes (e.g. 2x arm length) are mathematically valid but may
  produce visually poor results (stretching, interpenetration). v1 does not attempt to
  detect or warn about extreme drift — that is a later ergonomic concern.

## Follow-up

- implemented: `BindPoseRetargetService` with source-space bind-pose retargeting for
  authored skinned `.gltf` and `.glb` sources
- implemented: integration into `GlbMeshBundleCompiler.ExtractMesh`
- implemented: auto-derived compiler-owned reference skeleton export under `.jiangyu/`
- implemented: programmatic fixture tests covering identity, rotation, and proportion
  cases
- validated in-game against a Blender-authored re-posed model that previously mangled
  under animation and now deforms correctly
