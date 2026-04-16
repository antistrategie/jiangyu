# Blender glTF Round-Trip Skin Compatibility

**Date**: 2026-04-16
**Status**: investigation complete, not yet promoted
**Scope**: structural comparison of clean Jiangyu export vs Blender round-trip for skinned model replacement

## Context

The skinned-model replacement path is proven: a clean Jiangyu-exported conscript model
works in game when mesh/node names are rewritten to the target contract. But the same
model imported into Blender and re-exported produces "stringy" deformation in game.

Already tried and ruled out:
- GLB vs separate glTF export
- conservative Blender export settings
- checking object transforms

This investigation compares the two files at the glTF structural level to identify the
most likely cause and determine whether Blender settings alone could fix it.

## Artifacts compared

| Label | Path |
|-------|------|
| Clean Jiangyu export | `WoMENACE/exported/el.local_forces_conscript_soldier/model.gltf` |
| Blender round-trip | `WoMENACE/assets/replacements/models/el.local_forces_basic_soldier--519/model.gltf` |

Both files describe the same source mesh (conscript soldier, 4 LODs, 21 joints, single
material). The Blender file has entity/node names rewritten to the basic_soldier target
contract; internal mesh names remain conscript.

## Findings

### 1. Mesh node parenting under armature root — PRIMARY SUSPECT

**Clean export**: mesh nodes (LOD0-LOD3) are scene-root siblings with identity world
transform. The skeleton hierarchy lives under a separate entity root node.

```
scene roots: [entityRoot, LOD0_mesh, LOD1_mesh, LOD2_mesh, LOD3_mesh]
entityRoot -> Root(-90deg X) -> Hips -> skeleton...
```

**Blender export**: mesh nodes are children of Root (the armature root bone), which
carries a -90 degree X rotation.

```
scene roots: [entityRoot]
entityRoot -> Root(-90deg X) -> [Hips -> skeleton..., LOD0_mesh, LOD1_mesh, ...]
```

The glTF 2.0 spec says skinned mesh node transforms "must be ignored" by the client.
But this is widely violated by importers — Unity's own skinning pipeline uses the mesh
node's transform. If Jiangyu's mesh compiler doesn't strip the inherited Root rotation
from the mesh node world transform, the skinning equation gets an extra rotation factor
that causes each joint's contribution to pull in a slightly wrong direction.

This is inherent to how Blender models the mesh-armature relationship. There is no
Blender export setting that changes it. Blender always parents mesh objects under the
armature in glTF output.

**This is the most likely cause of the "stringy" deformation.**

### 2. Single shared skin vs per-mesh skins — SECONDARY

**Clean export**: 4 separate `skin` objects, one per LOD mesh, each with its own
`inverseBindMatrices` accessor (all numerically identical).

**Blender export**: 1 shared `skin` object, all 4 mesh nodes reference `skin: 0`.

Semantically equivalent per spec. The IBM values are numerically near-identical between
the two files (max difference: 0.00002 on leg joints — negligible). Joint order by bone
name is identical.

If the compiler assumes one-skin-per-mesh or indexes into skin arrays differently, this
could matter. Worth handling but not the primary cause.

### 3. Non-unit bone scales — LOW CONCERN

Blender introduces near-1.0 scale values on most bone nodes:

- typical bones: ~1.0000001 (float epsilon noise)
- `UpperLeg_L`/`UpperLeg_R`: ~1.0000173 (largest deviation)

At a 0.83m leg chain length, the upper leg deviation produces ~0.014mm positional error.
Visually negligible. The clean Jiangyu export has no explicit scale on any bone node.

Could contribute to drift if the compiler doesn't handle non-unit bone scales, but not
sufficient alone to cause "stringy" artifacts.

### 4. Buffer layout — NO CONCERN

**Clean export**: interleaved vertex attributes (byteStride=52, all attributes in one
bufferView per mesh).

**Blender export**: separate bufferViews per attribute per mesh (POSITION, NORMAL,
TEXCOORD_0, JOINTS_0, WEIGHTS_0 each in their own view).

Pure packing difference. Any compliant glTF reader handles both layouts identically.

### 5. Vertex count/ordering divergence — EXPECTED

Blender re-splits some edges during round-trip:

| LOD | Clean verts | Blender verts | Index count (both) |
|-----|-------------|---------------|--------------------|
| LOD0 | 3201 | 3207 | 13632 |
| LOD1 | 2427 | 2430 | 10224 |
| LOD2 | 1833 | 1840 | 7494 |
| LOD3 | 1239 | 1248 | 4632 |

Normal DCC round-trip behaviour (edge splitting, UV seam handling). Not a skinning
concern. Per-vertex data (joints, weights) is internally consistent within each file.

### 6. Missing Jiangyu metadata — EXPECTED

Blender strips:
- `extras.jiangyu.cleaned` flag
- per-material `extras.jiangyu.textures` (`_Effect_Map`, `_MaskMap` paths)
- accessor names

These are Jiangyu-specific metadata, not geometry or skinning data. The compiler handles
their absence through its existing texture discovery and material compilation paths.

## Inverse bind matrix comparison

Extracted and compared all 21 IBM matrices between the two files.

Upper body joints (Hips through Hand_R_Socket): max element difference < 0.000001.

Lower body joints (UpperLeg through Foot): max element difference ~0.00002. This
correlates with the non-unit bone scales on the upper leg nodes — Blender's IBM
computation incorporates the slightly different rest-pose world transforms. The magnitude
is far too small to cause visible deformation.

## Conclusion

**Blender settings alone are unlikely to be sufficient.**

The mesh-node-under-armature-root parenting is fundamental to Blender's glTF exporter
and cannot be changed by export settings. The single shared skin is also just how Blender
works when multiple meshes share one armature.

## Revised root cause (found during implementation)

Tracing through `GlbMeshBundleCompiler.ExtractMesh`, the compiler does NOT use the mesh
node's world transform for skinned meshes with direct skin bindings (`meshNode.Skin !=
null` → `bakeNodeTransform = false`). So mesh-node parenting under Root is a structural
concern but **not the direct cause** of the stringy deformation.

The actual bug is in `DetermineVertexSpaceMode`: an authored glTF-family source without
the Jiangyu `extras.jiangyu.cleaned` flag but with direct skin bindings can be
misclassified as `RawPrefabSkinned` (cm-space, mirror X only) instead of
`CleanedSkinned` (metre-space, mirror X + 100x scale). This causes the compiled mesh to
be 100x too small relative to the game's cm-space skeleton. Every vertex sits near the
bone origins instead of being offset from them, and the skinning equation pulls all
vertices to bone positions — producing exactly the observed "stringy" deformation.

The implemented fix does not rely on `.gltf` vs `.glb`. Vertex space detection now uses
inspected mesh extents to distinguish authored metre-space models from raw
centimeter-space prefab dumps, so Blender `.gltf` and `.glb` round-trips both take the
proven `CleanedSkinned` path when their geometry matches authored scale.

## Remaining structural differences

These are real differences but did not cause the stringy deformation:

- **Mesh node parenting under armature root**: structural concern for future code paths
  that might use the mesh world transform, but currently harmless in the compiler
- **Single shared skin**: the compiler already handles this correctly
- **Non-unit bone scales**: visually negligible (~0.014mm at foot distance)

## Follow-up

- implemented: compiler-side authored metre-space detection now uses inspected mesh
  extents rather than file extension, so Blender `.gltf` and `.glb` round-trips both
  follow the proven skinned replacement path
- implemented: direct-skin mesh-node world transforms remain explicitly ignored during
  skinned vertex extraction, which keeps Blender's armature-root parenting from being
  baked into authored replacements
- implemented: near-unit skeleton scales are snapped more aggressively during cleanup to
  absorb Blender's float noise on bone nodes
- mesh node parenting and shared skin normalisation can still be revisited if future
  code paths need them, but they are not blocking the current replacement workflow
- consider whether the normalisation should also handle non-Blender DCC tools that
  exhibit similar topology patterns (e.g. Maya, 3ds Max glTF exporters)

## Final resolution

The Blender round-trip issue is now resolved for the current proven character-replacement
path.

The final working fix was not a single change but a compiler-side normalisation chain:

- authored glTF-family sources (`.gltf` and `.glb`) are treated as the same authored
  model category
- skinned vertex-space mode is derived from inspected geometry scale rather than file
  extension or Jiangyu metadata alone
- texture extraction works for both `.gltf` and `.glb` material graphs
- bind-pose retargeting runs in **source space** against auto-derived target reference
  bind poses
- normals and tangents are rebuilt after retarget so shading follows the corrected mesh

This was validated end-to-end in game using a Blender-authored re-posed model that
previously produced the stringy deformation and now deforms correctly.
