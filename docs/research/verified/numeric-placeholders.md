# Numeric description placeholders

`BaseLocalizedString.GetTranslated(string[] _placeholderOverrides)` at RVA `0x4FDD60`
reads `m_AllowPlaceholders` and the length of `m_Placeholders`. For each slot it
uses a non-null caller override when present, falling back to the stored value.
It replaces the corresponding entry from `BaseLocalizedString.PLACEHOLDERS` in
the translated text. Empty override strings are valid overrides.

Verified against the current GameAssembly by disassembly. The override selection
is at `0x4FDEF6` to `0x4FDF42`. Both the default-language and translated-text
branches reach this substitution loop. `GetDefaultTranslation` at `0x4FDB30`
returns raw default text and does not substitute placeholders.

The placeholder collection is the non-generic `Il2CppStringArray`. Its metadata
exposes `Length`, an integer indexer returning `string`, and a constructor taking
`string[]`. The applier uses its array rebuild path for append, insert, remove and
clear. A loader regression check binds append and insert against the real wrapper
without allocating a native array.

Jiangyu numeric bindings store internal tokens in the native placeholder array.
The GetTranslated prefix supplies formatted numbers through the override argument,
leaving both the stored array and existing caller overrides intact. Template
cloning, array edits and translations therefore preserve ordinary game semantics.
Each display reads the current numeric source, including inherited or patched
handler fields. A missing or non-numeric source logs an error and displays `?`.

