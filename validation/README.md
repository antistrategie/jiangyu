# Validation

This directory contains Jiangyu's committed machine-readable validation artifacts.

These files support:

- structural drift detection after game updates
- reproducible validation passes
- human-reviewed baseline changes

They do **not** define runtime truth for the compiler or loader.

## Files

- `template-structure-baseline.sources.json`
  - human-curated input
  - lists which validated template types and support types belong in the structural baseline
  - lists the exact sample names used to generate that baseline

- `template-structure-baseline.json`
  - machine-generated output
  - structural snapshot produced from the curated source list
  - intended for diffing and re-audit work

## Workflow

- edit `*.sources.json` intentionally when Jiangyu promotes new validated contracts
- regenerate the matching baseline with `jiangyu templates baseline generate`
- review the diff before committing changes

For the policy and promotion rules behind these files, see:

- `docs/research/VALIDATION.md`
