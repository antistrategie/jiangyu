# Validation

This directory contains Jiangyu's committed machine-readable validation artifacts.

These files support:

- structural drift detection after game updates
- reproducible validation passes
- human-reviewed baseline changes

They do **not** define runtime truth for the compiler or loader.

They are also **not** Jiangyu's main discovery tool. Use `jiangyu templates inspect`, `jiangyu templates query`, and the loader-side `RuntimeInspector` first when you need fresh evidence from the current game/runtime. This directory exists for the narrower case where Jiangyu wants to keep a reviewed, diffable validation snapshot in git.

Short version:

- `inspect` / `RuntimeInspector` = fresh evidence
- `validation/` = frozen baseline

## Files

- `template-structure-baseline.sources.json`
  - human-curated input
  - lists which validated template types and support types belong in the structural baseline
  - lists the exact sample names used to generate that baseline

- `template-structure-baseline.json`
  - machine-generated output
  - structural snapshot produced from the curated source list
  - intended for diffing and re-audit work

- `verb-surface-baseline.json`
  - machine-generated output
  - the public method surface of the game types the verb manifest binds to
  - the codegen contract check fails the build when a bound target is removed or renamed. This baseline is the complementary half: it reports what an update added to those types (candidate new verbs)

- `hook-surface-baseline.json`
  - machine-generated output
  - the public method and event surface of the game types the hook manifest binds to
  - same role for hooks: a game update that adds an event or method to a hooked type shows up as a candidate new hook

## Workflow

- structural baseline: edit `template-structure-baseline.sources.json` intentionally when Jiangyu promotes new validated contracts, regenerate with `jiangyu templates baseline generate`, review the diff before committing
- surface baselines: regenerate with the codegen generators (`dotnet run --project src/Jiangyu.Codegen.Verbs -- src/Jiangyu.Codegen.Verbs/manifests src/Jiangyu.Sdk.Menace/Generated --update-surface`, and the matching `Jiangyu.Codegen.Hooks` invocation). Without `--update-surface` the generators print the drift report instead of rewriting the baseline
- review the diff before committing changes

## When To Add Files Here

Add to `validation/` only when all of these are true:

- the contract is important enough to preserve in git
- Jiangyu expects to regenerate or diff it later
- the committed artifact will help review drift after a game update or a
  deliberate contract expansion

Good fits:

- structural baselines for verified template/support-type contracts
- other narrow, machine-generated artifacts Jiangyu explicitly plans to
  rerun and compare

## When Not To Add Files Here

Do **not** use `validation/` for ordinary investigation or temporary
diagnostics.

Poor fits:

- ad hoc `RuntimeInspector` dumps
- one-off save/load smoke outputs
- temporary debugging artifacts
- runtime-behaviour questions that are better answered by fresh inspection
- anything Jiangyu is not actively diffing or regenerating later

After a game update:

- rerun `jiangyu templates index`
- treat the committed `template-structure-baseline.json` as stale until you intentionally regenerate it
- `jiangyu templates baseline diff` will refuse to use the committed current baseline if its `gameAssemblyHash` no longer matches the current game

For the policy and promotion rules behind these files, see:

- `docs/research/VALIDATION.md`
