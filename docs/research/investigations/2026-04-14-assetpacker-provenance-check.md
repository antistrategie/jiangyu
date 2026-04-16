# 2026-04-14 AssetPacker Provenance Check

This note is a small validation exercise using Jiangyu's research method.

It does not validate MENACE gameplay semantics yet.
It validates the provenance of some `MenaceAssetPacker` knowledge artifacts so Jiangyu can treat them appropriately.

## Claim 1

Claim:
`MenaceAssetPacker`'s `Menace.DataExtractor` did not derive its schema entirely at runtime; it depended on a pre-generated embedded `schema.json`.

Source:
`MenaceAssetPacker` extractor project and extractor runtime code.

Game version:
Not game-version-sensitive for this provenance check.

Evidence category:
Tooling / provenance claim.

Validation method:
- Check whether `Menace.DataExtractor` embeds a generated schema file at build time.
- Check whether extractor runtime code explicitly loads that embedded schema and uses it to drive extraction.

Result:
Verified.

Evidence:
- `Menace.DataExtractor.csproj` embeds `../../generated/schema.json` as `schema.json`. See [Menace.DataExtractor.csproj](/home/justin/dev/github.com/antistrategie/MenaceAssetPacker/src/Menace.DataExtractor/Menace.DataExtractor.csproj#L62).
- `DataExtractorMod` describes itself as `Schema-driven extraction: loaded from embedded schema.json`. See [DataExtractorMod.cs](/home/justin/dev/github.com/antistrategie/MenaceAssetPacker/src/Menace.DataExtractor/DataExtractorMod.cs#L125).
- `LoadEmbeddedSchema()` reads the embedded `schema.json`, parses `templates`, `embedded_classes`, `structs`, and `effect_handlers`, then registers them into extractor state. See [DataExtractorMod.cs](/home/justin/dev/github.com/antistrategie/MenaceAssetPacker/src/Menace.DataExtractor/DataExtractorMod.cs#L349).

Confidence:
High.

Follow-up:
When importing old schema content into Jiangyu, treat it as a derived artifact with upstream provenance that must be checked, not as a self-evident dump of runtime truth.

## Claim 2

Claim:
`MenaceAssetPacker` event-handler knowledge came from a layered pipeline rather than a single authoritative source.

Source:
Legacy Python scripts and extractor parser code.

Game version:
Not game-version-sensitive for this provenance check.

Evidence category:
Tooling / provenance claim.

Validation method:
- Check whether one script infers handler schema from extracted JSON.
- Check whether another script uses Ghidra/decompiled code to refine that knowledge.
- Check whether consolidation scripts merge multiple analysis outputs back into schema/knowledge artifacts.
- Check whether the extractor consumes registered handler schemas rather than deriving handler structure from scratch at read time.

Result:
Verified.

Evidence:
- `analyze_eventhandlers.py` reads extracted template JSON and infers handler field types from observed values. See [analyze_eventhandlers.py](/home/justin/dev/github.com/antistrategie/MenaceAssetPacker/scripts/analyze_eventhandlers.py#L1).
- `analyze_eventhandlers_with_ghidra.py` explicitly says it cross-references schema against Ghidra/decompiled code to verify field types, offsets, and semantics. See [analyze_eventhandlers_with_ghidra.py](/home/justin/dev/github.com/antistrategie/MenaceAssetPacker/scripts/analyze_eventhandlers_with_ghidra.py#L1).
- `consolidate_analysis_to_schema.py` explicitly says it reads analysis JSON files and updates `schema.json` and `eventhandler_knowledge.json`. See [consolidate_analysis_to_schema.py](/home/justin/dev/github.com/antistrategie/MenaceAssetPacker/scripts/consolidate_analysis_to_schema.py#L1).
- `EventHandlerParser` consumes registered handler schemas for type detection and field parsing. See [EventHandlerParser.cs](/home/justin/dev/github.com/antistrategie/MenaceAssetPacker/src/Menace.DataExtractor/EventHandlerParser.cs#L28).

Confidence:
High.

Follow-up:
Jiangyu should treat old event-handler knowledge as a composite derived artifact:
- some parts may be raw observation
- some parts are heuristic inference
- some parts are Ghidra-assisted interpretation

That means old handler artifacts are useful as hypotheses and leads, but not as authoritative truth.

## Takeaway

This exercise validates a process rule, not a gameplay rule:

- old AssetPacker knowledge artifacts have real provenance
- but that provenance is layered and derived
- therefore Jiangyu should mine them carefully and re-verify claims before promotion
