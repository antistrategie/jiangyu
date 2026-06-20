# Localisation Authoring Design

Date: 2026-06-20

## Goal

Establish how MENACE resolves display text at runtime, then design the authoring surface
a localiser uses to translate a mod. Cover three audiences: a mod author shipping their own
translations, a translator filling in a single language, and a standalone localisation mod
that translates a mod it does not own.

## MENACE localisation runtime

Decompiled from `Assembly-CSharp` (the Il2CppInterop assembly under
`MelonLoader/Il2CppAssemblies/`). Method bodies compile to native, so the model below is read
from type and member signatures plus observed in-game behaviour, not from native source.

### Every display string is a BaseLocalizedString

`Menace.Tools.LocalizedLine` (single line) and `LocalizedMultiLine` (multi line) both derive
from `BaseLocalizedString`. The serialised fields that matter:

- `m_DefaultTranslation : string` is the source text. At template-data level this is the only
  meaningful payload, alongside `m_Placeholders`. The earlier structural pass confirmed this:
  [2026-04-15-localized-support-type-spot-check.md](2026-04-15-localized-support-type-spot-check.md).
- `m_Category : LocaCategory`, `m_Id`, `m_Index`, `m_CategoryName`, `m_FieldName` form the
  runtime lookup key. These are bound at load by `Init(category, fieldName, allowPlaceholders)`,
  not stored in template data.

The members that produce displayed text:

- `GetTranslated(placeholderOverrides) : string`
- `GetDefaultTranslation() : string`, `GetRawDefaultTranslation() : string`
- `implicit operator string(BaseLocalizedString)` and `override ToString()`, both of which UI
  paths use to render a line.

### Resolution

`GetTranslated()` resolves against the loaded language table for the string's category and key.
On a miss, or when the current language is the default, it returns `m_DefaultTranslation`. This
is the observed behaviour: a mod that sets only `m_DefaultTranslation` renders that text in
every language, because no table entry exists for its key.

### LocaManager, LocaData, LocaCategoryData, LocaEntry

- `LocaManager` is the singleton (`Get()`, `s_Instance`). It holds `m_CurrentLanguage : LocaLanguage`
  and `m_Data : LocaData`, and exposes `GetCurrentLanguage()`, `IsCurrentLanguageDefault()`,
  `SetCurrentLanguage(LocaLanguage)`, `ReloadCurrentLanguage()`, and the static
  `DEFAULT_LANGUAGE` (English) plus `DetermineDefaultLanguage()`.
- `LocaData` loads one CSV per language: `LoadTranslation(LocaLanguage, stripUnsupportedTags)`,
  `GetCsvPath(LocaLanguage)`, `ReadCsvFile(path)`. Paths resolve under `Resources/Localization/`,
  so the per-language tables are Unity `TextAsset`s baked into `resources.assets`, not loose
  files on disk. It holds `m_Categories : LocaCategoryData[]` and `GetCategory(LocaCategory)`.
- `LocaCategoryData` is the per-category table: `m_Entries : Dictionary<string, LocaEntry>`,
  `TryGetTranslation(key, out translation)`, `GetDefaultTranslation(key)`, `HasEntry(key)`,
  and the live-mutation surface `AddEntry(key, context, LocaEntryType, printWarning)`,
  `RemoveEntry`, `Clear`, `ChangeKey`.
- `LocaEntry` holds `Key`, `Context`, `DefaultTranslation`, `Translation`, an `OUTDATED_MARKER`,
  `MarkTranslationAsOutdated()`, and `IsTranslationOutdated()`. The engine already models the
  drift between a source string and a translation made against an older source.

### Languages and categories

`LocaLanguage` (13 members, English is the default):

English, German, French, Polish, SpanishSpain, PortugueseBrazil, Russian, Japanese, Korean,
ChineseSimplified, ChineseTraditional, Turkish, Ukrainian.

`LocaCategory` (33 members). Mod content maps onto a subset:

Assets, Biomes, Conversations, Emotions, Entities, Factions, Items, Links, Missions,
LoadingQuotes, Operations, Planets, Properties, RandomStrings, Settings, Squaddies,
ShipUpgrades, Skills, Speakers, Subtitles, Surfaces, Tags, UnitLeaders, BlackMarketBarks,
Events, MissionPrepBarks, StrategyConversations, TacticalBarks, Enums, Dialogs, DropDownTexts,
Tooltips, UI.

## Why an untranslated clone renders English

A clone or patch sets `m_DefaultTranslation` and nothing keys into the language tables. The
tables ship baked in `Resources/Localization/` and contain only vanilla keys, loaded at startup
before any clone exists. So an untranslated mod field falls back to its default translation for
all 13 languages. Localising it means writing the missing language-table entry, which is what the
apply pass below does.

## Decision: write the engine loca store

Jiangyu applies localisation as a keyed post-pass that writes the active language's strings into
the game's own loca store. The game reads display text from `LocaData`: a `LocaCategoryData`
per category holds a `LocaEntry` per field, keyed `<Category>/<templateId>/<fieldName>`. A
dynamically cloned template has no such entry (the CSV tables load at startup, before the clone
exists), so the UI shows the field's English source regardless of the language. Jiangyu writes
the missing entry.

At load, jiangyu reads `LocaManager.Get().GetCurrentLanguage()`. For each translatable field
whose coordinate has a translation, it resolves the live template, walks to the
`BaseLocalizedString`, and writes two places:

- the `LocaData` entry the UI reads. The key and category come from the live line itself
  (`m_Category`, `m_FieldName`), so they match exactly what the game builds when it looks the
  field up. This is the load-bearing write: the weapon-stat UI, item lists, and similar read the
  table, not the line.
- the line's `m_DefaultTranslation`, for the code paths that read the localized string directly.

The translation key is a global coordinate: `mod-id :: template-type / template-id / field-path`.
Those are the same coordinates jiangyu addresses when it applies the patch.

The game rebuilds `LocaData` from its CSV on a language switch (`SetCurrentLanguage` calls
`ReloadCurrentLanguage`), which drops jiangyu's injected entries, so jiangyu re-applies on a
`SetCurrentLanguage` hook: it lays the English `msgid` baseline across every shipped PO first,
then overlays the new language.

### Why the field default alone is not enough

Writing only `m_DefaultTranslation` localises the code paths that call the line's
`GetTranslated()`, but the table-reading UI builds its key from the template id and finds no
entry for a clone, so it falls back to the baked English. Writing the `LocaData` entry is what
those screens read. The two writes together cover both paths.

## Authoring surface: gettext PO

Translations live in gettext `.po` files, one per locale, under a `locales/` directory in the
mod. PO is chosen over CSV because the mod content is multi-line narrative and dialogue, which
CSV quoting handles poorly, and because PO carries per-entry context comments and a `fuzzy`
flag that maps onto the engine's own outdated-translation concept. Every translation-management
platform a community would use reads PO.

The directory is named `locales/` to avoid the en-GB and en-US spelling fork in
`localisation` and `localization`, to stay self-explanatory to translators, and to sit next to
gettext's own `locale/` convention.

### Key scheme

The PO `msgctxt` is the global field coordinate. The `msgid` is the source string (the current
`m_DefaultTranslation`). The `msgstr` is the translation. A `#.` extracted comment carries the
`LocaCategory` and a human label. A `#:` reference points at the KDL source.

```po
#. Items · WeaponTemplate weapon.voymastina_ak15 · Title
#: templates/weapon/ak15.kdl:2
msgctxt "WOMENACE::WeaponTemplate/weapon.voymastina_ak15/Title"
msgid "Kalashnikova-15"
msgstr "Kalachnikova-15"
```

The category is derived from the template type and shown for translator context only:

- `WeaponTemplate`, `ItemTemplate`, `ArmorTemplate`, `CommodityTemplate` map to Items
- `EntityTemplate` maps to Entities
- `SpeakerTemplate` maps to Speakers
- `UnitLeaderTemplate` maps to UnitLeaders
- `SkillTemplate` and `PerkTemplate` map to Skills
- `TagTemplate` maps to Tags
- conversation and dialogue lines map to Conversations or Subtitles

### Locale file names

One `.po` per locale. English is the source and needs no file.

| LocaLanguage | locale file |
| --- | --- |
| English | source, no file |
| German | `de.po` |
| French | `fr.po` |
| Polish | `pl.po` |
| SpanishSpain | `es_ES.po` |
| PortugueseBrazil | `pt_BR.po` |
| Russian | `ru.po` |
| Japanese | `ja.po` |
| Korean | `ko.po` |
| ChineseSimplified | `zh_Hans.po` |
| ChineseTraditional | `zh_Hant.po` |
| Turkish | `tr.po` |
| Ukrainian | `uk.po` |

Jiangyu owns the `LocaLanguage` to locale-code map.

### Catalogue, translate, apply

1. Catalogue. The compiler emits `compiled/<mod>.pot` on every build, in the same pass that
   walks the patches for `compiled/templates.json`. It carries one entry per `LocalizedLine`
   and `LocalizedMultiLine` field a clone or patch sets, plus one per conversation SAY-node
   subtitle: coordinate, source string, category context, and source reference. Multi-line
   blocks fold into PO line continuations. There is no separate extract command, and because the
   POT regenerates each compile it never drifts from the KDL. The POT ships in `compiled/`, so
   anyone who downloads the mod has it.
2. Translate. The localiser copies the POT to a `<locale>.po` and fills the `msgstr`s in a PO
   editor or a translation-management platform. They never touch KDL or C#. Hand-authored
   `locales/*.po` are the source inputs the loader reads.
3. Apply. At load jiangyu selects the active language from `GetCurrentLanguage()`, builds a
   `coordinate -> translation` plan (skipping empty and `fuzzy` entries), and writes each entry
   into the engine loca store: the `LocaData` entry the UI reads, plus the live line default. A
   `SetCurrentLanguage` hook re-applies on a language switch.

### Compiled output

The `.po` is the shipped translation artifact: the loader reads it directly, so `PoFormat` and the
PO-to-manifest builder (`LocaleTable`) live in `Jiangyu.Shared`. The compiler only emits the
translator catalogue and stages the `.po` files so they deploy with the mod.

```
compiled/
  jiangyu.json
  templates.json
  locales/
    <mod>.pot       # source catalogue (PO), for translators, not read at runtime
    fr.po           # the staged translation, read by the loader when French is active
    ja.po
```

The loader parses each mod's `locales/**/<code>.po` for the active language and writes each entry
into the engine loca store (a `LocaData` table entry for template fields and conversation SAY
nodes, plus the live line default for template fields). The PO `msgid` doubles as the English
baseline: on a language switch the loader lays the msgid baseline across every shipped PO before
overlaying the new language, so no separate baseline file is needed. English needs no file: it is
the template default. A translation-only mod ships just its `.po` files plus `jiangyu.json` and
needs no compile.

First-party translations and a separate localisation mod share this shape. A mod's own
`locales/fr.po` keys its own coordinates. A localisation mod's `locales/<target>/fr.po` keys the
target's coordinates. At load jiangyu parses every loaded mod's active-locale PO and merges them by
coordinate, later-loaded winning, so first-party is not a special case.

### Keeping a translation current

The PO `msgid` records the source string the translation was made against. When a mod's source
string changes, the translator runs `msgmerge` against the freshly emitted POT: it marks changed
entries `fuzzy`. The apply step skips `fuzzy` and empty entries, so a not-yet-revisited
translation falls back to English rather than showing stale text. `fuzzy` lines up with the
engine's own `IsTranslationOutdated` concept.

## Code and UXML strings

Template text needs no opt-in. A user-facing string in mod C# or UXML is only localisable when
the mod routes it through an SDK helper with a stable key:

```csharp
new TextButton(Locale.Text("WOMENACE::ui/swap_form", "SWAP FORM"));
```

`Locale.Text(key, fallback)` (the SDK type is named `Locale` to avoid the game's own
`Il2CppMenace.Tools.Loca`) returns the active-language string for the key, or the fallback when
none is installed. UI entries live in the same PO under the `ui` namespace
(`msgctxt "WOMENACE::ui/swap_form"`). The loader collects them from the active-language PO and
installs them for `Locale.Text` to read. A raw hardcoded string with no key is invisible to
translators. Guidance for mod authors: route any user-facing C# or UXML string through the helper to
make it localisable.

UI keys are not auto-discovered from C# yet, so they do not appear in the generated POT: the
translator adds them by hand. Scanning compiled mod code for `Locale.Text` calls to seed the POT
is a follow-up.

## Separate localisation mods

A mod that translates another mod is a first-class outcome of the keyed post-pass. Translations
key off a global coordinate, not a mod-private handle, so the translator does not own the
templates.

A localisation mod is a mod with no templates, assets, or code. It declares a dependency on the
target and ships PO files keyed by the target's coordinates, namespaced by target mod id:

```
womenace-fr/
  jiangyu.json          # depends: ["Jiangyu >= 1.0.0", "WOMENACE >= 0.1.0"]
  locales/
    WOMENACE/
      fr.po             # msgctxt "WOMENACE::WeaponTemplate/weapon.voymastina_ak15/Title"
```

The target ships `compiled/WOMENACE.pot`, so the translator copies it to `<locale>.po`, fills
the `msgstr`s, and packages the result. Nothing to run against the target. The mod skips Unity
batchmode and compiles in seconds.

### Dependency, load order, merge

The dependency makes the loader order the localisation mod after the target, so the target's
patches have already set the English source when the post-pass runs. The post-pass merges every
loaded mod's PO contribution for the active language, target-coordinate keyed. A mod may bundle
its own `locales/`, and a dedicated localisation mod loaded later wins per key. A community
language pack supersedes a weak bundled translation key by key while inheriting the rest.

### Graceful staleness

The source-version guard applies across mods. When the target changes a source string, the
localisation mod's now-stale entry falls back to the target's current source. The pack degrades
to readable text rather than showing outdated strings, and the fix is a `msgmerge` against the
new target version's POT.

### Constraint

A localisation mod can reach the target's code and UXML strings only when the target registered
them through the helper with a key. Template and KDL text needs no opt-in. Raw hardcoded
strings in the target are unreachable.

## Validation status

Verified from the assembly: the type and member model above, the `LocaLanguage` and
`LocaCategory` enums, the CSV-in-`Resources/Localization/` storage, and the live-mutation
surface on `LocaCategoryData`.

Verified live over the bridge (2026-06-20, WOMENACE in a campaign), reading and writing
`weapon.voymastina_ak15`:

- The UI reads the engine table, not the live line, for the load-bearing fields. The weapon Title
  rendered the rewritten `m_DefaultTranslation` (its UI path reads the line), but the ShortName did
  not: that screen reads the `LocaData` entry keyed `Items/weapon.voymastina_ak15/ShortName`, and a
  clone has no such entry, so it showed the baked English. Writing the `LocaData` entry made it
  render. The implementation writes both the table entry and the line default.
- The engine key for a template field is `<Category>/<templateId>/<fieldName>`, built from the
  template id (the line's own `m_Id` is empty on a clone). An item ShortName resolves under category
  `Items` and field `ShortName`. The category and field name read off the live `BaseLocalizedString`
  match exactly, so jiangyu builds the same key.
- A clone's injected entry survives until the next language switch, which rebuilds `LocaData` from
  the CSV. The `SetCurrentLanguage` hook re-injects, so the switch path re-applies.
- Conversation barks resolve through `ConversationTemplate.GetLocaData()` and
  `BaseConversationNode.GetLocaKey()`; the node guid in the compiled template is deterministic, so a
  bark subtitle can be keyed at compile time and the live node found by guid at apply time.

## Built

The pipeline above is implemented:

- `Jiangyu.Shared.Localisation.PoFormat` (PO reader/writer), `LocaleCoordinate` (the
  `modId::Type/id/descent`, `modId::ui/key`, and `modId::conv/convId/nodeGuid` coordinate formats),
  and `LocaleTable` (PO to the loader plan: template-field ops, conversation ops, the UI map, and the
  English baseline). Shared so the loader reads `.po` itself. Unit-tested in
  `Jiangyu.Core.Tests/Localisation` and `Jiangyu.Loader.Tests/Localisation`.
- `Jiangyu.Core.Localisation.LocalisationCompiler` builds the POT: it walks the compiled template
  program for `m_DefaultTranslation` writes and conversation SAY-node subtitles, and scans
  `code/*.cs` for `Locale.Text("key","fallback")` and `unity/**/*.uxml` for `name="@key"` labels, so
  template, voice-line, code, and UXML strings reach the catalogue with no separate step.
- The compile step in `CompilationService` emits `compiled/locales/<mod>.pot` and stages the
  hand-written `locales/**/*.po` into `compiled/locales/` to deploy with the mod. No compiled tables.
- The loader's `LocaleResolver`, `LocalePlanner`, and `LocaleApplier` (driven from
  `ReplacementCoordinator`) parse each mod's `locales/**/<code>.po`, build the plan, and apply it via
  `LocaleTableInjector`, which writes the `LocaData` entry the UI reads (plus the line default for
  template fields, and the conversation node entry for barks) and installs the UI map for
  `Locale.Text`. `LocaleReloadPatch` postfixes `LocaManager.SetCurrentLanguage`, so an in-game switch
  re-injects over the `msgid` baseline and rebuilds injected UI without a restart.
- `Jiangyu.Sdk.Locale.Text(key, fallback)` for code strings, plus `UI.Localise` which resolves
  `@`-marked UXML labels and runs automatically on every injected tree (the authored `text` is the
  English fallback, visible to designers).

WOMENACE routes its code strings through `Locale.Text` and marks the gift-modal title with
`name="@WOMENACE::ui/give_gifts"`, so it is localisation-ready. It ships no bundled translation: the
sample French (template fields, UI buttons, and Voymastina voice-line subtitles) was verification-only
and has been removed.

## Remaining

- Per-mod live re-render on a switch covers injected mod UI (via `UI.RelocaliseAll`). Native screens
  showing template text rely on the game's own language-change UI refresh or a screen rebuild.
- Voice-line coverage is the SAY-node subtitle text. Conversation choice text
  (`ChoiceConversationNode`) and event title/message/sender fields are not yet extracted.
- Translation covers text only. Per-language voice audio (dubbing) would be a separate asset-swap
  feature, not part of the gettext text pipeline.

## Cross-reference

- [2026-04-15-localized-support-type-spot-check.md](2026-04-15-localized-support-type-spot-check.md):
  the serialised shape of `LocalizedLine` and `LocalizedMultiLine`.
