# Research

This directory is the map for Jiangyu's reverse-engineering and validation work.

- Start with [`verified/`](verified/README.md) for current Jiangyu-owned findings.
- Use [`investigations/`](investigations/README.md) when you need sample-level provenance, methodology, or unfinished edge-case work.
- Use [`VALIDATION.md`](VALIDATION.md) for the project's evidence and promotion rules.
- Use [`STRUCTURAL_VALIDATION_WORKFLOW.md`](STRUCTURAL_VALIDATION_WORKFLOW.md) for the repeatable structural-audit procedure.

## Current Status

- **Template contracts**
  - Status: broadly verified
  - See: [`verified/entitytemplate-contract.md`](verified/entitytemplate-contract.md)

- **Support and array-element contracts**
  - Status: partially promoted
  - See:
    - [`verified/entityproperties-contract.md`](verified/entityproperties-contract.md)
    - [`verified/array-element-contracts.md`](verified/array-element-contracts.md)
    - [`verified/unitleader-initial-attributes.md`](verified/unitleader-initial-attributes.md) — 7-byte save-frozen starting-attribute layout, readback-verified

- **Structural rules and cross-cutting patterns**
  - Status: verified
  - See:
    - [`verified/universal-delta-rule.md`](verified/universal-delta-rule.md)
    - [`verified/polymorphic-reference-arrays.md`](verified/polymorphic-reference-arrays.md)
    - [`verified/classifier-gap.md`](verified/classifier-gap.md)

- **Structural baseline tooling**
  - Status: established
  - See:
    - [`validation/`](../../validation/)
    - [`VALIDATION.md`](VALIDATION.md)

- **Open areas**
  - Status: still active
  - Current fronts:
    - prefab and template cloning for new-content workflows
    - animation-related contracts and authoring paths
    - runtime replacement identity beyond `sharedMesh.name` when variant-specific replacement is needed
    - post-update structural re-audits and edge-case contract checks

## How To Use These Docs

- Read `verified/` first when deciding what Jiangyu currently trusts.
- Read `investigations/` when you need to understand how a claim was derived.
- Use the baseline files for machine-readable structural snapshots and drift checks, not as runtime truth.
