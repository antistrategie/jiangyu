import type { editor } from "monaco-editor";

// Monaco's theme API takes raw hex strings, not CSS custom properties, so the
// theme has to be built at runtime from the live :root tokens. This keeps the
// editor in lockstep with tokens.css instead of drifting when the palette shifts.
//
// Rules' `foreground`/`background` want bare hex (no `#`); `colors.*` want it
// prefixed. Blended overlays (selection/find highlights) are composed by
// suffixing a 2-hex-digit alpha onto a resolved token.

function readToken(name: string): string {
  const raw = getComputedStyle(document.documentElement).getPropertyValue(name).trim();
  return raw.startsWith("#") ? raw.slice(1) : raw;
}

function hashed(hex: string): string {
  return hex.startsWith("#") ? hex : `#${hex}`;
}

export function buildJiangyuTheme(): editor.IStandaloneThemeData {
  const ink0 = readToken("--ink-0");
  const ink1 = readToken("--ink-1");
  const ink2 = readToken("--ink-2");
  const ink3 = readToken("--ink-3");
  const ink4 = readToken("--ink-4");
  const paper0 = readToken("--paper-0");
  const paper1 = readToken("--paper-1");
  const paper2 = readToken("--paper-2");
  const paper3 = readToken("--paper-3");
  const cinnabar0 = readToken("--cinnabar-0");
  const cinnabar1 = readToken("--cinnabar-1");
  const gold0 = readToken("--gold-0");
  const gold2 = readToken("--gold-2");
  const jade0 = readToken("--jade-0");
  const jade1 = readToken("--jade-1");
  // Editorial fallback for escape sequences / predefined variables — a darker
  // gold that doesn't currently have a token. Kept inline rather than promoted
  // because it only appears inside the Monaco theme.
  const goldDark = "6e5520";

  return {
    base: "vs",
    inherit: false,
    rules: [
      { token: "", foreground: ink1, background: paper0 },
      { token: "invalid", foreground: cinnabar1 },

      // Comments — jade, italic
      { token: "comment", foreground: jade1, fontStyle: "italic" },
      { token: "comment.block", foreground: jade1, fontStyle: "italic" },
      { token: "comment.line", foreground: jade1, fontStyle: "italic" },

      // Keywords — cinnabar
      { token: "keyword", foreground: cinnabar0 },
      { token: "keyword.control", foreground: cinnabar0 },
      { token: "storage", foreground: cinnabar0 },
      { token: "storage.type", foreground: cinnabar0 },

      // Strings — gold
      { token: "string", foreground: gold0 },
      { token: "string.escape", foreground: goldDark },

      // Numbers — gold
      { token: "number", foreground: gold0 },
      { token: "number.hex", foreground: gold0 },

      // Types — dark jade
      { token: "type", foreground: jade0 },
      { token: "type.identifier", foreground: jade0 },
      { token: "entity.name.type", foreground: jade0 },

      // Functions — strong ink
      { token: "entity.name.function", foreground: ink0 },
      { token: "support.function", foreground: ink0 },

      // Variables / identifiers
      { token: "variable", foreground: ink1 },
      { token: "variable.predefined", foreground: goldDark },
      { token: "variable.parameter", foreground: ink2 },

      // Operators / punctuation — muted
      { token: "delimiter", foreground: ink3 },
      { token: "delimiter.bracket", foreground: ink3 },
      { token: "operator", foreground: ink3 },

      // Tags (HTML/XML) — cinnabar
      { token: "tag", foreground: cinnabar0 },
      { token: "metatag", foreground: ink3 },
      { token: "tag.attribute.name", foreground: ink2 },

      // Attribute values — gold
      { token: "attribute.value", foreground: gold0 },

      // Constants
      { token: "constant", foreground: gold0 },

      // Regex
      { token: "regexp", foreground: jade1 },

      // Markdown
      { token: "markup.heading", foreground: ink0, fontStyle: "bold" },
      { token: "markup.bold", fontStyle: "bold" },
      { token: "markup.italic", fontStyle: "italic" },
      { token: "markup.inline.raw", foreground: goldDark },

      // JSON keys
      { token: "string.key.json", foreground: ink2 },
      { token: "string.value.json", foreground: gold0 },

      // YAML
      { token: "type.yaml", foreground: ink2 },

      // TOML / INI
      { token: "type.ini", foreground: ink2 },
    ],
    colors: {
      "editor.background": hashed(paper0),
      "editor.foreground": hashed(ink1),
      "editor.lineHighlightBackground": `#${paper1}40`,
      "editor.selectionBackground": `#${paper2}66`,
      "editor.inactiveSelectionBackground": `#${paper2}33`,
      "editor.selectionHighlightBackground": `#${paper2}44`,
      "editor.findMatchBackground": `#${gold2}44`,
      "editor.findMatchHighlightBackground": `#${gold2}22`,

      "editorLineNumber.foreground": hashed(ink4),
      "editorLineNumber.activeForeground": hashed(ink3),

      "editorCursor.foreground": hashed(cinnabar0),

      "editorIndentGuide.background": `#${paper2}80`,
      "editorIndentGuide.activeBackground": hashed(paper3),

      "editorGutter.background": hashed(paper0),

      "editorBracketMatch.background": `#${paper2}44`,
      "editorBracketMatch.border": hashed(paper3),

      "scrollbar.shadow": "#00000011",
      "scrollbarSlider.background": `#${paper3}44`,
      "scrollbarSlider.hoverBackground": `#${paper3}77`,
      "scrollbarSlider.activeBackground": `#${ink4}99`,

      "editorWidget.background": hashed(paper1),
      "editorWidget.border": hashed(paper2),
      "editorWidget.foreground": hashed(ink1),

      "editorHoverWidget.background": hashed(paper1),
      "editorHoverWidget.border": hashed(paper2),

      "editorOverviewRuler.border": hashed(paper1),

      "minimap.background": hashed(paper0),
    },
  };
}
