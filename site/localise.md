# Translate a mod

MENACE ships in 13 languages. A mod's text is English by default and shows the same in every language until you add translations. Jiangyu makes a mod translatable with no extra work for the text you author in KDL, and lets a translator add a language by filling in a `.po` file. A separate mod can even translate a mod it does not own.

## How it works

Every display string (a unit name, a weapon description, a UI label, a spoken bark subtitle) carries an English default. At build time the compiler scans your mod and writes a translation catalogue, `compiled/locales/<mod>.pot`, listing every translatable string with its English source. A translator copies that catalogue to `locales/<code>.po` and fills in the translations. The loader applies the active language at load and re-applies it when the player changes language in-game. Any string a language does not translate falls back to English.

## Make your mod translatable

For a mod author there is almost nothing to do.

**KDL text** is extracted automatically. Every `m_DefaultTranslation` you set, at any depth, becomes a catalogue entry: unit titles and descriptions, weapon names, speaker lines, and so on.

```kdl
clone "WeaponTemplate" from="..." id="weapon.ak15" {
    set "Title" { set "m_DefaultTranslation" "Kalashnikova-15" }
}
```

**Code strings** become translatable by routing user-facing text through `Locale.Text`. The first argument is a key namespaced by your mod id, the second is the English fallback shown when no translation is installed.

```csharp
using Jiangyu.Sdk;

new TextButton(Locale.Text("MyMod::ui/swap_form", "SWAP FORM"));
```

**UXML labels** mark themselves: give the label a name with a leading `@` (the rest is the key) and keep the authored English `text`. Injected UXML is translated automatically, and the English text stays in the file for designers to read.

```xml
<ui:Label name="@MyMod::ui/give_gifts" text="GIVE GIFTS" />
```

**Voice-line subtitles** are extracted automatically. The text of every spoken bark (a SAY node in a conversation) becomes a catalogue entry, so a translator can localise what your characters say on screen. This covers the subtitle text only, not the audio: the voice clips stay as recorded, the words shown change with the language.

Literal `Locale.Text` calls and `@`-marked UXML labels are collected into the catalogue automatically, so they reach translators alongside your KDL text.

## Add a language

This is the translator's job and needs no code.

### What is a PO file?

Jiangyu uses **gettext PO files**, the long-standing standard format for software translation. A `.po` ("portable object") file is plain text: a list of entries, each pairing an original English string (`msgid`) with its translation (`msgstr`). The matching `.pot` ("portable object template") is the same list with the translations left blank, generated for you as the starting point. Because it is a standard, every major translation tool reads and writes it, so you never have to wrangle the raw text.

You can edit a `.po` file in any text editor, but a dedicated PO editor shows the source and translation side by side and tracks how much is done:

- [Poedit](https://poedit.net/) is a free desktop editor for Windows, macOS, and Linux, and the simplest place to start.
- [Weblate](https://weblate.org/) is web-based and suits a community translating together.
- [Crowdin](https://crowdin.com/) and [Lokalise](https://lokalise.com/) are hosted translation platforms.
- [Lokalize](https://apps.kde.org/lokalize/) and [Gtranslator](https://gitlab.gnome.org/GNOME/gtranslator) are Linux desktop editors.

### Steps

1. Compile the mod, or get the `locales/<mod>.pot` from its compiled bundle.
2. Copy the POT to `locales/<code>.po` in the mod, naming it with the locale code from the table below.
3. Fill in each `msgstr`. Leave one empty to keep English for that string.
4. Ship the `.po` with the mod (a mod that compiles stages it on the next build, a translation-only mod just includes it). The loader reads it directly and applies it whenever that language is active.

An entry looks like this. The `msgctxt` is the string's coordinate, `msgid` is the English source, `msgstr` is your translation.

```po
#. Items · WeaponTemplate weapon.ak15 · Title
msgctxt "MyMod::WeaponTemplate/weapon.ak15/Title"
msgid "Kalashnikova-15"
msgstr "Kalachnikova-15"
```

### Locale codes

| Language | File |
| --- | --- |
| English | source, no file |
| German | `de.po` |
| French | `fr.po` |
| Polish | `pl.po` |
| Spanish (Spain) | `es_ES.po` |
| Portuguese (Brazil) | `pt_BR.po` |
| Russian | `ru.po` |
| Japanese | `ja.po` |
| Korean | `ko.po` |
| Chinese (Simplified) | `zh_Hans.po` |
| Chinese (Traditional) | `zh_Hant.po` |
| Turkish | `tr.po` |
| Ukrainian | `uk.po` |

### Keeping a translation current

When the mod changes an English source string, run `msgmerge` to fold the freshly generated catalogue into your `.po`. It flags the changed entries `fuzzy`, and the loader skips fuzzy (and empty) entries, so a not-yet-revisited translation falls back to English rather than showing stale text.

### Are these keys stable?

Most coordinates are tied to names you choose: a template id, a UI key, a conversation id. Those only change if you rename them, and they are unaffected by MENACE updates.

Voice-line coordinates end in a number, like `conv/Voymastina/click_bark/953672724`. That number is the conversation node's id, computed from your mod rather than the game, so a MENACE update never changes it. It is derived from the node's position in the conversation, so it shifts if you insert, remove, or reorder nodes ahead of it. That is the normal kind of drift `msgmerge` handles: re-emit the catalogue, merge, and the translation carries across by its English source. You do not need to manage these numbers by hand.

## A separate translation mod

Because every string is keyed by a global coordinate, a mod can translate another mod it does not own. It declares a dependency on the target, ships PO files keyed by the target's coordinates (copied from the target's POT), and carries no templates, assets, or code.

```
mymod-fr/
  jiangyu.json        # depends: ["Jiangyu >= 1.0.0", "TargetMod >= 1.0.0"]
  locales/
    TargetMod/
      fr.po
```

When several mods translate the same target, the one loaded last wins per string, so a dedicated language pack can supersede a bundled translation entry by entry.

## File layout

- `locales/<code>.po` is your hand-written translation, the one file you author. The loader reads it directly, so it is the shipped artifact, copied as-is into the built mod.
- `compiled/locales/<mod>.pot` is the generated catalogue a translator starts from.

A translation-only mod therefore needs no toolchain at all: drop `locales/<target>/<code>.po` and a `jiangyu.json` into a folder under the game's `Mods/` directory and the loader applies it. Running the compiler on it only regenerates the catalogue and validates the keys.
