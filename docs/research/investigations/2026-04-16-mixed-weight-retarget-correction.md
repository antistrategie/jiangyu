# Mixed-Weight Bind-Pose Retarget Correction

**Date**: 2026-04-16
**Status**: investigation complete, not yet implemented
**Scope**: identifies and corrects a matrix blending error in `BindPoseRetargetService` that
causes mixed-weight vertices to diverge under bind-pose retargeting
**Follows**: `2026-04-16-compiler-bind-pose-retargeting.md`

## Context

The bind-pose retargeting investigation produced a working `BindPoseRetargetService` with
deterministic test fixtures. The tests confirm:

- fully weighted vertices retarget correctly (rest-pose recovery and animation match)
- mixed-weight vertices do NOT recover the target rest pose and diverge under animation

This follow-up investigates whether the mixed-weight failure is a fundamental limitation
of the retarget approach or a correctable error.

## Finding

The mixed-weight failure is a matrix operation ordering error in the current
implementation, not a fundamental limitation.

### What the current code does

The current `Retarget` method precomputes a per-joint retarget matrix:

```
R[i] = IBM_authored[i] * B_game[i]
```

where `IBM_authored[i]` is the authored inverse bind matrix and `B_game[i]` is the game
rest-pose world transform (inverse of the game IBM). Each `R[i]` is the exact per-joint
inverse of the forward repose matrix `F[i] = IBM_game[i] * B_authored[i]`.

For each vertex, the current code blends these per-joint inverses by weight:

```
M_rev = w0*R[j0] + w1*R[j1] + w2*R[j2] + w3*R[j3]
v_recovered = v_authored * M_rev
```

### Why this is wrong for mixed-weight vertices

When a modder changes the rest pose (in Blender or any DCC tool), the tool repositions
each vertex using the forward repose operation:

```
F[i] = IBM_game[i] * B_authored[i]
M_fwd = w0*F[j0] + w1*F[j1] + w2*F[j2] + w3*F[j3]
v_authored = v_game * M_fwd
```

The exact inverse is:

```
v_game = v_authored * M_fwd^(-1)
```

The current code computes `sum(w_i * F_i^(-1))` instead of `(sum(w_i * F_i))^(-1)`.
Matrix inversion does not distribute over weighted addition, so these are different
matrices when the per-joint forward matrices differ — which they do whenever the rest
pose has changed between authored and game skeletons.

For single-bone vertices (one weight = 1.0, rest = 0.0), the sum contains one term, and
`F_i^(-1) == (F_i)^(-1)` trivially. The approaches are equivalent and both exact.

For mixed-weight vertices, the current approach blends the per-joint inverses. The correct
approach inverts the per-vertex blended forward matrix.

### Concrete test case

The existing test `RotationRetarget_MixedWeightVertex_CannotExactlyRecoverTargetRestPose`
uses:

- two-bone chain, child rotated 45 degrees between authored and game rest poses
- vertex at (0.15, 0.95, 0) with weights (0.5, 0.5) on Root and Child
- `ReposeVertex` creates the authored position using the forward blend `M_fwd`

The current code applies `sum(w_i * R_i)` and gets approximately (0.128, 0.957, 0) —
a residual error of ~0.023 units.

The correct operation `M_fwd^(-1)` recovers the target position exactly (within float
epsilon), because it is the algebraic inverse of the operation that created the authored
position.

### Rest-pose recovery implies animation correctness

If the retarget exactly recovers the target vertex position, the compiled mesh contains
`v_game` with the game's bind poses. The game's runtime skinning equation then evaluates
identically to the original mesh for every animation frame. The animation divergence
observed in the test is a consequence of the rest-pose error, not an independent problem.

## Alternatives considered

### Dual quaternion blending

DQB uses a different interpolation model that handles rotation blending more naturally.
Not appropriate here: MENACE uses Unity's standard linear blend skinning at runtime. A
DQB retarget paired with LBS runtime skinning introduces a model mismatch — a different
class of error, not an improvement.

### Iterative per-vertex solve

The forward equation `v_authored = v_game * M_fwd` is linear in `v_game`. One matrix
inversion gives the exact answer. Iteration is unnecessary.

### Laplacian/differential deformation transfer

Requires mesh connectivity (not just per-vertex data), a sparse linear solver, and is
designed for transferring shape between different topologies. The retarget operates on the
same mesh with the same weights. Massive scope increase for no benefit.

### Corrective blend shapes

Adds a per-vertex delta to compensate for LBS artifacts. Pose-dependent (rest-pose
correction may worsen other poses), requires blend shape pipeline support, adds runtime
cost. Unnecessary if the retarget itself is correct.

## Correction

Change `BindPoseRetargetService.Retarget` to:

1. Precompute per-bone forward matrices: `F[i] = IBM_game[i] * B_authored[i]`
2. Per vertex: blend `M_fwd = sum(w_i * F[j_i])`, invert, transform:
   `v_game = v_authored * M_fwd^(-1)`
3. Normal/tangent transform: use `BuildNormalMatrix(M_fwd^(-1))` as before

The per-bone precomputation changes from `R[i] = IBM_authored[i] * B_game[i]` to
`F[i] = IBM_game[i] * B_authored[i]` (same two matrices, reversed multiplication order).
The per-vertex loop adds one 4x4 matrix inversion per vertex (cheap) instead of directly
applying the blended matrix.

### Identity case

When authored IBMs match game IBMs, each `F[i] = IBM_game[i] * B_authored[i] = Identity`.
The blended `M_fwd` is identity for every vertex. Its inverse is identity. Vertex data
passes through unchanged. The identity fast-path (`NeedsRetarget` returns false) remains
the zero-overhead default.

### Singularity

`M_fwd` is theoretically singular if the weighted forward matrices cancel out. This
requires adversarial weight configurations that do not occur in real skinned meshes. If
encountered, a clear error is appropriate.

## Scope implications

The v1 feature scope does not change:

- same bone names, same hierarchy, rest-pose/proportion drift, compiler-side only
- the fix corrects the retarget math within the existing scope
- no new dependencies, no new pipeline stages, no additional game data access
- the test fixture changes from asserting mixed-weight failure to asserting success

## Follow-up

- implement the matrix operation order correction in `BindPoseRetargetService.Retarget`
- update `RotationRetarget_MixedWeightVertex_CannotExactlyRecoverTargetRestPose` to
  assert convergence for both rest-pose recovery and animation match
- consider adding a mixed-weight proportion-drift test case (non-trivial forward matrices
  from scale differences rather than rotation differences)
- the original investigation's integration plan, opt-in mechanism, and reference skeleton
  sourcing recommendations remain unchanged
